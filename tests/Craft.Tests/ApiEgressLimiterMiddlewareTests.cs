using System.Globalization;
using System.Text;
using Craft.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;

namespace Craft.Tests;

/// <summary>
/// The middleware is where the cap meets a real request. Two correctness edges matter most: it must
/// never touch interactive (UI) traffic — counting or capping a person is the bug the whole feature is
/// designed around — and its accounting must see the bytes an API response actually writes, whichever
/// write path the handler uses. These also pin the shed path: over budget → 429 with a Retry-After to
/// the next UTC midnight and the downstream call never runs.
/// </summary>
public class ApiEgressLimiterMiddlewareTests : IDisposable
{
    private readonly string _dir =
        Path.Combine(Path.GetTempPath(), "craft-egress-mw-" + Guid.NewGuid().ToString("N")[..8]);

    public ApiEgressLimiterMiddlewareTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* temp dir */ }
        GC.SuppressFinalize(this);
    }

    private EgressLedger Ledger(long cap) =>
        new(NullLogger<EgressLedger>.Instance, cap, flushSeconds: 60,
            Path.Combine(_dir, Guid.NewGuid().ToString("N")[..8] + ".json"));

    private static ApiEgressLimiterMiddleware Middleware(RequestDelegate next, EgressLedger ledger) =>
        new(next, ledger, NullLoggerFactory.Instance);

    private static DefaultHttpContext ApiContext()
    {
        var ctx = new DefaultHttpContext();
        // Exactly what CraftAuthMiddleware writes for a client-credentials caller (idp=aad + GUID AppId).
        ctx.Request.Headers["x-ms-client-principal-idp"] = "aad";
        ctx.Request.Headers["x-ms-client-principal-name"] = "11111111-2222-3333-4444-555555555555";
        ctx.Response.Body = new MemoryStream();
        return ctx;
    }

    private static DefaultHttpContext UiContext()
    {
        var ctx = new DefaultHttpContext();
        ctx.Request.Headers["x-ms-client-principal-idp"] = "azureStaticWebApps";
        ctx.Request.Headers["x-ms-client-principal-name"] = "user@contoso.com";
        ctx.Response.Body = new MemoryStream();
        return ctx;
    }

    private static string BodyText(HttpContext ctx)
    {
        var ms = (MemoryStream)ctx.Response.Body;
        return Encoding.UTF8.GetString(ms.ToArray());
    }

    // ── UI traffic is untouched ─────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task UiCaller_OverBudget_IsNeverRejectedOrCounted()
    {
        var ledger = Ledger(cap: 10);
        ledger.Record(1_000_000);                 // instance is way over budget
        Assert.True(ledger.ShouldReject());

        var called = false;
        var mw = Middleware(async ctx => { called = true; await ctx.Response.WriteAsync("ok"); }, ledger);

        var ctx = UiContext();
        await mw.InvokeAsync(ctx);

        Assert.True(called);                                  // the person's request runs
        Assert.NotEqual(StatusCodes.Status429TooManyRequests, ctx.Response.StatusCode);
        Assert.Equal(1_000_000L, ledger.CurrentBytes);       // and their bytes are not added to the tally
    }

    // ── API accounting ──────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task ApiCaller_UnderBudget_RunsAndBytesAreCounted_DirectBodyWrite()
    {
        var ledger = Ledger(cap: 1_000_000);
        var payload = Encoding.UTF8.GetBytes(new string('x', 4096));

        var mw = Middleware(async ctx => { await ctx.Response.Body.WriteAsync(payload); }, ledger);

        var ctx = ApiContext();
        await mw.InvokeAsync(ctx);

        Assert.Equal((long)payload.Length, ledger.CurrentBytes);
        Assert.Equal((long)payload.Length, ((MemoryStream)ctx.Response.Body).Length); // bytes really went out
    }

    [Fact]
    public async Task ApiCaller_BytesCounted_ThroughResponseWriteAsyncPath()
    {
        // Response.WriteAsync goes via the response pipe writer, not Body.WriteAsync directly. Swapping
        // Body must still capture it — this is the path the PowerShell dispatcher uses for string bodies.
        var ledger = Ledger(cap: 1_000_000);
        var text = new string('y', 3000);

        var mw = Middleware(async ctx => { await ctx.Response.WriteAsync(text); }, ledger);

        var ctx = ApiContext();
        await mw.InvokeAsync(ctx);

        Assert.Equal((long)Encoding.UTF8.GetByteCount(text), ledger.CurrentBytes);
    }

    [Fact]
    public async Task ApiCaller_CapZero_CountsButNeverRejects()
    {
        var ledger = Ledger(cap: 0);              // accounting-only phase
        var payload = Encoding.UTF8.GetBytes(new string('z', 2048));

        var mw = Middleware(async ctx => { await ctx.Response.Body.WriteAsync(payload); }, ledger);

        var ctx = ApiContext();
        await mw.InvokeAsync(ctx);

        Assert.NotEqual(StatusCodes.Status429TooManyRequests, ctx.Response.StatusCode);
        Assert.Equal((long)payload.Length, ledger.CurrentBytes);
    }

    [Fact]
    public async Task ApiCaller_RequestThatTipsOverBudget_StillCompletes()
    {
        // Accounting is post-hoc: the request that crosses the line finishes; the NEXT one is refused.
        var ledger = Ledger(cap: 1000);
        var payload = Encoding.UTF8.GetBytes(new string('x', 4000)); // one response blows past the cap

        var mw = Middleware(async ctx => { await ctx.Response.Body.WriteAsync(payload); }, ledger);

        var ctx = ApiContext();
        await mw.InvokeAsync(ctx);

        Assert.NotEqual(StatusCodes.Status429TooManyRequests, ctx.Response.StatusCode);
        Assert.True(ledger.ShouldReject());       // now over — the next API request will be shed
    }

    // ── API shed path ───────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task ApiCaller_OverBudget_Is429_WithRetryAfter_AndDownstreamNeverRuns()
    {
        var ledger = Ledger(cap: 1000);
        ledger.Record(1000);                      // exactly at budget → reject
        Assert.True(ledger.ShouldReject());

        var called = false;
        var mw = Middleware(_ => { called = true; return Task.CompletedTask; }, ledger);

        var ctx = ApiContext();
        await mw.InvokeAsync(ctx);

        Assert.False(called);                                            // the minute-long PS call is skipped
        Assert.Equal(StatusCodes.Status429TooManyRequests, ctx.Response.StatusCode);

        var retryAfter = ctx.Response.Headers.RetryAfter.ToString();
        Assert.False(string.IsNullOrEmpty(retryAfter));
        var seconds = int.Parse(retryAfter, CultureInfo.InvariantCulture);
        Assert.InRange(seconds, 1, 86_400);                             // a sane time-to-next-UTC-midnight

        var body = BodyText(ctx);
        Assert.Contains("egress_quota_exceeded", body);
        Assert.Contains("resetUtc", body);
        Assert.Equal("application/json", ctx.Response.ContentType);
    }

    [Fact]
    public async Task ApiCaller_ResponseBody_IsRestoredAfterInvoke()
    {
        // The counting wrapper must not leak past the request — Response.Body is put back to the
        // original stream in the finally, whether the handler succeeded or threw.
        var ledger = Ledger(cap: 1_000_000);
        var ctx = ApiContext();
        var original = ctx.Response.Body;

        var mw = Middleware(async c => { await c.Response.Body.WriteAsync(new byte[10]); }, ledger);
        await mw.InvokeAsync(ctx);

        Assert.Same(original, ctx.Response.Body);
    }

    [Fact]
    public async Task ApiCaller_HandlerThrows_BytesStillCounted_AndBodyRestored()
    {
        var ledger = Ledger(cap: 1_000_000);
        var ctx = ApiContext();
        var original = ctx.Response.Body;

        var mw = Middleware(async c =>
        {
            await c.Response.Body.WriteAsync(new byte[128]);
            throw new InvalidOperationException("boom");
        }, ledger);

        await Assert.ThrowsAsync<InvalidOperationException>(() => mw.InvokeAsync(ctx));

        Assert.Equal(128L, ledger.CurrentBytes);   // partial egress already went out — count it
        Assert.Same(original, ctx.Response.Body);   // and the wrapper is still unwound
    }

    [Fact]
    public async Task AnonymousCaller_IsIgnored()
    {
        var ledger = Ledger(cap: 10);
        ledger.Record(1_000_000);

        var called = false;
        var mw = Middleware(ctx => { called = true; return Task.CompletedTask; }, ledger);

        var ctx = new DefaultHttpContext();       // no principal headers at all
        ctx.Response.Body = new MemoryStream();
        await mw.InvokeAsync(ctx);

        Assert.True(called);
        Assert.NotEqual(StatusCodes.Status429TooManyRequests, ctx.Response.StatusCode);
    }
}

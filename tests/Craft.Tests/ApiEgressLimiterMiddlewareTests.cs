using System.Globalization;
using System.Text;
using Craft.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;

namespace Craft.Tests;

/// <summary>
/// The limiter is the policy half of the egress feature: classify the caller, shed when over budget, and
/// flag the requests that should be billed. Two correctness edges matter most — it must never touch
/// interactive (UI) traffic (counting or capping a person is the bug the whole feature is designed
/// around), and it must set the charge flag on exactly the API requests it lets through, so the wire
/// counter bills those and nothing else. The shed path is pinned too: over budget → 429 with a
/// Retry-After to the next UTC midnight, the downstream call never runs, and the 429 is left unflagged so
/// it is never charged. Recording the actual bytes is the wire counter's job — see
/// <see cref="ApiEgressWireCounterMiddlewareTests"/>.
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

    private static bool Charged(HttpContext ctx) =>
        ctx.Items.TryGetValue(ApiEgressWireCounterMiddleware.ChargeItemKey, out var v) && v is true;

    private static string BodyText(HttpContext ctx)
    {
        var ms = (MemoryStream)ctx.Response.Body;
        return Encoding.UTF8.GetString(ms.ToArray());
    }

    // ── UI traffic is untouched ─────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task UiCaller_OverBudget_IsNeverRejectedNorFlagged()
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
        Assert.False(Charged(ctx));                           // and it is not marked for billing
        Assert.Equal(1_000_000L, ledger.CurrentBytes);        // the ledger is untouched by a UI request
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
        Assert.False(Charged(ctx));
    }

    // ── API greenlight → flagged for billing (the limiter itself records nothing) ─────────────────────

    [Fact]
    public async Task ApiCaller_UnderBudget_RunsAndFlagsForBilling()
    {
        var ledger = Ledger(cap: 1_000_000);
        var called = false;

        var mw = Middleware(async ctx => { called = true; await ctx.Response.WriteAsync("data"); }, ledger);

        var ctx = ApiContext();
        await mw.InvokeAsync(ctx);

        Assert.True(called);                    // downstream ran
        Assert.True(Charged(ctx));              // and the request is flagged for the wire counter to bill
        Assert.NotEqual(StatusCodes.Status429TooManyRequests, ctx.Response.StatusCode);
        Assert.Equal(0L, ledger.CurrentBytes);  // the limiter does NOT record — that is the wire counter's job
    }

    [Fact]
    public async Task ApiCaller_CapZero_FlagsButNeverRejects()
    {
        var ledger = Ledger(cap: 0);              // accounting-only phase

        var mw = Middleware(async ctx => { await ctx.Response.WriteAsync("data"); }, ledger);

        var ctx = ApiContext();
        await mw.InvokeAsync(ctx);

        Assert.True(Charged(ctx));
        Assert.NotEqual(StatusCodes.Status429TooManyRequests, ctx.Response.StatusCode);
    }

    // ── API shed path ───────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task ApiCaller_OverBudget_Is429_WithRetryAfter_DownstreamSkipped_AndNotFlagged()
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
        Assert.False(Charged(ctx));                                      // the 429 body must never be billed

        var retryAfter = ctx.Response.Headers.RetryAfter.ToString();
        Assert.False(string.IsNullOrEmpty(retryAfter));
        var seconds = int.Parse(retryAfter, CultureInfo.InvariantCulture);
        Assert.InRange(seconds, 1, 86_400);                             // a sane time-to-next-UTC-midnight

        var body = BodyText(ctx);
        Assert.Contains("egress_quota_exceeded", body);
        Assert.Contains("resetUtc", body);
        Assert.Equal("application/json", ctx.Response.ContentType);
    }
}

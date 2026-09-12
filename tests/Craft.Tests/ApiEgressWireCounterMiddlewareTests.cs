using System.IO.Compression;
using System.Text;
using Craft.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;

namespace Craft.Tests;

/// <summary>
/// The wire counter is the accounting half of the egress feature. It carries no compression logic — it
/// measures whatever bytes are written to the response stream and, for the requests the limiter flagged,
/// records them into the ledger. The edges that matter: it counts through every write path; it counts
/// the <i>compressed</i> size when compression runs inside it (which is the whole reason it sits outside
/// the compressor in the pipeline); it records only flagged requests; and it always restores the
/// response body, even when the handler throws. The limiter+counter integration cases pin the handoff:
/// a greenlit API request is billed, a shed 429 is not.
/// </summary>
public class ApiEgressWireCounterMiddlewareTests : IDisposable
{
    private readonly string _dir =
        Path.Combine(Path.GetTempPath(), "craft-egress-wire-" + Guid.NewGuid().ToString("N")[..8]);

    public ApiEgressWireCounterMiddlewareTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* temp dir */ }
        GC.SuppressFinalize(this);
    }

    private EgressLedger Ledger(long cap) =>
        new(NullLogger<EgressLedger>.Instance, cap, flushSeconds: 60,
            Path.Combine(_dir, Guid.NewGuid().ToString("N")[..8] + ".json"));

    private static ApiEgressWireCounterMiddleware WireCounter(RequestDelegate next, EgressLedger ledger) =>
        new(next, ledger);

    private static DefaultHttpContext Context()
    {
        var ctx = new DefaultHttpContext();
        ctx.Response.Body = new MemoryStream();
        return ctx;
    }

    // The limiter greenlights an API request by setting this on the way through; the fake handlers below
    // do the same, since in the real pipeline the flag is set inside the counter's own invocation.
    private static void Flag(HttpContext ctx) =>
        ctx.Items[ApiEgressWireCounterMiddleware.ChargeItemKey] = true;

    // ── records the flagged request's wire bytes, through either write path ───────────────────────────

    [Fact]
    public async Task Flagged_RecordsWireBytes_DirectBodyWrite()
    {
        var ledger = Ledger(cap: 1_000_000);
        var payload = Encoding.UTF8.GetBytes(new string('x', 4096));

        var mw = WireCounter(async ctx => { Flag(ctx); await ctx.Response.Body.WriteAsync(payload); }, ledger);

        var ctx = Context();
        await mw.InvokeAsync(ctx);

        Assert.Equal((long)payload.Length, ledger.CurrentBytes);
        Assert.Equal((long)payload.Length, ((MemoryStream)ctx.Response.Body).Length); // bytes really went out
    }

    [Fact]
    public async Task Flagged_RecordsWireBytes_ThroughResponseWriteAsyncPath()
    {
        // Response.WriteAsync goes via the response pipe writer, not Body.WriteAsync directly. Swapping
        // Body must still capture it — this is the path the PowerShell dispatcher uses for string bodies.
        var ledger = Ledger(cap: 1_000_000);
        var text = new string('y', 3000);

        var mw = WireCounter(async ctx => { Flag(ctx); await ctx.Response.WriteAsync(text); }, ledger);

        var ctx = Context();
        await mw.InvokeAsync(ctx);

        Assert.Equal((long)Encoding.UTF8.GetByteCount(text), ledger.CurrentBytes);
    }

    [Fact]
    public async Task Unflagged_WritesGoOut_ButNothingIsRecorded()
    {
        // A UI request, an anonymous request or a shed 429 reaches the counter without the flag — the
        // body is still written, but it is never billed.
        var ledger = Ledger(cap: 1_000_000);
        var payload = Encoding.UTF8.GetBytes(new string('z', 2048));

        var mw = WireCounter(async ctx => { await ctx.Response.Body.WriteAsync(payload); }, ledger);

        var ctx = Context();
        await mw.InvokeAsync(ctx);

        Assert.Equal((long)payload.Length, ((MemoryStream)ctx.Response.Body).Length); // written
        Assert.Equal(0L, ledger.CurrentBytes);                                        // but not counted
    }

    // ── counts the COMPRESSED size when compression runs inside it ────────────────────────────────────

    [Fact]
    public async Task Flagged_CountsCompressedSize_WhenCompressionRunsInsideIt()
    {
        // The counter sits outside the response compressor precisely so it bills the on-the-wire bytes.
        // Model that here by writing a compressible body through gzip into the (wrapped) response stream:
        // the counter must see the compressed output, not the raw payload.
        var ledger = Ledger(cap: 10_000_000);
        var raw = Encoding.UTF8.GetBytes(new string('a', 20_000)); // highly compressible

        var mw = WireCounter(async ctx =>
        {
            Flag(ctx);
            await using var gz = new GZipStream(ctx.Response.Body, CompressionLevel.Fastest, leaveOpen: true);
            await gz.WriteAsync(raw);
        }, ledger);

        var ctx = Context();
        await mw.InvokeAsync(ctx);

        var wire = ((MemoryStream)ctx.Response.Body).Length;
        Assert.True(ledger.CurrentBytes > 0);
        Assert.True(ledger.CurrentBytes < raw.Length);   // compressed is smaller than the raw JSON
        Assert.Equal(wire, ledger.CurrentBytes);         // and it billed exactly what left the box
    }

    // ── body is always restored ───────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task RestoresResponseBody_AfterInvoke()
    {
        var ledger = Ledger(cap: 1_000_000);
        var ctx = Context();
        var original = ctx.Response.Body;

        var mw = WireCounter(async c => { Flag(c); await c.Response.Body.WriteAsync(new byte[10]); }, ledger);
        await mw.InvokeAsync(ctx);

        Assert.Same(original, ctx.Response.Body);
    }

    [Fact]
    public async Task HandlerThrows_PartialBytesStillRecorded_AndBodyRestored()
    {
        var ledger = Ledger(cap: 1_000_000);
        var ctx = Context();
        var original = ctx.Response.Body;

        var mw = WireCounter(async c =>
        {
            Flag(c);
            await c.Response.Body.WriteAsync(new byte[128]);
            throw new InvalidOperationException("boom");
        }, ledger);

        await Assert.ThrowsAsync<InvalidOperationException>(() => mw.InvokeAsync(ctx));

        Assert.Equal(128L, ledger.CurrentBytes);    // partial egress already went out — count it
        Assert.Same(original, ctx.Response.Body);    // and the wrapper is still unwound
    }

    // ── limiter + counter together (the flag handoff) ─────────────────────────────────────────────────

    private static DefaultHttpContext ApiContext()
    {
        var ctx = new DefaultHttpContext();
        ctx.Request.Headers["x-ms-client-principal-idp"] = "aad";
        ctx.Request.Headers["x-ms-client-principal-name"] = "11111111-2222-3333-4444-555555555555";
        ctx.Response.Body = new MemoryStream();
        return ctx;
    }

    [Fact]
    public async Task LimiterGreenlight_ThenCounterRecords_AdvancesLedger()
    {
        // Pipeline order: wire counter (outer) → egress limiter (inner) → handler. The limiter sets the
        // charge flag, the counter — running outside — records the bytes on the way back out.
        var ledger = Ledger(cap: 1_000_000);
        var payload = Encoding.UTF8.GetBytes(new string('x', 4096));

        var limiter = new ApiEgressLimiterMiddleware(
            async ctx => await ctx.Response.Body.WriteAsync(payload), ledger, NullLoggerFactory.Instance);
        var mw = WireCounter(ctx => limiter.InvokeAsync(ctx), ledger);

        var ctx = ApiContext();
        await mw.InvokeAsync(ctx);

        Assert.Equal((long)payload.Length, ledger.CurrentBytes);
    }

    [Fact]
    public async Task OverBudget_LimiterSheds_CounterDoesNotBillThe429()
    {
        var ledger = Ledger(cap: 1000);
        ledger.Record(1000);                    // at budget → the limiter sheds
        var before = ledger.CurrentBytes;

        var limiter = new ApiEgressLimiterMiddleware(
            _ => Task.CompletedTask, ledger, NullLoggerFactory.Instance);
        var mw = WireCounter(ctx => limiter.InvokeAsync(ctx), ledger);

        var ctx = ApiContext();
        await mw.InvokeAsync(ctx);

        Assert.Equal(StatusCodes.Status429TooManyRequests, ctx.Response.StatusCode);
        Assert.Equal(before, ledger.CurrentBytes); // the shed 429 body is never billed
    }
}

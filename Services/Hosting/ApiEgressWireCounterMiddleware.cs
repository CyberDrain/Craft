namespace Craft.Hosting;

/// <summary>
/// Measures the on-the-wire size of each <c>/api</c> response and, for the requests the egress limiter
/// greenlit, records it into the <see cref="EgressLedger"/>. It is the accounting half of the egress
/// feature and holds <b>no</b> compression logic of its own — it simply counts whatever bytes leave the
/// box, whether that is identity or a Brotli/gzip body produced by the generic <c>/api</c> response
/// compression that runs just inside it.
/// <para>
/// <b>Why a second middleware, and why here.</b> The egress cap is meant to bill the bytes that
/// actually transit the network, so the counter has to sit <i>outside</i> compression: response
/// compression only emits its trailing block when its middleware unwinds, so a counter placed inside it
/// would both count the pre-compression body and miss the final flush. This middleware is therefore
/// registered as the outermost link of the <c>/api</c> pipeline — ahead of
/// <c>UseResponseCompression</c> — so its <see cref="CountingStream"/> wraps the socket and sees the
/// compressed output, and its <c>finally</c> runs only after compression has fully flushed.
/// </para>
/// <para>
/// The decision of <i>which</i> requests to bill stays entirely in <see cref="ApiEgressLimiterMiddleware"/>,
/// which runs later (after auth, so it can classify the caller and shed when over budget). When it lets
/// an API request through it sets <see cref="ChargeItemKey"/> on the request; this middleware records
/// the wire bytes only when that flag is present, so UI traffic, anonymous requests, static assets and
/// shed 429s are never charged. Registered only when egress accounting is enabled — see
/// <c>CraftHostBuilderExtensions.AddCraftEgressLimiter</c> and the <c>/api</c> pipeline in <c>Program.cs</c>.
/// </para>
/// </summary>
public sealed class ApiEgressWireCounterMiddleware
{
    /// <summary>
    /// Request item set by <see cref="ApiEgressLimiterMiddleware"/> on an API request it lets through:
    /// the caller's AppId (a non-empty string), signalling this middleware to bill the response's wire
    /// bytes against that client. Absent for UI, anonymous, static and shed requests, which are never
    /// charged.
    /// </summary>
    public const string ChargeItemKey = "Craft.Egress.Charge";

    private readonly RequestDelegate _next;
    private readonly EgressLedger _ledger;

    public ApiEgressWireCounterMiddleware(RequestDelegate next, EgressLedger ledger)
    {
        _next = next ?? throw new ArgumentNullException(nameof(next));
        _ledger = ledger ?? throw new ArgumentNullException(nameof(ledger));
    }

    public async Task InvokeAsync(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        // Swapping Response.Body captures every write path (Body.WriteAsync, Response.WriteAsync, the
        // BodyWriter pipe) — see CountingStream. Restored in the finally, whether the pipeline completed
        // or threw. Only requests the limiter flagged are recorded; the flag is set downstream (inner)
        // and read here after the whole pipeline — including compression's flush — has unwound.
        var original = context.Response.Body;
        var counting = new CountingStream(original);
        context.Response.Body = counting;
        try
        {
            await _next(context);
        }
        finally
        {
            context.Response.Body = original;
            if (context.Items.TryGetValue(ChargeItemKey, out var charge) && charge is string appId && appId.Length > 0)
                _ledger.Record(counting.BytesWritten, appId);
        }
    }
}

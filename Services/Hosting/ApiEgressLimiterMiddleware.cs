using System.Globalization;

namespace Craft.Hosting;

/// <summary>
/// Sheds app-only API traffic once the instance has served its daily egress budget, and marks the
/// requests it lets through for billing. Runs only for API clients (client-credentials callers); every
/// interactive request passes straight through after a single header check.
/// <para>
/// This is the <i>policy</i> half of the egress feature: it classifies the caller and decides whether
/// to reject. It no longer counts bytes itself — the outbound size is measured post-compression by
/// <see cref="ApiEgressWireCounterMiddleware"/>, which runs outside the response compressor. When this
/// middleware lets an API request through it sets <see cref="ApiEgressWireCounterMiddleware.ChargeItemKey"/>
/// on the request, and the wire counter records the response's on-the-wire bytes for exactly those
/// requests. Splitting it this way keeps the cap billing what actually transits the network (the
/// compressed body) while the shed decision stays here, after auth, where the caller is known.
/// </para>
/// <para>
/// Registered only when egress accounting is enabled (hosted env, or forced) — see
/// <c>CraftHostBuilderExtensions.AddCraftEgressLimiter</c>. Placement mirrors the rate limiter: after
/// the auth middleware (so an app-only caller's AppId is resolved) and after static file serving (so a
/// page load's assets are never charged). Enforcement (the 429) only bites once a budget is configured;
/// with no budget it flags silently, which is the accounting-only rollout phase.
/// </para>
/// </summary>
public sealed class ApiEgressLimiterMiddleware
{
    private readonly RequestDelegate _next;
    private readonly EgressLedger _ledger;
    private readonly ILogger _logger;

    public ApiEgressLimiterMiddleware(RequestDelegate next, EgressLedger ledger, ILoggerFactory loggerFactory)
    {
        ArgumentNullException.ThrowIfNull(loggerFactory);
        _next = next ?? throw new ArgumentNullException(nameof(next));
        _ledger = ledger ?? throw new ArgumentNullException(nameof(ledger));
        _logger = loggerFactory.CreateLogger("Craft.Hosting.ApiEgressLimiter");
    }

    public async Task InvokeAsync(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        // UI and anonymous callers are never counted or capped — the feature is only about app-only
        // automation. One header check and out, so interactive traffic pays essentially nothing.
        if (!CallerClassifier.IsApiClient(context))
        {
            await _next(context);
            return;
        }

        // Already over budget for today → shed before running the (often minute-long) downstream call,
        // saving both the compute and the egress. Accounting is post-hoc, so the request that tips the
        // total over still completes; the NEXT one is the first to be refused. Combined with the API
        // concurrency cap, the overshoot is bounded to (concurrency × largest response).
        if (_ledger.ShouldReject())
        {
            await RejectAsync(context);
            return;
        }

        // Greenlit: mark the request so ApiEgressWireCounterMiddleware (running outside the response
        // compressor) bills its on-the-wire bytes. We don't count here — a counter at this position
        // would see the pre-compression body and miss the compressor's final flush, which unwinds
        // further out. The shed body above is deliberately left unflagged, so it is never charged.
        context.Items[ApiEgressWireCounterMiddleware.ChargeItemKey] = true;
        await _next(context);
    }

    private async Task RejectAsync(HttpContext context)
    {
        var retryAfter = _ledger.SecondsToNextUtcMidnight();
        var resetUtc = _ledger.NextUtcMidnightIso();

        context.Response.StatusCode = StatusCodes.Status429TooManyRequests;
        context.Response.Headers.RetryAfter = retryAfter.ToString(CultureInfo.InvariantCulture);
        context.Response.ContentType = "application/json";

        // A shed request is otherwise invisible to the operator. Warning, not Error: expected and
        // client-caused, but the caller (its AppId, via the same key the rate limiter logs) is exactly
        // who you want to identify. Never let logging turn the shed into a 500.
        try
        {
            _logger.LogWarning(
                "Egress cap reached — 429 for {Client} on {Method} {Path}; served {Bytes}/{Cap} bytes today, Retry-After {RetryAfter}s",
                RateLimitPartitionKey.Resolve(context), context.Request.Method, context.Request.Path.Value,
                _ledger.CurrentBytes, _ledger.CapBytes, retryAfter);
        }
        catch { /* logging must never break the response */ }

        // The rejection body itself is deliberately not counted — it is tiny and we are already over.
        await context.Response.WriteAsync(
            "{\"error\":\"egress_quota_exceeded\"," +
            "\"message\":\"Daily API egress limit reached for this instance. Requests resume after the UTC daily reset.\"," +
            $"\"retryAfterSeconds\":{retryAfter}," +
            $"\"resetUtc\":\"{resetUtc}\"}}");
    }
}

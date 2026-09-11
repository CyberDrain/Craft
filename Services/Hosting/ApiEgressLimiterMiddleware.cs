using System.Globalization;

namespace Craft.Hosting;

/// <summary>
/// Sheds app-only API traffic once the instance has served its daily egress budget, and records the
/// outbound bytes of the responses it lets through. Runs only for API clients (client-credentials
/// callers); every interactive request passes straight through after a single header check.
/// <para>
/// Registered only when egress accounting is enabled (hosted env, or forced) — see
/// <c>CraftHostBuilderExtensions.AddCraftEgressLimiter</c>. Placement mirrors the rate limiter: after
/// the auth middleware (so an app-only caller's AppId is resolved) and after static file serving (so a
/// page load's assets are never charged). Enforcement (the 429) only bites once a budget is configured;
/// with no budget it counts silently, which is the accounting-only rollout phase.
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

        // Count the body this request writes. Swapping Response.Body captures both a direct
        // Body.WriteAsync and Response.WriteAsync(string), since the response writer is re-adapted onto
        // our stream — see CountingStream.
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
            _ledger.Record(counting.BytesWritten);
        }
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

namespace Craft.Configuration;

/// <summary>
/// Request rate limiting. On by default: a per-client fixed-window limiter partitioned by
/// authenticated principal name (falling back to X-Forwarded-For / remote IP) so a single caller
/// cannot exhaust the HTTP worker pool.
/// </summary>
public class RateLimitSettings
{
    /// <summary>
    /// Enable the global rate limiter. Default true (300 requests / 10 s per client). Disable via
    /// App:RateLimit:Enabled=false; the CRAFT_RATELIMIT_ENABLED=true env var can also force it on.
    /// </summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Permitted requests per window, per client. Default 300.</summary>
    public int PermitPerWindow { get; set; } = 300;

    /// <summary>Window length in seconds. Default 10.</summary>
    public int WindowSeconds { get; set; } = 10;

    /// <summary>
    /// Requests queued when the limit is hit before rejecting with 429. Default 0 (reject
    /// immediately). Rejections carry a <c>Retry-After</c> header.
    /// </summary>
    public int QueueLimit { get; set; }

    /// <summary>
    /// Maximum requests a single app-only API client (client-credentials caller) may have occupying
    /// the HTTP worker system at once — counting both those queued waiting for a runspace and those
    /// already executing, since the limiter's lease spans the whole downstream pipeline. Keyed per
    /// client (its AppId), so one automation cannot monopolise the pool and starve the interactive UI,
    /// which is never limited by this. Over-limit requests are rejected immediately with 429 (no
    /// concurrency queue); the caller retries on <c>Retry-After</c>.
    ///
    /// <para>
    /// 0 (default) = unlimited: the feature is off until a value is set. Distinct from
    /// <see cref="PermitPerWindow"/>, which is a request-RATE cap; this is a simultaneous-in-flight cap.
    /// Interactive (browser) callers are classified as UI and never counted here.
    /// </para>
    ///
    /// Env override: <c>CRAFT_API_CONCURRENCY_LIMIT</c>.
    /// </summary>
    public int ApiConcurrencyLimit { get; set; }

    /// <summary>Resolved enabled state, honouring the CRAFT_RATELIMIT_ENABLED environment override.</summary>
    public bool IsEnabled =>
        Enabled
        || string.Equals(Environment.GetEnvironmentVariable("CRAFT_RATELIMIT_ENABLED"), "true", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Resolved per-client API concurrency cap, honouring the <c>CRAFT_API_CONCURRENCY_LIMIT</c>
    /// environment override (which wins when it parses to a non-negative integer). 0 = unlimited/off.
    /// </summary>
    public int ResolvedApiConcurrencyLimit =>
        int.TryParse(Environment.GetEnvironmentVariable("CRAFT_API_CONCURRENCY_LIMIT"),
            System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture,
            out var fromEnv) && fromEnv >= 0
            ? fromEnv
            : ApiConcurrencyLimit;

    /// <summary>
    /// Whether the rate-limiter middleware needs to run at all: either the per-client rate limiter is
    /// enabled, or an API concurrency cap is configured. When both are off, the middleware is skipped
    /// entirely (no per-request limiter cost).
    /// </summary>
    public bool RequiresLimiterMiddleware => IsEnabled || ResolvedApiConcurrencyLimit > 0;
}

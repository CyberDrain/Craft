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
    /// <c>App:RateLimit:Enabled=false</c>. The <c>CRAFT_RATELIMIT_ENABLED</c> environment variable
    /// (true/1 or false/0) wins when set.
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
    /// Resolved enabled state. <c>CRAFT_RATELIMIT_ENABLED</c> wins when set; otherwise
    /// <see cref="Enabled"/> applies.
    /// </summary>
    public bool IsEnabled
    {
        get
        {
            var v = Environment.GetEnvironmentVariable("CRAFT_RATELIMIT_ENABLED");
            if (string.IsNullOrWhiteSpace(v)) return Enabled;
            return v.Equals("true", StringComparison.OrdinalIgnoreCase) || v == "1";
        }
    }
}

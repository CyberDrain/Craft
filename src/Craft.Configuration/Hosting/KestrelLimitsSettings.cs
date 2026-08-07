namespace Craft.Configuration;

/// <summary>
/// Kestrel request limits applied unconditionally at startup. These protect the small HTTP worker
/// pool from oversized bodies and connection floods independently of the request timeout.
/// </summary>
public class KestrelLimitsSettings
{
    /// <summary>Maximum request body size in megabytes. Default 100. Set to 0 for unlimited (not recommended).</summary>
    public int MaxRequestBodyMB { get; set; } = 100;

    /// <summary>
    /// Maximum concurrent TCP connections. Default 200 (a reasonable cap for a B2-class host). Set to
    /// 0 or a negative value for unlimited (let the OS decide).
    /// </summary>
    public int MaxConcurrentConnections { get; set; } = 200;
}

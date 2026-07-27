namespace Craft.Configuration;

/// <summary>
/// Realtime SSE channel served at <c>/.craft/events</c>. <b>Opt-in — off by default.</b> Downstream code
/// publishes job lifecycle events through <see cref="Craft.Services.RealtimeBridge"/>; browsers consume them. In-memory,
/// single instance — see docs/realtime-bridge-plan.md. The limits below bound memory and the connection budget.
/// </summary>
public class RealtimeSettings
{
    /// <summary>
    /// Enable the realtime endpoint and bridge delivery. Default <c>false</c> — turn it on explicitly with
    /// <c>App:Realtime:Enabled=true</c> (delivery is then still role-gated to http/frontend nodes). While
    /// off, <c>/.craft/events</c> is not mapped and <see cref="Craft.Services.RealtimeBridge"/> publishes are no-ops.
    /// </summary>
    public bool Enabled { get; set; }

    /// <summary>Max serialized size of a single event's <c>data</c> payload. Over this it is dropped and a
    /// 413 "too large" frame is delivered instead. Default 16 KB.</summary>
    public int MaxMessageBytes { get; set; } = 16 * 1024;

    /// <summary>Max number of concurrently stored (userId, jobId) entries. Default 10000.</summary>
    public int MaxActiveJobs { get; set; } = 10_000;

    /// <summary>Max number of concurrent SSE connections across all users. Default 1000.</summary>
    public int MaxConnections { get; set; } = 1_000;

    /// <summary>Buffered frames per connection before the oldest is dropped (coalescing). Default 256.</summary>
    public int PerConnectionQueue { get; set; } = 256;

    /// <summary>Heartbeat comment interval, seconds, to keep the stream alive through proxies. Default 20.</summary>
    public int HeartbeatSeconds { get; set; } = 20;

    /// <summary>TTL for a stored entry that never receives an <c>end</c> (crash backstop), minutes. Default 60.</summary>
    public int EntryTtlMinutes { get; set; } = 60;

    /// <summary>
    /// Resolved enabled state. The <c>CRAFT_REALTIME_ENABLED</c> environment variable (true/1 or false/0)
    /// wins when set; otherwise <see cref="Enabled"/> applies.
    /// </summary>
    public bool IsEnabled
    {
        get
        {
            var v = Environment.GetEnvironmentVariable("CRAFT_REALTIME_ENABLED");
            if (string.IsNullOrWhiteSpace(v)) return Enabled;
            return v.Equals("true", StringComparison.OrdinalIgnoreCase) || v == "1";
        }
    }
}

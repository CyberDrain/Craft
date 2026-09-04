namespace Craft.Configuration;

/// <summary>
/// Startup phone-home telemetry (<c>App:Telemetry:*</c>). One usage report per process start, storm
/// guarded so a crash loop cannot flood the ingest. See <c>StartupTelemetryService</c>.
///
/// <para>
/// Off by default: nothing is sent until an operator both enables it and supplies an
/// <see cref="AppId"/> and an <see cref="Endpoint"/>. The <c>CRAFT_TELEMETRY_OPTOUT=1</c> environment
/// variable forces it off regardless of configuration.
/// </para>
/// </summary>
public class TelemetrySettings
{
    /// <summary>Master switch. Off by default (privacy posture is an operator decision).</summary>
    public bool Enabled { get; set; }

    /// <summary>Ingest URL, e.g. <c>https://reporting.example.com/API/TelemetryIngest</c>. No send without it.</summary>
    public string? Endpoint { get; set; }

    /// <summary>Application id for this image (<c>cipp</c>, <c>geoipdb</c>, …). No send without it.</summary>
    public string? AppId { get; set; }

    /// <summary>Storm-guard floor: at most one report per this many hours per instance. Floored at 1.</summary>
    public int MinIntervalHours { get; set; } = 6;

    /// <summary>Outbound POST timeout in seconds. No retry — the next boot is the retry.</summary>
    public int TimeoutSeconds { get; set; } = 10;

    /// <summary>Lower bound of the jittered startup delay, in seconds.</summary>
    public int MinStartupDelaySeconds { get; set; } = 60;

    /// <summary>Upper bound of the jittered startup delay, in seconds.</summary>
    public int MaxStartupDelaySeconds { get; set; } = 300;

    /// <summary>Table holding the per-instance storm-guard state (<c>instanceId</c>, <c>lastSentUtc</c>).</summary>
    public string GuardTable { get; set; } = "CraftTelemetryGuard";

    /// <summary>Optional shared token sent as <c>X-Telemetry-Token</c> to a token-gated ingest.</summary>
    public string? Token { get; set; }
}

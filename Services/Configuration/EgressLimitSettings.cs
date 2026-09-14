using System.Globalization;

namespace Craft.Configuration;

/// <summary>
/// Per-instance daily API egress cap. Sums the outbound response bytes served to app-only API clients
/// (client-credentials callers, idp=aad) across the WHOLE instance against a single daily budget and —
/// once a budget is set — sheds further API requests with HTTP 429 + <c>Retry-After</c> until the next
/// UTC midnight. Interactive (UI) callers are never counted and never capped.
/// <para>
/// This is a bandwidth cap, distinct from <see cref="RateLimitSettings.PermitPerWindow"/> (a request
/// RATE cap) and <see cref="RateLimitSettings.ApiConcurrencyLimit"/> (a simultaneous-in-flight cap). It
/// exists because a caller can pull tens of GB/day of egress while staying comfortably under both of
/// those — a single slow, large request every few seconds.
/// </para>
/// <para>
/// <b>Two-step activation.</b> Accounting (and the middleware) turns on when the env var named by
/// <see cref="HostedEnv"/> — default <c>CIPP_HOSTED</c>, the same flag the SKU profiles key off — is
/// present, or when <c>CRAFT_API_EGRESS_LIMIT_ENABLED</c> forces it. <b>Enforcement</b> is a separate
/// step: 429s only begin once <see cref="BytesPerDay"/> (or its env override) is above zero, so a
/// deployment can run accounting-only first to observe real numbers before it starts rejecting.
/// </para>
/// <para>
/// State is one counter plus the UTC date it belongs to, kept in memory and flushed to a small local
/// file every <see cref="FlushSeconds"/> (and on shutdown) so a restart does not reset the day's total.
/// Nothing is read back from Azure — the figure is only what this instance has served since the file
/// was last valid for today.
/// </para>
/// </summary>
public class EgressLimitSettings
{
    /// <summary>
    /// Name of the env var whose presence (any non-empty value) marks the hosted environment and turns
    /// egress accounting on. Defaults to <c>CIPP_HOSTED</c> — the same flag
    /// <see cref="WorkerSettings.SkuProfilesAltEnv"/> uses — so a single env var lights up both. Blank
    /// means never auto-enable (only <c>CRAFT_API_EGRESS_LIMIT_ENABLED=true</c> can then turn it on).
    /// </summary>
    public string? HostedEnv { get; set; } = "CIPP_HOSTED";

    /// <summary>
    /// Daily egress budget in bytes for the whole instance. 0 (default) = accounting only, no 429s.
    /// Env override: <c>CRAFT_API_EGRESS_LIMIT_BYTES</c> (e.g. 1073741824 for 1 GB/day).
    /// </summary>
    public long BytesPerDay { get; set; }

    /// <summary>
    /// Seconds between background flushes of the counter to disk; a flush also runs on shutdown, and a
    /// flush is skipped entirely when nothing changed since the last one, so an idle instance writes
    /// nothing. The request path never touches disk. Default 60. Floored at 1. Env override:
    /// <c>CRAFT_API_EGRESS_FLUSH_SECONDS</c>.
    /// </summary>
    public int FlushSeconds { get; set; } = 60;

    /// <summary>
    /// Table into which per-flush accounting is mirrored as time-bucketed rows (in addition to the local
    /// file), so the product can show usage history. Written by Craft's own table store — the same
    /// storage account the hosted app reads with Get-CIPPTable — so CIPP can query it directly. Default
    /// <c>CraftEgressAccounting</c>. Env override: <c>CRAFT_API_EGRESS_TABLE</c>. Blank disables the table
    /// mirror (file-only).
    /// </summary>
    public string TableName { get; set; } = "CraftEgressAccounting";

    /// <summary>
    /// Width in minutes of each accounting bucket written to the table. Default 15 (→ 96 buckets/day),
    /// which is the granularity the product surfaces. The local file keeps only running daily totals; the
    /// bucketed time-series lives in the table. Floored at 1. Env override: <c>CRAFT_API_EGRESS_BUCKET_MINUTES</c>.
    /// </summary>
    public int BucketMinutes { get; set; } = 15;

    /// <summary>
    /// Days of bucketed rows to retain in the table before a periodic purge deletes them. Default 7.
    /// Floored at 1. Env override: <c>CRAFT_API_EGRESS_RETENTION_DAYS</c>. Does not affect the local file,
    /// which only ever holds the current UTC day.
    /// </summary>
    public int RetentionDays { get; set; } = 7;

    internal const string EnabledEnv = "CRAFT_API_EGRESS_LIMIT_ENABLED";
    internal const string BytesEnv = "CRAFT_API_EGRESS_LIMIT_BYTES";
    internal const string FlushEnv = "CRAFT_API_EGRESS_FLUSH_SECONDS";
    internal const string TableEnv = "CRAFT_API_EGRESS_TABLE";
    internal const string BucketEnv = "CRAFT_API_EGRESS_BUCKET_MINUTES";
    internal const string RetentionEnv = "CRAFT_API_EGRESS_RETENTION_DAYS";

    /// <summary>
    /// Whether egress accounting (and therefore the middleware) should be active, resolved through
    /// <paramref name="env"/>. <c>CRAFT_API_EGRESS_LIMIT_ENABLED</c> wins when set — <c>true</c> forces
    /// it on even off-host (for testing), <c>false</c> is a kill switch even in the hosted env —
    /// otherwise it is on exactly when the <see cref="HostedEnv"/> var is present.
    /// </summary>
    public bool ResolveEnabled(Func<string, string?> env)
    {
        ArgumentNullException.ThrowIfNull(env);

        var forced = ParseFlag(env(EnabledEnv));
        if (forced is not null) return forced.Value;

        return !string.IsNullOrWhiteSpace(HostedEnv) && !string.IsNullOrWhiteSpace(env(HostedEnv));
    }

    /// <summary>Convenience overload resolving against the real process environment.</summary>
    public bool ResolvedEnabled => ResolveEnabled(Environment.GetEnvironmentVariable);

    /// <summary>
    /// Resolved daily budget in bytes, honouring the <c>CRAFT_API_EGRESS_LIMIT_BYTES</c> env override
    /// (which wins when it parses to a non-negative long). 0 = accounting only / no enforcement.
    /// </summary>
    public long ResolvedBytesPerDay =>
        long.TryParse(Environment.GetEnvironmentVariable(BytesEnv), NumberStyles.Integer,
            CultureInfo.InvariantCulture, out var fromEnv) && fromEnv >= 0
            ? fromEnv
            : Math.Max(0, BytesPerDay);

    /// <summary>
    /// Resolved flush interval in seconds, honouring the <c>CRAFT_API_EGRESS_FLUSH_SECONDS</c> env
    /// override (which wins when it parses to a positive int). Floored at 1.
    /// </summary>
    public int ResolvedFlushSeconds =>
        int.TryParse(Environment.GetEnvironmentVariable(FlushEnv), NumberStyles.Integer,
            CultureInfo.InvariantCulture, out var fromEnv) && fromEnv > 0
            ? fromEnv
            : Math.Max(1, FlushSeconds);

    /// <summary>Resolved table name, honouring <c>CRAFT_API_EGRESS_TABLE</c>. Blank/whitespace = the table
    /// mirror is off (file-only accounting).</summary>
    public string ResolvedTableName
    {
        get
        {
            var fromEnv = Environment.GetEnvironmentVariable(TableEnv);
            var name = string.IsNullOrWhiteSpace(fromEnv) ? TableName : fromEnv;
            return (name ?? string.Empty).Trim();
        }
    }

    /// <summary>Resolved bucket width in minutes, honouring <c>CRAFT_API_EGRESS_BUCKET_MINUTES</c>. Floored at 1.</summary>
    public int ResolvedBucketMinutes =>
        int.TryParse(Environment.GetEnvironmentVariable(BucketEnv), NumberStyles.Integer,
            CultureInfo.InvariantCulture, out var fromEnv) && fromEnv > 0
            ? fromEnv
            : Math.Max(1, BucketMinutes);

    /// <summary>Resolved retention in days, honouring <c>CRAFT_API_EGRESS_RETENTION_DAYS</c>. Floored at 1.</summary>
    public int ResolvedRetentionDays =>
        int.TryParse(Environment.GetEnvironmentVariable(RetentionEnv), NumberStyles.Integer,
            CultureInfo.InvariantCulture, out var fromEnv) && fromEnv > 0
            ? fromEnv
            : Math.Max(1, RetentionDays);

    // Tri-state flag parse (mirrors Craft.Hosting.EnvFlag, inlined to keep Configuration free of a
    // dependency on Hosting): null when unset/blank, true for "true"/"1" (any casing), false otherwise.
    private static bool? ParseFlag(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        return value.Equals("true", StringComparison.OrdinalIgnoreCase) || value == "1";
    }
}

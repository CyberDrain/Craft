namespace Craft.Configuration;

/// <summary>
/// Response cache settings.
/// </summary>
public class CacheSettings
{
    /// <summary>
    /// Whether the in-memory/disk-backed API response cache is active. Null (default) = auto: on only when
    /// the node serves BOTH a browser UI and its API (combined / frontend+http roles), off for api-only,
    /// worker-only and static-only nodes. Set true/false to force it on or off in any role.
    /// Overridable via the CRAFT_RESPONSE_CACHE environment variable (true/false), which takes precedence.
    /// When disabled, no _cache/ directory is created or scanned and all get/set operations are no-ops.
    /// </summary>
    public bool? Enabled { get; set; }

    /// <summary>Maximum number of cached responses in memory.</summary>
    public int MaxEntries { get; set; } = 1000;

    /// <summary>
    /// Budget (bytes) for keeping cached response bodies in memory (an LRU tier over the disk cache) so a
    /// cache HIT returns from RAM instead of re-reading + re-decoding the file every time. Default 64 MiB.
    /// 0 disables the in-memory tier (disk-only — every hit reads the file). The index is always in memory;
    /// this only governs the hot bodies. See docs/cache-analysis.md.
    /// </summary>
    public long MaxMemoryBytes { get; set; } = 64L * 1024 * 1024;

    /// <summary>Default TTL in seconds for cached responses.</summary>
    public int DefaultTtlSeconds { get; set; } = 600;

    /// <summary>
    /// Query parameter name that triggers cache invalidation when set to "true".
    /// </summary>
    public string InvalidateParam { get; set; } = "InvalidateCache";

    /// <summary>
    /// Query parameter name used for scoped cache invalidation (e.g. per-tenant).
    /// When a write operation includes this parameter, only cache entries
    /// containing this parameter value are invalidated.
    /// </summary>
    public string ScopeParam { get; set; } = "";

    /// <summary>
    /// Per-endpoint TTL overrides. Key = endpoint name, Value = TTL in seconds.
    /// Example: { "ListTenants": 300, "ListUsers": 120 }
    /// </summary>
    public Dictionary<string, int> EndpointTtl { get; set; } = new();
}

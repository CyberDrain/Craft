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
    /// this only governs the hot bodies. See perf-harness/cache-analysis.md.
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
    /// Endpoints that are never cached, whatever the query string says. Case-insensitive; <c>*</c>
    /// matches any run of characters, so "ListLog*" covers a family and "ListTenants" one endpoint.
    /// <para>
    /// Use this for the reads the query-parameter rules cannot express: endpoints that do take the
    /// required parameter but are still a bad fit for a shared cache (log tails, scheduler views,
    /// anything whose result depends on who is asking).
    /// </para>
    /// </summary>
    public List<string> ExcludedEndpoints { get; set; } = new();

    /// <summary>
    /// Query parameter a request must carry before its response is eligible for the cache.
    /// Empty (default) = no requirement, every cacheable read is cached.
    /// <para>
    /// Set this to the parameter that makes a response distinct — "tenantFilter" for CIPP — so that
    /// endpoints which do not take it (ListTenants, log tails, the scheduler view) are never cached.
    /// Those are typically fast, per-user and query-shaped, so caching them buys little and risks
    /// serving one caller's view to another.
    /// </para>
    /// </summary>
    public string RequiredParam { get; set; } = "";

    /// <summary>
    /// Values of <see cref="RequiredParam"/> that opt a request out of the cache. Case-insensitive.
    /// Example: [ "AllTenants" ] — an all-tenant query is a different (and far broader) result set than
    /// any single-tenant one, so it should not share the cache with them.
    /// Only meaningful when <see cref="RequiredParam"/> is set.
    /// </summary>
    public List<string> ExcludedParamValues { get; set; } = new();

    /// <summary>
    /// Request header a caller can send to bypass the cache for a single call (no read, no write).
    /// Default "x-craft-no-cache"; empty disables the header check entirely.
    /// Any non-empty value bypasses except an explicit "false"/"0"/"no".
    /// </summary>
    public string NoCacheHeader { get; set; } = "x-craft-no-cache";

    /// <summary>
    /// Per-endpoint TTL overrides. Key = endpoint name, Value = TTL in seconds.
    /// Example: { "ListTenants": 300, "ListUsers": 120 }
    /// </summary>
    public Dictionary<string, int> EndpointTtl { get; set; } = new();

    /// <summary>
    /// Resolves whether the response cache is active. <c>CRAFT_RESPONSE_CACHE</c> wins when set;
    /// otherwise <see cref="Enabled"/> if configured; otherwise <paramref name="autoDefault"/>
    /// (typically true only when the node serves both frontend and HTTP).
    /// </summary>
    public bool ResolveEnabled(bool autoDefault) =>
        ResolveEnabled(autoDefault, Environment.GetEnvironmentVariable);

    /// <summary>
    /// Same as <see cref="ResolveEnabled(bool)"/> but with an injectable environment lookup (for tests).
    /// </summary>
    public bool ResolveEnabled(bool autoDefault, Func<string, string?> env)
    {
        ArgumentNullException.ThrowIfNull(env);
        var v = env("CRAFT_RESPONSE_CACHE");
        if (!string.IsNullOrWhiteSpace(v))
            return v.Equals("true", StringComparison.OrdinalIgnoreCase) || v == "1";
        return Enabled ?? autoDefault;
    }
}

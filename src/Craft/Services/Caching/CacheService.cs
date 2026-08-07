using System.Collections.Concurrent;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Craft.Configuration;
using Craft.Services;

namespace Craft.Caching;

public class CacheService : IDisposable
{
    private readonly ILogger<CacheService> _logger;
    private readonly CraftSettings _settings;
    private readonly bool _enabled;
    private readonly string _cachePath;
    private readonly ConcurrentDictionary<string, CacheEntry> _index = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, byte> _refreshingKeys = new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim _bgRefreshSemaphore = new(3, 3);
    private readonly Timer _evictionTimer;

    // Generation counter: incremented on every invalidation.
    // BG refreshes capture this before starting and only write if it hasn't changed.
    private long _generation;

    // Configurable limits
    private readonly int _maxEntries;
    private readonly TimeSpan _defaultTtl;

    // In-memory body tier: keep hot body strings in RAM so a hit skips the disk read. Budget in bytes;
    // 0 = disabled (disk-only). _memBytes is the running total; _memLock serializes eviction.
    private readonly long _maxMemoryBytes;
    private long _memBytes;
    private readonly object _memLock = new();

    // Per-endpoint TTL overrides (e.g. "ListTenants" -> 5 minutes)
    private readonly Dictionary<string, TimeSpan> _endpointTtls = new(StringComparer.OrdinalIgnoreCase);

    // Control params excluded from cache key generation
    private readonly HashSet<string> _excludedParams;

    // Per-request admission policy (required param / excluded values / bypass header)
    private readonly ResponseCachePolicy _policy;

    /// <summary>Name of the query parameter used for scoped cache invalidation.</summary>
    public string ScopeParam => _settings.Cache.ScopeParam;

    /// <summary>Name of the query parameter that triggers full cache invalidation.</summary>
    public string InvalidateParam => _settings.Cache.InvalidateParam;

    /// <summary>
    /// Which requests are allowed into the cache at all. Callers must consult this before both the
    /// read and the write, so an excluded request neither serves nor populates an entry.
    /// </summary>
    public ResponseCachePolicy Policy => _policy;

    /// <summary>Whether the cache is active. When false, all get/set/invalidate operations are no-ops.</summary>
    public bool Enabled => _enabled;

    public CacheService(ILogger<CacheService> logger, CraftSettings settings, bool enabled = true)
    {
        _logger = logger;
        _settings = settings;
        _enabled = enabled;
        _cachePath = Path.Combine(AppContext.BaseDirectory, "_cache");

        _maxEntries = settings.Cache.MaxEntries;
        _defaultTtl = TimeSpan.FromSeconds(settings.Cache.DefaultTtlSeconds);
        _maxMemoryBytes = settings.Cache.MaxMemoryBytes;

        // Build excluded params set from config
        _excludedParams = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            settings.Cache.InvalidateParam
        };

        // Load per-endpoint TTL overrides from config
        foreach (var (endpoint, seconds) in settings.Cache.EndpointTtl)
        {
            _endpointTtls[endpoint] = TimeSpan.FromSeconds(seconds);
        }

        _policy = ResponseCachePolicy.FromSettings(settings.Cache);

        if (!_enabled)
        {
            // Disabled: no disk footprint, no orphan scan, no eviction timer. All operations short-circuit.
            _logger.LogInformation("[Cache] Response cache disabled — serving all responses fresh (no _cache/).");
            _evictionTimer = new Timer(_ => { }, null, Timeout.Infinite, Timeout.Infinite);
            return;
        }
        _logger.LogWarning("[Cache] enabled; in-memory body tier budget = {Bytes} bytes", _maxMemoryBytes);

        if (_policy.ExcludedEndpointCount > 0)
            _logger.LogInformation("[Cache] admission policy: {Count} endpoint(s) excluded outright",
                _policy.ExcludedEndpointCount);

        if (_policy.HasRequiredParam)
            _logger.LogInformation(
                "[Cache] admission policy: requires ?{Param}= and skips {Count} excluded value(s)",
                _policy.RequiredParam, _policy.ExcludedValueCount);
        else if (_policy.ExcludedValueCount > 0)
            // Excluded values are values *of the required param*, so on their own they gate nothing.
            _logger.LogWarning(
                "[Cache] App:Cache:ExcludedParamValues is set but App:Cache:RequiredParam is empty — " +
                "the excluded values are being ignored. Set RequiredParam to make them take effect.");

        Directory.CreateDirectory(_cachePath);

        // Clean up orphan files from previous runs
        CleanupOrphanFiles();

        // Eviction timer runs every 60 seconds to purge expired entries
        _evictionTimer = new Timer(_ => EvictExpired(), null, TimeSpan.FromSeconds(60), TimeSpan.FromSeconds(60));
    }

    /// <summary>
    /// Build a cache key from endpoint, query params (excluding control params), and user roles.
    /// </summary>
    public string BuildCacheKey(string endpoint, IQueryCollection query, string? userRoleHash)
    {
        var sb = new StringBuilder(endpoint);

        foreach (var kv in query.OrderBy(q => q.Key, StringComparer.OrdinalIgnoreCase))
        {
            if (_excludedParams.Contains(kv.Key))
                continue;
            sb.Append('|').Append(kv.Key).Append('=').Append(kv.Value);
        }

        if (!string.IsNullOrEmpty(userRoleHash))
        {
            sb.Append("|_roles=").Append(userRoleHash);
        }

        return sb.ToString();
    }

    /// <summary>
    /// Extract a stable hash of the user's roles from the x-ms-client-principal header.
    /// Returns null if the header is missing or unparseable.
    /// </summary>
    public static string? GetUserRoleHash(HttpContext context)
    {
        if (!context.Request.Headers.TryGetValue("x-ms-client-principal", out var headerValue))
            return null;

        try
        {
            var json = Encoding.UTF8.GetString(Convert.FromBase64String(headerValue.ToString()));
            using var doc = JsonDocument.Parse(json);

            if (doc.RootElement.TryGetProperty("userRoles", out var rolesElement))
            {
                var roles = new List<string>();
                foreach (var role in rolesElement.EnumerateArray())
                {
                    var r = role.GetString();
                    if (r != null && r != "anonymous" && r != "authenticated")
                        roles.Add(r);
                }
                roles.Sort(StringComparer.OrdinalIgnoreCase);
                var joined = string.Join(",", roles);
                // Short hash — just for cache key differentiation, not security
                var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(joined)))[..16];
                return hash;
            }
        }
        catch { /* swallow parse errors — treat as no roles */ }

        return null;
    }

    /// <summary>
    /// Get the TTL for a given endpoint.
    /// </summary>
    public TimeSpan GetTtl(string endpoint)
    {
        return _endpointTtls.TryGetValue(endpoint, out var ttl) ? ttl : _defaultTtl;
    }

    /// <summary>
    /// Try to get a cached response. Returns null if not cached, expired, or unreadable.
    /// </summary>
    public async Task<CachedResponse?> Get(string cacheKey, string endpoint)
    {
        if (!_enabled)
            return null;
        if (!_index.TryGetValue(cacheKey, out var entry))
            return null;

        var ttl = GetTtl(endpoint);
        var age = DateTime.UtcNow - entry.CachedAt;

        // Hard expired — remove it
        if (age > ttl * 2)
        {
            Remove(cacheKey);
            return null;
        }

        // In-memory tier: if the body is resident, return it without touching disk (the hot path).
        var memBody = entry.Body;
        if (memBody != null)
        {
            if (CacheProfiler.Enabled) CacheProfiler.RecordMemHit();
            return new CachedResponse
            {
                Result = ToResult(entry, memBody),
                CachedAt = entry.CachedAt,
                IsStale = age > ttl,
                Age = age,
                Ttl = ttl
            };
        }

        try
        {
            // Not resident — read from disk. No File.Exists pre-check: the read itself surfaces a missing
            // file via the catch below, so we save a redundant stat syscall on every disk-tier hit.
            var diskStart = CacheProfiler.Enabled ? Stopwatch.GetTimestamp() : 0;
            var body = await File.ReadAllTextAsync(entry.FilePath);
            if (CacheProfiler.Enabled) CacheProfiler.RecordGetDisk(Stopwatch.GetTimestamp() - diskStart);

            // Promote into the memory tier so subsequent hits skip the disk.
            StoreBody(entry, body);

            return new CachedResponse
            {
                Result = ToResult(entry, body),
                CachedAt = entry.CachedAt,
                IsStale = age > ttl,
                Age = age,
                Ttl = ttl
            };
        }
        catch
        {
            Remove(cacheKey);
            return null;
        }
    }

    /// <summary>
    /// Rebuilds a response from an index entry and its body. The headers dictionary is shared, not
    /// copied — nothing downstream mutates it, and copying it on every cache hit would defeat the
    /// point of the in-memory tier.
    /// </summary>
    private static ScriptResult ToResult(CacheEntry entry, string body) => new()
    {
        StatusCode = entry.StatusCode,
        Body = body,
        Headers = entry.Headers,
        ContentType = entry.ContentType
    };

    /// <summary>Charge a body to the in-memory tier (idempotent per entry), evicting LRU bodies if over budget.</summary>
    private void StoreBody(CacheEntry entry, string body)
    {
        if (_maxMemoryBytes <= 0) return;
        var bytes = body.Length * 2;              // ~UTF-16 in-memory size
        var old = entry.BodyBytes;
        entry.Body = body;
        entry.BodyBytes = bytes;
        Interlocked.Add(ref _memBytes, bytes - old);
        if (Interlocked.Read(ref _memBytes) > _maxMemoryBytes) EvictMemLru();
    }

    /// <summary>Drop an entry's in-memory body and reclaim its budget (index entry + disk file are untouched).</summary>
    private void ForgetBody(CacheEntry? entry)
    {
        if (entry == null) return;
        var b = entry.BodyBytes;
        if (entry.Body != null)
        {
            entry.Body = null;
            entry.BodyBytes = 0;
            Interlocked.Add(ref _memBytes, -b);
        }
    }

    /// <summary>Evict least-recently-used resident bodies until the memory budget is satisfied.</summary>
    private void EvictMemLru()
    {
        lock (_memLock)
        {
            if (Interlocked.Read(ref _memBytes) <= _maxMemoryBytes) return;
            var victims = _index.Values.Where(e => e.Body != null).OrderBy(e => e.LastAccessedAt).ToList();
            foreach (var v in victims)
            {
                if (Interlocked.Read(ref _memBytes) <= _maxMemoryBytes) break;
                ForgetBody(v);
            }
        }
    }

    /// <summary>
    /// Store a response on disk and update the in-memory index.
    /// Evicts LRU entries if over capacity.
    /// </summary>
    public async Task Set(string cacheKey, ScriptResult result)
    {
        if (!_enabled)
            return;
        var setStart = CacheProfiler.Enabled ? Stopwatch.GetTimestamp() : 0;
        try
        {
            // Evict if at capacity before adding
            if (_index.Count >= _maxEntries && !_index.ContainsKey(cacheKey))
            {
                EvictLru(_index.Count - _maxEntries + 1);
            }

            var fileName = Convert.ToHexString(SHA256.HashData(
                Encoding.UTF8.GetBytes(cacheKey)))[..32] + ".json";
            var filePath = Path.Combine(_cachePath, fileName);

            await File.WriteAllTextAsync(filePath, result.Body);

            var entry = new CacheEntry
            {
                StatusCode = result.StatusCode,
                CachedAt = DateTime.UtcNow,
                LastAccessedAt = DateTime.UtcNow,
                FilePath = filePath,
                Headers = result.Headers,
                ContentType = result.ContentType
            };
            // Populate the in-memory body BEFORE publishing the entry, so any concurrent Get that sees the
            // new entry always sees its body (the stale-while-revalidate refresh republishes constantly —
            // publishing a body-less entry first would make those Gets fall through to a disk read).
            StoreBody(entry, result.Body);
            _index[cacheKey] = entry;
            if (CacheProfiler.Enabled) CacheProfiler.RecordSet(Stopwatch.GetTimestamp() - setStart);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to write cache for key: {Key}", cacheKey);
        }
    }

    /// <summary>
    /// Remove a specific cache entry.
    /// </summary>
    public void Remove(string cacheKey)
    {
        if (_index.TryRemove(cacheKey, out var entry))
        {
            ForgetBody(entry);
            TryDeleteFile(entry.FilePath);
        }
    }

    /// <summary>
    /// Invalidate by endpoint prefix. E.g. "ListUsers" removes all ListUsers|... keys.
    /// </summary>
    public int InvalidateByEndpoint(string endpointPrefix)
    {
        Interlocked.Increment(ref _generation);
        var count = 0;
        foreach (var key in _index.Keys)
        {
            if (key.StartsWith(endpointPrefix, StringComparison.OrdinalIgnoreCase)
                && (key.Length == endpointPrefix.Length || key[endpointPrefix.Length] == '|'))
            {
                if (_index.TryRemove(key, out var entry))
                {
                    ForgetBody(entry);
                    TryDeleteFile(entry.FilePath);
                    count++;
                }
            }
        }
        if (count > 0)
            _logger.LogInformation("Cache invalidated {Count} entries for endpoint prefix: {Prefix}", count, endpointPrefix);
        return count;
    }

    /// <summary>
    /// Invalidate all entries containing a specific scope parameter value.
    /// The scope parameter name is configured via App:Cache:ScopeParam.
    /// </summary>
    public int InvalidateByScope(string scopeValue)
    {
        if (string.IsNullOrEmpty(_settings.Cache.ScopeParam)) return 0;

        Interlocked.Increment(ref _generation);
        var needle = $"{_settings.Cache.ScopeParam}={scopeValue}";
        var count = 0;
        foreach (var key in _index.Keys)
        {
            if (key.Contains(needle, StringComparison.OrdinalIgnoreCase))
            {
                if (_index.TryRemove(key, out var entry))
                {
                    ForgetBody(entry);
                    TryDeleteFile(entry.FilePath);
                    count++;
                }
            }
        }
        if (count > 0)
            _logger.LogInformation("Cache invalidated {Count} entries for scope {Param}={Value}", count, _settings.Cache.ScopeParam, scopeValue);
        return count;
    }

    /// <summary>
    /// Nuclear option — clear everything.
    /// </summary>
    public void InvalidateAll()
    {
        Interlocked.Increment(ref _generation);
        var count = _index.Count;
        foreach (var entry in _index.Values)
        {
            TryDeleteFile(entry.FilePath);
        }
        _index.Clear();
        _refreshingKeys.Clear();
        Interlocked.Exchange(ref _memBytes, 0);   // all bodies dropped with the index
        _logger.LogInformation("All response caches invalidated ({Count} entries)", count);
    }

    /// <summary>
    /// Touch a cache entry to update its last-accessed time (for LRU).
    /// </summary>
    public void Touch(string cacheKey)
    {
        if (_index.TryGetValue(cacheKey, out var entry))
        {
            entry.LastAccessedAt = DateTime.UtcNow;
        }
    }

    // --- Background refresh coordination ---

    /// <summary>
    /// Current cache generation. Incremented on every invalidation.
    /// BG refreshes capture this before starting and check before writing.
    /// </summary>
    public long Generation => Interlocked.Read(ref _generation);

    public bool TryStartRefresh(string cacheKey) => _refreshingKeys.TryAdd(cacheKey, 0);

    public void FinishRefresh(string cacheKey) => _refreshingKeys.TryRemove(cacheKey, out _);

    /// <summary>
    /// Store a response only if the cache generation hasn't changed since the refresh started.
    /// Returns true if the write was accepted, false if it was discarded due to invalidation.
    /// </summary>
    public bool SetIfSameGeneration(string cacheKey, ScriptResult result, long capturedGeneration)
    {
        if (Interlocked.Read(ref _generation) != capturedGeneration)
        {
            _logger.LogInformation("[BG] Discarding stale refresh for {Key} (generation changed: {Old} -> {Current})",
                cacheKey, capturedGeneration, Interlocked.Read(ref _generation));
            return false;
        }
        // Fire and forget the async set — the caller doesn't need to await this
        _ = Set(cacheKey, result);
        return true;
    }

    public async Task WaitForBgRefreshSlot(string endpoint)
    {
        _logger.LogInformation("[BG] {Endpoint} waiting for bg refresh slot", endpoint);
        await _bgRefreshSemaphore.WaitAsync();
        _logger.LogInformation("[BG] {Endpoint} acquired bg refresh slot", endpoint);
    }

    public void ReleaseBgRefreshSlot(string endpoint)
    {
        _bgRefreshSemaphore.Release();
        _logger.LogInformation("[BG] {Endpoint} released bg refresh slot", endpoint);
    }

    // --- Diagnostics ---

    public int Count => _index.Count;

    public CacheStats GetStats()
    {
        var now = DateTime.UtcNow;
        var entries = _index.ToArray();
        return new CacheStats
        {
            TotalEntries = entries.Length,
            MaxEntries = _maxEntries,
            DefaultTtlSeconds = (int)_defaultTtl.TotalSeconds,
            OldestAge = entries.Length > 0
                ? (now - entries.Min(e => e.Value.CachedAt)).TotalSeconds
                : 0,
            RefreshingKeys = _refreshingKeys.Count
        };
    }

    // --- Internal ---

    private void EvictExpired()
    {
        var now = DateTime.UtcNow;
        var evicted = 0;

        foreach (var kvp in _index)
        {
            // Extract endpoint from key (everything before the first |)
            var endpoint = kvp.Key;
            var pipeIndex = endpoint.IndexOf('|');
            if (pipeIndex > 0) endpoint = endpoint[..pipeIndex];

            var ttl = GetTtl(endpoint);
            // Hard-expire at 2x TTL (stale entries can still be served up to 1x TTL)
            if (now - kvp.Value.CachedAt > ttl * 2)
            {
                if (_index.TryRemove(kvp.Key, out var entry))
                {
                    ForgetBody(entry);
                    TryDeleteFile(entry.FilePath);
                    evicted++;
                }
            }
        }

        if (evicted > 0)
            _logger.LogInformation("Cache eviction timer removed {Count} expired entries", evicted);
    }

    private void EvictLru(int count)
    {
        var toEvict = _index
            .OrderBy(kvp => kvp.Value.LastAccessedAt)
            .Take(count)
            .Select(kvp => kvp.Key)
            .ToList();

        foreach (var key in toEvict)
        {
            if (_index.TryRemove(key, out var entry))
            {
                ForgetBody(entry);
                TryDeleteFile(entry.FilePath);
            }
        }

        if (toEvict.Count > 0)
            _logger.LogInformation("LRU eviction removed {Count} entries (index size: {Size}/{Max})",
                toEvict.Count, _index.Count, _maxEntries);
    }

    private void CleanupOrphanFiles()
    {
        try
        {
            if (!Directory.Exists(_cachePath)) return;
            var files = Directory.GetFiles(_cachePath, "*.json");
            if (files.Length > 0)
            {
                foreach (var f in files) TryDeleteFile(f);
                _logger.LogInformation("Cleaned up {Count} orphan cache files from previous run", files.Length);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to clean up orphan cache files");
        }
    }

    private static void TryDeleteFile(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { /* best effort */ }
    }

    public void Dispose()
    {
        _evictionTimer.Dispose();
        _bgRefreshSemaphore.Dispose();

        // Clean up cache directory
        if (Directory.Exists(_cachePath))
        {
            try { Directory.Delete(_cachePath, true); } catch { /* best effort */ }
        }
        GC.SuppressFinalize(this);
    }
}

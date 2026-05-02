using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Craft.Services;

public class CacheService : IDisposable
{
    private readonly ILogger<CacheService> _logger;
    private readonly CraftSettings _settings;
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

    // Per-endpoint TTL overrides (e.g. "ListTenants" -> 5 minutes)
    private readonly Dictionary<string, TimeSpan> _endpointTtls = new(StringComparer.OrdinalIgnoreCase);

    // Control params excluded from cache key generation
    private readonly HashSet<string> _excludedParams;

    /// <summary>Name of the query parameter used for scoped cache invalidation.</summary>
    public string ScopeParam => _settings.Cache.ScopeParam;

    /// <summary>Name of the query parameter that triggers full cache invalidation.</summary>
    public string InvalidateParam => _settings.Cache.InvalidateParam;

    public CacheService(ILogger<CacheService> logger, CraftSettings settings)
    {
        _logger = logger;
        _settings = settings;
        _cachePath = Path.Combine(AppContext.BaseDirectory, "_cache");
        Directory.CreateDirectory(_cachePath);

        _maxEntries = settings.Cache.MaxEntries;
        _defaultTtl = TimeSpan.FromSeconds(settings.Cache.DefaultTtlSeconds);

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

        try
        {
            if (!File.Exists(entry.FilePath))
            {
                _index.TryRemove(cacheKey, out _);
                return null;
            }

            var body = await File.ReadAllTextAsync(entry.FilePath);
            return new CachedResponse
            {
                Result = new ScriptResult { StatusCode = entry.StatusCode, Body = body },
                CachedAt = entry.CachedAt,
                IsStale = age > ttl,
                Age = age,
                Ttl = ttl
            };
        }
        catch
        {
            _index.TryRemove(cacheKey, out _);
            return null;
        }
    }

    /// <summary>
    /// Store a response on disk and update the in-memory index.
    /// Evicts LRU entries if over capacity.
    /// </summary>
    public async Task Set(string cacheKey, ScriptResult result)
    {
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

            _index[cacheKey] = new CacheEntry
            {
                StatusCode = result.StatusCode,
                CachedAt = DateTime.UtcNow,
                LastAccessedAt = DateTime.UtcNow,
                FilePath = filePath
            };
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

public class CacheStats
{
    public int TotalEntries { get; set; }
    public int MaxEntries { get; set; }
    public int DefaultTtlSeconds { get; set; }
    public double OldestAge { get; set; }
    public int RefreshingKeys { get; set; }
}

/// <summary>
/// Lightweight in-memory index entry — no JSON body, just metadata + file path.
/// </summary>
public class CacheEntry
{
    public int StatusCode { get; set; }
    public DateTime CachedAt { get; set; }
    public DateTime LastAccessedAt { get; set; }
    public required string FilePath { get; set; }
}

/// <summary>
/// Returned by cache Get — full result loaded from disk on demand.
/// </summary>
public class CachedResponse
{
    public required ScriptResult Result { get; set; }
    public DateTime CachedAt { get; set; }
    public bool IsStale { get; set; }
    public TimeSpan Age { get; set; }
    public TimeSpan Ttl { get; set; }
}

namespace Craft.Caching;

/// <summary>
/// In-memory index entry: metadata + file path, plus an optional in-memory copy of the body (the LRU
/// memory tier). When <see cref="Body"/> is non-null a cache Get returns it without touching disk.
/// </summary>
// Body is volatile — read without a lock on the cache hot path — and BodyBytes is updated alongside
// it under the memory-tier guard. volatile is not available on properties.
[System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1051:Do not declare visible instance fields",
    Justification = "Body is volatile for lock-free reads on the cache hot path; volatile is not available on properties.")]
public class CacheEntry
{
    public int StatusCode { get; set; }
    public DateTime CachedAt { get; set; }
    public DateTime LastAccessedAt { get; set; }
    public required string FilePath { get; set; }

    /// <summary>In-memory copy of the body (null = not resident; read from FilePath). Guarded by the mem tier.</summary>
    public volatile string? Body;
    /// <summary>Approx bytes charged to the memory budget for <see cref="Body"/> (chars × 2).</summary>
    public int BodyBytes;
}

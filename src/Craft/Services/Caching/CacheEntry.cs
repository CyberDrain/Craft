namespace Craft.Caching;

/// <summary>
/// In-memory index entry: metadata + file path, plus an optional in-memory copy of the body (the LRU
/// memory tier). When <see cref="Body"/> is non-null a cache Get returns it without touching disk.
/// </summary>
// Body is volatile — read without a lock on the cache hot path — and BodyBytes is updated alongside
// it under the memory-tier guard. volatile is not available on properties.
[System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1051:Do not declare visible instance fields",
    Justification = "Body is volatile for lock-free reads on the cache hot path; volatile is not available on properties.")]
internal sealed class CacheEntry
{
    public int StatusCode { get; set; }
    public DateTime CachedAt { get; set; }
    public DateTime LastAccessedAt { get; set; }
    public required string FilePath { get; set; }

    /// <summary>
    /// Handler headers to replay on a hit. Lives in the index rather than on disk, exactly like
    /// <see cref="StatusCode"/> — orphan cache files are deleted at startup, so the body file never
    /// outlives the metadata that describes it. A cached redirect is useless without this: the
    /// status replays, the <c>Location</c> does not, and the response dead-ends in the browser.
    /// </summary>
    public Dictionary<string, string>? Headers { get; set; }

    /// <summary>Content type to replay on a hit. Null = <c>application/json</c>.</summary>
    public string? ContentType { get; set; }

    /// <summary>In-memory copy of the body (null = not resident; read from FilePath). Guarded by the mem tier.</summary>
    public volatile string? Body;
    /// <summary>Approx bytes charged to the memory budget for <see cref="Body"/> (chars × 2).</summary>
    public int BodyBytes;
}

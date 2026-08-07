using Craft.Services;

namespace Craft.Caching;

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

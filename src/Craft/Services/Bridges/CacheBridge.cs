using System.Text.Json;
using Craft.Caching;

// NAMESPACE PINNED — do not change.
// Downstream PowerShell reaches these types by fully-qualified name, e.g.
//   [Craft.Services.RealtimeBridge]::Publish($userId, $jobId, 'start', $data)
// Renaming the namespace compiles fine and then fails at runtime in the hosted app
// ("Unable to find type"). Type forwarding cannot help — it only works across assemblies.
// The folder is free to move; the namespace is a published contract.
namespace Craft.Services;

/// <summary>
/// Static bridge so PowerShell can invalidate cache entries without DI.
/// PS usage: [Craft.Services.CacheBridge]::InvalidateByEndpoint("ListUsers")
///           [Craft.Services.CacheBridge]::InvalidateByScope("contoso.com")
///           [Craft.Services.CacheBridge]::InvalidateAll()
///           [Craft.Services.CacheBridge]::GetStats()
/// </summary>
/// <remarks>
/// Uninitialized policy: all APIs throw (cache is always registered when the host runs).
/// </remarks>
public static class CacheBridge
{
    private static CacheService? s_cache;

    public static void Initialize(CacheService cache) => s_cache = cache;

    /// <summary>Invalidate all cache entries whose key starts with the given endpoint prefix.</summary>
    public static int InvalidateByEndpoint(string endpointPrefix) =>
        (s_cache ?? throw new InvalidOperationException("CacheService not initialized"))
            .InvalidateByEndpoint(endpointPrefix);

    /// <summary>Invalidate all cache entries whose key contains the given scope value (e.g. tenant domain).</summary>
    public static int InvalidateByScope(string scopeValue) =>
        (s_cache ?? throw new InvalidOperationException("CacheService not initialized"))
            .InvalidateByScope(scopeValue);

    /// <summary>Invalidate all cache entries.</summary>
    public static void InvalidateAll() =>
        (s_cache ?? throw new InvalidOperationException("CacheService not initialized"))
            .InvalidateAll();

    /// <summary>Returns cache diagnostics as a JSON string.</summary>
    public static string GetStats()
    {
        var stats = (s_cache ?? throw new InvalidOperationException("CacheService not initialized"))
            .GetStats();
        return JsonSerializer.Serialize(stats);
    }
}

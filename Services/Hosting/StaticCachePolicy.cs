namespace Craft.Hosting;

/// <summary>Cache-control decision for one static asset.</summary>
/// <param name="CacheControl">Value for the <c>Cache-Control</c> header.</param>
/// <param name="IncludeETag">
/// Whether to emit an ETag. Skipped for immutable content-hashed bundles, where the URL already
/// identifies the exact bytes and a revalidation round-trip would be pure waste.
/// </param>
public readonly record struct StaticCacheDirective(string CacheControl, bool IncludeETag);

/// <summary>
/// Cache-control policy for files served out of <c>Frontend/</c>.
/// <para>
/// The stakes are asymmetric: caching a control file too aggressively can strand every browser on a
/// stale build with no way to recover, while under-caching a hashed bundle only costs bandwidth. The
/// rules below are ordered accordingly — the never-cache list is checked first.
/// </para>
/// </summary>
public static class StaticCachePolicy
{
    private const string NoCache = "no-cache, must-revalidate";
    private const string Immutable = "public, max-age=86400, immutable";
    private const string LongLivedRevalidate = "public, max-age=86400, must-revalidate";

    /// <summary>
    /// Files that must never be cached: the service worker controls what the browser fetches next, and
    /// the version probe is how the app discovers a new build exists. A stale copy of either can pin
    /// clients to an old release indefinitely.
    /// </summary>
    private static readonly string[] NeverCacheSuffixes = ["/sw.js", "/version.json", "/manifest.json"];

    /// <summary>Stable-named binaries — the filename does not change when the content does.</summary>
    private static readonly string[] BinaryAssetExtensions =
        [".png", ".jpg", ".jpeg", ".gif", ".ico", ".svg", ".webp", ".woff", ".woff2"];

    /// <summary>Resolves the directive for <paramref name="path"/>.</summary>
    public static StaticCacheDirective For(string path)
    {
        path ??= "";

        foreach (var suffix in NeverCacheSuffixes)
        {
            if (path.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
                return new StaticCacheDirective(NoCache, IncludeETag: true);
        }

        // Content-hashed bundles: the hash is in the filename, so the bytes can never change under a
        // given URL. No ETag needed — there is nothing to revalidate.
        if (path.StartsWith("/_next/static/", StringComparison.OrdinalIgnoreCase))
            return new StaticCacheDirective(Immutable, IncludeETag: false);

        foreach (var extension in BinaryAssetExtensions)
        {
            if (path.EndsWith(extension, StringComparison.OrdinalIgnoreCase))
                return new StaticCacheDirective(LongLivedRevalidate, IncludeETag: true);
        }

        // Non-hashed data JSON (permission lists, score tables) — store, but revalidate cheaply.
        if (path.EndsWith(".json", StringComparison.OrdinalIgnoreCase) &&
            !path.Contains("/api/", StringComparison.OrdinalIgnoreCase))
        {
            return new StaticCacheDirective(NoCache, IncludeETag: true);
        }

        // HTML and everything else: storable, always revalidated.
        return new StaticCacheDirective(NoCache, IncludeETag: true);
    }

    /// <summary>
    /// Cache-control for a precompressed variant served directly by the precompression middleware.
    /// </summary>
    public static string ForPrecompressed(string path) =>
        path.StartsWith("/_next/static/", StringComparison.OrdinalIgnoreCase) ? Immutable : NoCache;
}

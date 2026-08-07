using System.Diagnostics;

namespace Craft.Caching;

/// <summary>
/// Opt-in profiler for the inbound-request auth header transform + the disk-backed response cache, for perf
/// analysis of frontend+http / combined nodes. Enabled by CRAFT_CACHE_TIMING=true; otherwise every hook is a
/// cheap <see cref="Enabled"/> check. Windowed averages (µs/request) are logged every <see cref="ReportEvery"/>
/// cacheable requests at Warning level so they surface without Info spam.
///
/// Segments per cacheable (List* GET) request: auth (the x-ms-client-principal middleware) → roleHash
/// (GetUserRoleHash: base64 decode + JSON parse + SHA256) → keyBuild (BuildCacheKey) → get (whole
/// CacheService.Get), of which getDisk (File.Exists + File.ReadAllTextAsync of the body) is the disk-backed
/// part. set is the miss-path disk write. hits/misses count the outcome.
/// </summary>
internal static class CacheProfiler
{
    internal static readonly bool Enabled =
        string.Equals(Environment.GetEnvironmentVariable("CRAFT_CACHE_TIMING"), "true", StringComparison.OrdinalIgnoreCase);

    private const long ReportEvery = 2000;

    private static long _count, _auth, _roleHash, _keyBuild, _get, _getDisk, _set, _hits, _misses, _memHits;
    private static long _lc, _la, _lrh, _lkb, _lg, _lgd, _ls, _lh, _lm, _lmh;
    private static ILogger? _logger;

    internal static void SetLogger(ILogger logger) => _logger = logger;

    /// <summary>The auth header middleware's per-request cost (runs for every HTTP request).</summary>
    internal static void RecordAuth(long ticks) => Interlocked.Add(ref _auth, ticks);

    /// <summary>The disk-backed portion of a cache Get (File.Exists + ReadAllTextAsync), recorded inside Get.</summary>
    internal static void RecordGetDisk(long ticks) => Interlocked.Add(ref _getDisk, ticks);

    /// <summary>A cache Get served from the in-memory body tier (no disk).</summary>
    internal static void RecordMemHit() => Interlocked.Increment(ref _memHits);

    /// <summary>A cache Set (miss path): disk write of the body.</summary>
    internal static void RecordSet(long ticks) => Interlocked.Add(ref _set, ticks);

    /// <summary>One cacheable request: the handler-side segments + hit/miss. Advances the window counter.</summary>
    internal static void RecordRequest(long roleHashTicks, long keyBuildTicks, long getTicks, bool hit)
    {
        Interlocked.Add(ref _roleHash, roleHashTicks);
        Interlocked.Add(ref _keyBuild, keyBuildTicks);
        Interlocked.Add(ref _get, getTicks);
        if (hit) Interlocked.Increment(ref _hits); else Interlocked.Increment(ref _misses);
        var n = Interlocked.Increment(ref _count);
        if (n % ReportEvery == 0) Report(n);
    }

    private static void Report(long n)
    {
        long c = n, a = _auth, rh = _roleHash, kb = _keyBuild, g = _get, gd = _getDisk, s = _set, h = _hits, m = _misses, mh = _memHits;
        long dn = c - _lc;
        if (dn <= 0) return;
        double Avg(long cur, long last) => (cur - last) * 1_000_000.0 / Stopwatch.Frequency / dn;
        _logger?.LogWarning(
            "[CacheProfile] window={Dn} hits={H} (mem={Mh}) misses={M} us/req: auth={A:F1} roleHash={Rh:F1} keyBuild={Kb:F1} get={G:F1} (disk={Gd:F1}) set={S:F1}",
            dn, h - _lh, mh - _lmh, m - _lm, Avg(a, _la), Avg(rh, _lrh), Avg(kb, _lkb), Avg(g, _lg), Avg(gd, _lgd), Avg(s, _ls));
        _lc = c; _la = a; _lrh = rh; _lkb = kb; _lg = g; _lgd = gd; _ls = s; _lh = h; _lm = m; _lmh = mh;
    }
}

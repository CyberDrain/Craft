using System.Diagnostics;

namespace Craft.Hosting;

/// <summary>
/// Opt-in per-segment timing for the HTTP dispatch pipeline, for perf analysis. Enabled by
/// CRAFT_DISPATCH_TIMING=true; otherwise every hook is a cheap <see cref="Enabled"/> bool check and does
/// nothing. Segments are accumulated with Interlocked and a windowed average is logged every
/// <see cref="ReportEvery"/> requests so each line is an independent steady-state sample (warmup doesn't
/// skew later windows). Times are microseconds/request.
///
/// Segments: marshal (build $Request) → checkout (borrow a worker) → invoke (worker.InvokeAsync total),
/// of which pipeline (PS BeginInvoke/EndInvoke) and cleanup (Cleanup() in the invoke finally) are subsets →
/// extract (ExtractResponse + JSON). total is the runner's wall time for the whole ExecuteHttpScript.
/// </summary>
internal static class DispatchProfiler
{
    internal static readonly bool Enabled =
        string.Equals(Environment.GetEnvironmentVariable("CRAFT_DISPATCH_TIMING"), "true", StringComparison.OrdinalIgnoreCase);

    private const long ReportEvery = 2000;

    private static long _count, _marshal, _checkout, _invoke, _build, _run, _copy, _cleanup, _extract, _total;
    // Last-reported snapshots, for windowed (delta) averages.
    private static long _lc, _lm, _lch, _li, _lb, _lr, _lcp, _lcl, _le, _lt;
    private static ILogger? _logger;

    internal static void SetLogger(ILogger logger) => _logger = logger;

    /// <summary>Record the runner-side segments and advance the request counter. Call once per request.</summary>
    internal static void Record(long marshalTicks, long checkoutTicks, long invokeTicks, long extractTicks, long totalTicks)
    {
        Interlocked.Add(ref _marshal, marshalTicks);
        Interlocked.Add(ref _checkout, checkoutTicks);
        Interlocked.Add(ref _invoke, invokeTicks);
        Interlocked.Add(ref _extract, extractTicks);
        Interlocked.Add(ref _total, totalTicks);
        var n = Interlocked.Increment(ref _count);
        if (n % ReportEvery == 0) Report(n);
    }

    /// <summary>Record the invoke sub-segments measured inside worker.InvokeAsync.
    /// build = AddCommand/AddParameter, run = BeginInvoke→EndInvoke, copy = output Collection, cleanup = Cleanup().</summary>
    internal static void RecordInvokeDetail(long buildTicks, long runTicks, long copyTicks, long cleanupTicks)
    {
        Interlocked.Add(ref _build, buildTicks);
        Interlocked.Add(ref _run, runTicks);
        Interlocked.Add(ref _copy, copyTicks);
        Interlocked.Add(ref _cleanup, cleanupTicks);
    }

    private static void Report(long n)
    {
        // Windowed averages since the last report line.
        long c = n, m = _marshal, ch = _checkout, iv = _invoke, b = _build, r = _run, cp = _copy, cl = _cleanup, e = _extract, t = _total;
        long dn = c - _lc;
        if (dn <= 0) return;
        double Avg(long cur, long last) => (cur - last) * 1_000_000.0 / Stopwatch.Frequency / dn;
        _logger?.LogWarning(
            "[DispatchProfile] window={Dn} us/req: marshal={M:F1} checkout={C:F1} invoke={I:F1} (build={B:F1} run={R:F1} copy={Cp:F1} cleanup={Cl:F1}) extract={E:F1} total={T:F1}",
            dn, Avg(m, _lm), Avg(ch, _lch), Avg(iv, _li), Avg(b, _lb), Avg(r, _lr), Avg(cp, _lcp), Avg(cl, _lcl), Avg(e, _le), Avg(t, _lt));
        _lc = c; _lm = m; _lch = ch; _li = iv; _lb = b; _lr = r; _lcp = cp; _lcl = cl; _le = e; _lt = t;
    }
}

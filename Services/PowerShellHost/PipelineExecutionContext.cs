namespace Craft.PowerShellHost;

/// <summary>
/// Clears per-invocation <c>AsyncLocal</c> state that would otherwise leak across invocations on a
/// worker's reused pipeline thread (<c>PSThreadOptions.ReuseThread</c>). An AsyncLocal value lives in
/// the thread's <see cref="System.Threading.ExecutionContext"/>; runspace SessionState (module
/// <c>$script:</c> variables, ModuleInjections caches) and process env do NOT — so restoring a clean
/// baseline ExecutionContext drops the per-request AsyncLocal state while leaving persisted per-worker
/// state untouched.
///
/// A captured ExecutionContext is an immutable snapshot, not thread-bound, so a single clean baseline
/// captured once (at worker init, before any request context exists) can be restored onto every
/// worker's pipeline thread. Uses only the public, supported
/// <see cref="System.Threading.ExecutionContext.Capture"/> /
/// <see cref="System.Threading.ExecutionContext.Restore(System.Threading.ExecutionContext)"/> (public
/// since net8). Both entry points must run ON the pipeline thread (i.e. via a pipeline invocation).
/// </summary>
public static class PipelineExecutionContext
{
    private static System.Threading.ExecutionContext? s_baseline;
    private static volatile bool s_captured;

    /// <summary>True once a baseline has been captured for the pool (worker init ran at least once).</summary>
    public static bool Captured => s_captured;

    /// <summary>True if the captured baseline is the runtime default (Capture() returned null), in which
    /// case <see cref="Reset"/> is a no-op — there is no public way to Restore the default context.</summary>
    public static bool BaselineIsDefault => s_captured && s_baseline is null;

    /// <summary>
    /// Capture the clean baseline once, from the calling (pipeline) thread. Idempotent — only the first
    /// call captures. Call at worker init, after warmup, before serving invocations.
    /// </summary>
    public static void CaptureBaselineIfNeeded()
    {
        if (s_captured) return;
        s_baseline = System.Threading.ExecutionContext.Capture(); // clean, post-warmup context (no per-request AsyncLocals)
        s_captured = true;                                        // volatile write publishes s_baseline
    }

    /// <summary>
    /// Restore the clean baseline onto the calling (pipeline) thread, dropping any AsyncLocal set during
    /// the invocation. Throws if no baseline was ever captured; the caller catches, logs and continues.
    /// </summary>
    public static void Reset()
    {
        if (!s_captured)
            throw new InvalidOperationException("ExecutionContext baseline was never captured for this worker pool.");
        var baseline = s_baseline;
        if (baseline is not null)
            System.Threading.ExecutionContext.Restore(baseline); // public API (net8+)
        // else: warmup context was the runtime default; nothing to Restore via the public API.
    }
}

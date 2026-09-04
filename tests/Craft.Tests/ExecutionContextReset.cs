using System.Reflection;

namespace Craft.Tests;

/// <summary>
/// "Solution 1": reset the reused pipeline thread's ExecutionContext to a clean warmup baseline
/// between invocations, dropping any AsyncLocal value set during an invocation. Runspace SessionState
/// (module $script: vars, ModuleInjections caches) and process env do NOT live in the ExecutionContext,
/// so this is safe for persisted per-worker state.
///
/// Mechanism: <see cref="ExecutionContext.Capture"/> + <see cref="ExecutionContext.Restore"/> — both
/// PUBLIC and supported on net8/net9/net10. Restore throws on a null argument, so we restore a baseline
/// captured on the pipeline thread at warmup (when it is clean). In the rare case the warmup context is
/// the runtime default (Capture() == null), we fall back to the internal RestoreInternal(null); a fix
/// that had to do that would probe it at startup and degrade to Solution 2 if absent.
///
/// Must be driven ON the pipeline thread: <see cref="CaptureBaseline"/> once at warmup, then
/// <see cref="ResetToClean"/> at each invocation boundary.
/// </summary>
public static class ExecutionContextReset
{
    [ThreadStatic] private static ExecutionContext? s_baseline;
    [ThreadStatic] private static bool s_haveBaseline;

    // Only used when the captured warmup baseline is the default (null) context, which public Restore rejects.
    private static readonly MethodInfo? s_restoreInternalNull =
        typeof(ExecutionContext).GetMethod("RestoreInternal",
            BindingFlags.Static | BindingFlags.NonPublic, binder: null,
            types: new[] { typeof(ExecutionContext) }, modifiers: null);

    /// <summary>Whether the supported public reset primitive exists on this runtime (the reliability answer).</summary>
    public static bool PublicRestoreAvailable { get; } =
        typeof(ExecutionContext).GetMethod("Restore",
            BindingFlags.Static | BindingFlags.Public, binder: null,
            types: new[] { typeof(ExecutionContext) }, modifiers: null) != null;

    /// <summary>Capture the current (clean) context as the per-thread baseline. Call at warmup, on the pipeline thread.</summary>
    public static void CaptureBaseline()
    {
        s_baseline = ExecutionContext.Capture();   // may be null if the warmup context is the runtime default
        s_haveBaseline = true;
    }

    /// <summary>What ResetToClean would use on the calling thread — for reporting.</summary>
    public static string Mechanism =>
        !s_haveBaseline ? "(baseline not captured on this thread)"
        : s_baseline != null ? "ExecutionContext.Restore(baseline)  [public, supported]"
        : s_restoreInternalNull != null ? "ExecutionContext.RestoreInternal(null)  [internal; warmup ctx was default]"
        : "unavailable (default baseline, no internal fallback)";

    /// <summary>Restore the warmup baseline on the calling (pipeline) thread, dropping AsyncLocals set since.</summary>
    public static void ResetToClean()
    {
        if (!s_haveBaseline) CaptureBaseline();   // best effort; prefer an explicit warmup capture
        if (s_baseline != null)
        {
            ExecutionContext.Restore(s_baseline); // supported public API (net8+)
            return;
        }
        if (s_restoreInternalNull != null)
        {
            s_restoreInternalNull.Invoke(null, new object?[] { null });
            return;
        }
        throw new NotSupportedException("Warmup context was the default and no internal null-restore is available.");
    }
}

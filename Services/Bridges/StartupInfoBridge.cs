// NAMESPACE PINNED — do not change.
// Downstream PowerShell reaches these types by fully-qualified name, e.g.
//   [Craft.Services.RealtimeBridge]::Publish($userId, $jobId, 'start', $data)
// Renaming the namespace compiles fine and then fails at runtime in the hosted app
// ("Unable to find type"). Type forwarding cannot help — it only works across assemblies.
// The folder is free to move; the namespace is a published contract.
namespace Craft.Services;

/// <summary>
/// Static bridge exposing container startup metrics to PowerShell and HTTP endpoints.
/// Populated during pool initialization. Read-only after startup completes.
///
/// PS usage:
///   $info = [Craft.Services.StartupInfoBridge]::GetInfo()
///   $info.HttpReadyMs      # time in ms until first HTTP worker was ready
///   $info.IsFullyReady     # true once all pools are done
///   $info.Phase            # current phase: "Starting", "HttpReady", "Ready"
/// </summary>
public static class StartupInfoBridge
{
    private static readonly StartupStats s_stats = new();

    /// <summary>Get the current startup statistics snapshot.</summary>
    public static StartupStats GetInfo() => s_stats;

    // ── Setters (called by PowerShellWorkerPool during init) ───────────

    internal static void SetReadinessMode(string mode) => s_stats.ReadinessMode = mode;
    internal static void SetWarmupMode(string mode) => s_stats.WarmupMode = mode;
    internal static void SetCpuCount(int count) => s_stats.CpuCount = count;
    internal static void SetPoolConfig(int httpSize, int bgSize)
    {
        s_stats.HttpPoolSize = httpSize;
        s_stats.BgPoolSize = bgSize;
    }
    internal static void SetModuleCounts(int shared, int httpOnly, int bgOnly)
    {
        s_stats.SharedModuleCount = shared;
        s_stats.HttpOnlyModuleCount = httpOnly;
        s_stats.BgOnlyModuleCount = bgOnly;
    }
    internal static void SetBaseWorkerDone(long ms, int functionCount)
    {
        s_stats.BaseWorkerMs = ms;
        s_stats.BaseFunctionCount = functionCount;
        s_stats.Phase = "BaseReady";
    }
    internal static void SetWarmupDone(long ms) => s_stats.WarmupMs = ms;
    internal static void SetHttpReady(long ms, int functionCount)
    {
        s_stats.HttpReadyMs = ms;
        s_stats.HttpFunctionCount = functionCount;
        s_stats.Phase = "HttpReady";
    }
    internal static void SetHttpPoolFull(long ms) => s_stats.HttpPoolFullMs = ms;
    internal static void SetBgReady(long ms, int functionCount)
    {
        s_stats.BgReadyMs = ms;
        s_stats.BgFunctionCount = functionCount;
    }
    internal static void SetFullyReady(long ms)
    {
        s_stats.FullyReadyMs = ms;
        s_stats.Phase = "Ready";
        s_stats.IsFullyReady = true;
    }
}

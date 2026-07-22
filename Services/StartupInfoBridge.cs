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

/// <summary>
/// Immutable-ish container for startup timing and configuration stats.
/// Properties are set once during initialization and read thereafter.
/// </summary>
public class StartupStats
{
    // ── Configuration ──────────────────────────────────────────────────
    public string ReadinessMode { get; internal set; } = "Unknown";
    public string WarmupMode { get; internal set; } = "Unknown";
    public int CpuCount { get; internal set; }
    public int HttpPoolSize { get; internal set; }
    public int BgPoolSize { get; internal set; }
    public int SharedModuleCount { get; internal set; }
    public int HttpOnlyModuleCount { get; internal set; }
    public int BgOnlyModuleCount { get; internal set; }

    // ── Timing (milliseconds from init start) ──────────────────────────
    public long WarmupMs { get; internal set; }
    public long BaseWorkerMs { get; internal set; }
    public long HttpReadyMs { get; internal set; }
    public long HttpPoolFullMs { get; internal set; }
    public long BgReadyMs { get; internal set; }
    public long FullyReadyMs { get; internal set; }

    // ── Counts ─────────────────────────────────────────────────────────
    public int BaseFunctionCount { get; internal set; }
    public int HttpFunctionCount { get; internal set; }
    public int BgFunctionCount { get; internal set; }

    // ── State ──────────────────────────────────────────────────────────
    /// <summary>Current phase: "Starting", "BaseReady", "HttpReady", "Ready"</summary>
    public string Phase { get; internal set; } = "Starting";
    public bool IsFullyReady { get; internal set; }
}

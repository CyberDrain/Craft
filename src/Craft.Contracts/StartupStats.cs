// NAMESPACE PINNED — do not change.
// Downstream PowerShell reaches these types by fully-qualified name, e.g.
//   [Craft.Services.RealtimeBridge]::Publish($userId, $jobId, 'start', $data)
// Renaming the namespace compiles fine and then fails at runtime in the hosted app
// ("Unable to find type"). Type forwarding cannot help — it only works across assemblies.
// The folder is free to move; the namespace is a published contract.
namespace Craft.Services;

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

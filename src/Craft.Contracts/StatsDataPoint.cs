// NAMESPACE PINNED — do not change.
// Downstream PowerShell reaches these types by fully-qualified name, e.g.
//   [Craft.Services.RealtimeBridge]::Publish($userId, $jobId, 'start', $data)
// Renaming the namespace compiles fine and then fails at runtime in the hosted app
// ("Unable to find type"). Type forwarding cannot help — it only works across assemblies.
// The folder is free to move; the namespace is a published contract.
namespace Craft.Services;

/// <summary>
/// A single historical data point capturing worker pool state and interval deltas.
/// </summary>
public class StatsDataPoint
{
    public DateTime TimestampUtc { get; set; }
    public long UptimeSeconds { get; set; }

    // ── HTTP pool (current state) ──
    public int HttpBusy { get; set; }
    public int HttpPoolSize { get; set; }
    public double HttpUtilizationPct { get; set; }
    public long HttpAvgDurationMs { get; set; }

    // ── HTTP pool (delta since last sample) ──
    public long HttpInvocations { get; set; }
    public long HttpFaults { get; set; }
    public long HttpBusyMs { get; set; }

    // ── BG pool (current state) ──
    public int BgBusy { get; set; }
    public int BgPoolSize { get; set; }
    public double BgUtilizationPct { get; set; }
    public long BgAvgDurationMs { get; set; }

    // ── BG pool (delta since last sample) ──
    public long BgInvocations { get; set; }
    public long BgFaults { get; set; }
    public long BgBusyMs { get; set; }

    // ── Jobs (current state + delta) ──
    public int JobsQueued { get; set; }
    public int JobsRunning { get; set; }
    public long JobsCompleted { get; set; }
    public long JobsFailed { get; set; }

    // ── Limiter (current state) ──
    public int LimiterActive { get; set; }
    public int LimiterWaiting { get; set; }
    public int LimiterCurrentMax { get; set; }
    public bool IsHttpThrottled { get; set; }

    // ── Memory (current state) ──
    public long HeapMB { get; set; }
    public long RssMB { get; set; }
    public long CommittedMB { get; set; }
    public long ContainerLimitMB { get; set; }
    public long ContainerUsedMB { get; set; }
    public long OtherRssMB { get; set; }
    public long GCHeapLimitMB { get; set; }
    public double MemoryUsagePct { get; set; }
    public int GC0 { get; set; }
    public int GC1 { get; set; }
    public int GC2 { get; set; }

    // ── CPU (delta-computed) ──
    public double CpuPct { get; set; }
    public double ContainerCpuPct { get; set; }
    public double OtherCpuPct { get; set; }
}

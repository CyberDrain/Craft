// NAMESPACE PINNED — do not change.
// Downstream PowerShell reaches these types by fully-qualified name, e.g.
//   [Craft.Services.RealtimeBridge]::Publish($userId, $jobId, 'start', $data)
// Renaming the namespace compiles fine and then fails at runtime in the hosted app
// ("Unable to find type"). Type forwarding cannot help — it only works across assemblies.
// The folder is free to move; the namespace is a published contract.
namespace Craft.Services;

public class PoolMetrics
{
    public int PoolSize { get; set; }
    public int Available { get; set; }
    public int BusyCount { get; set; }
    public long TotalInvocations { get; set; }
    public long TotalBusyMs { get; set; }
    public long TotalFaults { get; set; }
    public double AvgUtilizationPct { get; set; }
    public long AvgDurationMs { get; set; }
    public List<WorkerDetail> Workers { get; set; } = new();
}

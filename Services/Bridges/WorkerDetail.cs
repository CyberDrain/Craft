// NAMESPACE PINNED — do not change.
// Downstream PowerShell reaches these types by fully-qualified name, e.g.
//   [Craft.Services.RealtimeBridge]::Publish($userId, $jobId, 'start', $data)
// Renaming the namespace compiles fine and then fails at runtime in the hosted app
// ("Unable to find type"). Type forwarding cannot help — it only works across assemblies.
// The folder is free to move; the namespace is a published contract.
namespace Craft.Services;

public class WorkerDetail
{
    public int WorkerId { get; set; }
    public bool IsBusy { get; set; }
    public string? CurrentFunction { get; set; }
    public long TotalInvocations { get; set; }
    public long TotalBusyMs { get; set; }
    public long TotalFaults { get; set; }
    public double UtilizationPct { get; set; }
    public long LastDurationMs { get; set; }
    public long MinDurationMs { get; set; }
    public long MaxDurationMs { get; set; }
    public long AvgDurationMs { get; set; }
    public double TotalAllocMB { get; set; }
    public double LastAllocMB { get; set; }
    public double AvgAllocMB { get; set; }
    public DateTime? LastCheckoutUtc { get; set; }
    public DateTime? LastReclaimUtc { get; set; }
}

// NAMESPACE PINNED — do not change.
// Downstream PowerShell reaches these types by fully-qualified name, e.g.
//   [Craft.Services.RealtimeBridge]::Publish($userId, $jobId, 'start', $data)
// Renaming the namespace compiles fine and then fails at runtime in the hosted app
// ("Unable to find type"). Type forwarding cannot help — it only works across assemblies.
// The folder is free to move; the namespace is a published contract.
namespace Craft.Services;

public class MemoryMetrics
{
    public long HeapMB { get; set; }
    public long RssMB { get; set; }
    public long CommittedMB { get; set; }
    public long ContainerLimitMB { get; set; }
    public long ContainerUsedMB { get; set; }
    public long ContainerFreeMB { get; set; }
    public long OtherRssMB { get; set; }
    public long GCHeapLimitMB { get; set; }
    public double UsagePct { get; set; }
    public int GC0 { get; set; }
    public int GC1 { get; set; }
    public int GC2 { get; set; }
    public double CpuPct { get; set; }
    public double ContainerCpuPct { get; set; }
    public double OtherCpuPct { get; set; }
}

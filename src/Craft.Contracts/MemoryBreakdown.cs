// NAMESPACE PINNED — do not change.
// Downstream PowerShell reaches these types by fully-qualified name, e.g.
//   [Craft.Services.RealtimeBridge]::Publish($userId, $jobId, 'start', $data)
// Renaming the namespace compiles fine and then fails at runtime in the hosted app
// ("Unable to find type"). Type forwarding cannot help — it only works across assemblies.
// The folder is free to move; the namespace is a published contract.
namespace Craft.Services;

public class MemoryBreakdown
{
    public DateTime TimestampUtc { get; set; }
    public long UptimeSeconds { get; set; }

    // Top-level sizes
    public long HeapBytes { get; set; }
    public double HeapMB { get; set; }
    public long RssBytes { get; set; }
    public double RssMB { get; set; }
    public long CommittedBytes { get; set; }
    public double CommittedMB { get; set; }
    public long NativeEstimateBytes { get; set; }
    public double NativeEstimateMB { get; set; }
    public long ContainerLimitMB { get; set; }
    public long GCHeapLimitMB { get; set; }

    // Per-generation detail
    public GenerationDetail[] Generations { get; set; } = Array.Empty<GenerationDetail>();
    public long TotalFragmentationBytes { get; set; }
    public double TotalFragmentationMB { get; set; }
    public long PinnedObjectsCount { get; set; }
    public long FinalizationPendingCount { get; set; }
    public long PromotedBytes { get; set; }
    public double PromotedMB { get; set; }

    // GC state
    public int GC0 { get; set; }
    public int GC1 { get; set; }
    public int GC2 { get; set; }
    public bool Compacted { get; set; }
    public bool Concurrent { get; set; }
    public double GCPauseMs { get; set; }
    public long MemoryLoadBytes { get; set; }
    public double MemoryLoadMB { get; set; }
    public long HighMemoryLoadThresholdBytes { get; set; }
    public double HighMemoryLoadThresholdMB { get; set; }

    // Threads
    public int ThreadCount { get; set; }
    public Dictionary<string, int> ThreadStates { get; set; } = new();
    public int ProcessorCount { get; set; }

    // Assemblies
    public int LoadedAssemblyCount { get; set; }
    public long AssemblyDiskSizeBytes { get; set; }
    public double AssemblyDiskSizeMB { get; set; }
    public Dictionary<string, int> AssemblyCategories { get; set; } = new();

    // Worker pool context
    public int HttpWorkers { get; set; }
    public int BgWorkers { get; set; }
    public int TotalWorkers { get; set; }
    public double EstPerWorkerMB { get; set; }

    public double CpuPct { get; set; }
}

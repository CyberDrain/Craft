// NAMESPACE PINNED — do not change.
// Downstream PowerShell reaches these types by fully-qualified name, e.g.
//   [Craft.Services.RealtimeBridge]::Publish($userId, $jobId, 'start', $data)
// Renaming the namespace compiles fine and then fails at runtime in the hosted app
// ("Unable to find type"). Type forwarding cannot help — it only works across assemblies.
// The folder is free to move; the namespace is a published contract.
namespace Craft.Services;

public class WorkerMetricsSnapshot
{
    public DateTime TimestampUtc { get; set; }
    public long UptimeSeconds { get; set; }
    public PoolMetrics HttpPool { get; set; } = new();
    public PoolMetrics BgPool { get; set; } = new();
    public LimiterMetrics Limiter { get; set; } = new();
    public JobMetrics Jobs { get; set; } = new();
    public MemoryMetrics Memory { get; set; } = new();
}

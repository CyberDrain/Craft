using Craft.Hosting;

// NAMESPACE PINNED — do not change.
// Downstream PowerShell reaches these types by fully-qualified name, e.g.
//   [Craft.Services.RealtimeBridge]::Publish($userId, $jobId, 'start', $data)
// Renaming the namespace compiles fine and then fails at runtime in the hosted app
// ("Unable to find type"). Type forwarding cannot help — it only works across assemblies.
// The folder is free to move; the namespace is a published contract.
namespace Craft.Services;

/// <summary>
/// Static bridge exposing worker pool metrics and utilization data to PowerShell.
/// Domain code injects <see cref="WorkerMetricsService"/> instead of calling these statics.
///
/// PS usage:
///   $metrics = [Craft.Services.WorkerMetricsBridge]::GetSnapshot()
///   $metrics.HttpPool.BusyCount
///   $metrics.HttpPool.Workers[0].TotalInvocations
///   $metrics.BgPool.Workers
///   $metrics.Limiter.IsHttpThrottled
///   $metrics.Jobs.Running
/// </summary>
/// <remarks>
/// Uninitialized policy: all APIs throw (metrics require the PowerShell graph).
/// </remarks>
public static class WorkerMetricsBridge
{
    private static WorkerMetricsService? s_metrics;

    internal static void Initialize(WorkerMetricsService metrics) => s_metrics = metrics;

    private static WorkerMetricsService Require() =>
        s_metrics ?? throw new InvalidOperationException("WorkerMetricsBridge not initialized");

    /// <summary>Pre-register a worker so it appears in snapshots even before first use.</summary>
    internal static void RegisterWorker(int workerId, bool isHttp) =>
        Require().RegisterWorker(workerId, isHttp);

    /// <summary>Remove a worker's stats when it is recycled/replaced.</summary>
    internal static void DeregisterWorker(int workerId) => Require().DeregisterWorker(workerId);

    /// <summary>Record that a worker was checked out (started processing).</summary>
    internal static void RecordCheckout(int workerId, bool isHttp) =>
        Require().RecordCheckout(workerId, isHttp);

    /// <summary>Record that a worker was reclaimed (finished processing).</summary>
    internal static void RecordReclaim(int workerId, bool faulted, long elapsedMs) =>
        Require().RecordReclaim(workerId, faulted, elapsedMs);

    /// <summary>Record the function name being executed on a worker.</summary>
    internal static void RecordFunction(int workerId, string functionName) =>
        Require().RecordFunction(workerId, functionName);

    /// <summary>Get a full snapshot of all worker metrics.</summary>
    public static WorkerMetricsSnapshot GetSnapshot() => Require().GetSnapshot();

    /// <summary>
    /// Get a detailed memory breakdown including per-generation heap sizes, LOH/POH,
    /// fragmentation, pinned objects, thread info, loaded assemblies, and native memory.
    /// </summary>
    public static MemoryBreakdown GetMemoryBreakdown() => Require().GetMemoryBreakdown();

    /// <summary>Get metrics for a specific pool type ("http" or "bg").</summary>
    public static PoolMetrics? GetPoolMetrics(string poolType) => Require().GetPoolMetrics(poolType);

    /// <summary>Get a summary of just the busy/available counts.</summary>
    public static WorkerSummary GetSummary() => Require().GetSummary();

    /// <summary>Get detailed job list with wait/duration times.</summary>
    public static List<JobDetail> GetJobDetails(string? runName = null, string? status = null, int limit = 100) =>
        Require().GetJobDetails(runName, status, limit);

    /// <summary>Get run group summaries.</summary>
    public static List<JobRunSummary> GetRunSummaries() => Require().GetRunSummaries();

    /// <summary>Cancel a single queued job by ID.</summary>
    public static bool CancelJob(string jobId) => Require().CancelJob(jobId);

    /// <summary>Cancel all queued jobs in a run group.</summary>
    public static int CancelRun(string runName) => Require().CancelRun(runName);

    /// <summary>Delete a completed/failed/cancelled job from tracking.</summary>
    public static bool DeleteJob(string jobId) => Require().DeleteJob(jobId);

    /// <summary>Change a queued job's priority (re-enqueues with new priority).</summary>
    public static bool ChangePriority(string jobId, int newPriority) =>
        Require().ChangePriority(jobId, newPriority);

    /// <summary>
    /// Force a full GC collection with LOH compaction and working-set trim.
    /// Returns the MB reclaimed, or -1 if skipped due to cooldown.
    /// </summary>
    public static long TrimMemory() => Require().TrimMemory();
}

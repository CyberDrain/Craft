using System.Collections.Concurrent;

namespace Craft.Services;

/// <summary>
/// Static bridge exposing worker pool metrics and utilization data to PowerShell.
///
/// PS usage:
///   $metrics = [Craft.Services.WorkerMetricsBridge]::GetSnapshot()
///   $metrics.HttpPool.BusyCount
///   $metrics.HttpPool.Workers[0].TotalInvocations
///   $metrics.BgPool.Workers
///   $metrics.Limiter.IsHttpThrottled
///   $metrics.Jobs.Running
/// </summary>
public static class WorkerMetricsBridge
{
    private static PowerShellWorkerPool? s_pool;
    private static BackgroundTaskLimiter? s_limiter;
    private static JobManager? s_jobManager;

    // Per-worker tracking: workerId → stats
    private static readonly ConcurrentDictionary<int, WorkerStats> s_workerStats = new();
    private static readonly DateTime s_startTimeUtc = DateTime.UtcNow;

    public static void Initialize(PowerShellWorkerPool pool, BackgroundTaskLimiter limiter, JobManager jobManager)
    {
        s_pool = pool;
        s_limiter = limiter;
        s_jobManager = jobManager;
    }

    // ── Called by PowerShellWorkerPool/RunnerService at checkout/reclaim ──

    /// <summary>Pre-register a worker so it appears in snapshots even before first use.</summary>
    public static void RegisterWorker(int workerId, bool isHttp)
    {
        var stats = s_workerStats.GetOrAdd(workerId, _ => new WorkerStats { WorkerId = workerId });
        stats.IsHttp = isHttp;
    }

    /// <summary>Record that a worker was checked out (started processing).</summary>
    public static void RecordCheckout(int workerId, bool isHttp)
    {
        var stats = s_workerStats.GetOrAdd(workerId, _ => new WorkerStats { WorkerId = workerId });
        stats.IsHttp = isHttp;
        stats.LastCheckoutUtc = DateTime.UtcNow;
        stats.IsBusy = true;
        Interlocked.Increment(ref stats._totalInvocations);
    }

    /// <summary>Record that a worker was reclaimed (finished processing).</summary>
    public static void RecordReclaim(int workerId, bool faulted, long elapsedMs)
    {
        if (!s_workerStats.TryGetValue(workerId, out var stats)) return;

        stats.IsBusy = false;
        stats.LastReclaimUtc = DateTime.UtcNow;
        stats.LastDurationMs = elapsedMs;

        Interlocked.Add(ref stats._totalBusyMs, elapsedMs);
        if (faulted) Interlocked.Increment(ref stats._totalFaults);

        // Track min/max/recent durations
        UpdateDurationStats(stats, elapsedMs);
    }

    /// <summary>Record the function name being executed on a worker.</summary>
    public static void RecordFunction(int workerId, string functionName)
    {
        if (!s_workerStats.TryGetValue(workerId, out var stats)) return;
        stats.CurrentFunction = functionName;
    }

    // ── Public query methods ──

    /// <summary>Get a full snapshot of all worker metrics.</summary>
    public static WorkerMetricsSnapshot GetSnapshot()
    {
        var snapshot = new WorkerMetricsSnapshot
        {
            TimestampUtc = DateTime.UtcNow,
            UptimeSeconds = (long)(DateTime.UtcNow - s_startTimeUtc).TotalSeconds,
        };

        if (s_pool != null)
        {
            var httpWorkers = new List<WorkerDetail>();
            var bgWorkers = new List<WorkerDetail>();

            foreach (var (workerId, stats) in s_workerStats)
            {
                var detail = BuildWorkerDetail(stats);
                if (stats.IsHttp)
                    httpWorkers.Add(detail);
                else
                    bgWorkers.Add(detail);
            }

            snapshot.HttpPool = new PoolMetrics
            {
                PoolSize = s_pool.HttpPoolSize,
                Available = s_pool.HttpAvailable,
                BusyCount = s_pool.HttpPoolSize - s_pool.HttpAvailable,
                Workers = httpWorkers,
            };

            snapshot.BgPool = new PoolMetrics
            {
                PoolSize = s_pool.BgPoolSize,
                Available = s_pool.BgAvailable,
                BusyCount = s_pool.BgPoolSize - s_pool.BgAvailable,
                Workers = bgWorkers,
            };

            // Aggregate pool-level stats
            AggregatePoolStats(snapshot.HttpPool, httpWorkers);
            AggregatePoolStats(snapshot.BgPool, bgWorkers);
        }

        if (s_limiter != null)
        {
            snapshot.Limiter = new LimiterMetrics
            {
                BaseConcurrency = s_limiter.BaseConcurrency,
                CeilingConcurrency = s_limiter.CeilingConcurrency,
                CurrentMax = s_limiter.CurrentMax,
                Active = s_limiter.Active,
                Waiting = s_limiter.Waiting,
                IsHttpThrottled = s_limiter.IsHttpThrottled,
            };
        }

        if (s_jobManager != null)
        {
            var summary = s_jobManager.GetSummary();
            snapshot.Jobs = new JobMetrics
            {
                Queued = summary.Queued,
                Running = summary.Running,
                Completed = summary.Completed,
                Failed = summary.Failed,
                TotalProcessed = summary.TotalProcessed,
                MaxConcurrency = summary.MaxConcurrency,
                ActiveConcurrency = summary.ActiveConcurrency,
            };
        }

        return snapshot;
    }

    /// <summary>Get metrics for a specific pool type ("http" or "bg").</summary>
    public static PoolMetrics? GetPoolMetrics(string poolType)
    {
        var snapshot = GetSnapshot();
        return poolType.Equals("http", StringComparison.OrdinalIgnoreCase)
            ? snapshot.HttpPool
            : snapshot.BgPool;
    }

    /// <summary>Get a summary of just the busy/available counts.</summary>
    public static WorkerSummary GetSummary()
    {
        return new WorkerSummary
        {
            HttpBusy = s_pool != null ? s_pool.HttpPoolSize - s_pool.HttpAvailable : 0,
            HttpAvailable = s_pool?.HttpAvailable ?? 0,
            HttpPoolSize = s_pool?.HttpPoolSize ?? 0,
            BgBusy = s_pool != null ? s_pool.BgPoolSize - s_pool.BgAvailable : 0,
            BgAvailable = s_pool?.BgAvailable ?? 0,
            BgPoolSize = s_pool?.BgPoolSize ?? 0,
            LimiterActive = s_limiter?.Active ?? 0,
            LimiterWaiting = s_limiter?.Waiting ?? 0,
            LimiterMax = s_limiter?.CurrentMax ?? 0,
            IsHttpThrottled = s_limiter?.IsHttpThrottled ?? false,
            JobsQueued = s_jobManager?.QueuedCount ?? 0,
            JobsActive = s_jobManager?.ActiveCount ?? 0,
        };
    }

    // ── Job management (exposed to PowerShell) ──

    /// <summary>Get detailed job list with wait/duration times.</summary>
    public static List<JobDetail> GetJobDetails(string? runName = null, string? status = null, int limit = 100)
        => s_jobManager?.GetJobDetails(runName, status, limit) ?? new();

    /// <summary>Get run group summaries.</summary>
    public static List<JobRunSummary> GetRunSummaries()
        => s_jobManager?.GetRunSummaries() ?? new();

    /// <summary>Cancel a single queued job by ID.</summary>
    public static bool CancelJob(string jobId)
        => s_jobManager?.CancelJob(jobId) ?? false;

    /// <summary>Cancel all queued jobs in a run group.</summary>
    public static int CancelRun(string runName)
        => s_jobManager?.CancelRun(runName) ?? 0;

    /// <summary>Delete a completed/failed/cancelled job from tracking.</summary>
    public static bool DeleteJob(string jobId)
        => s_jobManager?.DeleteJob(jobId) ?? false;

    /// <summary>Purge all completed/failed/cancelled jobs.</summary>
    public static int PurgeCompleted()
        => s_jobManager?.PurgeCompleted() ?? 0;

    /// <summary>Change a queued job's priority (re-enqueues with new priority).</summary>
    public static bool ChangePriority(string jobId, int newPriority)
        => s_jobManager?.ChangePriority(jobId, newPriority) ?? false;

    // ── Private helpers ──

    private static WorkerDetail BuildWorkerDetail(WorkerStats stats)
    {
        var uptimeMs = (long)(DateTime.UtcNow - s_startTimeUtc).TotalMilliseconds;
        var utilizationPct = uptimeMs > 0
            ? Math.Round(Interlocked.Read(ref stats._totalBusyMs) * 100.0 / uptimeMs, 1)
            : 0;

        return new WorkerDetail
        {
            WorkerId = stats.WorkerId,
            IsBusy = stats.IsBusy,
            CurrentFunction = stats.IsBusy ? stats.CurrentFunction : null,
            TotalInvocations = Interlocked.Read(ref stats._totalInvocations),
            TotalBusyMs = Interlocked.Read(ref stats._totalBusyMs),
            TotalFaults = Interlocked.Read(ref stats._totalFaults),
            UtilizationPct = utilizationPct,
            LastDurationMs = stats.LastDurationMs,
            MinDurationMs = stats.MinDurationMs == long.MaxValue ? 0 : stats.MinDurationMs,
            MaxDurationMs = stats.MaxDurationMs,
            AvgDurationMs = Interlocked.Read(ref stats._totalInvocations) > 0
                ? Interlocked.Read(ref stats._totalBusyMs) / Interlocked.Read(ref stats._totalInvocations)
                : 0,
            LastCheckoutUtc = stats.LastCheckoutUtc,
            LastReclaimUtc = stats.LastReclaimUtc,
        };
    }

    private static void AggregatePoolStats(PoolMetrics pool, List<WorkerDetail> workers)
    {
        if (workers.Count == 0) return;
        pool.TotalInvocations = workers.Sum(w => w.TotalInvocations);
        pool.TotalBusyMs = workers.Sum(w => w.TotalBusyMs);
        pool.TotalFaults = workers.Sum(w => w.TotalFaults);
        pool.AvgUtilizationPct = Math.Round(workers.Average(w => w.UtilizationPct), 1);
        pool.AvgDurationMs = pool.TotalInvocations > 0
            ? pool.TotalBusyMs / pool.TotalInvocations
            : 0;
    }

    private static void UpdateDurationStats(WorkerStats stats, long durationMs)
    {
        // Lock-free min/max updates using compare-exchange
        long current;
        do { current = stats.MinDurationMs; }
        while (durationMs < current && Interlocked.CompareExchange(ref stats.MinDurationMs, durationMs, current) != current);

        do { current = stats.MaxDurationMs; }
        while (durationMs > current && Interlocked.CompareExchange(ref stats.MaxDurationMs, durationMs, current) != current);
    }
}

// ── Data models ──

public class WorkerStats
{
    public int WorkerId;
    public bool IsHttp;
    public volatile bool IsBusy;
    public string? CurrentFunction;
    public DateTime? LastCheckoutUtc;
    public DateTime? LastReclaimUtc;
    public long LastDurationMs;
    public long MinDurationMs = long.MaxValue;
    public long MaxDurationMs;

    // Interlocked fields
    internal long _totalInvocations;
    internal long _totalBusyMs;
    internal long _totalFaults;
}

public class WorkerMetricsSnapshot
{
    public DateTime TimestampUtc { get; set; }
    public long UptimeSeconds { get; set; }
    public PoolMetrics HttpPool { get; set; } = new();
    public PoolMetrics BgPool { get; set; } = new();
    public LimiterMetrics Limiter { get; set; } = new();
    public JobMetrics Jobs { get; set; } = new();
}

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
    public DateTime? LastCheckoutUtc { get; set; }
    public DateTime? LastReclaimUtc { get; set; }
}

public class LimiterMetrics
{
    public int BaseConcurrency { get; set; }
    public int CeilingConcurrency { get; set; }
    public int CurrentMax { get; set; }
    public int Active { get; set; }
    public int Waiting { get; set; }
    public bool IsHttpThrottled { get; set; }
}

public class JobMetrics
{
    public int Queued { get; set; }
    public int Running { get; set; }
    public int Completed { get; set; }
    public int Failed { get; set; }
    public long TotalProcessed { get; set; }
    public int MaxConcurrency { get; set; }
    public int ActiveConcurrency { get; set; }
}

public class WorkerSummary
{
    public int HttpBusy { get; set; }
    public int HttpAvailable { get; set; }
    public int HttpPoolSize { get; set; }
    public int BgBusy { get; set; }
    public int BgAvailable { get; set; }
    public int BgPoolSize { get; set; }
    public int LimiterActive { get; set; }
    public int LimiterWaiting { get; set; }
    public int LimiterMax { get; set; }
    public bool IsHttpThrottled { get; set; }
    public int JobsQueued { get; set; }
    public int JobsActive { get; set; }
}

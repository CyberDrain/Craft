using System.Collections.Concurrent;

namespace CRAFT.Services;

// ── API Models ──

public class JobRecord
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string? RunName { get; set; }
    public int Priority { get; set; }
    public string Status { get; set; } = "Queued";
    public DateTime QueuedUtc { get; set; }
    public DateTime? StartedUtc { get; set; }
    public DateTime? CompletedUtc { get; set; }
    public string? LastError { get; set; }
}

public class JobSummary
{
    public int Queued { get; set; }
    public int Running { get; set; }
    public int Completed { get; set; }
    public int Failed { get; set; }
    public long TotalProcessed { get; set; }
    public DateTime? OldestQueuedUtc { get; set; }
    public int MaxConcurrency { get; set; }
    public int ActiveConcurrency { get; set; }
}

public class JobRunSummary
{
    public string Name { get; set; } = string.Empty;
    public int Priority { get; set; }
    public int Total { get; set; }
    public int Queued { get; set; }
    public int Running { get; set; }
    public int Completed { get; set; }
    public int Failed { get; set; }
    public DateTime? StartedUtc { get; set; }
    public DateTime? CompletedUtc { get; set; }
}

/// <summary>
/// Priority-aware job queue with concurrency control.
///
/// Design:
///   - Jobs are enqueued with a priority (lower number = higher priority)
///   - A single dispatch loop dequeues highest-priority jobs first
///   - Concurrency is capped to BgPoolSize (the real bottleneck is BG worker count)
///   - All jobs are tracked with timing and status for API queries
///   - Old completed jobs are cleaned up every 5 minutes
///
/// Priority levels (convention — callers can use any int):
///   0 = Critical (audit logs — every 15 min, must jump the queue)
///   1 = High     (standards — every 12 hours)
///   2 = Normal   (DB cache, tests — nightly batch)
///
/// How priority dispatch works:
///   The dispatch loop waits for both an item AND a concurrency slot.
///   When a slot opens, it dequeues the highest-priority item (lowest int)
///   from the PriorityQueue. This means a P0 audit-log task that arrives
///   while 400 P2 DB-cache tasks are queued will run as soon as a slot frees up,
///   rather than waiting behind all 400 P2 tasks (which is what FIFO does).
/// </summary>
public class JobManager : BackgroundService
{
    private readonly ILogger<JobManager> _logger;

    // ── Priority queue ──
    private readonly PriorityQueue<QueuedJob, int> _pendingQueue = new();
    private readonly object _queueLock = new();
    private readonly SemaphoreSlim _itemAvailable = new(0);

    // ── Concurrency ──
    private readonly SemaphoreSlim _concurrencyGate;
    private int _activeCount;

    // ── Tracking ──
    private readonly ConcurrentDictionary<string, JobRecord> _jobs = new();
    private long _totalProcessed;

    // ── Cleanup ──
    private readonly Timer _cleanupTimer;
    private static readonly TimeSpan CleanupInterval = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan MaxJobAge = TimeSpan.FromHours(24);
    private const int MaxTrackedJobs = 10_000;

    // ── Config ──
    public int MaxConcurrency { get; }
    public int ActiveCount => _activeCount;
    public int QueuedCount { get { lock (_queueLock) return _pendingQueue.Count; } }

    public JobManager(ILogger<JobManager> logger, IConfiguration configuration)
    {
        _logger = logger;
        MaxConcurrency = Math.Max(1, configuration.GetValue("PowerShell:BgPoolSize", 4));
        _concurrencyGate = new SemaphoreSlim(MaxConcurrency, MaxConcurrency);

        _cleanupTimer = new Timer(_ => CleanupOldJobs(), null, CleanupInterval, CleanupInterval);

        _logger.LogInformation("[JobManager] Initialized: maxConcurrency={Max}", MaxConcurrency);
    }

    /// <summary>
    /// Enqueue a job for priority-based execution.
    /// Returns immediately with the job ID. The job runs when a concurrency slot
    /// is available and it is the highest-priority item in the queue.
    /// </summary>
    /// <param name="name">Display name for the job (e.g. "CIPPDBCacheRun-Graph_tenant.com")</param>
    /// <param name="priority">Lower = higher priority. 0=Critical, 1=High, 2=Normal</param>
    /// <param name="work">Async work function. Receives a CancellationToken for shutdown.</param>
    /// <param name="runName">Optional run group name (e.g. "CIPPDBCacheRun") for grouping in status APIs.</param>
    /// <param name="id">Optional explicit job ID. Auto-generated if null.</param>
    public string Enqueue(string name, int priority, Func<CancellationToken, Task> work,
        string? runName = null, string? id = null)
    {
        var jobId = id ?? $"{name}_{Guid.NewGuid():N}";
        var record = new JobRecord
        {
            Id = jobId,
            Name = name,
            RunName = runName,
            Priority = priority,
            Status = "Queued",
            QueuedUtc = DateTime.UtcNow
        };

        _jobs.TryAdd(jobId, record);

        lock (_queueLock)
        {
            _pendingQueue.Enqueue(new QueuedJob(record, work), priority);
        }
        _itemAvailable.Release();

        _logger.LogDebug("[JobManager] Enqueued: {Name} P{Priority} run={Run} ({Queued} queued, {Active} active)",
            name, priority, runName ?? "-", QueuedCount, _activeCount);

        return jobId;
    }

    /// <summary>
    /// Dispatch loop: waits for items + concurrency slots, dispatches highest-priority first.
    /// </summary>
    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        _logger.LogInformation("[JobManager] Dispatch loop started");

        while (!ct.IsCancellationRequested)
        {
            try
            {
                // 1. Wait for at least one item in the queue
                await _itemAvailable.WaitAsync(ct);

                // 2. Wait for a concurrency slot (blocks until a running job completes)
                await _concurrencyGate.WaitAsync(ct);

                // 3. Dequeue highest priority item (lowest int wins)
                QueuedJob? job;
                lock (_queueLock)
                {
                    _pendingQueue.TryDequeue(out job, out _);
                }

                if (job == null)
                {
                    _concurrencyGate.Release();
                    continue;
                }

                Interlocked.Increment(ref _activeCount);

                // 4. Fire-and-forget: job runs asynchronously, releases slot on completion
                _ = RunJobAsync(job, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
        }

        _logger.LogInformation("[JobManager] Dispatch loop stopped ({Active} active, {Queued} queued)",
            _activeCount, QueuedCount);
    }

    private async Task RunJobAsync(QueuedJob job, CancellationToken ct)
    {
        // Set operation context for traceability — ExecuteScript reads RunName from this
        var parentInvocation = new OperationContext.Invocation(job.Record.Name)
        {
            RunName = job.Record.RunName,
            Category = "Job"
        };
        using var opScope = OperationContext.Set(parentInvocation);

        try
        {
            job.Record.Status = "Running";
            job.Record.StartedUtc = DateTime.UtcNow;

            var queueTime = job.Record.StartedUtc.Value - job.Record.QueuedUtc;
            if (queueTime.TotalSeconds > 1)
            {
                _logger.LogInformation(
                    "[JobManager] Started: {Name} P{Priority} after {QueueSec:F1}s wait ({Active} active)",
                    job.Record.Name, job.Record.Priority, queueTime.TotalSeconds, _activeCount);
            }

            await job.Work(ct);

            job.Record.Status = "Completed";
            job.Record.CompletedUtc = DateTime.UtcNow;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            job.Record.Status = "Cancelled";
            job.Record.CompletedUtc = DateTime.UtcNow;
            _logger.LogInformation("[JobManager] Cancelled (shutdown): {Name}", job.Record.Name);
        }
        catch (Exception ex)
        {
            job.Record.Status = "Failed";
            job.Record.LastError = ex.Message;
            job.Record.CompletedUtc = DateTime.UtcNow;
            _logger.LogError(ex, "[JobManager] Failed: {Name}", job.Record.Name);
        }
        finally
        {
            Interlocked.Decrement(ref _activeCount);
            Interlocked.Increment(ref _totalProcessed);
            _concurrencyGate.Release();
        }
    }

    // ─── Status Queries ───

    public JobSummary GetSummary()
    {
        var jobs = _jobs.Values.ToList();
        var queued = jobs.Where(j => j.Status == "Queued").ToList();

        return new JobSummary
        {
            Queued = queued.Count,
            Running = jobs.Count(j => j.Status == "Running"),
            Completed = jobs.Count(j => j.Status == "Completed"),
            Failed = jobs.Count(j => j.Status == "Failed"),
            TotalProcessed = _totalProcessed,
            OldestQueuedUtc = queued.Count > 0 ? queued.Min(j => j.QueuedUtc) : null,
            MaxConcurrency = MaxConcurrency,
            ActiveConcurrency = _activeCount
        };
    }

    public List<JobRunSummary> GetRunSummaries()
    {
        return _jobs.Values
            .Where(j => j.RunName != null)
            .GroupBy(j => j.RunName!)
            .Select(g =>
            {
                var jobs = g.ToList();
                return new JobRunSummary
                {
                    Name = g.Key,
                    Priority = jobs.FirstOrDefault()?.Priority ?? 2,
                    Total = jobs.Count,
                    Queued = jobs.Count(j => j.Status == "Queued"),
                    Running = jobs.Count(j => j.Status == "Running"),
                    Completed = jobs.Count(j => j.Status == "Completed"),
                    Failed = jobs.Count(j => j.Status == "Failed"),
                    StartedUtc = jobs.Where(j => j.StartedUtc.HasValue).Select(j => j.StartedUtc).Min(),
                    CompletedUtc = jobs.All(j => j.Status is "Completed" or "Failed" or "Cancelled")
                        ? jobs.Max(j => j.CompletedUtc)
                        : null
                };
            })
            .OrderBy(r => r.Priority)
            .ThenByDescending(r => r.StartedUtc)
            .ToList();
    }

    public List<JobRecord> GetJobs(string? runName = null, string? status = null, int? limit = null)
    {
        var query = _jobs.Values.AsEnumerable();

        if (!string.IsNullOrEmpty(runName))
            query = query.Where(j => string.Equals(j.RunName, runName, StringComparison.OrdinalIgnoreCase));
        if (!string.IsNullOrEmpty(status))
            query = query.Where(j => string.Equals(j.Status, status, StringComparison.OrdinalIgnoreCase));

        query = query.OrderBy(j => j.Priority).ThenBy(j => j.QueuedUtc);

        if (limit.HasValue)
            query = query.Take(limit.Value);

        return query.ToList();
    }

    // ─── Cleanup ───

    private void CleanupOldJobs()
    {
        var cutoff = DateTime.UtcNow - MaxJobAge;
        var toRemove = _jobs.Values
            .Where(j => j.Status is "Completed" or "Failed" or "Cancelled"
                     && j.CompletedUtc.HasValue && j.CompletedUtc < cutoff)
            .Select(j => j.Id)
            .ToList();

        foreach (var id in toRemove)
            _jobs.TryRemove(id, out _);

        // Also cap total tracked jobs
        if (_jobs.Count > MaxTrackedJobs)
        {
            var excess = _jobs.Values
                .Where(j => j.Status is "Completed" or "Failed" or "Cancelled")
                .OrderBy(j => j.CompletedUtc)
                .Take(_jobs.Count - MaxTrackedJobs)
                .Select(j => j.Id)
                .ToList();

            foreach (var id in excess)
                _jobs.TryRemove(id, out _);
        }

        if (toRemove.Count > 0)
            _logger.LogDebug("[JobManager] Cleaned up {Count} old jobs, tracking {Total}",
                toRemove.Count, _jobs.Count);
    }

    public override void Dispose()
    {
        _cleanupTimer.Dispose();
        _concurrencyGate.Dispose();
        _itemAvailable.Dispose();
        base.Dispose();
    }

    // ── Internal Types ──
    private record QueuedJob(JobRecord Record, Func<CancellationToken, Task> Work);
}

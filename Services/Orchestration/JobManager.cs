using System.Collections.Concurrent;
using Craft.Configuration;
using Craft.Hosting;
using Craft.Services;

namespace Craft.Orchestration;

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
/// Priority levels (lower = higher priority, callers can use any int):
///   0-1 = Critical (system cleanup, user tasks)
///   2-3 = High     (audit logs, webhooks)
///   4-5 = Normal   (standards, drift, cache)
///   6+  = Low      (alerts, DB cache, tests, extensions)
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
    private readonly BackgroundTaskLimiter _limiter;

    // ── Priority queue ──
    private readonly PriorityQueue<QueuedJob, int> _pendingQueue = new();
    private readonly object _queueLock = new();
    private readonly SemaphoreSlim _itemAvailable = new(0);

    // ── Concurrency (tracked locally for API queries; actual gating is via _limiter) ──
    private int _activeCount;

    // ── Tracking ──
    private readonly ConcurrentDictionary<string, JobRecord> _jobs = new();
    private readonly ConcurrentDictionary<string, bool> _cancelledJobIds = new();
    private readonly ConcurrentDictionary<string, Func<CancellationToken, Task>> _pendingWork = new();
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

    public JobManager(ILogger<JobManager> logger, CraftSettings settings, BackgroundTaskLimiter limiter)
    {
        _logger = logger;
        _limiter = limiter;
        MaxConcurrency = Math.Max(1, settings.Worker.BgPoolSize);

        _cleanupTimer = new Timer(_ => CleanupOldJobs(), null, CleanupInterval, CleanupInterval);

        _logger.LogInformation("[JobManager] Initialized: maxConcurrency={Max} (gated by BackgroundTaskLimiter)", MaxConcurrency);
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
        _pendingWork.TryAdd(jobId, work);

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
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("[JobManager] Dispatch loop started");

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                // 1. Wait for at least one item in the queue
                await _itemAvailable.WaitAsync(stoppingToken);

                // 2. Peek the name for logging, then wait for a limiter slot.
                //    The limiter starts at baseline concurrency (e.g. 2-4) and
                //    only scales up after sustained backlog — this prevents
                //    instantly saturating all BG workers on startup.
                string peekName;
                lock (_queueLock)
                {
                    _pendingQueue.TryPeek(out var peeked, out _);
                    peekName = peeked?.Record.Name ?? "unknown";
                }
                await _limiter.AcquireAsync(peekName, stoppingToken);

                // 3. Dequeue highest priority item (lowest int wins)
                QueuedJob? job;
                lock (_queueLock)
                {
                    _pendingQueue.TryDequeue(out job, out _);
                }

                if (job == null)
                {
                    _limiter.ReleaseSlot();
                    continue;
                }

                // Skip cancelled jobs — release the slot and move on
                if (_cancelledJobIds.TryRemove(job.Record.Id, out _))
                {
                    _limiter.ReleaseSlot();
                    continue;
                }

                // Remove from pending work ref (no longer re-prioritizable)
                _pendingWork.TryRemove(job.Record.Id, out _);

                Interlocked.Increment(ref _activeCount);

                // 4. Fire-and-forget: job runs asynchronously, releases slot on completion
                _ = RunJobAsync(job, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
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
            _limiter.ReleaseSlot();
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

        query = query.OrderByDescending(j => j.QueuedUtc);

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

        if (toRemove.Count > 0 || _jobs.Count > MaxTrackedJobs)
            _logger.LogDebug("[JobManager] Cleaned up {Count} old jobs, tracking {Total}",
                toRemove.Count, _jobs.Count);
    }

    public override void Dispose()
    {
        // Nothing here owns unmanaged resources directly, but suppressing finalization keeps a
        // derived type that adds a finalizer from having to re-implement IDisposable to do it.
        GC.SuppressFinalize(this);
        _cleanupTimer.Dispose();
        _itemAvailable.Dispose();
        base.Dispose();
    }

    // ─── Job Management ───

    /// <summary>Cancel a queued job (running jobs cannot be cancelled via this method).</summary>
    public bool CancelJob(string jobId)
    {
        if (!_jobs.TryGetValue(jobId, out var record)) return false;
        if (record.Status != "Queued") return false;

        record.Status = "Cancelled";
        record.CompletedUtc = DateTime.UtcNow;
        record.LastError = "Cancelled by user";
        _cancelledJobIds.TryAdd(jobId, true);
        _logger.LogInformation("[JobManager] Cancelled: {Name} ({Id})", record.Name, jobId);
        return true;
    }

    /// <summary>Cancel all queued jobs in a run group.</summary>
    public int CancelRun(string runName)
    {
        var cancelled = 0;
        foreach (var record in _jobs.Values.Where(j => j.RunName == runName && j.Status == "Queued"))
        {
            record.Status = "Cancelled";
            record.CompletedUtc = DateTime.UtcNow;
            record.LastError = "Run cancelled by user";
            _cancelledJobIds.TryAdd(record.Id, true);
            cancelled++;
        }
        if (cancelled > 0)
            _logger.LogInformation("[JobManager] Cancelled run {Run}: {Count} jobs", runName, cancelled);
        return cancelled;
    }

    /// <summary>Remove a completed/failed/cancelled job from tracking.</summary>
    public bool DeleteJob(string jobId)
    {
        if (!_jobs.TryGetValue(jobId, out var record)) return false;
        if (record.Status is "Queued" or "Running") return false; // Must cancel first
        _jobs.TryRemove(jobId, out _);
        return true;
    }

    /// <summary>
    /// Change a queued job's priority. Updates the record immediately.
    /// The PriorityQueue doesn't support re-ordering, so the job is cancelled
    /// and re-enqueued with the new priority (preserving the original work function).
    /// </summary>
    public bool ChangePriority(string jobId, int newPriority)
    {
        if (!_jobs.TryGetValue(jobId, out var record)) return false;
        if (record.Status != "Queued") return false;

        // Find and remove the old entry by marking it cancelled, then re-enqueue
        // with the new priority using a stored work reference
        if (_pendingWork.TryRemove(jobId, out var work))
        {
            _cancelledJobIds.TryAdd(jobId, true);

            // Update the record's priority
            record.Priority = newPriority;

            // Re-enqueue with new priority
            lock (_queueLock)
            {
                _pendingQueue.Enqueue(new QueuedJob(record, work), newPriority);
            }
            _itemAvailable.Release();

            // Remove from cancelled set so the re-queued entry won't be skipped
            _cancelledJobIds.TryRemove(jobId, out _);

            _logger.LogInformation("[JobManager] Reprioritized: {Name} → P{Priority}", record.Name, newPriority);
            return true;
        }

        // Fallback: just update the record (work ref not found — already dispatched from queue)
        record.Priority = newPriority;
        _logger.LogInformation("[JobManager] Priority updated (display only): {Name} → P{Priority}", record.Name, newPriority);
        return true;
    }

    /// <summary>Get detailed job list with queue wait times.</summary>
    public List<JobDetail> GetJobDetails(string? runName = null, string? status = null, int limit = 100)
    {
        var now = DateTime.UtcNow;
        var query = _jobs.Values.AsEnumerable();

        if (!string.IsNullOrEmpty(runName))
            query = query.Where(j => string.Equals(j.RunName, runName, StringComparison.OrdinalIgnoreCase));
        if (!string.IsNullOrEmpty(status))
            query = query.Where(j => string.Equals(j.Status, status, StringComparison.OrdinalIgnoreCase));

        return query
            .OrderBy(j => j.Priority)
            .ThenBy(j => j.QueuedUtc)
            .Take(limit)
            .Select(j => new JobDetail
            {
                Id = j.Id,
                Name = j.Name,
                RunName = j.RunName,
                Priority = j.Priority,
                Status = j.Status,
                QueuedUtc = j.QueuedUtc,
                StartedUtc = j.StartedUtc,
                CompletedUtc = j.CompletedUtc,
                LastError = j.LastError,
                WaitSeconds = j.Status == "Queued" ? (now - j.QueuedUtc).TotalSeconds
                            : j.StartedUtc.HasValue ? (j.StartedUtc.Value - j.QueuedUtc).TotalSeconds
                            : 0,
                DurationSeconds = j.Status == "Running" && j.StartedUtc.HasValue ? (now - j.StartedUtc.Value).TotalSeconds
                                : j.CompletedUtc.HasValue && j.StartedUtc.HasValue ? (j.CompletedUtc.Value - j.StartedUtc.Value).TotalSeconds
                                : null,
            })
            .ToList();
    }

    // ── Internal Types ──
    private sealed record QueuedJob(JobRecord Record, Func<CancellationToken, Task> Work);
}

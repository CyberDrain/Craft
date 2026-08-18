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
///   0-1 = Critical (reserved: system cleanup, emergencies)
///   2   = User-initiated (HTTP-triggered fan-outs, user scheduled tasks, run-now starters)
///   3   = Elevated background (baseline runs)
///   4-5 = Normal   (background fan-out default; non-HTTP queue starters)
///   6+  = Low      (alerts, tests, extensions)
///
/// How priority dispatch works:
///   The dispatch loop waits for both an item AND a concurrency slot.
///   When a slot opens, it dequeues the highest-priority item (lowest int)
///   from the PriorityQueue. This means a P0 audit-log task that arrives
///   while 400 P2 DB-cache tasks are queued will run as soon as a slot frees up,
///   rather than waiting behind all 400 P2 tasks (which is what FIFO does).
///
/// Two invariants keep this loop live, both learned from production stalls:
///
///   1. THE LOOP NEVER RUNS JOB WORK ON ITS OWN THREAD. Jobs are handed to the thread pool.
///      An async method body runs synchronously on its caller until its first real await, so any
///      synchronous block inside a job — e.g. PowerShellRunnerService.ExecuteScript, which opens with
///      an untimed CheckoutBackground → BlockingCollection.Take() before any await — would otherwise
///      park this loop entirely. That produced hour-long windows with a full queue, free capacity and
///      zero dispatches, reported by the limiter as the maximally-misleading "1 active, 0 waiting"
///      (the loop holds at most one outstanding AcquireAsync, so parked elsewhere it counts as none).
///
///   2. THE LOOP NEVER DOES I/O. Descriptor rehydration happens inside the dispatched task, for the
///      same reason.
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

    /// <summary>Live queue epoch per reprioritized job. Only populated by <see cref="ChangePriority"/>.</summary>
    private readonly ConcurrentDictionary<string, int> _reprioritized = new();
    private long _totalProcessed;

    // ── Descriptor rehydration + durability ──
    private volatile JobWorkResolver? _resolver;
    private volatile IJobDescriptorStateWriter? _stateWriter;

    // ── Cleanup ──
    private readonly Timer _cleanupTimer;
    private static readonly TimeSpan CleanupInterval = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan MaxJobAge = TimeSpan.FromHours(24);
    private const int MaxTrackedJobs = 10_000;

    // ── Config ──
    public int MaxConcurrency { get; }
    public int ActiveCount => _activeCount;
    public int QueuedCount { get { lock (_queueLock) return _pendingQueue.Count; } }

    /// <summary>
    /// Is this job still in flight — queued or running?
    ///
    /// Presence in <see cref="_jobs"/> is NOT enough: terminal records are retained there up to
    /// <see cref="MaxTrackedJobs"/> for the status API, so a completed job would answer true and a caller
    /// checking "does anything still own this work" would wrongly conclude yes. Status is the answer.
    /// </summary>
    public bool IsQueuedOrRunning(string jobId) =>
        _jobs.TryGetValue(jobId, out var record) && record.Status is "Queued" or "Running";

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
    /// <param name="inheritPriority">Priority that work THIS JOB ENQUEUES should inherit, exposed to the
    /// job via <see cref="OperationContext.Invocation.Priority"/>. Distinct from <paramref name="priority"/>:
    /// a starter script's own queue priority orders the starter, not its fan-out. Only post-execution jobs
    /// pass this (the run's priority); leave null everywhere else.</param>
    public string Enqueue(string name, int priority, Func<CancellationToken, Task> work,
        string? runName = null, string? id = null, int? inheritPriority = null)
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
            _pendingQueue.Enqueue(new QueuedJob(record, work, null) { InheritPriority = inheritPriority }, priority);
        }
        _itemAvailable.Release();

        _logger.LogDebug("[JobManager] Enqueued: {Name} P{Priority} run={Run} ({Queued} queued, {Active} active)",
            name, priority, runName ?? "-", QueuedCount, _activeCount);

        return jobId;
    }

    /// <summary>
    /// Register the resolver that turns a <see cref="JobDescriptor"/> into runnable work at dispatch
    /// time. Called once at startup by <see cref="OrchestratorService"/>. Without a resolver, descriptor
    /// jobs fail fast rather than silently vanishing.
    /// </summary>
    public void SetWorkResolver(JobWorkResolver resolver) => _resolver = resolver;

    /// <summary>
    /// Register the sink that persists operator-initiated changes to queued descriptor jobs
    /// (reprioritize, cancel) so they survive a restart. Called at startup alongside
    /// <see cref="SetWorkResolver"/>. Optional: without it those changes stay in-memory only.
    /// </summary>
    public void SetDescriptorStateWriter(IJobDescriptorStateWriter writer) => _stateWriter = writer;

    /// <summary>
    /// Find the live queue entry for a job id. Superseded entries (an older epoch left behind by
    /// <see cref="ChangePriority"/>) are skipped. Caller must hold <see cref="_queueLock"/>.
    /// </summary>
    private QueuedJob? FindLiveEntry(JobRecord record)
    {
        foreach (var (entry, _) in _pendingQueue.UnorderedItems)
        {
            if (!ReferenceEquals(entry.Record, record)) continue;
            if (_reprioritized.TryGetValue(record.Id, out var live) && entry.Epoch != live) continue;
            return entry;
        }
        return null;
    }

    /// <summary>
    /// Enqueue a job by IDENTITY rather than by closure. The queue then retains only
    /// (runName, taskId, priority) plus the tracking record — no captured run graph, no captured task,
    /// no delegate — and the work is rehydrated at dispatch time by the registered resolver.
    ///
    /// The job id is the caller-supplied <paramref name="name"/>, which is already unique per
    /// (run, task); descriptor jobs deliberately do NOT mint a Guid-suffixed id, because that second
    /// string was measured to be the single largest per-job allocation in the queue.
    /// </summary>
    public string Enqueue(JobDescriptor descriptor, string name, string? id = null)
    {
        var jobId = id ?? name;
        var record = new JobRecord
        {
            Id = jobId,
            Name = name,
            RunName = descriptor.RunName,
            Priority = descriptor.Priority,
            Status = "Queued",
            QueuedUtc = DateTime.UtcNow
        };

        // Assign, do not TryAdd. Descriptor job ids are STABLE — "{runName}-{taskId}" — so re-enqueueing
        // a task collides with its own previous record, and TryAdd resolves that by keeping the OLD one
        // and discarding this one. The work still runs (it goes on the queue below, and the dispatcher
        // writes status onto the queue item's own record), so the TRACKED record sat frozen at the
        // previous outing's "Completed" while a live copy of the job was queued or running.
        //
        // IsQueuedOrRunning reads this dictionary, and JobQueuePump.ReleaseFinishedAsync treats a "no"
        // as permission to DELETE that task's durable queue row. A stale record therefore had the pump
        // dropping rows out from under running work — observed live releasing 7-9 "finished" jobs per
        // second against 8 slots. RedrivePendingTasks consults the same predicate, so it was misreading
        // task state for the same reason.
        _jobs[jobId] = record;

        lock (_queueLock)
        {
            _pendingQueue.Enqueue(new QueuedJob(record, null, descriptor), descriptor.Priority);
        }
        _itemAvailable.Release();

        _logger.LogDebug("[JobManager] Enqueued descriptor: {Name} P{Priority} run={Run} ({Queued} queued, {Active} active)",
            name, descriptor.Priority, descriptor.RunName, QueuedCount, _activeCount);

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
            // Ownership of this iteration's limiter slot: set when AcquireAsync returns, cleared once the
            // slot is released or handed to RunJobAsync's finally. Lets the handlers below tell "never
            // acquired" from "acquired and now orphaned".
            var slotHeld = false;

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
                slotHeld = true;

                // 3. Dequeue highest priority item (lowest int wins)
                QueuedJob? job;
                lock (_queueLock)
                {
                    _pendingQueue.TryDequeue(out job, out _);
                }

                if (job == null)
                {
                    _limiter.ReleaseSlot();
                    slotHeld = false;
                    continue;
                }

                // Superseded by a ChangePriority re-enqueue — the live entry is elsewhere in the queue.
                if (_reprioritized.TryGetValue(job.Record.Id, out var liveEpoch) && job.Epoch != liveEpoch)
                {
                    _limiter.ReleaseSlot();
                    slotHeld = false;
                    continue;
                }

                // Skip cancelled jobs — release the slot and move on.
                // The work ref must go too: CancelJob only marks the id, so leaving the entry here
                // stranded the captured closure (and everything it captured) in _pendingWork forever —
                // nothing else ever removes it, not even CleanupOldJobs.
                if (_cancelledJobIds.TryRemove(job.Record.Id, out _))
                {
                    _pendingWork.TryRemove(job.Record.Id, out _);
                    _limiter.ReleaseSlot();
                    slotHeld = false;
                    continue;
                }

                // Remove from pending work ref (no longer re-prioritizable)
                _pendingWork.TryRemove(job.Record.Id, out _);

                Interlocked.Increment(ref _activeCount);

                // 4. Hand the job to the thread pool — see invariant 1 on the class. This loop must get
                //    straight back to _itemAvailable/AcquireAsync; running RunJobAsync inline lets any
                //    synchronous block inside the job stop dispatch for the whole process.
                //
                //    RunJobAsync's finally owns the slot from here. If the handoff itself fails that
                //    finally never runs, so this iteration's finally has to return the slot.
                try
                {
                    _ = Task.Run(() => RunJobAsync(job, stoppingToken), CancellationToken.None);
                    slotHeld = false; // RunJobAsync's finally owns the slot from here.
                }
                catch (Exception ex)
                {
                    // slotHeld stays set — the finally below returns it.
                    Interlocked.Decrement(ref _activeCount);
                    job.Record.Status = "Failed";
                    job.Record.LastError = ex.Message;
                    job.Record.CompletedUtc = DateTime.UtcNow;
                    _logger.LogError(ex, "[JobManager] Could not dispatch: {Name}", job.Record.Name);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                // Anything else — a throw out of the queue lock, the limiter or the semaphore — used to
                // escape ExecuteAsync and silently end dispatch for the life of the process. One bad
                // iteration must not stop every future job.
                _logger.LogError(ex, "[JobManager] Dispatch iteration failed; continuing");
            }
            finally
            {
                // Acquired but never handed over: release rather than carry an untracked slot forward.
                if (slotHeld) _limiter.ReleaseSlot();
            }
        }

        _logger.LogInformation("[JobManager] Dispatch loop stopped ({Active} active, {Queued} queued)",
            _activeCount, QueuedCount);
    }

    private async Task RunJobAsync(QueuedJob job, CancellationToken ct)
    {
        // The operation-context setup below used to sit ABOVE the try. The dispatch loop acquires the slot
        // and this method's finally releases it, so a throw up there orphaned the slot permanently — and
        // silently, because the caller discards this task. Setup that cannot run is a failed job, not a
        // lost slot.
        IDisposable? opScope = null;

        try
        {
            // Set operation context for traceability — ExecuteScript reads RunName from this.
            // Priority is the value nested enqueues should inherit: for descriptor jobs (orchestrator
            // activities) that is the task's own queue priority; for closures it is only set when the
            // enqueuer said so (post-exec passes the run's priority). Plain starters expose none —
            // their job priority orders the starter script, not the work it spawns.
            var parentInvocation = new OperationContext.Invocation(job.Record.Name)
            {
                RunName = job.Record.RunName,
                Priority = job.Descriptor != null ? job.Record.Priority : job.InheritPriority,
                Category = "Job"
            };
            opScope = OperationContext.Set(parentInvocation);

            job.Record.Status = "Running";
            job.Record.StartedUtc = DateTime.UtcNow;

            var queueTime = job.Record.StartedUtc.Value - job.Record.QueuedUtc;
            if (queueTime.TotalSeconds > 1)
            {
                _logger.LogInformation(
                    "[JobManager] Started: {Name} P{Priority} after {QueueSec:F1}s wait ({Active} active)",
                    job.Record.Name, job.Record.Priority, queueTime.TotalSeconds, _activeCount);
            }

            // Rehydrate descriptor jobs here — on the thread pool, never on the dispatch loop.
            var work = job.Work ?? await ResolveWorkAsync(job.Descriptor!.Value, ct);

            if (work == null)
            {
                // The task is gone or already terminal (e.g. finalized by crash recovery while queued).
                // Not a failure: the queue entry is simply stale.
                job.Record.Status = "Skipped";
                job.Record.CompletedUtc = DateTime.UtcNow;
                _logger.LogDebug("[JobManager] Skipped stale descriptor: {Name}", job.Record.Name);
                return;
            }

            await work(ct);

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
            // Accounting first, context teardown second: ReleaseSlot cannot throw, Dispose can.
            Interlocked.Decrement(ref _activeCount);
            Interlocked.Increment(ref _totalProcessed);
            _limiter.ReleaseSlot();
            opScope?.Dispose();
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

    /// <summary>
    /// Cancel a queued job (running jobs cannot be cancelled via this method).
    /// Descriptor jobs are also cancelled durably, so recovery does not re-queue them.
    /// </summary>
    public bool CancelJob(string jobId)
    {
        if (!_jobs.TryGetValue(jobId, out var record)) return false;
        if (record.Status != "Queued") return false;

        record.Status = "Cancelled";
        record.CompletedUtc = DateTime.UtcNow;
        record.LastError = "Cancelled by user";
        _cancelledJobIds.TryAdd(jobId, true);

        JobDescriptor? descriptor;
        lock (_queueLock) descriptor = FindLiveEntry(record)?.Descriptor;
        if (descriptor is { } d)
            NotifyStateWriter(w => w.Cancelled(d), "cancellation", record.Name);

        _logger.LogInformation("[JobManager] Cancelled: {Name} ({Id})", record.Name, jobId);
        return true;
    }

    /// <summary>Cancel all queued jobs in a run group.</summary>
    public int CancelRun(string runName)
    {
        var toCancel = _jobs.Values.Where(j => j.RunName == runName && j.Status == "Queued").ToList();
        if (toCancel.Count == 0) return 0;

        // Collect descriptors in ONE pass under the lock — a scan per job would be O(n²) on a run
        // whose queue depth is in the hundreds.
        var descriptors = new List<JobDescriptor>(toCancel.Count);
        lock (_queueLock)
        {
            var wanted = toCancel.ToDictionary(r => r.Id, r => r);
            foreach (var (entry, _) in _pendingQueue.UnorderedItems)
            {
                if (entry.Descriptor is not { } d) continue;
                if (!wanted.TryGetValue(entry.Record.Id, out var rec)) continue;
                if (!ReferenceEquals(entry.Record, rec)) continue;
                if (_reprioritized.TryGetValue(rec.Id, out var live) && entry.Epoch != live) continue;
                descriptors.Add(d);
            }
        }

        foreach (var record in toCancel)
        {
            record.Status = "Cancelled";
            record.CompletedUtc = DateTime.UtcNow;
            record.LastError = "Run cancelled by user";
            _cancelledJobIds.TryAdd(record.Id, true);
        }

        foreach (var d in descriptors)
            NotifyStateWriter(w => w.Cancelled(d), "cancellation", $"{d.RunName}-{d.TaskId}");

        _logger.LogInformation("[JobManager] Cancelled run {Run}: {Count} jobs", runName, toCancel.Count);
        return toCancel.Count;
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
    /// The PriorityQueue doesn't support re-ordering, so the job is re-enqueued at the new priority and
    /// the superseded entry is retired by epoch when it reaches the front.
    ///
    /// Epochs, not the cancelled-id set, because that set is keyed by job id and cannot tell the two
    /// entries apart: the previous implementation added the id, re-enqueued, then removed the id again
    /// so the NEW entry would not be skipped — which left the OLD entry live as well, and the job ran
    /// twice. <see cref="_reprioritized"/> is only populated when a job is actually reprioritized, so
    /// the normal path costs one missing lookup and no memory.
    /// </summary>
    public bool ChangePriority(string jobId, int newPriority)
    {
        if (!_jobs.TryGetValue(jobId, out var record)) return false;
        if (record.Status != "Queued") return false;

        QueuedJob? superseded;
        lock (_queueLock)
        {
            superseded = FindLiveEntry(record);

            if (superseded == null)
            {
                // Already dispatched from the queue — display-only update.
                record.Priority = newPriority;
                _logger.LogInformation("[JobManager] Priority updated (display only): {Name} → P{Priority}",
                    record.Name, newPriority);
                return true;
            }

            // Re-enqueue carries the same payload as the entry it supersedes — descriptor jobs stay
            // descriptor jobs rather than being downgraded to a closure.
            var epoch = _reprioritized.AddOrUpdate(jobId, 1, (_, e) => e + 1);
            record.Priority = newPriority;
            _pendingQueue.Enqueue(superseded with { Epoch = epoch }, newPriority);
        }
        _itemAvailable.Release();

        // Persist outside the queue lock — the sink writes through to storage.
        if (superseded.Descriptor is { } descriptor)
            NotifyStateWriter(w => w.PriorityChanged(descriptor, newPriority), "priority change", record.Name);

        _logger.LogInformation("[JobManager] Reprioritized: {Name} → P{Priority}", record.Name, newPriority);
        return true;
    }

    /// <summary>
    /// Invoke the durability sink without letting a storage failure fail the operator's in-memory
    /// action — the queue has already been reordered / the job already marked cancelled, and throwing
    /// here would report failure for a change that did take effect in this process.
    /// </summary>
    private void NotifyStateWriter(Action<IJobDescriptorStateWriter> action, string what, string jobName)
    {
        var writer = _stateWriter;
        if (writer == null) return;
        try
        {
            action(writer);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[JobManager] Failed to persist {What} for {Name} — it will not survive a restart",
                what, jobName);
        }
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

    /// <summary>
    /// Turn a descriptor into runnable work. Throws if no resolver is registered — a descriptor that
    /// cannot be resolved must surface as a failed job, not a silently dropped one.
    /// </summary>
    private async Task<Func<CancellationToken, Task>?> ResolveWorkAsync(JobDescriptor descriptor, CancellationToken ct)
    {
        var resolver = _resolver
            ?? throw new InvalidOperationException(
                $"No JobWorkResolver registered — cannot dispatch descriptor {descriptor.RunName}/{descriptor.TaskId}");

        return await resolver(descriptor, ct);
    }

    // ── Internal Types ──

    /// <summary>
    /// A queue entry carries EITHER an inline closure (simple scheduled scripts, post-execution) OR a
    /// descriptor to rehydrate (orchestrator fan-out, which is where the depth actually comes from).
    /// </summary>
    private sealed record QueuedJob(JobRecord Record, Func<CancellationToken, Task>? Work, JobDescriptor? Descriptor)
    {
        /// <summary>Bumped by <see cref="ChangePriority"/>; entries below the live epoch are superseded.</summary>
        public int Epoch { get; init; }

        /// <summary>Priority nested enqueues should inherit (closure jobs only — see Enqueue).</summary>
        public int? InheritPriority { get; init; }
    }
}

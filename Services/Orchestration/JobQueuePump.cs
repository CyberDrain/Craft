using Craft.Configuration;
using Craft.Storage;

namespace Craft.Orchestration;

/// <summary>
/// Keeps the in-memory job queue small by feeding it from storage a batch at a time.
///
/// The point is that the backlog lives in the table, not in this process: the JobManager holds at most
/// a worker-pool-sized buffer plus whatever is in flight, and tops up only when it runs low. A run of
/// 7,336 tasks is 7,336 rows in storage and a handful of objects here.
///
/// Deliberately a separate pump rather than a change to the dispatch loop. That loop owns the limiter
/// slot lifecycle, and its invariants were written to close a leak that wedged a production instance for
/// 28 hours (see BackgroundTaskLimiter's accounting notes). Feeding it through the enqueue path it
/// already has means none of that is disturbed — this component can only ever add work.
///
/// Lease handling is what makes the claim safe to hold in memory: a claimed row stays owned by this
/// instance for LeaseSeconds, renewed while the job is still in flight, and reclaimable by anyone once
/// it lapses. An instance that dies mid-batch gives its work back without anything having to notice.
/// </summary>
public class JobQueuePump : BackgroundService
{
    private readonly ILogger<JobQueuePump> _logger;
    private readonly JobQueueStore _queue;
    private readonly JobManager _jobs;
    private readonly string _owner;
    private readonly int _batchSize;
    private readonly int _lowWater;
    private readonly TimeSpan _lease;
    private readonly TimeSpan _pollInterval;
    private readonly TimeSpan _idlePollInterval;

    /// <summary>Renew a claim only once its lease has less than this left. The lease is set comfortably
    /// longer than a task can run, so most claims finish without ever needing a renewal; this is the tail
    /// (a deep buffer, or a genuinely long task) that has been held long enough to approach expiry.</summary>
    private readonly TimeSpan _renewWhenWithin;

    /// <summary>Rows claimed by this instance, by the job id they were handed to the JobManager under.</summary>
    private readonly Dictionary<string, JobQueueStore.ClaimedJob> _inFlight = new(StringComparer.Ordinal);

    /// <summary>UTC lease expiry per in-flight job id, so renewal can skip claims with plenty of lease
    /// left instead of re-reading every row every tick.</summary>
    private readonly Dictionary<string, DateTime> _leaseExpiry = new(StringComparer.Ordinal);

    public JobQueuePump(ILogger<JobQueuePump> logger, JobQueueStore queue, JobManager jobs,
        IConfiguration configuration, CraftSettings settings)
    {
        _logger = logger;
        _queue = queue;
        _jobs = jobs;

        // Identifies this instance's claims. The container id is stable for the life of the process and
        // distinct per instance, which is exactly the scope a lease needs.
        _owner = Environment.GetEnvironmentVariable("HOSTNAME")
                 ?? $"instance-{Environment.ProcessId}";

        // One batch per worker, so a refill hands the pool exactly enough to stay busy.
        _batchSize = Math.Max(1, configuration.GetValue("JobQueueBatchSize", Math.Max(1, settings.Worker.BgPoolSize)));

        // Top up before the buffer empties, so workers never wait on a storage round-trip.
        _lowWater = Math.Max(0, configuration.GetValue("JobQueueLowWaterMark", 2));

        // Comfortably longer than the 1200s task timeout: a lease that lapses under a task still running
        // would hand its work to a second worker.
        _lease = TimeSpan.FromSeconds(Math.Max(60, configuration.GetValue("JobQueueLeaseSeconds", 1800)));

        // Renew in the last third of the lease. Below the lease length by construction, so a claim is
        // never renewed on the same tick it was taken.
        _renewWhenWithin = TimeSpan.FromTicks(_lease.Ticks / 3);

        _pollInterval = TimeSpan.FromMilliseconds(
            Math.Max(100, configuration.GetValue("JobQueuePollIntervalMs", 1000)));

        // Ceiling for the idle backoff. Clamped to at least the base interval, or "backing off" would
        // speed the loop up.
        _idlePollInterval = TimeSpan.FromMilliseconds(Math.Max(
            _pollInterval.TotalMilliseconds,
            configuration.GetValue("JobQueueIdlePollIntervalMs", 10_000)));
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation(
            "[JobQueuePump] Started: owner={Owner} batch={Batch} lowWater={Low} lease={Lease}s poll={Poll}ms idlePoll={IdlePoll}ms",
            _owner, _batchSize, _lowWater, _lease.TotalSeconds,
            _pollInterval.TotalMilliseconds, _idlePollInterval.TotalMilliseconds);

        var idleTicks = 0;

        while (!stoppingToken.IsCancellationRequested)
        {
            var claimedAny = false;
            try
            {
                // Ensure the queue schema is migrated before this pump ever claims. It is a cheap bool
                // check after the first success; before it, claiming could hand out a row the one-time
                // key migration is still rewriting. Idempotent and shared with the enqueue paths.
                await _queue.InitializeAsync(stoppingToken);

                await ReleaseFinishedAsync(stoppingToken);
                claimedAny = await RefillAsync(stoppingToken);
                await RenewAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                // One bad cycle must not end the pump; the next tick tries again. A pump that dies
                // silently would look exactly like an empty queue.
                //
                // The log itself is guarded: under a pegged GC hard limit even LogError allocates and can
                // throw OOM, and this catch is the last frame before ExecuteAsync — an escape here faults
                // the service and, with the host's default StopHost behaviour, restarts the container
                // mid-run. A failed log is never worth the pump. (BackgroundTaskLimiter guards its logs
                // past the point of commitment for the same reason.)
                try { _logger.LogError(ex, "[JobQueuePump] Cycle failed; continuing"); }
                catch { /* logging is never worth the loop */ }
            }

            // Idle means this pump has nothing: it claimed nothing AND holds nothing. Note what is
            // deliberately NOT idle — a full buffer. RefillAsync returns early without claiming while
            // QueuedCount is above the low-water mark, and treating that as idle would back the loop off
            // exactly when a busy run is about to need its next batch. A refill delivers at most
            // batchSize jobs per tick, so the poll interval is a hard throughput ceiling of
            // batchSize/interval: at a flat 10s that is 0.8 tasks/sec, which on a 7,336-task fan-out is
            // hours of pure waiting however fast the tasks are. Backing off only when genuinely empty
            // keeps that ceiling at the base interval whenever it could bind.
            if (claimedAny || _inFlight.Count > 0) idleTicks = 0;
            else idleTicks++;

            // Wait for the poll interval OR an enqueue signal, whichever comes first. The signal is what
            // makes a freshly-queued run start now instead of on the next tick — a cold system had backed
            // the interval off toward its idle ceiling, so without this the first task of a quiet-time
            // orchestration waited up to that ceiling just to be claimed. The interval stays as the
            // backstop (a missed signal, cross-instance work, freed leases), so this only removes the wait.
            try { await _queue.WaitForWorkAsync(NextDelay(idleTicks), stoppingToken); }
            catch (OperationCanceledException) { break; }
        }

        _logger.LogInformation("[JobQueuePump] Stopped ({InFlight} claims still held)", _inFlight.Count);
    }

    /// <summary>
    /// Drop the queue rows for jobs the JobManager has finished with.
    ///
    /// Removal is deliberately AFTER the work is done, not at claim time: a row deleted on claim would
    /// take the task with it if this instance died holding it. Until then the lease is what stops anyone
    /// else running it.
    /// </summary>
    private async Task ReleaseFinishedAsync(CancellationToken ct)
    {
        if (_inFlight.Count == 0) return;

        var finished = _inFlight.Where(kv => !_jobs.IsQueuedOrRunning(kv.Key)).ToList();
        if (finished.Count == 0) return;

        // One transaction per partition rather than two point deletes per task. Tracking is cleared only
        // after the storage delete lands, so a failure here throws, the cycle guard logs it, and the same
        // claims are retried next tick (the delete is idempotent — a row already gone is tolerated).
        await _queue.RemoveBatchAsync(finished.Select(kv => kv.Value).ToList(), ct);

        foreach (var (jobId, _) in finished)
        {
            _inFlight.Remove(jobId);
            _leaseExpiry.Remove(jobId);
        }

        _logger.LogDebug("[JobQueuePump] Released {Count} finished job(s)", finished.Count);
    }

    /// <summary>
    /// How long to wait before the next cycle: the base interval while there is anything to do, doubling
    /// toward <see cref="_idlePollInterval"/> once the pump has gone quiet.
    ///
    /// The doubling matters more than the ceiling — it means a queue that goes briefly empty between
    /// batches barely slows down, while one that is empty for minutes stops scanning storage every
    /// second. Any tick that claims or holds work resets it, so the pump is back at full speed on the
    /// cycle after work appears.
    /// </summary>
    private TimeSpan NextDelay(int idleTicks)
    {
        if (idleTicks <= 0) return _pollInterval;

        // Clamped before the shift so the multiplier cannot overflow on a long idle stretch.
        var factor = 1L << Math.Min(idleTicks, 20);
        var ms = Math.Min(_idlePollInterval.TotalMilliseconds, _pollInterval.TotalMilliseconds * factor);
        return TimeSpan.FromMilliseconds(ms);
    }

    /// <summary>
    /// Claim another batch once the buffer has drawn down to the low-water mark. Returns whether
    /// anything was claimed, which is what tells the loop this tick was not idle.
    /// </summary>
    private async Task<bool> RefillAsync(CancellationToken ct)
    {
        if (_jobs.QueuedCount > _lowWater) return false;

        var claimed = await _queue.ClaimBatchAsync(_owner, _batchSize, _lease, ct);
        if (claimed.Count == 0) return false;

        var leaseExpiry = DateTime.UtcNow + _lease;
        foreach (var job in claimed)
        {
            // Enqueued by identity, the way the orchestrator already does it, so the work is rebuilt at
            // dispatch time and nothing but the descriptor is retained here.
            var name = $"{job.RunName}-{job.TaskId}";
            var jobId = _jobs.Enqueue(new JobDescriptor(job.RunName, job.TaskId, job.Priority), name);
            _inFlight[jobId] = job;
            // Slightly earlier than the lease storage actually recorded (claimed a moment before this),
            // so renewal errs toward being early rather than late.
            _leaseExpiry[jobId] = leaseExpiry;
        }

        _logger.LogDebug("[JobQueuePump] Claimed {Count} job(s) ({Queued} queued after refill)",
            claimed.Count, _jobs.QueuedCount);

        return true;
    }

    /// <summary>
    /// Extend the lease on everything still in flight. A failure here means a claim lapsed and the work
    /// may already have been taken by someone else, so it is logged loudly — but not acted on, because
    /// the task itself is the JobManager's to finish or fail.
    /// </summary>
    private async Task RenewAsync(CancellationToken ct)
    {
        if (_inFlight.Count == 0) return;

        // Only the claims whose lease is actually running low. Everything else has ample lease left and
        // does not need a storage round-trip this tick — the old code re-read every in-flight row every
        // tick regardless of how much lease remained.
        var now = DateTime.UtcNow;
        var dueIds = _inFlight.Keys
            .Where(id => !_leaseExpiry.TryGetValue(id, out var expiry) || expiry - now < _renewWhenWithin)
            .ToList();
        if (dueIds.Count == 0) return;

        var due = dueIds.Select(id => _inFlight[id]).ToList();
        if (!await _queue.RenewAsync(due, _owner, _lease, ct))
        {
            _logger.LogWarning("[JobQueuePump] One or more leases could not be renewed — work may have been reclaimed");
            return;
        }

        var renewedExpiry = now + _lease;
        foreach (var id in dueIds) _leaseExpiry[id] = renewedExpiry;
    }
}

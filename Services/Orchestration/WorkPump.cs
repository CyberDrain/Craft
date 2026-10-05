using Craft.Configuration;
using Craft.Storage;

namespace Craft.Orchestration;

/// <summary>
/// Keeps the JobManager's buffer topped up from storage. Each refill reads the Ready list (best band first,
/// oldest run first), claims from those runs' partitions until the batch is full, and hands the claims to the
/// JobManager as descriptors. A run with nothing claimable is skipped for a while rather than read every tick.
///
/// One process works the queue: the pump claims nothing until it holds the instance lock (a single row, renewed
/// every few seconds and released on shutdown), so a recycle never has two processes claiming at once. While it
/// holds the lock, any claim in storage owned by another process belongs to one that has stopped, and is taken
/// back the first time its run is read. The pump holds no other state that matters after a crash.
/// </summary>
public class WorkPump : BackgroundService
{
    private readonly ILogger<WorkPump> _logger;
    private readonly WorkStore _store;
    private readonly JobManager _jobs;
    private readonly OrchestratorService? _orchestrator;
    private readonly string _owner;
    private readonly int _batchSize;
    private readonly int _lowWater;
    private readonly TimeSpan _lease;
    private readonly TimeSpan _pollInterval;
    private readonly TimeSpan _idlePollInterval;
    private readonly Task? _claimGate;

    /// <summary>
    /// Runs the pump has written off for now, so they cost no storage read until something can have changed.
    ///
    /// A run whose pending tasks ran out (its remaining work is running, or it waits on child runs) can only
    /// have claimable work again when its counts move (a task or child finishes, its aggregation falls due),
    /// when a claim is released back to pending, or when a claim lapses unrenewed. So it is skipped until its
    /// Ready counts change, a release names it, or its earliest claim's lease runs out. Without this, runs
    /// waiting at the head of a large queue spend the per-refill read budget over and over and starve every run
    /// behind them.
    ///
    /// A run that came back empty for another reason (a lost race, a busy sequential driver) is skipped for
    /// 30 s, doubling each time it is found empty again, up to 15 minutes, until its counts move.
    /// </summary>
    private readonly Dictionary<string, (int Done, int Total, DateTime Until, int Strikes)> _skip = new(StringComparer.Ordinal);
    private readonly System.Collections.Concurrent.ConcurrentQueue<string> _changed = new();
    private static readonly TimeSpan EmptyBackoff = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan MaxEmptyBackoff = TimeSpan.FromMinutes(15);

    /// <summary>Runs read from storage per refill. Skipped runs cost nothing.</summary>
    private const int MaxRunsReadPerRefill = 32;

    /// <summary>Ready rows per page: the whole list of a normal instance in one request, and few requests when a
    /// large backlog has to be scanned past.</summary>
    private const int ReadyPageSize = 1000;

    /// <summary>The instance lock: how long it is held for without renewal, and how often it is renewed.</summary>
    private readonly TimeSpan _lockLease;
    private readonly TimeSpan _lockRenewEvery;
    private DateTime _lockRenewedAt;
    private volatile bool _holdsLock;

    /// <summary>Whether this pump holds the instance lock (and so may treat other owners' claims as dead).</summary>
    internal bool HoldsLock => _holdsLock;

    /// <summary>How long a run may sit with its creation unfinished before the startup repair removes it.</summary>
    private static readonly TimeSpan AbandonUnfinishedCreation = TimeSpan.FromMinutes(10);

    /// <summary>Claims handed to the JobManager, by job id, with when their lease runs out.</summary>
    private readonly Dictionary<string, (WorkStore.ClaimedTask Claim, DateTime LeaseUntil)> _inFlight = new(StringComparer.Ordinal);

    public WorkPump(ILogger<WorkPump> logger, WorkStore store, JobManager jobs, IConfiguration configuration,
        CraftSettings settings, OrchestratorService? orchestrator = null)
    {
        _logger = logger;
        _store = store;
        _jobs = jobs;
        _orchestrator = orchestrator;
        _claimGate = orchestrator?.RecoveryDone;
        _owner = orchestrator?.Owner ?? OrchestratorService.NewOwnerId();
        _batchSize = Math.Max(1, configuration.GetValue("JobQueueBatchSize", Math.Max(1, settings.Worker.BgPoolSize)));
        _lowWater = Math.Max(0, configuration.GetValue("JobQueueLowWaterMark", 2));
        _lease = orchestrator?.Lease ?? TimeSpan.FromSeconds(Math.Max(60, configuration.GetValue("JobQueueLeaseSeconds", 1800)));
        _pollInterval = TimeSpan.FromMilliseconds(Math.Max(100, configuration.GetValue("JobQueuePollIntervalMs", 1000)));
        _idlePollInterval = TimeSpan.FromMilliseconds(Math.Max(_pollInterval.TotalMilliseconds,
            configuration.GetValue("JobQueueIdlePollIntervalMs", 10_000)));
        _lockLease = TimeSpan.FromSeconds(Math.Max(5, configuration.GetValue("InstanceLockSeconds", 30)));
        _lockRenewEvery = _lockLease / 3;
        _store.RunChanged += runKey =>
        {
            _changed.Enqueue(runKey);
            Wake();
        };
        _jobs.Dispatched += () =>
        {
            if (_jobs.QueuedCount <= _lowWater) Wake();
        };
    }

    /// <summary>
    /// Refill now rather than at the next poll: the JobManager's buffer has drained to the low-water mark, or a
    /// run was created or moved on in this process. Wakes are coalesced to one refill per
    /// <see cref="MinRefillGap"/>, so a burst of finishes does not re-read the Ready list for each one.
    /// </summary>
    private void Wake()
    {
        try
        {
            if (_wake.CurrentCount == 0) _wake.Release();
        }
        catch (SemaphoreFullException) { }
    }

    private readonly SemaphoreSlim _wake = new(0, 1);
    private static readonly TimeSpan MinRefillGap = TimeSpan.FromMilliseconds(50);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("[WorkPump] Started: owner={Owner} batch={Batch} lowWater={Low} lease={Lease}s",
            _owner, _batchSize, _lowWater, _lease.TotalSeconds);

        try
        {
            if (_claimGate is { IsCompleted: false }) await _claimGate.WaitAsync(stoppingToken);
            await AcquireLockAsync(stoppingToken);
        }
        catch (OperationCanceledException) { return; }
        _ = RepairAsync(stoppingToken);

        var idleTicks = 0;
        while (!stoppingToken.IsCancellationRequested)
        {
            var claimed = 0;
            try
            {
                if (!await KeepLockAsync(stoppingToken)) await AcquireLockAsync(stoppingToken);
                claimed = await RefillAsync(stoppingToken);
                await RenewAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception ex)
            {
                try { _logger.LogError(ex, "[WorkPump] Cycle failed; continuing"); } catch { }
            }

            idleTicks = claimed > 0 || _inFlight.Count > 0 ? 0 : idleTicks + 1;
            var delay = idleTicks == 0
                ? _pollInterval
                : TimeSpan.FromMilliseconds(Math.Min(_idlePollInterval.TotalMilliseconds,
                    _pollInterval.TotalMilliseconds * (1L << Math.Min(idleTicks, 20))));
            if (delay > _lockRenewEvery) delay = _lockRenewEvery;
            try
            {
                if (await _wake.WaitAsync(delay, stoppingToken))
                {
                    idleTicks = 0;
                    await Task.Delay(MinRefillGap, stoppingToken);
                }
            }
            catch (OperationCanceledException) { break; }
        }
    }

    internal void ForgetBackoff() => _skip.Clear();

    /// <summary>
    /// Wait until this process holds the instance lock. A predecessor that shut down cleanly released it, so this
    /// is immediate after a normal recycle; one that crashed holds it until its lease runs out.
    /// </summary>
    internal async Task AcquireLockAsync(CancellationToken ct)
    {
        string? waitingOn = null;
        while (true)
        {
            var (held, holder) = await _store.TryHoldInstanceLockAsync(_owner, _lockLease, ct);
            if (held)
            {
                _holdsLock = true;
                _lockRenewedAt = DateTime.UtcNow;
                _logger.LogInformation("[WorkPump] Holding the instance lock as {Owner}; claims of any other process are taken back on sight",
                    _owner);
                return;
            }
            if (holder?.Owner != waitingOn)
            {
                waitingOn = holder?.Owner;
                _logger.LogInformation("[WorkPump] Waiting for the instance lock, held by {Holder} until {Until:O}",
                    holder?.Owner, holder?.LeaseUntil);
            }
            var wait = (holder?.LeaseUntil ?? DateTimeOffset.UtcNow) - DateTimeOffset.UtcNow;
            await Task.Delay(wait < TimeSpan.FromSeconds(1) ? TimeSpan.FromSeconds(1) : wait > _lockRenewEvery ? _lockRenewEvery : wait, ct);
        }
    }

    /// <summary>Renew the instance lock when due. False when it was lost (a renewal failed long enough for
    /// another process to take it): claiming stops until it is held again.</summary>
    internal async Task<bool> KeepLockAsync(CancellationToken ct)
    {
        if (!_holdsLock) return false;
        if (DateTime.UtcNow - _lockRenewedAt < _lockRenewEvery) return true;
        try
        {
            var (held, holder) = await _store.TryHoldInstanceLockAsync(_owner, _lockLease, ct);
            if (held)
            {
                _lockRenewedAt = DateTime.UtcNow;
                return true;
            }
            _holdsLock = false;
            _logger.LogCritical("[WorkPump] Lost the instance lock to {Holder}; claiming stops until it is held again", holder?.Owner);
            return false;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // A storage blip: keep claiming while the lease we hold is still good, and say so if it is not.
            if (DateTime.UtcNow - _lockRenewedAt < _lockLease) return true;
            _holdsLock = false;
            _logger.LogCritical(ex, "[WorkPump] Could not renew the instance lock before it ran out; claiming stops until it is held again");
            return false;
        }
    }

    private int _repairing;
    private int _indexFailuresSeen;

    /// <summary>
    /// Rebuild the indexes from the active-run list: once when the lock is first held, and again whenever an index
    /// write has failed for good since the last pass (so a run whose Ready entry never landed is relisted now, not
    /// at the next restart). Runs beside claiming; one pass at a time.
    /// </summary>
    private async Task RepairAsync(CancellationToken ct)
    {
        if (Interlocked.Exchange(ref _repairing, 1) == 1) return;
        _indexFailuresSeen = _store.IndexFailures;
        try
        {
            var r = await _store.RepairIndexesAsync(AbandonUnfinishedCreation, ct);
            _logger.LogInformation("[WorkPump] Index repair: {Active} active run(s), {Relisted} relisted, {Retired} retired, {Removed} unfinished creation(s) removed, {Young} still being created",
                r.Active, r.Relisted, r.Retired, r.Removed, r.Young);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "[WorkPump] Index repair failed; it runs again at the next start, or via RepairIndexes");
        }
        finally
        {
            Volatile.Write(ref _repairing, 0);
        }
    }

    /// <summary>The background repair started by the last refill that saw a failed index write; tests await it.</summary>
    internal Task? LastRepair { get; private set; }

    /// <summary>The pump's notion of now, for its backoff and renewal timing; tests replace it.</summary>
    internal Func<DateTime> Clock { get; set; } = () => DateTime.UtcNow;

    /// <summary>Stop tracking claims the JobManager is done with. Their finish (or release) was written by the job.</summary>
    private void Forget()
    {
        foreach (var id in _inFlight.Keys.Where(id => !_jobs.IsQueuedOrRunning(id)).ToList())
            _inFlight.Remove(id);
    }

    /// <summary>
    /// Claim until the buffer holds a batch. Returns how many tasks were claimed.
    ///
    /// A run's concurrency limit is applied here, against the claims this process holds: only one Craft
    /// instance works the tables, so what it holds is what is running. A claim left by a process that has
    /// since died is not running, so it rightly does not count. During an overlapping restart, two processes
    /// could briefly run up to the limit each.
    /// </summary>
    internal async Task<int> RefillAsync(CancellationToken ct)
    {
        Forget();
        while (_changed.TryDequeue(out var changedRun)) _skip.Remove(changedRun);
        if (_store.IndexFailures != _indexFailuresSeen && Volatile.Read(ref _repairing) == 0) LastRepair = RepairAsync(ct);
        if (_jobs.QueuedCount > _lowWater) return 0;
        var need = _batchSize - _jobs.QueuedCount;
        var claimed = 0;
        var now = Clock();
        var read = 0;
        Dictionary<string, int>? held = null;

        await foreach (var entry in _store.ReadReadyAsync(ReadyPageSize, ct))
        {
            if (need <= 0 || read >= MaxRunsReadPerRefill) break;
            var progressed = true;
            if (_skip.TryGetValue(entry.RunKey, out var skip) && skip.Done == entry.Done && skip.Total == entry.Total)
            {
                if (skip.Until > now) continue;
                progressed = false;
            }

            var want = need;
            if (entry.MaxConcurrency > 0 && !entry.Sequential)
            {
                held ??= _inFlight.Values.GroupBy(v => v.Claim.RunKey).ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);
                want = Math.Min(need, entry.MaxConcurrency - held.GetValueOrDefault(entry.RunKey));
                if (want <= 0) continue;
            }
            read++;

            var header = await _store.GetRunAsync(entry.RunKey, ct);
            if (header == null || header.IsFinished)
            {
                await _store.DropReadyAsync(entry, ct);
                continue;
            }

            IReadOnlyList<WorkStore.ClaimedTask> claims;
            var probe = new WorkStore.ClaimProbe();
            if (header.Sequential)
            {
                var step = await _store.ClaimSequentialAsync(header.RunKey, _owner, _lease, othersAreDead: _holdsLock, ct: ct);
                claims = step == null ? [] : [step];
            }
            else
            {
                claims = await _store.ClaimAsync(header.RunKey, want, _owner, _lease, reclaimExpired: true, probe, othersAreDead: _holdsLock, ct);
            }

            if (probe.PendingExhausted)
            {
                // A lapsing claim is the one change that bumps no count; look again when the earliest lease is due.
                _skip[header.RunKey] = (entry.Done, entry.Total, probe.EarliestLeaseUntil?.UtcDateTime ?? DateTime.MaxValue, 0);
            }
            else if (claims.Count == 0)
            {
                var strikes = progressed ? 0 : skip.Strikes + 1;
                var backoff = TimeSpan.FromTicks(Math.Min(MaxEmptyBackoff.Ticks, EmptyBackoff.Ticks << Math.Min(strikes, 10)));
                _skip[header.RunKey] = (entry.Done, entry.Total, now + backoff, strikes);
                continue;
            }
            else
            {
                _skip.Remove(header.RunKey);
            }
            if (claims.Count == 0) continue;

            foreach (var c in claims)
            {
                var name = c.Seq == WorkStore.AggregateSeq ? $"{header.Name}-PostExec" : $"{header.Name}-{c.TaskId}";
                var descriptor = new JobDescriptor(header.Name, c.TaskId, header.Priority) { RunKey = c.RunKey, Seq = c.Seq, Attempt = c.Attempt };
                var jobId = _jobs.Enqueue(descriptor, name, id: $"{c.RunKey}|{c.Seq}");
                _inFlight[jobId] = (c, now + _lease);
                if (held != null) held[c.RunKey] = held.GetValueOrDefault(c.RunKey) + 1;
            }
            need -= claims.Count;
            claimed += claims.Count;
        }

        // ponytail: forgetting every mark costs one read per run on the next pass; prune by Ready membership if that ever shows up.
        if (_skip.Count > 50_000) _skip.Clear();

        return claimed;
    }

    /// <summary>Renew claims in their last third, so a long buffer wait or a long task never loses its lease.</summary>
    internal async Task RenewAsync(CancellationToken ct)
    {
        var now = Clock();
        var due = _inFlight.Where(kv => kv.Value.LeaseUntil - now < _lease / 3).ToList();
        if (due.Count == 0) return;

        var lost = await _store.RenewAsync(due.Select(kv => kv.Value.Claim).ToList(), _owner, _lease, ct);
        if (lost.Count > 0)
            _logger.LogWarning("[WorkPump] {Count} claim(s) were taken back before renewal — their leases had lapsed", lost.Count);
        foreach (var (id, v) in due) _inFlight[id] = (v.Claim, now + _lease);
    }

    /// <summary>On shutdown, hand back claims that never started so another process can run them now.</summary>
    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        await base.StopAsync(cancellationToken);
        foreach (var (id, v) in _inFlight.ToList())
        {
            if (_jobs.GetJobs().FirstOrDefault(j => j.Id == id) is not { Status: "Queued" }) continue;
            try { await _store.ReleaseAsync(v.Claim.RunKey, v.Claim.Seq, _owner, refundAttempt: true, cancellationToken); }
            catch (Exception ex) { _logger.LogDebug(ex, "[WorkPump] Could not release {Job} on shutdown", id); }
        }
        if (_holdsLock)
        {
            // Hold the lock while this process still runs claimed tasks: a successor holding it would take those
            // claims back as dead and run them a second time. If shutdown is cut short, the lock simply lapses.
            try
            {
                while (_jobs.GetJobs(status: "Running").Any(j => _inFlight.ContainsKey(j.Id)))
                {
                    await KeepLockAsync(cancellationToken);
                    await Task.Delay(250, cancellationToken);
                }
            }
            catch (OperationCanceledException) { return; }
            try { await _store.ReleaseInstanceLockAsync(_owner, cancellationToken); }
            catch (Exception ex) { _logger.LogDebug(ex, "[WorkPump] Could not release the instance lock on shutdown"); }
            _holdsLock = false;
        }
    }
}

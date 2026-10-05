using Craft.Configuration;
using Craft.Storage;

namespace Craft.Orchestration;

/// <summary>
/// Keeps the JobManager's buffer topped up from storage. Each refill reads the Ready list (best band first,
/// oldest run first), claims from those runs' partitions until the batch is full, and hands the claims to the
/// JobManager as descriptors. A run with nothing claimable is skipped for a while rather than read every tick.
///
/// The pump holds no state that matters after a crash: claims it held lapse and are claimed again, by this
/// process or any other.
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

    /// <summary>How long a run that had nothing claimable is skipped while its counts stand still, and how often a
    /// run's lapsed leases are looked for.</summary>
    private static readonly TimeSpan EmptyBackoff = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan ExpiredCheckInterval = TimeSpan.FromSeconds(60);

    /// <summary>Runs read from storage per refill. Skipped runs cost nothing, so a head of runs that are all waiting
    /// (on children, or on their own running tasks) cannot hide the runs behind it.</summary>
    private const int MaxRunsReadPerRefill = 32;
    private const int ReadyPageSize = 100;

    private readonly Dictionary<string, (DateTime Until, int Done, int Total)> _emptyUntil = new(StringComparer.Ordinal);
    private readonly Dictionary<string, DateTime> _expiredCheckedAt = new(StringComparer.Ordinal);

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
        _owner = orchestrator?.Owner ?? Environment.GetEnvironmentVariable("HOSTNAME") ?? $"instance-{Environment.ProcessId}";
        _batchSize = Math.Max(1, configuration.GetValue("JobQueueBatchSize", Math.Max(1, settings.Worker.BgPoolSize)));
        _lowWater = Math.Max(0, configuration.GetValue("JobQueueLowWaterMark", 2));
        _lease = orchestrator?.Lease ?? TimeSpan.FromSeconds(Math.Max(60, configuration.GetValue("JobQueueLeaseSeconds", 1800)));
        _pollInterval = TimeSpan.FromMilliseconds(Math.Max(100, configuration.GetValue("JobQueuePollIntervalMs", 1000)));
        _idlePollInterval = TimeSpan.FromMilliseconds(Math.Max(_pollInterval.TotalMilliseconds,
            configuration.GetValue("JobQueueIdlePollIntervalMs", 10_000)));
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("[WorkPump] Started: owner={Owner} batch={Batch} lowWater={Low} lease={Lease}s",
            _owner, _batchSize, _lowWater, _lease.TotalSeconds);

        if (_claimGate is { IsCompleted: false })
        {
            try { await _claimGate.WaitAsync(stoppingToken); }
            catch (OperationCanceledException) { return; }
        }

        var idleTicks = 0;
        while (!stoppingToken.IsCancellationRequested)
        {
            var claimed = 0;
            try
            {
                Forget();
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
            try { await Task.Delay(delay, stoppingToken); }
            catch (OperationCanceledException) { break; }
        }
    }

    internal void ForgetBackoff() => _emptyUntil.Clear();

    /// <summary>Stop tracking claims the JobManager is done with. Their finish (or release) was written by the job.</summary>
    private void Forget()
    {
        foreach (var id in _inFlight.Keys.Where(id => !_jobs.IsQueuedOrRunning(id)).ToList())
            _inFlight.Remove(id);
    }

    /// <summary>Claim until the buffer holds a batch. Returns how many tasks were claimed.</summary>
    internal async Task<int> RefillAsync(CancellationToken ct)
    {
        if (_jobs.QueuedCount > _lowWater) return 0;
        var need = _batchSize - _jobs.QueuedCount;
        var claimed = 0;
        var now = DateTime.UtcNow;
        var read = 0;

        await foreach (var entry in _store.ReadReadyAsync(ReadyPageSize, ct))
        {
            if (need <= 0 || read >= MaxRunsReadPerRefill) break;
            if (_emptyUntil.TryGetValue(entry.RunKey, out var skip) && skip.Until > now
                && skip.Done == entry.Done && skip.Total == entry.Total) continue;
            read++;

            var header = await _store.GetRunAsync(entry.RunKey, ct);
            if (header == null || header.IsFinished)
            {
                await _store.DropReadyAsync(entry, ct);
                continue;
            }

            IReadOnlyList<WorkStore.ClaimedTask> claims;
            if (header.Sequential)
            {
                var step = await _store.ClaimSequentialAsync(header.RunKey, _owner, _lease, ct: ct);
                claims = step == null ? [] : [step];
            }
            else
            {
                var checkExpired = !_expiredCheckedAt.TryGetValue(header.RunKey, out var at) || now - at >= ExpiredCheckInterval;
                if (checkExpired) _expiredCheckedAt[header.RunKey] = now;
                claims = await _store.ClaimAsync(header.RunKey, need, _owner, _lease, checkExpired, ct);
            }

            if (claims.Count == 0)
            {
                _emptyUntil[header.RunKey] = (now + EmptyBackoff, entry.Done, entry.Total);
                continue;
            }
            _emptyUntil.Remove(header.RunKey);

            foreach (var c in claims)
            {
                var name = c.Seq == WorkStore.AggregateSeq ? $"{header.Name}-PostExec" : $"{header.Name}-{c.TaskId}";
                var descriptor = new JobDescriptor(header.Name, c.TaskId, header.Priority) { RunKey = c.RunKey, Seq = c.Seq, Attempt = c.Attempt };
                var jobId = _jobs.Enqueue(descriptor, name);
                _inFlight[jobId] = (c, now + _lease);
            }
            need -= claims.Count;
            claimed += claims.Count;
        }

        if (_emptyUntil.Count > 10_000)
            foreach (var key in _emptyUntil.Where(kv => kv.Value.Until <= now).Select(kv => kv.Key).ToList())
            {
                _emptyUntil.Remove(key);
                _expiredCheckedAt.Remove(key);
            }

        return claimed;
    }

    /// <summary>Renew claims in their last third, so a long buffer wait or a long task never loses its lease.</summary>
    private async Task RenewAsync(CancellationToken ct)
    {
        var now = DateTime.UtcNow;
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
    }
}

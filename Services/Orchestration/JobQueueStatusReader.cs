using Craft.Services;
using Craft.Storage;

namespace Craft.Orchestration;

/// <summary>
/// The table-backed view of the job queue, for the status APIs.
///
/// Since ownership of queued tasks moved into the {prefix}Queue table, the in-memory JobManager holds
/// only a worker-pool-sized buffer of claims plus whatever closure jobs were enqueued directly. Every
/// consumer that used to read it as "the queue" — the worker-health page, /API/jobs/*, the stats
/// history — was therefore reporting the buffer as if it were the backlog: a 7,000-task fan-out showed
/// eight queued jobs. This type merges the two truths: the JobManager for what THIS instance is doing,
/// the tables for what exists.
///
/// Reads are cached with a short TTL and refreshed single-flight, because the queue scan is
/// proportional to the backlog and the snapshot consumers poll — the stats sampler on its timer, the
/// dashboard at a few hertz, the perf harness at 4 Hz. One scan per TTL window serves all of them.
/// A refresh failure keeps the previous snapshot: stale numbers with an honest timestamp beat an
/// exception on a health endpoint.
///
/// THE TTL ADAPTS TO WHAT THE SCAN ACTUALLY COSTS, and that is not a refinement. The snapshot used to
/// be stamped with the time the refresh STARTED, so on a large backlog an 80-second scan produced a
/// snapshot already 80 seconds past a 5-second TTL the moment it was stored. The next poll — the
/// worker-health page sits at a few hertz — saw it stale and started another. The single-flight gate
/// kept it to one at a time, but they ran back to back, permanently, against the largest table in the
/// account, on the instance least able to afford it. Measured on the instance that motivated this:
/// full scans of a 743,000-row queue looping continuously, alongside 12,028 per-run counter reads
/// each (see BuildSnapshotAsync). A customer reporting sluggishness had "kept the Worker Health page
/// open" — which is what was driving it.
///
/// So: stamp on completion, and never re-scan more often than the last scan took.
/// </summary>
public class JobQueueStatusReader : IDisposable
{
    private readonly ILogger<JobQueueStatusReader> _logger;
    private readonly JobManager _jobs;
    private readonly JobQueueStore _queue;
    private readonly OrchestratorTableStore _store;

    private static readonly TimeSpan DefaultTtl = TimeSpan.FromSeconds(5);

    /// <summary>
    /// How many runs <see cref="GetRunSummariesAsync"/> will resolve counter rows for. Each is a point
    /// read, cheap alone and not in the thousands. A run without one falls back to local numbers, which
    /// is the same graceful path a pre-counter run already takes — so this degrades detail, not
    /// correctness. Truncation is logged rather than silent.
    /// </summary>
    private const int MaxCounterLookups = 200;

    private volatile QueueSnapshot? _cached;
    private readonly SemaphoreSlim _refreshGate = new(1, 1);

    /// <summary>
    /// How long the last successful snapshot took to build. The floor under the effective TTL, so a
    /// scan that costs 80s is not re-run 5s later. Volatile read/write of a long via Interlocked —
    /// ticks rather than TimeSpan so it stays atomic.
    /// </summary>
    private long _lastBuildTicks;

    public JobQueueStatusReader(ILogger<JobQueueStatusReader> logger, JobManager jobs,
        JobQueueStore queue, OrchestratorTableStore store)
    {
        _logger = logger;
        _jobs = jobs;
        _queue = queue;
        _store = store;
    }

    /// <summary>The underlying durable queue, for callers that need its maintenance operations.</summary>
    public JobQueueStore Queue => _queue;

    /// <summary>
    /// Per-run slice of the durable queue, derived entirely from the rows the scan already returned.
    ///
    /// Deliberately carries no counter data. The counter lives in a different table, partitioned per
    /// run, so folding it in here meant one point read per distinct run on EVERY refresh — 12,028 of
    /// them on the instance that motivated this — even though only <see cref="GetRunSummariesAsync"/>
    /// ever read those fields. The two paths that poll hardest, <see cref="GetSummaryAsync"/> and
    /// <see cref="GetJobDetailsAsync"/>, never used them at all.
    /// </summary>
    public sealed record RunQueueInfo(int Unclaimed, int Claimed, int MinPriority,
        DateTime? OldestQueuedUtc);

    /// <summary>One scan of the queue table, aggregated the way the status APIs consume it.</summary>
    public sealed record QueueSnapshot(DateTime TakenUtc, IReadOnlyList<JobQueueStore.QueuedRow> Rows,
        int Unclaimed, int Claimed, DateTime? OldestUnclaimedUtc,
        IReadOnlyDictionary<string, RunQueueInfo> ByRun)
    {
        public int Total => Rows.Count;
        public double AgeSeconds => (DateTime.UtcNow - TakenUtc).TotalSeconds;
    }

    /// <summary>
    /// The most recent snapshot without ever blocking on storage — for the paths that must stay cheap
    /// and non-blocking (GetSnapshot on a PS worker, the stats sampler, the 4 Hz allocation poll).
    /// A stale or missing snapshot kicks off a background refresh and returns what exists NOW; the
    /// refreshed data is simply what the next call sees.
    /// </summary>
    /// <summary>
    /// The shortest interval we will re-scan at: never less than the requested age, and never less than
    /// the last scan took. On a healthy instance the scan is milliseconds and this is just the 5s TTL;
    /// on a degraded one it is what stops the refresh loop from consuming the storage account.
    /// </summary>
    private TimeSpan EffectiveTtl(TimeSpan? maxAge)
    {
        var requested = maxAge ?? DefaultTtl;
        var lastBuild = TimeSpan.FromTicks(Interlocked.Read(ref _lastBuildTicks));
        return lastBuild > requested ? lastBuild : requested;
    }

    public QueueSnapshot? GetCached(TimeSpan? maxAge = null)
    {
        var cached = _cached;
        if (cached == null || DateTime.UtcNow - cached.TakenUtc > EffectiveTtl(maxAge))
        {
            _ = Task.Run(() => GetAsync(maxAge, CancellationToken.None));
        }
        return cached;
    }

    /// <summary>
    /// A snapshot no older than <paramref name="maxAge"/>, refreshing if needed. Returns the previous
    /// snapshot when the refresh fails, and null only when storage has never answered at all.
    /// </summary>
    public async Task<QueueSnapshot?> GetAsync(TimeSpan? maxAge = null, CancellationToken ct = default)
    {
        var ttl = EffectiveTtl(maxAge);
        var cached = _cached;
        if (cached != null && DateTime.UtcNow - cached.TakenUtc <= ttl) return cached;

        await _refreshGate.WaitAsync(ct);
        try
        {
            // Re-check under the gate, against a TTL recomputed AFTER the wait: whoever held the gate
            // may also have just told us how expensive this scan is now.
            cached = _cached;
            if (cached != null && DateTime.UtcNow - cached.TakenUtc <= EffectiveTtl(maxAge)) return cached;

            var startedUtc = DateTime.UtcNow;
            var snapshot = await BuildSnapshotAsync(ct);
            Interlocked.Exchange(ref _lastBuildTicks, (DateTime.UtcNow - startedUtc).Ticks);
            _cached = snapshot;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[JobQueueStatus] Queue snapshot refresh failed — serving previous data");
        }
        finally
        {
            _refreshGate.Release();
        }

        return _cached;
    }

    /// <summary>
    /// One scan of the queue table, aggregated. No per-run storage reads: everything here comes from
    /// the rows the scan already returned, so the cost is one scan regardless of how many runs the
    /// backlog spans.
    /// </summary>
    private async Task<QueueSnapshot> BuildSnapshotAsync(CancellationToken ct)
    {
        var rows = await _queue.ListQueuedAsync(ct);

        var unclaimed = 0;
        DateTime? oldestUnclaimed = null;
        var byRun = new Dictionary<string, RunQueueInfo>(StringComparer.Ordinal);

        foreach (var group in rows.GroupBy(r => r.RunName))
        {
            var runUnclaimed = 0;
            var runClaimed = 0;
            var minPriority = int.MaxValue;
            DateTime? oldest = null;

            foreach (var row in group)
            {
                if (row.Claimed) runClaimed++;
                else
                {
                    runUnclaimed++;
                    if (oldest == null || row.QueuedUtc < oldest) oldest = row.QueuedUtc;
                }
                if (row.Priority < minPriority) minPriority = row.Priority;
            }

            unclaimed += runUnclaimed;
            if (oldest != null && (oldestUnclaimed == null || oldest < oldestUnclaimed))
                oldestUnclaimed = oldest;

            byRun[group.Key] = new RunQueueInfo(runUnclaimed, runClaimed,
                minPriority == int.MaxValue ? 0 : minPriority, oldest);
        }

        // Stamped on COMPLETION, not on entry. A scan that took longer than the TTL would otherwise
        // return a snapshot that is already expired, and the next poll would start another immediately.
        return new QueueSnapshot(DateTime.UtcNow, rows, unclaimed, rows.Count - unclaimed,
            oldestUnclaimed, byRun);
    }

    // ─── Merged views ───

    /// <summary>
    /// The JobManager summary with the durable backlog folded in: Queued counts local jobs PLUS
    /// unclaimed queue rows (claimed rows are already represented by local records on the instance
    /// that holds them), and OldestQueuedUtc considers both sources.
    /// </summary>
    public async Task<JobSummary> GetSummaryAsync(CancellationToken ct = default)
    {
        var summary = _jobs.GetSummary();
        summary.QueuedLocal = summary.Queued;

        var snap = await GetAsync(ct: ct);
        if (snap == null) return summary;

        summary.QueuedDurable = snap.Unclaimed;
        summary.Queued += snap.Unclaimed;

        if (snap.OldestUnclaimedUtc is { } oldest
            && (summary.OldestQueuedUtc == null || oldest < summary.OldestQueuedUtc))
        {
            summary.OldestQueuedUtc = oldest;
        }

        return summary;
    }

    /// <summary>
    /// The job listing the worker-health page shows: local records merged with the unclaimed durable
    /// backlog. Durable rows only participate when the status filter admits "Queued" — every other
    /// status describes work an instance has already claimed, which local records cover.
    /// </summary>
    public async Task<List<JobDetail>> GetJobDetailsAsync(string? runName = null, string? status = null,
        int limit = 100, CancellationToken ct = default)
    {
        var local = _jobs.GetJobDetails(runName, status, limit);

        var includeDurable = string.IsNullOrEmpty(status)
            || status.Equals("Queued", StringComparison.OrdinalIgnoreCase);
        if (!includeDurable) return local;

        var snap = await GetAsync(ct: ct);
        if (snap == null || snap.Rows.Count == 0) return local;

        var now = DateTime.UtcNow;
        var merged = new List<JobDetail>(local);

        foreach (var row in snap.Rows)
        {
            // Claimed rows are queued or running inside some instance's JobManager; on this instance
            // they are the local records already in the list. The id guard closes the enqueue/claim
            // race window on top of that.
            if (row.Claimed) continue;
            if (!string.IsNullOrEmpty(runName)
                && !string.Equals(row.RunName, runName, StringComparison.OrdinalIgnoreCase)) continue;

            var id = $"{row.RunName}-{row.TaskId}";
            if (_jobs.IsQueuedOrRunning(id)) continue;

            merged.Add(new JobDetail
            {
                Id = id,
                Name = id,
                RunName = row.RunName,
                Priority = row.Priority,
                Status = "Queued",
                QueuedUtc = row.QueuedUtc,
                WaitSeconds = Math.Max(0, (now - row.QueuedUtc).TotalSeconds),
            });
        }

        // Same ordering contract as JobManager.GetJobDetails, re-applied across the merged set.
        return merged
            .OrderBy(j => j.Priority)
            .ThenBy(j => j.QueuedUtc)
            .Take(limit)
            .ToList();
    }

    /// <summary>
    /// Run summaries with durable truth folded in: Total from the run's counter row, Queued including
    /// the unclaimed backlog, Completed at least the durably-terminal count. Runs that exist only in
    /// the table — a backlog nothing here has claimed yet — get a synthesized entry, because a run the
    /// page cannot see is a run nobody can cancel.
    /// </summary>
    public async Task<List<JobRunSummary>> GetRunSummariesAsync(CancellationToken ct = default)
    {
        var summaries = _jobs.GetRunSummaries();
        var snap = await GetAsync(ct: ct);
        if (snap == null) return summaries;

        var byName = summaries.ToDictionary(s => s.Name, StringComparer.Ordinal);

        foreach (var (run, info) in snap.ByRun)
        {
            if (byName.TryGetValue(run, out var summary))
            {
                summary.Queued += info.Unclaimed;
            }
            else
            {
                // Claimed rows here belong to another instance's buffer — not distinguishable from
                // queued at this distance, and not terminal, so Queued is the honest bucket.
                var synthesized = new JobRunSummary
                {
                    Name = run,
                    Priority = info.MinPriority,
                    Queued = info.Unclaimed + info.Claimed,
                    Total = info.Unclaimed + info.Claimed,
                };
                summaries.Add(synthesized);
                byName[run] = synthesized;
            }
        }

        // Counter rows, resolved HERE rather than in the snapshot, because this is the only caller that
        // reads them. Each is a point read on the run's own task partition.
        //
        // The counter is a run's true size and durable progress — a restart or a multi-instance claim
        // pattern otherwise shrinks Total to whatever this node happened to process. It covers runs with
        // queue rows and runs whose backlog is fully claimed alike, which is why this is one pass over
        // the active summaries rather than two loops keyed off the snapshot.
        var ordered = summaries
            .OrderBy(r => r.Priority)
            .ThenByDescending(r => r.StartedUtc)
            .ToList();

        var active = ordered.Where(s => s.Queued + s.Running > 0).ToList();
        var lookups = Math.Min(active.Count, MaxCounterLookups);

        for (var i = 0; i < lookups; i++)
        {
            try
            {
                if (await _store.GetCounterAsync(active[i].Name, ct) is { } counter)
                    Overlay(active[i], counter.Remaining, counter.Total);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "[JobQueueStatus] Counter read failed for {Run}", active[i].Name);
            }
        }

        if (active.Count > lookups)
        {
            // Said out loud: a silent cap here would read as "these runs have no durable progress"
            // rather than "we did not look", and the two are indistinguishable in the UI.
            _logger.LogDebug(
                "[JobQueueStatus] Resolved counters for {Looked} of {Active} active runs (cap {Cap}) — " +
                "the remainder report local numbers only",
                lookups, active.Count, MaxCounterLookups);
        }

        return ordered;
    }

    public void Dispose()
    {
        GC.SuppressFinalize(this);
        _refreshGate.Dispose();
    }

    /// <summary>
    /// Fold a run's counter into its summary. Total is authoritative when present. The durably-terminal
    /// count (Total − Remaining) spans Completed, Failed and Cancelled across every instance and every
    /// restart; local Failed is kept (it is a lower bound) and the rest raises Completed.
    /// </summary>
    private static void Overlay(JobRunSummary summary, int? remaining, int? total)
    {
        if (total is not { } t || remaining is not { } r) return;

        if (t > summary.Total) summary.Total = t;

        var durablyDone = Math.Max(0, t - r);
        summary.Completed = Math.Max(summary.Completed, durablyDone - summary.Failed);
    }
}

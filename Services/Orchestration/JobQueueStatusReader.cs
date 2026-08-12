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
/// </summary>
public class JobQueueStatusReader : IDisposable
{
    private readonly ILogger<JobQueueStatusReader> _logger;
    private readonly JobManager _jobs;
    private readonly JobQueueStore _queue;
    private readonly OrchestratorTableStore _store;

    private static readonly TimeSpan DefaultTtl = TimeSpan.FromSeconds(5);

    private volatile QueueSnapshot? _cached;
    private readonly SemaphoreSlim _refreshGate = new(1, 1);

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

    /// <summary>Per-run slice of the durable queue plus that run's counter row, when it has one.</summary>
    public sealed record RunQueueInfo(int Unclaimed, int Claimed, int MinPriority,
        DateTime? OldestQueuedUtc, int? Remaining, int? Total);

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
    public QueueSnapshot? GetCached(TimeSpan? maxAge = null)
    {
        var cached = _cached;
        if (cached == null || DateTime.UtcNow - cached.TakenUtc > (maxAge ?? DefaultTtl))
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
        var ttl = maxAge ?? DefaultTtl;
        var cached = _cached;
        if (cached != null && DateTime.UtcNow - cached.TakenUtc <= ttl) return cached;

        await _refreshGate.WaitAsync(ct);
        try
        {
            // Re-check under the gate: a concurrent caller may have refreshed while we waited.
            cached = _cached;
            if (cached != null && DateTime.UtcNow - cached.TakenUtc <= ttl) return cached;

            _cached = await BuildSnapshotAsync(ct);
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

    private async Task<QueueSnapshot> BuildSnapshotAsync(CancellationToken ct)
    {
        var takenUtc = DateTime.UtcNow;
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

            // The counter row is the run's true size and durable progress — the queue rows alone only
            // say what has not been claimed yet. Best-effort per run: a missing counter (pre-counter
            // run) simply leaves those fields null and the consumer falls back to local numbers.
            (int Remaining, int Total)? counter = null;
            try { counter = await _store.GetCounterAsync(group.Key, ct); }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "[JobQueueStatus] Counter read failed for {Run}", group.Key);
            }

            byRun[group.Key] = new RunQueueInfo(runUnclaimed, runClaimed,
                minPriority == int.MaxValue ? 0 : minPriority, oldest,
                counter?.Remaining, counter?.Total);
        }

        return new QueueSnapshot(takenUtc, rows, unclaimed, rows.Count - unclaimed, oldestUnclaimed, byRun);
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
                Overlay(summary, info.Remaining, info.Total);
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
                Overlay(synthesized, info.Remaining, info.Total);
                summaries.Add(synthesized);
            }
        }

        // A run whose backlog is fully claimed has no queue rows, but its counter still knows the true
        // size — a restart or a multi-instance claim pattern otherwise shrinks Total to whatever this
        // node happened to process. Only active runs; finished ones are locally complete records.
        foreach (var summary in summaries)
        {
            if (summary.Queued + summary.Running == 0) continue;
            if (snap.ByRun.ContainsKey(summary.Name)) continue;

            try
            {
                if (await _store.GetCounterAsync(summary.Name, ct) is { } counter)
                    Overlay(summary, counter.Remaining, counter.Total);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "[JobQueueStatus] Counter read failed for {Run}", summary.Name);
            }
        }

        return summaries
            .OrderBy(r => r.Priority)
            .ThenByDescending(r => r.StartedUtc)
            .ToList();
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

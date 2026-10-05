using Craft.Services;
using Craft.Storage;

namespace Craft.Orchestration;

/// <summary>
/// The status APIs' view of queued work: the JobManager for what this process holds, the Ready list for what
/// exists durably. One Ready scan (one small row per active run) feeds every count, and only the head of the
/// queue is read row by row, so a snapshot costs the same whatever the backlog. Snapshots are cached and
/// re-scanned no more often than four times the last scan took.
/// </summary>
public class JobQueueStatusReader : IDisposable
{
    private readonly ILogger<JobQueueStatusReader> _logger;
    private readonly JobManager _jobs;
    private readonly WorkStore _store;

    private static readonly TimeSpan DefaultTtl = TimeSpan.FromSeconds(5);

    /// <summary>Rows listed from the head of the queue; runs past it are counted, not listed.</summary>
    internal const int HeadRows = 2_000;
    private const int HeadRuns = 50;

    private volatile QueueSnapshot? _cached;
    private readonly SemaphoreSlim _refreshGate = new(1, 1);
    private long _lastBuildTicks;

    public JobQueueStatusReader(ILogger<JobQueueStatusReader> logger, JobManager jobs, WorkStore store)
    {
        _logger = logger;
        _jobs = jobs;
        _store = store;
    }

    /// <summary>A task waiting in storage, as the job listings show it.</summary>
    /// <summary>A task waiting in storage, as the job listings show it; <see cref="RunKey"/> and <see cref="Seq"/>
    /// address its row, so acting on it is a point read.</summary>
    public sealed record QueuedRow(string RunName, string TaskId, int Priority, DateTime QueuedUtc, bool Claimed,
        string RunKey = "", int Seq = 0);

    /// <summary>A run name's durable counts (summed over runs sharing the name). Merged views subtract what this
    /// process holds right now, never the counts held when the snapshot was taken.</summary>
    public sealed record RunQueueInfo(int Unclaimed, int Claimed, int MinPriority, DateTime? OldestQueuedUtc,
        int Total, int Done, string? Reference, int Failed = 0)
    {
        public int Outstanding => Math.Max(0, Total - Done);
    }

    /// <summary>One Ready scan. <see cref="Rows"/> is the head of the queue only; the counts cover every run.</summary>
    public sealed record QueueSnapshot(DateTime TakenUtc, IReadOnlyList<QueuedRow> Rows, int Total, int Unclaimed,
        int Claimed, DateTime? OldestUnclaimedUtc, IReadOnlyDictionary<string, RunQueueInfo> ByRun)
    {
        public double AgeSeconds => (DateTime.UtcNow - TakenUtc).TotalSeconds;
    }

    private TimeSpan EffectiveTtl(TimeSpan? maxAge)
    {
        var requested = maxAge ?? DefaultTtl;
        var floor = TimeSpan.FromTicks(Interlocked.Read(ref _lastBuildTicks) * 4);
        return floor > requested ? floor : requested;
    }

    /// <summary>The latest snapshot without blocking; a stale one starts a refresh in the background.</summary>
    public QueueSnapshot? GetCached(TimeSpan? maxAge = null)
    {
        var cached = _cached;
        if (cached == null || DateTime.UtcNow - cached.TakenUtc > EffectiveTtl(maxAge))
            _ = Task.Run(() => GetAsync(maxAge, CancellationToken.None));
        return cached;
    }

    public async Task<QueueSnapshot?> GetAsync(TimeSpan? maxAge = null, CancellationToken ct = default)
    {
        var cached = _cached;
        if (cached != null && DateTime.UtcNow - cached.TakenUtc <= EffectiveTtl(maxAge)) return cached;

        await _refreshGate.WaitAsync(ct);
        try
        {
            cached = _cached;
            if (cached != null && DateTime.UtcNow - cached.TakenUtc <= EffectiveTtl(maxAge)) return cached;
            var started = DateTime.UtcNow;
            _cached = await BuildSnapshotAsync(ct);
            Interlocked.Exchange(ref _lastBuildTicks, (DateTime.UtcNow - started).Ticks);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
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
        // Local jobs are counted per run name; runs sharing a name take them oldest first.
        var local = _jobs.GetJobs().Where(j => j.RunName != null && j.Status is "Queued" or "Running")
            .GroupBy(j => j.RunName!).ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);

        var byRun = new Dictionary<string, RunQueueInfo>(StringComparer.Ordinal);
        var head = new List<QueuedRow>();
        int total = 0, unclaimed = 0, runsListed = 0;
        DateTime? oldest = null;

        await foreach (var e in _store.ReadReadyAsync(1000, ct))
        {
            var outstanding = Math.Max(0, e.Total - e.Done);
            var claimed = Math.Min(outstanding, local.GetValueOrDefault(e.Name));
            local[e.Name] = local.GetValueOrDefault(e.Name) - claimed;
            total += outstanding;
            unclaimed += outstanding - claimed;
            if (outstanding > claimed && (oldest == null || e.StartedUtc < oldest)) oldest = e.StartedUtc;
            byRun[e.Name] = byRun.TryGetValue(e.Name, out var same)
                ? new RunQueueInfo(same.Unclaimed + outstanding - claimed, same.Claimed + claimed, Math.Min(same.MinPriority, e.Band),
                    same.OldestQueuedUtc, same.Total + e.Total, same.Done + e.Done, same.Reference ?? e.Reference, same.Failed + e.Failed)
                : new RunQueueInfo(outstanding - claimed, claimed, e.Band, e.StartedUtc, e.Total, e.Done, e.Reference, e.Failed);

            if (head.Count < HeadRows && runsListed < HeadRuns && outstanding > claimed)
            {
                runsListed++;
                foreach (var t in await _store.GetTasksAsync(e.RunKey, 'P', HeadRows - head.Count, ct))
                    head.Add(new QueuedRow(e.Name, t.TaskId, e.Band, e.StartedUtc, false, e.RunKey, t.Seq));
            }
        }

        return new QueueSnapshot(DateTime.UtcNow, head, total, unclaimed, total - unclaimed, oldest, byRun);
    }

    // ─── Merged views ───

    /// <summary>The JobManager summary with the durable backlog folded in.</summary>
    public async Task<JobSummary> GetSummaryAsync(CancellationToken ct = default)
    {
        var summary = _jobs.GetSummary();
        summary.QueuedLocal = summary.Queued;
        var snap = await GetAsync(ct: ct);
        if (snap == null) return summary;

        // Waiting in storage = everything outstanding less what this process holds now (its queued and running
        // orchestrator jobs). Using the snapshot's own subtraction would count claims made since it was taken twice.
        var held = _jobs.GetJobs().Count(j => j.RunName != null && j.Status is "Queued" or "Running");
        summary.QueuedDurable = Math.Max(0, snap.Total - held);
        summary.Queued += summary.QueuedDurable;
        if (snap.OldestUnclaimedUtc is { } oldest && (summary.OldestQueuedUtc == null || oldest < summary.OldestQueuedUtc))
            summary.OldestQueuedUtc = oldest;
        return summary;
    }

    /// <summary>Local job records plus the head of the durable queue, for the job listing.</summary>
    public async Task<List<JobDetail>> GetJobDetailsAsync(string? runName = null, string? status = null,
        int limit = 100, CancellationToken ct = default)
    {
        var local = _jobs.GetJobDetails(runName, status, limit);
        if (!string.IsNullOrEmpty(status) && !status.Equals("Queued", StringComparison.OrdinalIgnoreCase)) return local;

        var snap = await GetAsync(ct: ct);
        if (snap == null || snap.Rows.Count == 0) return local;

        var now = DateTime.UtcNow;
        var merged = new List<JobDetail>(local);
        var held = _jobs.GetJobs().Where(j => j.Status is "Queued" or "Running").Select(j => j.Name).ToHashSet(StringComparer.Ordinal);
        foreach (var row in snap.Rows)
        {
            if (!string.IsNullOrEmpty(runName) && !string.Equals(row.RunName, runName, StringComparison.OrdinalIgnoreCase)) continue;
            var id = $"{row.RunName}-{row.TaskId}";
            if (held.Contains(id)) continue;
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
        return merged.OrderBy(j => j.Priority).ThenBy(j => j.QueuedUtc).Take(limit).ToList();
    }

    /// <summary>Run summaries: local job records with each active run's durable size and progress folded in.</summary>
    public async Task<List<JobRunSummary>> GetRunSummariesAsync(CancellationToken ct = default)
    {
        var summaries = _jobs.GetRunSummaries();
        var snap = await GetAsync(ct: ct);
        if (snap == null) return summaries;

        var byName = summaries.ToDictionary(s => s.Name, StringComparer.Ordinal);
        foreach (var (run, info) in snap.ByRun)
        {
            if (!byName.TryGetValue(run, out var summary))
            {
                summary = new JobRunSummary { Name = run, Priority = info.MinPriority, StartedUtc = info.OldestQueuedUtc };
                summaries.Add(summary);
                byName[run] = summary;
            }
            // Storage is the truth for a run still going: outstanding work is running here or waiting, and done
            // splits into completed and failed. Local job history can include earlier outings of the name.
            summary.Reference ??= info.Reference;
            summary.Total = info.Total;
            summary.Running = Math.Min(summary.Running, info.Outstanding);
            summary.Queued = info.Outstanding - summary.Running;
            summary.Failed = info.Failed;
            summary.Completed = Math.Max(0, info.Done - info.Failed);
            summary.CompletedUtc = null;
        }

        return summaries.OrderBy(r => r.Priority).ThenByDescending(r => r.StartedUtc).ToList();
    }

    public void Dispose()
    {
        GC.SuppressFinalize(this);
        _refreshGate.Dispose();
    }
}

using System.Globalization;
using Craft.Configuration;

namespace Craft.Storage;

/// <summary>
/// The durable job queue: one row per queued task, claimed in batches under a lease.
///
/// This exists so the dispatch side can hold a worker-pool-sized buffer instead of the whole backlog,
/// and so an edit to a queued task in storage is what actually runs — the in-memory copy is a buffer,
/// not the truth.
///
/// Key design, and it is doing real work:
///
///   PartitionKey "P04"                     — the priority bucket, zero-padded so it sorts numerically.
///   RowKey "{queuedTicks:D19}-{run}-{task}" — time-ordered within a bucket, unique by construction.
///
/// Azure Table returns rows ordered by partition key then row key, so a single unfiltered read yields
/// the highest-priority, oldest-first work FIRST, across every run. That is the cross-run priority
/// ordering the in-memory PriorityQueue provides today, for one round-trip and without probing each
/// bucket in turn — probing 16 buckets per refill would have cost more round-trips than the whole
/// batching exercise saves.
///
/// A claim is one conditional transaction over rows sharing a bucket: one round-trip per BATCH, not per
/// task. Guarded by each row's ETag, so an external edit — or another instance claiming first — makes
/// the claim fail rather than silently overwrite, and the caller re-reads.
/// </summary>
public class JobQueueStore
{
    private readonly ILogger<JobQueueStore> _logger;
    private readonly ICraftTableStore _store;
    private readonly string _queueTable;
    private bool _initialized;

    /// <summary>Priorities above this share the lowest bucket. Callers use 0-6; the cap only bounds the key.</summary>
    private const int MaxPriorityBucket = 99;

    public JobQueueStore(ILogger<JobQueueStore> logger, CraftSettings settings, ICraftTableStore store)
    {
        _logger = logger;
        _store = store;
        _queueTable = $"{settings.Orchestrator.TablePrefix}Queue";
    }

    public async Task InitializeAsync()
    {
        if (_initialized) return;
        await _store.EnsureTableAsync(_queueTable);
        _initialized = true;
    }

    internal static string Bucket(int priority) =>
        "P" + Math.Clamp(priority, 0, MaxPriorityBucket).ToString("D2", CultureInfo.InvariantCulture);

    internal static string BuildRowKey(DateTime queuedUtc, string runName, string taskId) =>
        // D19 so ticks sort lexically the way they sort numerically — without the padding a queue that
        // straddles a tick-digit boundary would silently reorder.
        $"{queuedUtc.Ticks.ToString("D19", CultureInfo.InvariantCulture)}-{runName}-{taskId}";

    /// <summary>Add one task to the queue. Idempotent for a given (queuedUtc, run, task).</summary>
    public Task EnqueueAsync(string runName, string taskId, int priority, DateTime queuedUtc,
        CancellationToken ct = default) =>
        _store.UpsertAsync(_queueTable, new StoreRow(Bucket(priority), BuildRowKey(queuedUtc, runName, taskId))
        {
            Properties =
            {
                ["RunName"] = runName,
                ["TaskId"] = taskId,
                ["Priority"] = priority,
                ["Owner"] = "",
                ["LeaseUntil"] = (DateTimeOffset?)null,
            }
        }, ct);

    /// <summary>Queue many tasks for one run. Chunked by the caller's priority into per-bucket batches.</summary>
    public async Task EnqueueBatchAsync(string runName, IReadOnlyList<(string TaskId, int Priority)> tasks,
        DateTime queuedUtc, CancellationToken ct = default)
    {
        foreach (var byBucket in tasks.GroupBy(t => Bucket(t.Priority)))
        {
            var rows = byBucket.Select(t => new StoreRow(byBucket.Key, BuildRowKey(queuedUtc, runName, t.TaskId))
            {
                Properties =
                {
                    ["RunName"] = runName,
                    ["TaskId"] = t.TaskId,
                    ["Priority"] = t.Priority,
                    ["Owner"] = "",
                    ["LeaseUntil"] = (DateTimeOffset?)null,
                }
            }).ToList();

            await _store.UpsertBatchAsync(_queueTable, byBucket.Key, rows, ct);
        }
    }

    /// <summary>A queued task this worker now owns, with the row key needed to release it.</summary>
    public sealed record ClaimedJob(string RunName, string TaskId, int Priority, string Bucket, string RowKey);

    /// <summary>
    /// Claim up to <paramref name="max"/> of the highest-priority, oldest queued tasks for
    /// <paramref name="owner"/>, for <paramref name="leaseFor"/>.
    ///
    /// One read plus one conditional transaction. The read stops as soon as it has a batch, so it costs
    /// a single page however deep the queue is; the transaction covers one bucket, because that is the
    /// unit a backend transaction can span.
    ///
    /// Returns empty when there is nothing claimable, and ALSO when another worker won the race — the
    /// caller simply tries again rather than forcing the write, which is what stops two workers running
    /// the same task.
    /// </summary>
    public async Task<IReadOnlyList<ClaimedJob>> ClaimBatchAsync(string owner, int max, TimeSpan leaseFor,
        CancellationToken ct = default)
    {
        if (max <= 0) return [];

        var now = DateTimeOffset.UtcNow;
        var candidates = new List<StoreRow>(max);
        string? bucket = null;

        // Ordered partition-then-row, so this walks highest priority first, oldest first within it.
        //
        // The filter is the same predicate as IsClaimable, pushed to the service so a backlog is not
        // paged to the client on every pump tick just to find the few free rows at its head. It is an
        // optimisation ONLY — a store that ignores it still returns everything — so IsClaimable below
        // stays as the authority. Nothing here may assume the filter was applied.
        await foreach (var row in _store.QueryTableAsync(_queueTable, ClaimableFilter(now), ct))
        {
            if (!IsClaimable(row, now)) continue;

            // A transaction cannot span partitions, so the batch is whatever the top bucket offers.
            bucket ??= row.PartitionKey;
            if (row.PartitionKey != bucket) break;

            candidates.Add(row);
            if (candidates.Count == max) break;
        }

        if (candidates.Count == 0) return [];

        var leaseUntil = now.Add(leaseFor);
        foreach (var row in candidates)
        {
            row["Owner"] = owner;
            row["LeaseUntil"] = leaseUntil;
        }

        if (!await _store.TryReplaceBatchAsync(_queueTable, bucket!, candidates, ct))
        {
            // Someone else got there first, or a row changed underneath us. Not an error: the caller
            // retries and takes whatever is genuinely free.
            _logger.LogDebug("[JobQueue] Claim of {Count} from {Bucket} lost the race", candidates.Count, bucket);
            return [];
        }

        return candidates.Select(r => new ClaimedJob(
            r.GetString("RunName") ?? "",
            r.GetString("TaskId") ?? "",
            r.GetInt32("Priority") ?? 0,
            r.PartitionKey,
            r.RowKey)).ToList();
    }

    /// <summary>
    /// Claimable means unowned, or owned under a lease that has expired.
    ///
    /// Lease expiry is what replaces the age-based re-drive: a worker that dies holding a claim gives the
    /// task back on its own, without anything having to notice the worker is gone.
    /// </summary>
    private static bool IsClaimable(StoreRow row, DateTimeOffset now)
    {
        if (string.IsNullOrEmpty(row.GetString("Owner"))) return true;

        var lease = row.GetDateTimeOffset("LeaseUntil");
        return lease == null || lease <= now;
    }

    /// <summary>
    /// The server-side half of <see cref="IsClaimable"/>: free rows, plus rows whose lease has run out.
    ///
    /// Enqueue writes Owner as an empty string and LeaseUntil as null, and a null property is simply
    /// absent from an Azure Tables entity — so a free row is matched by the Owner clause rather than by
    /// anything about LeaseUntil, which is why this does not try to express "LeaseUntil is null".
    ///
    /// One case is deliberately narrower than IsClaimable: a row with an Owner but NO LeaseUntil, which
    /// IsClaimable treats as claimable, is not matched here. No write path produces one — Owner and
    /// LeaseUntil are always set together, by the claim, the renewal and the release alike — so this
    /// costs nothing in practice, and IsClaimable keeps the defensive reading for anything that
    /// arrives through the unfiltered path.
    /// </summary>
    private static string ClaimableFilter(DateTimeOffset now) =>
        $"Owner eq '' or LeaseUntil lt datetime'{now.UtcDateTime:yyyy-MM-ddTHH:mm:ss.fffffffZ}'";

    /// <summary>Remove a finished task from the queue. A missing row is not an error — it is the normal
    /// result of a retry after the removal already landed.</summary>
    public Task RemoveAsync(ClaimedJob job, CancellationToken ct = default) =>
        _store.DeleteAsync(_queueTable, job.Bucket, job.RowKey, ct);

    /// <summary>
    /// Extend the lease on jobs still in flight. One transaction per bucket, so a full buffer costs one
    /// round-trip rather than one per job. Returns false if any renewal was rejected, which means the
    /// lease had already lapsed and the work may have been taken.
    /// </summary>
    public async Task<bool> RenewAsync(IReadOnlyList<ClaimedJob> jobs, string owner, TimeSpan leaseFor,
        CancellationToken ct = default)
    {
        if (jobs.Count == 0) return true;

        var leaseUntil = DateTimeOffset.UtcNow.Add(leaseFor);
        var ok = true;

        foreach (var group in jobs.GroupBy(j => j.Bucket))
        {
            var rows = new List<StoreRow>();
            foreach (var job in group)
            {
                var row = await _store.GetAsync(_queueTable, job.Bucket, job.RowKey, ct);
                // Gone means finished and removed; still ours means renewable. Anything else is not ours.
                if (row == null) continue;
                if (row.GetString("Owner") != owner) { ok = false; continue; }

                row["LeaseUntil"] = leaseUntil;
                rows.Add(row);
            }

            if (rows.Count > 0 && !await _store.TryReplaceBatchAsync(_queueTable, group.Key, rows, ct))
                ok = false;
        }

        return ok;
    }

    /// <summary>Drop every queued row for a run — used when a run is cancelled or cleaned up.</summary>
    public async Task RemoveRunAsync(string runName, CancellationToken ct = default)
    {
        await foreach (var row in _store.QueryTableAsync(_queueTable, RunFilter(runName), ct))
        {
            if (row.GetString("RunName") == runName)
                await _store.DeleteAsync(_queueTable, row.PartitionKey, row.RowKey, ct);
        }
    }

    /// <summary>
    /// Server-side narrowing for the run-scoped reads, matching the client-side RunName check each of
    /// them already applies.
    ///
    /// Unfiltered, every one of these pages the ENTIRE queue table to the client to find one run's rows,
    /// and they run on hot paths — once per finalize, once per resumed run, once per dispatch. A
    /// production log showed 19,339 unfiltered scans of this table in an hour, peaking at 1,028 in a
    /// single minute, with individual scans taking up to 200 seconds once the storage account began
    /// throttling them. The claim path is the one that starves when that happens, but these reads are
    /// what generate much of the volume.
    ///
    /// Narrowing only, on the same terms as ClaimableFilter: the callers keep their own RunName checks,
    /// so a store that ignores the filter still behaves correctly.
    /// </summary>
    private static string RunFilter(string runName) => $"RunName eq '{runName.Replace("'", "''")}'";

    /// <summary>
    /// Hand back every claim on a run's rows, making them immediately claimable again. Returns how many
    /// were released.
    ///
    /// For crash recovery only, where "this run was interrupted" already means the process that held
    /// these claims is gone. Without it a crash strands the run for up to the full lease: the rows are
    /// owned with a live LeaseUntil, so nothing can claim them, while re-dispatch correctly declines to
    /// write duplicates for tasks that already have rows. Seen on a killed 140-task fanout — 12 tasks sat
    /// Pending with 0 running for the remainder of a 30 minute lease.
    ///
    /// Rows are updated in place (same PartitionKey/RowKey), so this frees the existing row rather than
    /// adding another one.
    /// </summary>
    public async Task<int> ReleaseRunClaimsAsync(string runName, CancellationToken ct = default)
    {
        var released = 0;

        await foreach (var row in _store.QueryTableAsync(_queueTable, RunFilter(runName), ct))
        {
            if (row.GetString("RunName") != runName) continue;
            if (string.IsNullOrEmpty(row.GetString("Owner"))) continue;   // already free

            row["Owner"] = "";
            row["LeaseUntil"] = (DateTimeOffset?)null;
            await _store.UpsertAsync(_queueTable, row, ct);
            released++;
        }

        return released;
    }

    /// <summary>
    /// The task ids this run still has rows for, claimed or not.
    ///
    /// This is what tells a re-drive the difference between a task that is merely WAITING — Pending in
    /// the run graph, sitting in this queue, not yet claimed by the pump — and one whose row is
    /// genuinely gone. Under the pump, waiting is the normal state of a backlog: a 124-task run against
    /// eight workers has most of its tasks Pending and absent from the JobManager for minutes at a time.
    /// Treating that as orphaned re-queues the whole backlog on a timer, and because a RowKey is
    /// prefixed with the enqueue timestamp, each pass adds a SECOND row for the same task rather than
    /// updating the first — so the task is claimed and executed once per copy.
    /// </summary>
    public async Task<HashSet<string>> GetQueuedTaskIdsAsync(string runName, CancellationToken ct = default)
    {
        var ids = new HashSet<string>(StringComparer.Ordinal);

        await foreach (var row in _store.QueryTableAsync(_queueTable, RunFilter(runName), ct))
        {
            if (row.GetString("RunName") != runName) continue;

            var taskId = row.GetString("TaskId");
            if (!string.IsNullOrEmpty(taskId)) ids.Add(taskId);
        }

        return ids;
    }
}

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
///
/// THE RUN INDEX, and why the queue table alone is not enough:
///
/// The key design above is right for the claim path and wrong for everything else. RunName is not part
/// of the key, so every run-scoped read — "which tasks does this run still have queued", "release this
/// run's claims", "drop this run's rows" — can only be answered by scanning every partition. Those run
/// on hot paths: once per finalize, once per resumed run, once per orphan re-drive.
///
/// Measured on a production instance whose queue reached ~743,000 rows: 61,939 such scans in ten
/// minutes, p50 80 seconds, p95 100 seconds, then TaskCanceledException. The timeouts fell on the task
/// status writes, so tasks never reached a terminal state, so runs never finalized, so their rows were
/// never deleted — and the table that made the scans slow could only grow. 65% of the log file was the
/// resulting HTTP-SLOW warnings; 0.3% was actual task execution.
///
/// A server-side $filter does not fix this and previously appeared to: filtering on a non-key property
/// narrows what crosses the wire, not what the backend reads. The scan is the cost.
///
/// So run-scoped access gets its own index table, keyed the way those reads actually ask:
///
///   PartitionKey  the run name, escaped for the key charset
///   RowKey        "{bucket}|{queue row key}" — enough to address the queue row directly
///
/// Every run-scoped method below is now a single-partition read followed by point operations, and the
/// claim path is untouched. The index is maintained by the enqueue/remove paths, and built once for a
/// pre-existing queue by <see cref="BackfillIndexAsync"/>.
/// </summary>
public sealed class JobQueueStore : IDisposable
{
    private readonly ILogger<JobQueueStore> _logger;
    private readonly ICraftTableStore _store;
    private readonly string _queueTable;
    private readonly string _indexTable;
    private bool _initialized;

    /// <summary>
    /// Wakes the <see cref="Craft.Orchestration.JobQueuePump"/> the moment claimable rows appear, instead
    /// of it discovering them only on its next poll tick. Bounded at one pending permit: many enqueues
    /// between two pump cycles coalesce into a single wake, because one refill claims a whole batch anyway.
    /// The pump keeps polling on its (idle-backing-off) interval as the backstop — this signal only
    /// removes the wait, it does not replace the loop. Same-instance only, which is all that is needed:
    /// the pump and the enqueue paths share this singleton, and a lease keeps cross-instance work safe.
    /// </summary>
    private readonly SemaphoreSlim _pumpWake = new(0, 1);

    /// <summary>Signal the pump that new claimable rows exist. Never throws and never exceeds one permit.</summary>
    private void WakePump()
    {
        try { _pumpWake.Release(); }
        catch (SemaphoreFullException) { /* a wake is already pending; the pump will claim the batch */ }
        catch (ObjectDisposedException) { /* shutting down; the pump loop has already stopped */ }
    }

    public void Dispose() => _pumpWake.Dispose();

    /// <summary>
    /// Block until the pump is woken by an enqueue or <paramref name="pollInterval"/> elapses, whichever
    /// comes first. Returns true if woken (new work signalled), false on the poll timeout.
    /// </summary>
    public Task<bool> WaitForWorkAsync(TimeSpan pollInterval, CancellationToken ct = default) =>
        _pumpWake.WaitAsync(pollInterval, ct);

    /// <summary>Priorities above this share the lowest bucket. Callers use 0-6; the cap only bounds the key.</summary>
    private const int MaxPriorityBucket = 99;

    /// <summary>Width of the zero-padded bucket key ("P04"), so the index row key splits at a fixed offset.</summary>
    private const int BucketKeyLength = 3;

    /// <summary>
    /// Where the backfill marker lives. '$' is legal in a key and no run name starts with it — run names
    /// are "{OrchestratorName}-{tenant}-{guid}" or "{OrchestratorName}_{...}".
    /// </summary>
    private const string SchemaPartition = "$schema";
    private const string SchemaRowKey = "queue-index";
    private const int SchemaVersion = 1;

    /// <summary>
    /// Rows buffered before the backfill flushes. Bounds peak memory on a very large queue — the
    /// instance that motivated this was at 85% of a 2398MB heap cap before the backfill even started,
    /// and buffering 743,000 rows to group them perfectly would have been the thing that OOMed it.
    /// Rows for one run are written adjacently, so a window this size still groups them in practice.
    /// </summary>
    private const int BackfillFlushThreshold = 5_000;

    public JobQueueStore(ILogger<JobQueueStore> logger, CraftSettings settings, ICraftTableStore store)
    {
        _logger = logger;
        _store = store;
        _queueTable = $"{settings.Orchestrator.TablePrefix}Queue";
        _indexTable = $"{settings.Orchestrator.TablePrefix}QueueIndex";
    }

    public async Task InitializeAsync(CancellationToken ct = default)
    {
        if (_initialized) return;
        await _store.EnsureTableAsync(_queueTable, ct);
        await _store.EnsureTableAsync(_indexTable, ct);
        await BackfillIndexAsync(ct);
        _initialized = true;
    }

    internal static string Bucket(int priority) =>
        "P" + Math.Clamp(priority, 0, MaxPriorityBucket).ToString("D2", CultureInfo.InvariantCulture);

    internal static string BuildRowKey(DateTime queuedUtc, string runName, string taskId) =>
        // D19 so ticks sort lexically the way they sort numerically — without the padding a queue that
        // straddles a tick-digit boundary would silently reorder.
        $"{queuedUtc.Ticks.ToString("D19", CultureInfo.InvariantCulture)}-{runName}-{taskId}";

    /// <summary>
    /// A run name as an index partition key. Azure Tables rejects '/', '\', '#', '?' and control
    /// characters in a key, and a run name carries a user-supplied scheduled-task name — "Alert on
    /// Huntress Rogue Apps detected" is a real one, and nothing stops the next one containing a slash
    /// or a question mark. Percent-escaping is reversible and leaves ordinary names untouched, so the
    /// table stays readable in the portal, which is where anyone debugging this will be looking.
    /// </summary>
    internal static string IndexPartition(string runName)
    {
        var needsEscape = false;
        foreach (var c in runName)
        {
            if (c is '/' or '\\' or '#' or '?' or '%' || char.IsControl(c)) { needsEscape = true; break; }
        }
        if (!needsEscape) return runName;

        var sb = new System.Text.StringBuilder(runName.Length + 8);
        foreach (var c in runName)
        {
            // '%' first, or the escape sequences themselves would be ambiguous.
            if (c is '/' or '\\' or '#' or '?' or '%' || char.IsControl(c))
                sb.Append('%').Append(((int)c).ToString("X2", CultureInfo.InvariantCulture));
            else
                sb.Append(c);
        }
        return sb.ToString();
    }

    /// <summary>Index row key: the bucket (fixed width) plus the queue row key it points at.</summary>
    internal static string IndexRowKey(string bucket, string queueRowKey) => $"{bucket}|{queueRowKey}";

    /// <summary>The inverse of <see cref="IndexRowKey"/>. Split at a fixed offset — the bucket is always
    /// three characters, so a '|' inside the queue row key cannot confuse this.</summary>
    internal static (string Bucket, string QueueRowKey)? SplitIndexRowKey(string indexRowKey)
    {
        if (indexRowKey.Length < BucketKeyLength + 2 || indexRowKey[BucketKeyLength] != '|') return null;
        return (indexRowKey[..BucketKeyLength], indexRowKey[(BucketKeyLength + 1)..]);
    }

    private static StoreRow IndexRow(string runName, string taskId, string bucket, string queueRowKey) =>
        new(IndexPartition(runName), IndexRowKey(bucket, queueRowKey))
        {
            Properties = { ["TaskId"] = taskId, ["RunName"] = runName }
        };

    /// <summary>Add one task to the queue. Idempotent for a given (queuedUtc, run, task).</summary>
    /// <remarks>
    /// Queue row first, index row second. The queue row is what makes the task actually run; the index
    /// only accelerates lookups. If the process dies between the two the task still executes, and the
    /// missing index entry is repaired by the next enqueue of the same (queuedUtc, run, task), which
    /// rewrites both keys unchanged. The other order would leave the index claiming a task is queued
    /// when no row exists — the orphan re-drive trusts the index, would decline to re-queue, and the
    /// run would sit Pending with nothing running.
    /// </remarks>
    public async Task EnqueueAsync(string runName, string taskId, int priority, DateTime queuedUtc,
        CancellationToken ct = default)
    {
        var bucket = Bucket(priority);
        var rowKey = BuildRowKey(queuedUtc, runName, taskId);

        await _store.UpsertAsync(_queueTable, new StoreRow(bucket, rowKey)
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

        await _store.UpsertAsync(_indexTable, IndexRow(runName, taskId, bucket, rowKey), ct);

        WakePump();
    }

    /// <summary>Queue many tasks for one run. Chunked by the caller's priority into per-bucket batches.</summary>
    /// <remarks>
    /// The index rows for one run all share a partition, so however many buckets the tasks span the
    /// index costs exactly one transaction. Ordering is as <see cref="EnqueueAsync"/>.
    /// </remarks>
    public async Task EnqueueBatchAsync(string runName, IReadOnlyList<(string TaskId, int Priority)> tasks,
        DateTime queuedUtc, CancellationToken ct = default)
    {
        var indexRows = new List<StoreRow>(tasks.Count);

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

            indexRows.AddRange(byBucket.Select(t =>
                IndexRow(runName, t.TaskId, byBucket.Key, BuildRowKey(queuedUtc, runName, t.TaskId))));
        }

        if (indexRows.Count > 0)
            await _store.UpsertBatchAsync(_indexTable, IndexPartition(runName), indexRows, ct);

        if (tasks.Count > 0) WakePump();
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
    /// <remarks>
    /// Index row first, mirroring the enqueue rationale from the other side. A crash between the two
    /// leaves a queue row for a task that has finished; it gets claimed once more and the resolver drops
    /// it as a stale descriptor, which is already a handled path. Deleting the queue row first would
    /// instead leave the index advertising queued work that does not exist, which stalls the run.
    /// </remarks>
    public async Task RemoveAsync(ClaimedJob job, CancellationToken ct = default)
    {
        await _store.DeleteAsync(_indexTable, IndexPartition(job.RunName), IndexRowKey(job.Bucket, job.RowKey), ct);
        await _store.DeleteAsync(_queueTable, job.Bucket, job.RowKey, ct);
    }

    /// <summary>
    /// Remove many finished tasks at once. The pump releases a whole claimed batch per cycle, so this
    /// turns what was 2 point deletes per task (index + queue, one <see cref="RemoveAsync"/> each) into
    /// one transaction per partition: index rows share a run's partition, queue rows share a bucket.
    ///
    /// Ordering matches <see cref="RemoveAsync"/> at the batch level: ALL index rows first, then the
    /// queue rows. A crash in between leaves queue rows whose tasks are finished — claimed once more and
    /// dropped as stale descriptors, an already-handled path — whereas deleting the queue rows first
    /// would leave the index advertising work that no longer exists and stall those runs.
    /// </summary>
    public async Task RemoveBatchAsync(IReadOnlyList<ClaimedJob> jobs, CancellationToken ct = default)
    {
        if (jobs.Count == 0) return;

        foreach (var byRun in jobs.GroupBy(j => IndexPartition(j.RunName)))
            await _store.DeleteBatchAsync(_indexTable, byRun.Key,
                byRun.Select(j => IndexRowKey(j.Bucket, j.RowKey)).ToList(), ct);

        foreach (var byBucket in jobs.GroupBy(j => j.Bucket))
            await _store.DeleteBatchAsync(_queueTable, byBucket.Key,
                byBucket.Select(j => j.RowKey).ToList(), ct);
    }

    /// <summary>
    /// This run's index rows. One single-partition read — the operation every run-scoped method below
    /// used to perform as a full-table scan.
    /// </summary>
    private async Task<List<(string TaskId, string Bucket, string QueueRowKey, string IndexRowKey)>>
        ReadIndexAsync(string runName, CancellationToken ct)
    {
        var entries = new List<(string, string, string, string)>();

        await foreach (var row in _store.QueryPartitionAsync(_indexTable, IndexPartition(runName), ct))
        {
            var split = SplitIndexRowKey(row.RowKey);
            if (split == null) continue;

            var taskId = row.GetString("TaskId");
            if (string.IsNullOrEmpty(taskId)) continue;

            entries.Add((taskId, split.Value.Bucket, split.Value.QueueRowKey, row.RowKey));
        }

        return entries;
    }

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
        foreach (var e in await ReadIndexAsync(runName, ct))
            await _store.DeleteAsync(_queueTable, e.Bucket, e.QueueRowKey, ct);

        // One call, and it also takes any entry whose queue row was already gone.
        await _store.DeletePartitionAsync(_indexTable, IndexPartition(runName), ct);
    }

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

        foreach (var e in await ReadIndexAsync(runName, ct))
        {
            var row = await _store.GetAsync(_queueTable, e.Bucket, e.QueueRowKey, ct);
            if (row == null) continue;                                     // finished and removed
            if (string.IsNullOrEmpty(row.GetString("Owner"))) continue;    // already free

            row["Owner"] = "";
            row["LeaseUntil"] = (DateTimeOffset?)null;
            await _store.UpsertAsync(_queueTable, row, ct);
            released++;
        }

        // Freed claims are claimable again — wake the pump to pick them up rather than waiting for the
        // recovery-path re-drive on its own timer.
        if (released > 0) WakePump();

        return released;
    }

    /// <summary>A queued row as the status APIs see it: identity, priority, age and claim state.</summary>
    public sealed record QueuedRow(string RunName, string TaskId, int Priority, DateTime QueuedUtc,
        bool Claimed, string Owner, string Bucket, string RowKey);

    /// <summary>
    /// The enqueue timestamp a row key was built from, or null for a key that predates the format.
    /// The inverse of <see cref="BuildRowKey"/>'s D19 tick prefix.
    /// </summary>
    internal static DateTime? ParseQueuedUtc(string rowKey)
    {
        if (rowKey.Length < 20 || rowKey[19] != '-') return null;
        if (!long.TryParse(rowKey.AsSpan(0, 19), NumberStyles.None, CultureInfo.InvariantCulture, out var ticks))
            return null;
        if (ticks <= 0 || ticks > DateTime.MaxValue.Ticks) return null;
        return new DateTime(ticks, DateTimeKind.Utc);
    }

    /// <summary>
    /// Every row currently in the queue, in storage order (highest priority bucket first, oldest first
    /// within it). This is the durable backlog the status APIs report — the in-memory JobManager only
    /// ever holds a worker-pool-sized buffer of it.
    ///
    /// Claimed means owned under a live lease, i.e. buffered or running on some instance; everything
    /// else is waiting for a pump to take it. One unfiltered scan, so the cost is proportional to the
    /// backlog — callers are expected to cache the result rather than call this per poll.
    /// </summary>
    public async Task<IReadOnlyList<QueuedRow>> ListQueuedAsync(CancellationToken ct = default)
    {
        var now = DateTimeOffset.UtcNow;
        var rows = new List<QueuedRow>();

        await foreach (var row in _store.QueryTableAsync(_queueTable, ct))
        {
            rows.Add(new QueuedRow(
                row.GetString("RunName") ?? "",
                row.GetString("TaskId") ?? "",
                row.GetInt32("Priority") ?? 0,
                ParseQueuedUtc(row.RowKey) ?? DateTime.UtcNow,
                !IsClaimable(row, now),
                row.GetString("Owner") ?? "",
                row.PartitionKey,
                row.RowKey));
        }

        return rows;
    }

    /// <summary>
    /// Remove every queue row for one task, regardless of claim state. Returns how many were removed.
    /// Used by the durable cancel path — the caller must have already marked the task terminal in the
    /// run graph, or the orphan re-drive sees a Pending task with no row and puts one straight back.
    /// </summary>
    public async Task<int> RemoveTaskAsync(string runName, string taskId, CancellationToken ct = default)
    {
        var removed = 0;
        var partition = IndexPartition(runName);

        foreach (var e in await ReadIndexAsync(runName, ct))
        {
            if (e.TaskId != taskId) continue;

            await _store.DeleteAsync(_indexTable, partition, e.IndexRowKey, ct);
            await _store.DeleteAsync(_queueTable, e.Bucket, e.QueueRowKey, ct);
            removed++;
        }

        return removed;
    }

    /// <summary>
    /// Move a task's queue rows to a new priority bucket, keeping their enqueue timestamp so the task
    /// keeps its place in line within the new priority. Returns how many rows moved.
    ///
    /// Delete-then-add, in that order: a crash in between loses the row, which the orphan re-drive
    /// repairs by re-queueing the task. The other order leaves TWO claimable rows for one task, and a
    /// duplicated row is executed once per copy — that is the failure mode this queue exists to prevent.
    /// </summary>
    public async Task<int> ReprioritizeTaskAsync(string runName, string taskId, int newPriority,
        CancellationToken ct = default)
    {
        var moved = 0;
        var now = DateTimeOffset.UtcNow;
        var partition = IndexPartition(runName);
        var toMove = new List<StoreRow>();

        foreach (var e in await ReadIndexAsync(runName, ct))
        {
            if (e.TaskId != taskId) continue;
            if (e.Bucket == Bucket(newPriority)) continue;   // already there

            var row = await _store.GetAsync(_queueTable, e.Bucket, e.QueueRowKey, ct);
            if (row == null) continue;

            // A claimed row is already buffered on some instance and about to run — re-adding it
            // unclaimed would create a second runnable copy of the task. Leave it be.
            if (!IsClaimable(row, now)) continue;

            toMove.Add(row);
        }

        foreach (var row in toMove)
        {
            await _store.DeleteAsync(_indexTable, partition, IndexRowKey(row.PartitionKey, row.RowKey), ct);
            await _store.DeleteAsync(_queueTable, row.PartitionKey, row.RowKey, ct);

            var queuedUtc = ParseQueuedUtc(row.RowKey) ?? DateTime.UtcNow;
            await EnqueueAsync(runName, taskId, newPriority, queuedUtc, ct);
            moved++;
        }

        return moved;
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

        // Index only — this never touches the queue table. It is the hottest of the run-scoped reads
        // (once per run per re-drive) and was the single largest source of the scan volume.
        await foreach (var row in _store.QueryPartitionAsync(_indexTable, IndexPartition(runName), ct))
        {
            var taskId = row.GetString("TaskId");
            if (!string.IsNullOrEmpty(taskId)) ids.Add(taskId);
        }

        return ids;
    }

    /// <summary>
    /// Build the run index for a queue that predates it. Runs at most once per storage account, ever:
    /// the marker row written at the end is checked first, so every later start is a single point read.
    ///
    /// Awaited by <see cref="InitializeAsync"/> rather than backgrounded, because the run-scoped reads
    /// are only correct once it has finished. A half-built index under-reports, the orphan re-drive
    /// reads that as "this task has no queue row", and re-queueing a task that already has one is how
    /// the same task gets executed twice — the failure this queue exists to prevent.
    ///
    /// Concurrency needs no lock. Two instances starting together both scan and both write the same
    /// deterministic rows, so the duplicated work is wasted but not wrong, and neither serves traffic
    /// until its own scan completed. Rows enqueued during the scan are indexed by the enqueue path.
    /// </summary>
    private async Task BackfillIndexAsync(CancellationToken ct)
    {
        var marker = await _store.GetAsync(_indexTable, SchemaPartition, SchemaRowKey, ct);
        if ((marker?.GetInt32("Version") ?? 0) >= SchemaVersion) return;

        var started = DateTime.UtcNow;
        _logger.LogInformation("[JobQueue] Building the run index for the first time — one full pass over {Table}", _queueTable);

        var pending = new Dictionary<string, List<StoreRow>>(StringComparer.Ordinal);
        var buffered = 0;
        var indexed = 0;
        var skipped = 0;

        async Task FlushAsync()
        {
            foreach (var (partition, rows) in pending)
                await _store.UpsertBatchAsync(_indexTable, partition, rows, ct);

            indexed += buffered;
            pending.Clear();
            buffered = 0;
        }

        await foreach (var row in _store.QueryTableAsync(_queueTable, ct))
        {
            var runName = row.GetString("RunName");
            var taskId = row.GetString("TaskId");
            if (string.IsNullOrEmpty(runName) || string.IsNullOrEmpty(taskId)) { skipped++; continue; }

            var partition = IndexPartition(runName);
            if (!pending.TryGetValue(partition, out var rows))
                pending[partition] = rows = [];

            rows.Add(IndexRow(runName, taskId, row.PartitionKey, row.RowKey));
            buffered++;

            if (buffered >= BackfillFlushThreshold)
            {
                await FlushAsync();
                _logger.LogInformation("[JobQueue] Run index backfill: {Indexed:N0} rows so far", indexed);
            }
        }

        await FlushAsync();

        await _store.UpsertAsync(_indexTable, new StoreRow(SchemaPartition, SchemaRowKey)
        {
            Properties =
            {
                ["Version"] = SchemaVersion,
                ["BuiltUtc"] = new DateTimeOffset(started, TimeSpan.Zero),
                ["RowsIndexed"] = indexed,
            }
        }, ct);

        _logger.LogInformation(
            "[JobQueue] Run index built: {Indexed:N0} rows in {Seconds:N0}s{Skipped} — this will not run again",
            indexed, (DateTime.UtcNow - started).TotalSeconds,
            skipped > 0 ? $", {skipped:N0} malformed row(s) skipped" : "");
    }
}

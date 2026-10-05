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
///   PartitionKey "P04"                        — the priority bucket, zero-padded so it sorts numerically.
///   RowKey "{runEpochTicks:D19}|{run}|{task}" — the run's start time first, so a bucket drains oldest run
///                                               first, and one row per task because a run's start never
///                                               changes. A run's rows in a bucket are also contiguous, so
///                                               run-scoped queue reads are one RowKey range.
///
/// Azure Table returns rows ordered by partition key then row key, so a single read yields the
/// highest-priority, oldest-run work FIRST, across every run, for one round-trip.
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
/// claim path is untouched. The index is maintained by the enqueue/remove paths, and built (and, from
/// schema v2, re-keyed) once for a pre-existing queue by <see cref="MigrateSchemaAsync"/>.
/// </summary>
public sealed class JobQueueStore : IDisposable
{
    private readonly ILogger<JobQueueStore> _logger;
    private readonly ICraftTableStore _store;
    private readonly string _queueTable;
    private readonly string _indexTable;
    private readonly string _runsTable;
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
    /// Where the schema marker lives. '$' is legal in a key and no run name starts with it — run names
    /// are "{OrchestratorName}-{tenant}-{guid}" or "{OrchestratorName}_{...}".
    /// </summary>
    private const string SchemaPartition = "$schema";
    private const string SchemaRowKey = "queue-index";

    /// <summary>
    /// Current on-disk schema version, applied once per storage account by <see cref="MigrateSchemaAsync"/>:
    ///   1 — the run index exists (see the RUN INDEX note above).
    ///   2 — queue RowKeys are deterministic per (run, task) — <c>{run}|{task}</c> — instead of
    ///       time-prefixed, so re-dispatching a task UPDATES its row instead of writing a second one
    ///       (the duplicate-execution class). Enqueue time moves to the <c>QueuedUtc</c> property.
    ///   3 — the key gains the run's start time as a prefix — <c>{epoch}|{run}|{task}</c> — so a bucket
    ///       drains oldest run first instead of alphabetically by run name, which starved late-sorting runs.
    /// A single forward migration takes any older account straight to this version; there is no
    /// backward-compatible dual-read — after the migration only the new key scheme is used.
    /// </summary>
    private const int SchemaVersion = 3;

    /// <summary>
    /// Rows buffered before the backfill flushes. Bounds peak memory on a very large queue — the
    /// instance that motivated this was at 85% of a 2398MB heap cap before the backfill even started,
    /// and buffering 743,000 rows to group them perfectly would have been the thing that OOMed it.
    /// Rows for one run are written adjacently, so a window this size still groups them in practice.
    /// </summary>
    private const int BackfillFlushThreshold = 5_000;

    /// <summary>Partition writes in flight at once during the migration.</summary>
    private const int MigrationConcurrency = 16;

    public JobQueueStore(ILogger<JobQueueStore> logger, CraftSettings settings, ICraftTableStore store)
    {
        _logger = logger;
        _store = store;
        _queueTable = $"{settings.Orchestrator.TablePrefix}Queue";
        _indexTable = $"{settings.Orchestrator.TablePrefix}QueueIndex";
        _runsTable = $"{settings.Orchestrator.TablePrefix}Runs";
    }

    public async Task InitializeAsync(CancellationToken ct = default)
    {
        if (_initialized) return;
        await _store.EnsureTableAsync(_queueTable, ct);
        await _store.EnsureTableAsync(_indexTable, ct);
        await MigrateSchemaAsync(ct);
        _initialized = true;
    }

    internal static string Bucket(int priority) =>
        "P" + Math.Clamp(priority, 0, MaxPriorityBucket).ToString("D2", CultureInfo.InvariantCulture);

    /// <summary>
    /// The queue RowKey. The epoch is the run's start time, which every caller reads off the run, so the
    /// key is the same on every enqueue of a task and a re-dispatch upserts its one row.
    /// </summary>
    internal static string BuildRowKey(DateTime runStartedUtc, string runName, string taskId) =>
        $"{RunKeyPrefix(runStartedUtc, runName)}{EscapeKeyComponent(taskId)}";

    /// <summary>The shared start of every queue key of one run: <c>{epoch}|{run}|</c>.</summary>
    private static string RunKeyPrefix(DateTime runStartedUtc, string runName) =>
        $"{EpochOf(runStartedUtc).Ticks.ToString("D19", CultureInfo.InvariantCulture)}|{EscapeKeyComponent(runName)}|";

    /// <summary>The run prefix of a v3 queue key, or null for an older key.</summary>
    private static string? RunKeyPrefixOf(string rowKey)
    {
        if (ParseEpoch(rowKey) == null) return null;
        var end = rowKey.IndexOf('|', 20);
        return end < 0 ? null : rowKey[..(end + 1)];
    }

    /// <summary>Everything that starts with <paramref name="prefix"/>: '|' is followed by '}' in ordinal order.</summary>
    private static string PrefixUpperBound(string prefix) => prefix[..^1] + '}';

    /// <summary>Whole seconds, so an epoch builds the same key before and after a storage round trip.</summary>
    internal static DateTime EpochOf(DateTime utc) =>
        new(utc.Ticks - utc.Ticks % TimeSpan.TicksPerSecond, DateTimeKind.Utc);

    /// <summary>The run epoch a v3 key starts with, or null for an older key.</summary>
    internal static DateTime? ParseEpoch(string rowKey)
    {
        if (rowKey.Length < 21 || rowKey[19] != '|') return null;
        if (!long.TryParse(rowKey.AsSpan(0, 19), NumberStyles.None, CultureInfo.InvariantCulture, out var ticks))
            return null;
        if (ticks <= 0 || ticks > DateTime.MaxValue.Ticks) return null;
        return new DateTime(ticks, DateTimeKind.Utc);
    }

    /// <summary>
    /// Escape a run or task id for use inside a queue RowKey: the Azure-illegal key characters plus '|'
    /// (the separator) and '%' (the escape marker itself), percent-encoded. Reversible and injective, so
    /// distinct (run, task) pairs never collide, and ordinary names pass through untouched.
    /// </summary>
    private static string EscapeKeyComponent(string value)
    {
        var needsEscape = false;
        foreach (var c in value)
        {
            if (c is '/' or '\\' or '#' or '?' or '%' or '|' || char.IsControl(c)) { needsEscape = true; break; }
        }
        if (!needsEscape) return value;

        var sb = new System.Text.StringBuilder(value.Length + 8);
        foreach (var c in value)
        {
            if (c is '/' or '\\' or '#' or '?' or '%' or '|' || char.IsControl(c))
                sb.Append('%').Append(((int)c).ToString("X2", CultureInfo.InvariantCulture));
            else
                sb.Append(c);
        }
        return sb.ToString();
    }

    /// <summary>The enqueue time embedded in a legacy (v1) row key — <c>{ticks:D19}-{run}-{task}</c> — or
    /// null for a key that is not in that format. Used only by the one-time migration to carry the old
    /// key's timestamp into the new row's <c>QueuedUtc</c> property.</summary>
    internal static DateTime? ParseLegacyQueuedUtc(string rowKey)
    {
        if (rowKey.Length < 20 || rowKey[19] != '-') return null;
        if (!long.TryParse(rowKey.AsSpan(0, 19), NumberStyles.None, CultureInfo.InvariantCulture, out var ticks))
            return null;
        if (ticks <= 0 || ticks > DateTime.MaxValue.Ticks) return null;
        return new DateTime(ticks, DateTimeKind.Utc);
    }

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

    /// <summary>Add one task to the queue. Idempotent per (run, task).</summary>
    public Task EnqueueAsync(string runName, string taskId, int priority, DateTime runStartedUtc,
        CancellationToken ct = default) =>
        EnqueueBatchAsync(runName, [(taskId, priority)], runStartedUtc, ct);

    /// <summary>Queue tasks of one run, keyed by the run's start time and grouped into per-bucket batches.</summary>
    /// <remarks>
    /// Queue rows first, index rows second. The queue row is what makes the task actually run; the index
    /// only accelerates lookups. If the process dies between the two the task still executes, and the
    /// missing index entry is repaired by the next enqueue of the same task, which rewrites both keys
    /// unchanged. The other order would leave the index claiming a task is queued when no row exists — the
    /// orphan re-drive trusts the index, would decline to re-queue, and the run would sit Pending with
    /// nothing running. The index rows share the run's partition, so they cost one transaction.
    /// </remarks>
    public async Task EnqueueBatchAsync(string runName, IReadOnlyList<(string TaskId, int Priority)> tasks,
        DateTime runStartedUtc, CancellationToken ct = default)
    {
        if (tasks.Count == 0) return;

        var indexRows = new List<StoreRow>(tasks.Count);
        var queuedOffset = new DateTimeOffset(DateTime.SpecifyKind(runStartedUtc, DateTimeKind.Utc));

        foreach (var byBucket in tasks.GroupBy(t => Bucket(t.Priority)))
        {
            var rows = byBucket.Select(t => new StoreRow(byBucket.Key, BuildRowKey(runStartedUtc, runName, t.TaskId))
            {
                Properties =
                {
                    ["RunName"] = runName,
                    ["TaskId"] = t.TaskId,
                    ["Priority"] = t.Priority,
                    ["Owner"] = "",
                    ["LeaseUntil"] = (DateTimeOffset?)null,
                    ["QueuedUtc"] = queuedOffset,
                }
            }).ToList();

            await _store.UpsertBatchAsync(_queueTable, byBucket.Key, rows, ct);

            indexRows.AddRange(rows.Select(r => IndexRow(runName, r.GetString("TaskId")!, byBucket.Key, r.RowKey)));
        }

        await _store.UpsertBatchAsync(_indexTable, IndexPartition(runName), indexRows, ct);

        WakePump();
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
        await foreach (var row in _store.QueryTableAsync(_queueTable, ClaimableFilter(now), max, ct))
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

    /// <summary>
    /// Drop a run's queued rows. With <paramref name="runStartedUtc"/>, only that outing's rows: a recurring
    /// run name shares one index partition across outings, and a late removal of the previous outing must
    /// not take the next one's rows with it.
    /// </summary>
    public async Task RemoveRunAsync(string runName, DateTime? runStartedUtc = null, CancellationToken ct = default)
    {
        var entries = await ReadIndexAsync(runName, ct);
        if (runStartedUtc is { } started)
        {
            var prefix = RunKeyPrefix(started, runName);
            entries = entries.Where(e => e.QueueRowKey.StartsWith(prefix, StringComparison.Ordinal)).ToList();
        }

        foreach (var byBucket in entries.GroupBy(e => e.Bucket))
            await _store.DeleteBatchAsync(_queueTable, byBucket.Key, byBucket.Select(e => e.QueueRowKey).ToList(), ct);

        if (runStartedUtc == null)
            await _store.DeletePartitionAsync(_indexTable, IndexPartition(runName), ct);
        else if (entries.Count > 0)
            await _store.DeleteBatchAsync(_indexTable, IndexPartition(runName), entries.Select(e => e.IndexRowKey).ToList(), ct);
    }

    /// <summary>
    /// Empty the durable queue: delete every queue row and every index row (keeping only the schema
    /// marker). Returns the number of queue rows removed.
    ///
    /// A maintenance/reset primitive. It drops the BACKLOG, not in-flight work — a row a worker is already
    /// running finishes, and the pump's later removal of it simply 404s. Tasks still Pending in the
    /// orchestrator's own tables can be re-driven onto the queue by recovery, so pair this with cancelling
    /// the runs when the intent is to STOP work rather than to clear a wedged or corrupted queue.
    /// Deletes are streamed in bounded windows, so this holds a fixed amount of memory on any queue size.
    /// </summary>
    public async Task<int> ClearAllAsync(CancellationToken ct = default)
    {
        await InitializeAsync(ct);

        var removed = await ClearTableAsync(_queueTable, keepSchema: false, ct);
        await ClearTableAsync(_indexTable, keepSchema: true, ct);

        _logger.LogWarning("[JobQueue] Durable queue cleared — {Count} queue row(s) removed", removed);
        return removed;
    }

    /// <summary>Delete every row of one table (optionally sparing the schema marker), batched per
    /// partition and flushed in bounded windows so a huge table never lands in memory at once.</summary>
    private async Task<int> ClearTableAsync(string table, bool keepSchema, CancellationToken ct)
    {
        var removed = 0;
        var pending = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        var buffered = 0;

        async Task FlushAsync()
        {
            foreach (var (partition, keys) in pending)
                await _store.DeleteBatchAsync(table, partition, keys, ct);
            removed += buffered;
            pending.Clear();
            buffered = 0;
        }

        await foreach (var row in _store.QueryTableAsync(table, ct))
        {
            if (keepSchema && row.PartitionKey == SchemaPartition) continue;
            if (!pending.TryGetValue(row.PartitionKey, out var keys)) pending[row.PartitionKey] = keys = [];
            keys.Add(row.RowKey);
            if (++buffered >= BackfillFlushThreshold) await FlushAsync();
        }

        await FlushAsync();
        return removed;
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

        foreach (var (bucket, rows) in await ReadRunQueueRowsAsync(await ReadIndexAsync(runName, ct), null, ct))
        {
            var owned = rows.Where(r => !string.IsNullOrEmpty(r.GetString("Owner"))).ToList();
            foreach (var row in owned)
            {
                row["Owner"] = "";
                row["LeaseUntil"] = (DateTimeOffset?)null;
            }
            if (owned.Count == 0) continue;
            await _store.UpsertBatchAsync(_queueTable, bucket, owned, ct);
            released += owned.Count;
        }

        // Freed claims are claimable again — wake the pump to pick them up rather than waiting for the
        // recovery-path re-drive on its own timer.
        if (released > 0) WakePump();

        return released;
    }

    /// <summary>Above this many tasks a run's queue rows are read as one key range rather than one GET each.</summary>
    private const int PointReadLimit = 32;

    /// <summary>
    /// The queue rows behind <paramref name="entries"/>, per bucket. A few are point reads; more are one
    /// range read over the run's contiguous keys, so the cost follows the run's size, never the queue's.
    /// </summary>
    private async Task<List<(string Bucket, List<StoreRow> Rows)>> ReadRunQueueRowsAsync(
        List<(string TaskId, string Bucket, string QueueRowKey, string IndexRowKey)> entries,
        IReadOnlyList<string>? properties, CancellationToken ct)
    {
        var result = new List<(string, List<StoreRow>)>();
        foreach (var byBucket in entries.GroupBy(e => e.Bucket))
        {
            var rows = new List<StoreRow>();
            var keys = byBucket.Select(e => e.QueueRowKey).ToHashSet(StringComparer.Ordinal);
            var prefixes = keys.Select(RunKeyPrefixOf).Distinct().ToList();

            if (keys.Count <= PointReadLimit || prefixes.Contains(null))
            {
                foreach (var key in keys)
                    if (await _store.GetAsync(_queueTable, byBucket.Key, key, ct) is { } row) rows.Add(row);
            }
            else
            {
                foreach (var prefix in prefixes)
                    await foreach (var row in _store.QueryRowKeyRangeAsync(_queueTable, byBucket.Key, prefix!,
                                       PrefixUpperBound(prefix!), properties, ct))
                        if (keys.Contains(row.RowKey)) rows.Add(row);
            }

            result.Add((byBucket.Key, rows));
        }
        return result;
    }

    /// <summary>A queued row as the status APIs see it: identity, priority, age and claim state.</summary>
    public sealed record QueuedRow(string RunName, string TaskId, int Priority, DateTime QueuedUtc,
        bool Claimed, string Owner, string Bucket, string RowKey);

    /// <summary>
    /// A row's enqueue time: the <c>QueuedUtc</c> property (schema v2), falling back to the timestamp a
    /// legacy v1 key was built from, then to now. For age/status reporting only.
    /// </summary>
    private static DateTime QueuedUtcOf(StoreRow row) =>
        row.GetDateTimeOffset("QueuedUtc")?.UtcDateTime
        ?? ParseLegacyQueuedUtc(row.RowKey)
        ?? DateTime.UtcNow;

    /// <summary>A queue row's columns. Keys are named because a projection returns only what it lists.</summary>
    private static readonly string[] s_queuedRowProperties =
        ["PartitionKey", "RowKey", "RunName", "TaskId", "Priority", "Owner", "LeaseUntil", "QueuedUtc"];

    /// <summary>
    /// Every row currently in the queue, streamed in storage order (highest priority bucket first, oldest
    /// run first within it) and projected to what the status APIs read. Claimed means owned under a live
    /// lease. One scan, proportional to the backlog, so callers aggregate as it streams and cache the
    /// result rather than holding the rows or calling this per poll.
    /// </summary>
    public async IAsyncEnumerable<QueuedRow> StreamQueuedAsync(
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        var now = DateTimeOffset.UtcNow;
        await foreach (var row in _store.QueryTableAsync(_queueTable, null, s_queuedRowProperties, ct))
        {
            yield return new QueuedRow(
                row.GetString("RunName") ?? "",
                row.GetString("TaskId") ?? "",
                row.GetInt32("Priority") ?? 0,
                QueuedUtcOf(row),
                !IsClaimable(row, now),
                row.GetString("Owner") ?? "",
                row.PartitionKey,
                row.RowKey);
        }
    }

    /// <summary><see cref="StreamQueuedAsync"/> materialized, for small queues and tests.</summary>
    public async Task<IReadOnlyList<QueuedRow>> ListQueuedAsync(CancellationToken ct = default)
    {
        var rows = new List<QueuedRow>();
        await foreach (var row in StreamQueuedAsync(ct)) rows.Add(row);
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
    /// Move a task's queue rows to a new priority bucket, keeping the run's epoch so the task keeps its
    /// place in line within the new priority. Returns how many rows moved.
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

            await EnqueueAsync(runName, taskId, newPriority, ParseEpoch(row.RowKey) ?? QueuedUtcOf(row), ct);
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
    /// Treating that as orphaned re-queues the whole backlog on a timer.
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
    /// Of <paramref name="taskIds"/> (all belonging to <paramref name="runName"/>), the ones the pump
    /// can still dispatch: they have a queue row that EXISTS and that the claim filter will match — now,
    /// because it is free, or later, because it holds a lease that will lapse.
    ///
    /// This is the queue-table counterpart to <see cref="GetQueuedTaskIdsAsync"/>, which answers purely
    /// from the index. The index is what makes "does this run still have queued work" a single-partition
    /// read, but it can OUTLIVE the queue rows it points at, and then it lies: it reports a task queued
    /// that no pump will ever run. That divergence is not hypothetical —
    /// <list type="bullet">
    ///   <item><description>a removal deletes the index row first, so a crash in between (or a
    ///     <see cref="RemoveRunAsync"/> that deleted queue rows before its index partition) can leave the
    ///     opposite;</description></item>
    ///   <item><description>a run left Pending under a build that dispatched into memory rather than this
    ///     queue re-enters here with index rows and no queue rows;</description></item>
    ///   <item><description>a row owned with NO <c>LeaseUntil</c> is excluded by
    ///     <see cref="ClaimableFilter"/> forever, so it sits with an index entry advertising it.</description></item>
    /// </list>
    /// A task in any of those states is invisible to the pump AND reported "queued" by the index, so the
    /// re-drive that trusts the index never re-enqueues it and the run stalls indefinitely with it, its
    /// watchdog never firing. One index read plus <see cref="ReadRunQueueRowsAsync"/>, so the cost follows
    /// the run's size.
    /// </summary>
    public async Task<HashSet<string>> GetDispatchableTaskIdsAsync(
        string runName, IReadOnlyCollection<string> taskIds, CancellationToken ct = default)
    {
        var result = new HashSet<string>(StringComparer.Ordinal);
        if (taskIds.Count == 0) return result;

        var wanted = taskIds as HashSet<string> ?? new HashSet<string>(taskIds, StringComparer.Ordinal);
        var entries = (await ReadIndexAsync(runName, ct)).Where(e => wanted.Contains(e.TaskId)).ToList();

        foreach (var (_, rows) in await ReadRunQueueRowsAsync(entries, s_queuedRowProperties, ct))
        {
            foreach (var row in rows)
            {
                // Owned with no lease is what the server-side claim filter cannot match (it is neither
                // Owner eq '' nor LeaseUntil lt now), so the pump would never dispatch it — a ghost as
                // surely as a missing row.
                if (!string.IsNullOrEmpty(row.GetString("Owner")) && row.GetDateTimeOffset("LeaseUntil") == null)
                    continue;
                if (row.GetString("TaskId") is { } taskId) result.Add(taskId);
            }
        }

        return result;
    }

    /// <summary>
    /// Bring the queue tables up to <see cref="SchemaVersion"/>, once per storage account. The marker row
    /// written at the end is checked first, so every later start is a single point read.
    ///
    /// Re-keys every queue row to <see cref="BuildRowKey"/>, taking each run's start time from the Runs
    /// table, and rebuilds the index entries. One forward pass takes any older account straight to the
    /// current version — there is no dual-read, and after the pass only the new key scheme is used.
    ///
    /// Rows are rewritten new-key-first, old-key-deleted-after, so a crash mid-pass leaves the marker
    /// unset and the next start finishes the job, skipping rows already on their key. The pump awaits <see cref="InitializeAsync"/> before it claims — and every
    /// enqueue path calls it too — so no row is ever claimed or written while the migration is only
    /// half-applied.
    ///
    /// Awaited by InitializeAsync rather than backgrounded: the run-scoped reads and the deterministic
    /// keys are only correct once it has finished.
    /// </summary>
    private async Task MigrateSchemaAsync(CancellationToken ct)
    {
        var marker = await _store.GetAsync(_indexTable, SchemaPartition, SchemaRowKey, ct);
        if ((marker?.GetInt32("Version") ?? 0) >= SchemaVersion) return;

        var started = DateTime.UtcNow;
        _logger.LogInformation(
            "[JobQueue] Migrating queue schema to v{Version} — one full pass over {Table}", SchemaVersion, _queueTable);

        var newQueue = new Dictionary<string, List<StoreRow>>(StringComparer.Ordinal); // by bucket
        var newIndex = new Dictionary<string, List<StoreRow>>(StringComparer.Ordinal); // by run partition
        var oldQueue = new Dictionary<string, List<string>>(StringComparer.Ordinal);   // bucket -> old row keys
        var oldIndex = new Dictionary<string, List<string>>(StringComparer.Ordinal);   // run partition -> old index keys
        var buffered = 0;
        var migrated = 0;
        var rekeyed = 0;
        var skipped = 0;
        var epochs = new Dictionary<string, DateTime>(StringComparer.Ordinal);
        await foreach (var run in _store.QueryTableAsync(_runsTable, "PartitionKey eq 'Run'", ["PartitionKey", "RowKey", "StartedUtc"], ct))
            if (run.PartitionKey == "Run" && run.GetDateTimeOffset("StartedUtc") is { } runStarted)
                epochs[run.RowKey] = runStarted.UtcDateTime;

        static void AddRow(Dictionary<string, List<StoreRow>> map, string key, StoreRow row)
        {
            if (!map.TryGetValue(key, out var list)) map[key] = list = [];
            list.Add(row);
        }
        static void AddKey(Dictionary<string, List<string>> map, string key, string rowKey)
        {
            if (!map.TryGetValue(key, out var list)) map[key] = list = [];
            list.Add(rowKey);
        }

        // Chunked to a transaction's worth, so the one big bucket partition goes in parallel too.
        Task EachAsync<T>(Dictionary<string, List<T>> map, Func<string, IReadOnlyList<T>, Task> write) =>
            Parallel.ForEachAsync(map.SelectMany(kv => kv.Value.Chunk(100).Select(c => (kv.Key, c))),
                new ParallelOptions { MaxDegreeOfParallelism = MigrationConcurrency, CancellationToken = ct },
                async (part, _) => await write(part.Key, part.c));

        async Task FlushAsync()
        {
            // New rows first, so a crash before the deletes leaves BOTH and the re-run converges — never
            // the index advertising a queue row that no longer exists. Within a phase the partitions are
            // independent, and the index has one per run, so they go in parallel: one at a time, a
            // 16k-run backlog was ~32k sequential round trips while the pump waited to claim.
            await EachAsync(newQueue, (bucket, rows) => _store.UpsertBatchAsync(_queueTable, bucket, rows, ct));
            await EachAsync(newIndex, (partition, rows) => _store.UpsertBatchAsync(_indexTable, partition, rows, ct));
            await EachAsync(oldQueue, (bucket, keys) => _store.DeleteBatchAsync(_queueTable, bucket, keys, ct));
            await EachAsync(oldIndex, (partition, keys) => _store.DeleteBatchAsync(_indexTable, partition, keys, ct));

            migrated += buffered;
            newQueue.Clear(); newIndex.Clear(); oldQueue.Clear(); oldIndex.Clear();
            buffered = 0;
        }

        await foreach (var row in _store.QueryTableAsync(_queueTable, ct))
        {
            var runName = row.GetString("RunName");
            var taskId = row.GetString("TaskId");
            if (string.IsNullOrEmpty(runName) || string.IsNullOrEmpty(taskId)) { skipped++; continue; }

            var bucket = row.PartitionKey;
            var runPartition = IndexPartition(runName);

            // The run's start time, as every later enqueue will key it. A run with no Run row falls back to
            // the first of its rows the scan meets, and keeps that for the rest of them.
            if (!epochs.TryGetValue(runName, out var epoch))
                epochs[runName] = epoch = ParseEpoch(row.RowKey) ?? QueuedUtcOf(row);
            var newKey = BuildRowKey(epoch, runName, taskId);
            buffered++;

            // Already on its key (a re-run after a crash): only make sure the index points at it.
            if (row.RowKey == newKey)
            {
                AddRow(newIndex, runPartition, IndexRow(runName, taskId, bucket, newKey));
                continue;
            }

            // The new-scheme row: same bucket + claim state + priority, key deterministic, enqueue time as
            // a property (from the row, or the legacy key, or now).
            AddRow(newQueue, bucket, new StoreRow(bucket, newKey)
            {
                Properties =
                {
                    ["RunName"] = runName,
                    ["TaskId"] = taskId,
                    ["Priority"] = row.GetInt32("Priority") ?? 0,
                    ["Owner"] = row.GetString("Owner") ?? "",
                    ["LeaseUntil"] = row.GetDateTimeOffset("LeaseUntil"),
                    ["QueuedUtc"] = new DateTimeOffset(QueuedUtcOf(row), TimeSpan.Zero),
                }
            });
            AddRow(newIndex, runPartition, IndexRow(runName, taskId, bucket, newKey));
            rekeyed++;
            AddKey(oldQueue, bucket, row.RowKey);
            AddKey(oldIndex, runPartition, IndexRowKey(bucket, row.RowKey));

            if (buffered >= BackfillFlushThreshold)
            {
                await FlushAsync();
                _logger.LogInformation("[JobQueue] Schema migration: {Migrated:N0} rows so far", migrated);
            }
        }

        await FlushAsync();

        await _store.UpsertAsync(_indexTable, new StoreRow(SchemaPartition, SchemaRowKey)
        {
            Properties =
            {
                ["Version"] = SchemaVersion,
                ["BuiltUtc"] = new DateTimeOffset(started, TimeSpan.Zero),
                ["RowsIndexed"] = migrated,
            }
        }, ct);

        _logger.LogInformation(
            "[JobQueue] Queue schema at v{Version}: {Migrated:N0} row(s) processed, {Rekeyed:N0} re-keyed, in {Seconds:N0}s{Skipped} — this will not run again",
            SchemaVersion, migrated, rekeyed, (DateTime.UtcNow - started).TotalSeconds,
            skipped > 0 ? $", {skipped:N0} malformed row(s) skipped" : "");
    }
}

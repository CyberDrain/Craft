using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json;
using Craft.Configuration;

namespace Craft.Storage;

/// <summary>
/// Durable orchestration state. Each run is one partition of the Work table, and every task's whole state
/// is one row in it whose RowKey prefix is its state:
///
///   $run         header: identity, options and the Total/Done counts
///   T|{seq}      payload (immutable)
///   P|{seq}      pending          R|{seq}  running (Owner, LeaseUntil)          D|{seq}  done
///   C|{child}    a child run the parent waits for
///
/// So every state change is one partition transaction, the counts can never drift from the rows, and
/// nothing has to be reconciled: a task is exactly where its row says. A worker that dies leaves an R row
/// whose lease lapses, and the next claim takes it back.
///
/// Three small tables sit beside it: Ready (one row per run with work, ordered by band then start time,
/// read by the scheduler), Names (latest run per name; every unfinished run, partition <c>A</c>; and the
/// instance lock) and Finished (completion order, for retention).
///
/// The active-run row is written before anything else of a run and removed after everything else, so it is
/// the authoritative list of runs that exist; Ready and Finished are indexes derived from the runs and can be
/// rebuilt from it (<see cref="RepairIndexesAsync"/>). A stale index row costs a read, never a wrong answer.
/// </summary>
public sealed class WorkStore
{
    public const string HeaderKey = "$run";

    /// <summary>The PostExecution parameters, kept off the header: the header is rewritten in every finish
    /// transaction, where rows cannot be split, and the parameters can outgrow a property.</summary>
    private const string PostExecKey = "$post";
    public const int AggregateSeq = 99_999_999;

    /// <summary>Tasks per claim or completion transaction: two ops each plus the header.</summary>
    public const int MaxPerTransaction = 49;

    private readonly int MaxAttempts;
    private const int ConflictRetries = 16;
    private const int MaxRunningScan = 500;

    private readonly ICraftTableStore _store;
    private readonly ILogger<WorkStore> _logger;
    private readonly PartitionRateLimiter _rate;
    private readonly string _work, _ready, _names, _finished, _results;
    private readonly string[] _legacyTables;
    private volatile bool _initialized;

    public WorkStore(ILogger<WorkStore> logger, CraftSettings settings, ICraftTableStore store,
        PartitionRateLimiter? rate = null)
    {
        _logger = logger;
        _store = store;
        _rate = rate ?? new PartitionRateLimiter();
        MaxAttempts = Math.Max(1, settings.Orchestrator.MaxRetries);
        var p = settings.Orchestrator.TablePrefix;
        _work = $"{p}Work";
        _ready = $"{p}Ready";
        _names = $"{p}Names";
        _finished = $"{p}Finished";
        _results = $"{p}TaskResults";
        _legacyTables = [$"{p}Queue", $"{p}QueueIndex", $"{p}Tasks", $"{p}Runs", $"{p}Results"];
    }

    public string ResultsTable => _results;

    /// <summary>Called after every finish transaction that reached the barrier or completed a run, whoever made it.</summary>
    public Func<FinishOutcome, Task>? AfterFinish { get; set; }

    public async Task InitializeAsync(CancellationToken ct = default)
    {
        if (_initialized) return;
        foreach (var t in new[] { _work, _ready, _names, _finished, _results })
            await _store.EnsureTableAsync(t, ct);
        _initialized = true;
    }

    /// <summary>The earlier orchestration tables, which this store replaces outright. Their in-flight work is
    /// dropped; the timers that created it create it again.</summary>
    public IReadOnlyList<string> LegacyTables => _legacyTables;

    // ── keys ──

    public static string RunKeyFor(string name, DateTime startedUtc) =>
        $"{name}~{startedUtc.Ticks.ToString("x", CultureInfo.InvariantCulture)}";

    private static string Seq(int seq) => seq.ToString("D8", CultureInfo.InvariantCulture);
    private static string Key(char state, int seq) => $"{state}|{Seq(seq)}";
    private static int SeqOf(string rowKey) => int.Parse(rowKey.AsSpan(2), CultureInfo.InvariantCulture);
    private static string ReadyPartition(int band) => "P" + Math.Clamp(band, 0, 99).ToString("D2", CultureInfo.InvariantCulture);
    private static string ReadyKey(RunHeader h) => $"{h.StartedUtc.Ticks.ToString("D19", CultureInfo.InvariantCulture)}|{h.RunKey}";

    /// <summary>A run's rows in one state, in seq order; <paramref name="max"/> 0 means all. A bounded read asks
    /// the service for only that many rows a page.</summary>
    private async IAsyncEnumerable<StoreRow> Range(string runKey, char state, int max = 0,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        var count = 0;
        await foreach (var row in _store.QueryRowKeyRangeAsync(_work, runKey, $"{state}|", $"{state}}}",
            maxPerPage: max > 0 ? Math.Min(max, 1000) : null, ct: ct))
        {
            yield return row;
            if (max > 0 && ++count >= max) yield break;
        }
    }

    // ── create ──

    public sealed record NewTask(string TaskId, Dictionary<string, object> Parameters);

    /// <summary>
    /// Persist a run. Payload and pending rows go first and the header last, so a crash part-way leaves rows
    /// that no Ready entry points at, never a visible run with tasks missing.
    /// </summary>
    public async Task<RunHeader> CreateRunAsync(RunHeader header, IReadOnlyList<NewTask> tasks, CancellationToken ct = default)
    {
        await InitializeAsync(ct);
        var payload = new List<StoreRow>(tasks.Count);
        var pending = new List<StoreRow>(tasks.Count);
        for (var i = 0; i < tasks.Count; i++)
        {
            payload.Add(new StoreRow(header.RunKey, Key('T', i))
            {
                Properties = { ["TaskId"] = tasks[i].TaskId, ["ParametersJson"] = JsonSerializer.Serialize(tasks[i].Parameters, RunHeader.Json) }
            });
            pending.Add(PendingRow(header.RunKey, i, tasks[i].TaskId, 0));
        }

        // The active-run row first: from here on a crash leaves a run the startup repair can see, finish or remove.
        await _store.UpsertAsync(_names, new StoreRow(ActivePartition, header.RunKey)
        {
            Properties = { ["Name"] = header.Name, ["StartedUtc"] = new DateTimeOffset(DateTime.SpecifyKind(header.StartedUtc, DateTimeKind.Utc)) }
        }, ct);
        await _rate.TakeAsync(header.RunKey, tasks.Count * 2 + 1, ct);
        await _store.UpsertBatchAsync(_work, header.RunKey, payload, ct);
        await _store.UpsertBatchAsync(_work, header.RunKey, pending, ct);
        if (header.PostExecParametersJson is { } post)
            await _store.UpsertAsync(_work, new StoreRow(header.RunKey, PostExecKey) { Properties = { ["Json"] = post } }, ct);
        header.Total = tasks.Count;
        await _store.UpsertAsync(_work, header.ToRow(), ct);

        await IndexAsync("record the latest run of its name", header.RunKey,
            () => _store.UpsertAsync(_names, new StoreRow("N", header.Name) { Properties = { ["RunKey"] = header.RunKey } }, ct), ct);
        await IndexAsync("list it as ready", header.RunKey, () => PublishReadyAsync(header, ct), ct);
        Changed(header.RunKey);
        return (await GetRunAsync(header.RunKey, ct))!;
    }

    private static StoreRow PendingRow(string runKey, int seq, string taskId, int attempt) => new(runKey, Key('P', seq))
    {
        Properties = { ["TaskId"] = taskId, ["Attempt"] = attempt }
    };

    public Task PublishReadyAsync(RunHeader h, CancellationToken ct = default) =>
        _store.UpsertAsync(_ready, new StoreRow(ReadyPartition(h.Priority), ReadyKey(h))
        {
            Properties =
            {
                ["RunKey"] = h.RunKey, ["Name"] = h.Name, ["Total"] = h.Total, ["Done"] = h.Done, ["Failed"] = h.Failed,
                ["Cancelled"] = h.Cancelled, ["Reference"] = h.Reference, ["Sequential"] = h.Sequential ? 1 : 0,
                ["MaxConcurrency"] = h.MaxConcurrency,
            }
        }, ct);

    // ── read ──

    public async Task<RunHeader?> GetRunAsync(string runKey, CancellationToken ct = default)
    {
        var row = await _store.GetAsync(_work, runKey, HeaderKey, ct);
        return row == null ? null : RunHeader.FromRow(row);
    }

    private const string ActivePartition = "A";

    /// <summary>
    /// Every unfinished run with this name, oldest first. Run keys are <c>{name}~{hex ticks}</c>, so one name's
    /// outings share a key prefix and sort by start; the Name check drops a different name that merely
    /// starts with this one and a tilde.
    /// </summary>
    public async Task<List<RunHeader>> GetActiveRunsAsync(string name, CancellationToken ct = default)
    {
        var runs = new List<RunHeader>();
        var stale = new List<string>();
        await foreach (var row in _store.QueryRowKeyRangeAsync(_names, ActivePartition, $"{name}~", $"{name}~g", ct: ct))
        {
            if (row.GetString("Name") != name) continue;
            if (await GetRunAsync(row.RowKey, ct) is { IsFinished: false } run) runs.Add(run);
            else stale.Add(row.RowKey);
        }
        foreach (var key in stale) await _store.DeleteAsync(_names, ActivePartition, key, ct);
        return runs;
    }

    /// <summary>
    /// The name runs collide on: the run name without a trailing <c>-{guid}</c>, the queue id a caller appends so
    /// its queue page can find each outing (<c>Cache-{queueId}</c> and <c>Cache</c> are one family).
    /// </summary>
    public static string CollisionFamily(string name) =>
        name.Length > 37 && name[^37] == '-' && Guid.TryParseExact(name.AsSpan(name.Length - 36), "D", out _)
            ? name[..^37]
            : name;

    /// <summary>Whether any run of this collision family is unfinished: one range read for the plain name and one
    /// for <c>{family}-...</c>.</summary>
    public async Task<bool> IsFamilyActiveAsync(string family, CancellationToken ct = default)
    {
        if ((await GetActiveRunsAsync(family, ct)).Count > 0) return true;
        await foreach (var row in _store.QueryRowKeyRangeAsync(_names, ActivePartition, $"{family}-", $"{family}.", ct: ct))
        {
            if (row.GetString("Name") is { } name && CollisionFamily(name) == family
                && await GetRunAsync(row.RowKey, ct) is { IsFinished: false })
                return true;
        }
        return false;
    }

    /// <summary>A run by its key, or else the newest unfinished run with that name.</summary>
    public async Task<RunHeader?> ResolveRunAsync(string keyOrName, CancellationToken ct = default) =>
        (keyOrName.Contains('~') ? await GetRunAsync(keyOrName, ct) : null)
        ?? (await GetActiveRunsAsync(keyOrName, ct)).LastOrDefault();

    /// <summary>The latest run with this name, active or finished.</summary>
    public async Task<RunHeader?> GetRunByNameAsync(string name, CancellationToken ct = default)
    {
        var row = await _store.GetAsync(_names, "N", name, ct);
        return row?.GetString("RunKey") is { } key ? await GetRunAsync(key, ct) : null;
    }

    public async Task<string?> GetPostExecParametersAsync(string runKey, CancellationToken ct = default) =>
        (await _store.GetAsync(_work, runKey, PostExecKey, ct))?.GetString("Json");

    public async Task<Dictionary<string, object>?> GetPayloadAsync(string runKey, int seq, CancellationToken ct = default)
    {
        var row = await _store.GetAsync(_work, runKey, Key('T', seq), ct);
        var json = row?.GetString("ParametersJson");
        if (json == null) return null;
        try { return JsonSerializer.Deserialize<Dictionary<string, object>>(json, RunHeader.Json) ?? []; }
        catch (JsonException) { return []; }
    }

    /// <summary>A run with work, as the scheduler sees it; its mode rides along so the pump can apply a
    /// concurrency limit without reading the run.</summary>
    public sealed record ReadyEntry(int Band, string RunKey, string Name, int Total, int Done, DateTime StartedUtc,
        string? Reference = null, int Failed = 0, int Cancelled = 0, bool Sequential = false, int MaxConcurrency = 0);

    /// <summary>Runs with work, best band first and oldest first within it.</summary>
    public async IAsyncEnumerable<ReadyEntry> ReadReadyAsync(int pageSize = 32,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        await foreach (var row in _store.QueryTableAsync(_ready, null, pageSize, ct))
        {
            if (row.GetString("RunKey") is not { } key) continue;
            var band = int.TryParse(row.PartitionKey.AsSpan(1), NumberStyles.None, CultureInfo.InvariantCulture, out var b) ? b : 99;
            var ticks = long.TryParse(row.RowKey.AsSpan(0, Math.Min(19, row.RowKey.Length)), NumberStyles.None, CultureInfo.InvariantCulture, out var t) ? t : 0;
            yield return new ReadyEntry(band, key, row.GetString("Name") ?? key, row.GetInt32("Total") ?? 0,
                row.GetInt32("Done") ?? 0, new DateTime(ticks, DateTimeKind.Utc), row.GetString("Reference"),
                row.GetInt32("Failed") ?? 0, row.GetInt32("Cancelled") ?? 0, row.GetInt32("Sequential") == 1,
                row.GetInt32("MaxConcurrency") ?? 0);
        }
    }

    public sealed record TaskRow(int Seq, string TaskId, char State, string? Status, int Attempt, string? Owner,
        DateTimeOffset? LeaseUntil, string? LastError);

    private static TaskRow ToTask(StoreRow r) => new(SeqOf(r.RowKey), r.GetString("TaskId") ?? "", r.RowKey[0],
        r.GetString("Status"), r.GetInt32("Attempt") ?? 0, r.GetString("Owner"), r.GetDateTimeOffset("LeaseUntil"),
        r.GetString("LastError"));

    /// <summary>A run's task rows (P, R and D, or one state), in seq order. <paramref name="max"/> bounds the
    /// rows read per state; 0 reads them all, which on a large run is a long read, so status views pass a bound.</summary>
    public async Task<List<TaskRow>> GetTasksAsync(string runKey, char? state = null, int max = 0, CancellationToken ct = default)
    {
        var rows = new List<TaskRow>();
        foreach (var s in state is { } one ? [one] : new[] { 'P', 'R', 'D' })
            await foreach (var r in Range(runKey, s, max, ct)) rows.Add(ToTask(r));
        return rows;
    }

    /// <summary>One pending task by its position, or null when it is not pending. A point read.</summary>
    public async Task<TaskRow?> GetPendingAsync(string runKey, int seq, CancellationToken ct = default) =>
        await _store.GetAsync(_work, runKey, Key('P', seq), ct) is { } row ? ToTask(row) : null;

    // ── claim ──

    public sealed record ClaimedTask(string RunKey, int Seq, string TaskId, int Attempt);

    /// <summary>What a claim saw of the run, for the scheduler: whether its pending tasks ran out, and when its
    /// earliest live claim lapses (if it is not renewed by then, there is work to take back).</summary>
    public sealed class ClaimProbe
    {
        public bool PendingExhausted { get; internal set; }
        public DateTimeOffset? EarliestLeaseUntil { get; internal set; }
    }

    /// <summary>Raised in-process with the run key whenever a run's work may have changed (created, a task
    /// finished or was handed back, a child added, moved band), so a scheduler that had written the run off
    /// looks at it again without depending on the Ready index write having landed.</summary>
    public event Action<string>? RunChanged;

    private void Changed(string runKey)
    {
        try { RunChanged?.Invoke(runKey); }
        catch (Exception ex) { _logger.LogWarning(ex, "[WorkStore] A run-changed listener failed for {Run}", runKey); }
    }

    /// <summary>
    /// Move up to <paramref name="max"/> pending tasks to running under <paramref name="owner"/>, plus, with
    /// <paramref name="reclaimExpired"/>, running tasks whose lease lapsed. A task claimed for the
    /// <see cref="MaxAttempts"/>th time and lapsing again is failed instead of claimed. One transaction; a lost
    /// race returns empty and the caller moves on.
    /// </summary>
    public async Task<IReadOnlyList<ClaimedTask>> ClaimAsync(string runKey, int max, string owner, TimeSpan lease,
        bool reclaimExpired, ClaimProbe? probe = null, bool othersAreDead = false, CancellationToken ct = default)
    {
        max = Math.Min(max, MaxPerTransaction);
        if (max <= 0) return [];

        var now = DateTimeOffset.UtcNow;
        var expired = new List<StoreRow>();
        if (reclaimExpired || probe != null)
        {
            // Bounded so a run left with thousands of lapsed claims costs a fixed read per claim; an earliest
            // lease from a partial scan only makes the scheduler look again sooner.
            await foreach (var r in Range(runKey, 'R', MaxRunningScan, ct))
            {
                var dead = r.GetDateTimeOffset("LeaseUntil") is not { } until || until <= now
                    || (othersAreDead && r.GetString("Owner") != owner);
                if (dead)
                {
                    if (reclaimExpired && expired.Count < max) expired.Add(r);
                    else if (probe != null) probe.EarliestLeaseUntil = now;
                }
                else if (probe != null && r.GetDateTimeOffset("LeaseUntil") is { } live
                    && (probe.EarliestLeaseUntil is not { } seen || live < seen))
                {
                    probe.EarliestLeaseUntil = live;
                }
                if (probe == null && expired.Count >= max) break;
            }
        }

        var pending = new List<StoreRow>();
        if (expired.Count < max)
            await foreach (var r in Range(runKey, 'P', max - expired.Count, ct: ct)) pending.Add(r);
        if (probe != null) probe.PendingExhausted = pending.Count < max - expired.Count;
        if (pending.Count + expired.Count == 0) return [];

        var leaseUntil = now.Add(lease);
        var ops = new List<StoreOp>();
        var claimed = new List<ClaimedTask>();
        var poisoned = new List<(StoreRow Row, int Attempt)>();

        foreach (var r in pending)
        {
            var seq = SeqOf(r.RowKey);
            var attempt = (r.GetInt32("Attempt") ?? 0) + 1;
            ops.Add(StoreOp.Delete(r));
            ops.Add(StoreOp.Insert(RunningRow(runKey, seq, r.GetString("TaskId")!, attempt, owner, leaseUntil)));
            claimed.Add(new ClaimedTask(runKey, seq, r.GetString("TaskId")!, attempt));
        }
        foreach (var r in expired)
        {
            var attempt = r.GetInt32("Attempt") ?? 0;
            if (attempt >= MaxAttempts) { poisoned.Add((r, attempt)); continue; }
            var seq = SeqOf(r.RowKey);
            ops.Add(StoreOp.Replace(new StoreRow(runKey, r.RowKey)
            {
                ETag = r.ETag,
                Properties = RunningRow(runKey, seq, r.GetString("TaskId")!, attempt + 1, owner, leaseUntil).Properties
            }));
            claimed.Add(new ClaimedTask(runKey, seq, r.GetString("TaskId")!, attempt + 1));
        }

        if (poisoned.Count > 0)
        {
            var finish = poisoned.Select(p => new Finish(SeqOf(p.Row.RowKey), "Failed",
                $"Interrupted {p.Attempt} times without completing")).ToList();
            await FinishAsync(runKey, finish, null, ct);
        }
        if (ops.Count == 0) return [];

        await _rate.TakeAsync(runKey, ops.Count, ct);
        return await _store.TrySubmitAsync(_work, runKey, ops, ct) ? claimed : [];
    }

    /// <summary>
    /// Claim the next step of a sequential run for <paramref name="owner"/>, taking or keeping the run's driver
    /// lease in the same transaction. Empty while another owner's driver lease is live, so a run never has two
    /// drivers; a lapsed driver's step is reclaimed with it.
    /// </summary>
    /// <param name="continuing">True for the driver claiming its own next step; false (the pump) defers to any live driver.</param>
    /// <param name="othersAreDead">The caller holds the instance lock, so a driver lease held by any other process
    /// belongs to a process that has stopped, and its step is taken over at once.</param>
    public async Task<ClaimedTask?> ClaimSequentialAsync(string runKey, string owner, TimeSpan lease, bool continuing = false,
        bool othersAreDead = false, CancellationToken ct = default)
    {
        var headerRow = await _store.GetAsync(_work, runKey, HeaderKey, ct);
        if (headerRow == null) return null;
        var header = RunHeader.FromRow(headerRow);
        var now = DateTimeOffset.UtcNow;
        var driverLive = header.DriverOwner != null && header.DriverLease > now && !(othersAreDead && header.DriverOwner != owner);
        if (driverLive && !(continuing && header.DriverOwner == owner)) return null;

        StoreRow? row = null;
        await foreach (var r in Range(runKey, 'R', ct: ct)) { row = r; break; }
        var reclaiming = row != null;
        if (row == null) await foreach (var r in Range(runKey, 'P', 1, ct)) { row = r; break; }
        if (row == null) return null;

        var seq = SeqOf(row.RowKey);
        var attempt = (row.GetInt32("Attempt") ?? 0) + 1;
        if (reclaiming && attempt > MaxAttempts)
        {
            await FinishAsync(runKey, [new Finish(seq, "Failed", $"Interrupted {attempt - 1} times without completing")], 'R', ct);
            if (header.StopOnFailure)
            {
                await CancelPendingAsync(runKey, StoppedReason(row.GetString("TaskId")), ct: ct);
                return null;
            }
            return await ClaimSequentialAsync(runKey, owner, lease, continuing, othersAreDead, ct);
        }

        var until = now.Add(lease);
        header.DriverOwner = owner;
        header.DriverLease = until;
        var running = RunningRow(runKey, seq, row.GetString("TaskId")!, attempt, owner, until);
        var ops = new List<StoreOp> { StoreOp.Replace(header.ToRow(headerRow.ETag)) };
        if (reclaiming) ops.Add(StoreOp.Replace(new StoreRow(runKey, row.RowKey) { ETag = row.ETag, Properties = running.Properties }));
        else { ops.Add(StoreOp.Delete(row)); ops.Add(StoreOp.Insert(running)); }

        await _rate.TakeAsync(runKey, ops.Count, ct);
        return await _store.TrySubmitAsync(_work, runKey, ops, ct)
            ? new ClaimedTask(runKey, seq, row.GetString("TaskId")!, attempt)
            : null;
    }

    /// <summary>Why a stop-on-failure run cancelled its remaining steps.</summary>
    public static string StoppedReason(string? failedTaskId) => $"Not run: step {failedTaskId} failed and the run stops on failure";

    /// <summary>Give up a sequential run's driver lease so the next step can be claimed by anyone.</summary>
    public async Task ReleaseDriverAsync(string runKey, string owner, CancellationToken ct = default)
    {
        for (var attempt = 0; attempt < ConflictRetries; attempt++)
        {
            var headerRow = await _store.GetAsync(_work, runKey, HeaderKey, ct);
            if (headerRow == null) return;
            var header = RunHeader.FromRow(headerRow);
            if (header.DriverOwner != owner) return;
            header.DriverOwner = null;
            header.DriverLease = null;
            if (await _store.TrySubmitAsync(_work, runKey, [StoreOp.Replace(header.ToRow(headerRow.ETag))], ct)) return;
        }
    }

    private static StoreRow RunningRow(string runKey, int seq, string taskId, int attempt, string owner, DateTimeOffset leaseUntil) =>
        new(runKey, Key('R', seq))
        {
            Properties = { ["TaskId"] = taskId, ["Attempt"] = attempt, ["Owner"] = owner, ["LeaseUntil"] = leaseUntil }
        };

    /// <summary>
    /// Hand a running task back to pending without counting the attempt as spent: a shutdown that interrupted
    /// it, or an aggregation that failed and has attempts left. Only if <paramref name="owner"/> still holds it.
    /// </summary>
    public async Task<bool> ReleaseAsync(string runKey, int seq, string owner, bool refundAttempt, CancellationToken ct = default)
    {
        var row = await _store.GetAsync(_work, runKey, Key('R', seq), ct);
        if (row == null || row.GetString("Owner") != owner) return false;
        var attempt = (row.GetInt32("Attempt") ?? 1) - (refundAttempt ? 1 : 0);
        await _rate.TakeAsync(runKey, 2, ct);
        var released = await _store.TrySubmitAsync(_work, runKey,
            [StoreOp.Delete(row), StoreOp.Insert(PendingRow(runKey, seq, row.GetString("TaskId")!, attempt))], ct);
        if (released) Changed(runKey);
        return released;
    }

    /// <summary>Push the lease out on claims this owner still holds. Returns the claims it no longer holds.</summary>
    public async Task<IReadOnlyList<ClaimedTask>> RenewAsync(IReadOnlyList<ClaimedTask> claims, string owner, TimeSpan lease,
        CancellationToken ct = default)
    {
        var lost = new List<ClaimedTask>();
        var leaseUntil = DateTimeOffset.UtcNow.Add(lease);
        foreach (var byRun in claims.GroupBy(c => c.RunKey))
        {
            var ops = new List<StoreOp>();
            foreach (var c in byRun)
            {
                var row = await _store.GetAsync(_work, c.RunKey, Key('R', c.Seq), ct);
                if (row == null) continue;
                if (row.GetString("Owner") != owner) { lost.Add(c); continue; }
                row["LeaseUntil"] = leaseUntil;
                ops.Add(StoreOp.Replace(row));
            }
            foreach (var chunk in ops.Chunk(100))
            {
                await _rate.TakeAsync(byRun.Key, chunk.Length, ct);
                if (!await _store.TrySubmitAsync(_work, byRun.Key, chunk, ct))
                    _logger.LogWarning("[WorkStore] Lease renewal for {Run} lost a race; the next renewal retries", byRun.Key);
            }
        }
        return lost;
    }

    // ── finish ──

    /// <summary>A task reaching a terminal status. <see cref="Owner"/> set means "only if I still hold it".</summary>
    public sealed record Finish(int Seq, string Status, string? Error = null, string? Owner = null, string? ChildKey = null);

    /// <summary>What a finish did to the run: the header after it, and whether this was the transaction that
    /// completed the run's tasks (the barrier) or its aggregation.</summary>
    public sealed record FinishOutcome(RunHeader Header, bool ReachedBarrier, bool Completed, int Applied);

    /// <summary>
    /// Move tasks (R or, for a cancel, P) and child placeholders to done and update the counts, in one
    /// transaction per chunk. The chunk that brings Done to Total is the barrier: it inserts the aggregation
    /// task when the run has one, or completes the run when it does not. Finishing the aggregation task
    /// completes the run.
    /// </summary>
    public async Task<FinishOutcome?> FinishAsync(string runKey, IReadOnlyList<Finish> finishes, char? fromState = 'R',
        CancellationToken ct = default)
    {
        FinishOutcome? outcome = null;
        foreach (var chunk in finishes.Chunk(MaxPerTransaction))
            outcome = await FinishChunkAsync(runKey, chunk, fromState, ct) ?? outcome;
        return outcome;
    }

    /// <summary>What <see cref="FinishStepAsync"/> did: the finish, the step it claimed next (null when there is
    /// none, the run was asked to cancel, or another driver holds it) and that step's payload when it could be read.</summary>
    public sealed record StepResult(FinishOutcome? Outcome, ClaimedTask? Next, Dictionary<string, object>? Payload);

    private sealed class StepClaim(string owner, TimeSpan lease)
    {
        public string Owner { get; } = owner;
        public TimeSpan Lease { get; } = lease;
        public ClaimedTask? Claimed { get; set; }
    }

    /// <summary>
    /// Finish a sequential run's current step and claim its next one in the same transaction, renewing the
    /// driver lease: the next pending step, or the aggregation task when this step completes the run's tasks.
    /// One round trip of concurrent reads, one transaction, then the Ready update alongside the next payload read.
    /// </summary>
    public async Task<StepResult> FinishStepAsync(string runKey, Finish finish, string owner, TimeSpan lease,
        CancellationToken ct = default)
    {
        var claim = new StepClaim(owner, lease);
        Task<Dictionary<string, object>?>? payload = null;
        var outcome = await FinishChunkAsync(runKey, [finish], 'R', ct, claim: claim,
            afterSubmit: () => payload = claim.Claimed is { Seq: not AggregateSeq } next ? TryGetPayloadAsync(runKey, next.Seq, ct) : null);
        return new StepResult(outcome, claim.Claimed, payload == null ? null : await payload);
    }

    private async Task<Dictionary<string, object>?> TryGetPayloadAsync(string runKey, int seq, CancellationToken ct)
    {
        try { return await GetPayloadAsync(runKey, seq, ct); }
        catch (Exception ex) when (ex is not OperationCanceledException) { return null; }
    }

    private async Task<StoreRow?> FirstAsync(char state, string runKey, CancellationToken ct)
    {
        await foreach (var r in Range(runKey, state, 1, ct)) return r;
        return null;
    }

    /// <param name="known">Rows the caller has just read (by seq), used on the first attempt instead of reading each
    /// one again; the transaction is still guarded by their ETags, and a retry reads afresh.</param>
    private async Task<FinishOutcome?> FinishChunkAsync(string runKey, IReadOnlyList<Finish> chunk, char? fromState,
        CancellationToken ct, Dictionary<int, StoreRow>? known = null, StepClaim? claim = null, Action? afterSubmit = null)
    {
        for (var attempt = 0; attempt < ConflictRetries; attempt++)
        {
            if (attempt > 0) known = null;
            StoreRow? headerRow, nextRow = null;
            if (claim != null)
            {
                var headerRead = _store.GetAsync(_work, runKey, HeaderKey, ct);
                var stepRead = _store.GetAsync(_work, runKey, Key('R', chunk[0].Seq), ct);
                var nextRead = FirstAsync('P', runKey, ct);
                await Task.WhenAll(headerRead, stepRead, nextRead);
                (headerRow, nextRow) = (headerRead.Result, nextRead.Result);
                known = stepRead.Result is { } stepRow ? new() { [chunk[0].Seq] = stepRow } : [];
            }
            else headerRow = await _store.GetAsync(_work, runKey, HeaderKey, ct);
            if (headerRow == null) return null;
            var header = RunHeader.FromRow(headerRow);
            var ops = new List<StoreOp>();
            var applied = 0;
            var barrier = false;
            var completed = false;

            // An entity may appear once in a transaction: a task finished twice in one batch (a cancel and a
            // completion landing together, or a retried finish) is applied once.
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var f in chunk)
            {
                if (!seen.Add(f.ChildKey is { } ck ? $"C|{ck}" : Seq(f.Seq))) continue;
                if (f.ChildKey is { } child)
                {
                    var placeholder = await _store.GetAsync(_work, runKey, $"C|{child}", ct);
                    if (placeholder == null) continue;
                    ops.Add(StoreOp.Delete(placeholder));
                    header.Done++;
                    if (f.Status != "Completed") header.Failed++;
                    applied++;
                    continue;
                }

                StoreRow? row = known != null && known.TryGetValue(f.Seq, out var seenRow) ? seenRow : null;
                foreach (var state in row != null ? [] : fromState is { } s ? [s] : new[] { 'R', 'P' })
                {
                    row = await _store.GetAsync(_work, runKey, Key(state, f.Seq), ct);
                    if (row != null) break;
                }
                if (row == null) continue;
                if (f.Owner != null && row.RowKey[0] == 'R' && row.GetString("Owner") != f.Owner) continue;

                ops.Add(StoreOp.Delete(row));
                if (f.Seq == AggregateSeq)
                {
                    header.PostExecStatus = f.Status == "Completed" ? "Completed" : "Failed";
                    completed = true;
                }
                else
                {
                    ops.Add(StoreOp.Insert(new StoreRow(runKey, Key('D', f.Seq))
                    {
                        Properties =
                        {
                            ["TaskId"] = row.GetString("TaskId"),
                            ["Status"] = f.Status,
                            ["LastError"] = f.Error,
                            ["Attempt"] = row.GetInt32("Attempt") ?? 0,
                            ["CompletedUtc"] = DateTimeOffset.UtcNow,
                        }
                    }));
                    header.Done++;
                    if (f.Status == "Failed") header.Failed++;
                    else if (f.Status == "Cancelled") header.Cancelled++;
                }
                applied++;
            }

            if (applied == 0) return new FinishOutcome(header, false, false, 0);

            var now = DateTimeOffset.UtcNow;
            var claimable = claim != null && !header.CancelRequested
                && !(header.DriverOwner is { } driver && driver != claim.Owner && header.DriverLease > now);
            var until = now.Add(claim?.Lease ?? TimeSpan.Zero);
            ClaimedTask? next = null;

            if (!completed && header.Phase == RunPhase.Tasks && header.Done >= header.Total)
            {
                barrier = true;
                if (header.HasPostExec)
                {
                    header.Phase = RunPhase.Aggregate;
                    header.PostExecStatus = "Pending";
                    if (claimable)
                    {
                        ops.Add(StoreOp.Insert(RunningRow(runKey, AggregateSeq, "PostExecution", 1, claim!.Owner, until)));
                        next = new ClaimedTask(runKey, AggregateSeq, "PostExecution", 1);
                    }
                    else ops.Add(StoreOp.Insert(PendingRow(runKey, AggregateSeq, "PostExecution", 0)));
                }
                else completed = true;
            }
            else if (claimable && !completed && nextRow != null)
            {
                var seq = SeqOf(nextRow.RowKey);
                var taskId = nextRow.GetString("TaskId")!;
                var nextAttempt = (nextRow.GetInt32("Attempt") ?? 0) + 1;
                ops.Add(StoreOp.Delete(nextRow));
                ops.Add(StoreOp.Insert(RunningRow(runKey, seq, taskId, nextAttempt, claim!.Owner, until)));
                next = new ClaimedTask(runKey, seq, taskId, nextAttempt);
            }
            if (next != null)
            {
                header.DriverOwner = claim!.Owner;
                header.DriverLease = until;
            }
            if (completed)
            {
                header.Phase = RunPhase.Done;
                header.Status = header.Failed > 0 || header.Cancelled > 0 ? "CompletedWithErrors" : "Completed";
                header.CompletedUtc = DateTime.UtcNow;
            }
            ops.Add(StoreOp.Replace(header.ToRow(headerRow.ETag)));

            await _rate.TakeAsync(runKey, ops.Count, ct);
            if (await _store.TrySubmitAsync(_work, runKey, ops, ct))
            {
                if (claim != null)
                {
                    claim.Claimed = next;
                    afterSubmit?.Invoke();
                }
                // The step path wrote the header under its ETag, so what it wrote is the state after it.
                var after = claim != null ? header : (await GetRunAsync(runKey, ct)) ?? header;
                if (completed) await RetireAsync(after, ct);
                else await IndexAsync("update its Ready counts", runKey, () => PublishReadyAsync(after, ct), ct);
                Changed(runKey);
                var outcome = new FinishOutcome(after, barrier, completed, applied);
                if ((barrier || completed) && AfterFinish is { } hook)
                {
                    try { await hook(outcome); }
                    catch (Exception ex) { _logger.LogWarning(ex, "[WorkStore] After-finish handling for {Run} failed", runKey); }
                }
                return outcome;
            }
        }

        throw new InvalidOperationException($"Finishing {chunk.Count} task(s) of {runKey} kept losing races to other writers");
    }

    /// <summary>Take a finished run off the Ready list and record it for retention.</summary>
    /// <summary>Retire a finished run: the Finished row first (so retention will always find it), then off the
    /// Ready list, then off the active list last. Each step is idempotent, so the repair can redo any of them.</summary>
    private async Task RetireAsync(RunHeader h, CancellationToken ct)
    {
        _rate.Forget(h.RunKey);
        await IndexAsync("record it for retention", h.RunKey, () => RecordFinishedAsync(h, ct), ct);
        await IndexAsync("take it off the Ready list", h.RunKey, () => _store.DeleteAsync(_ready, ReadyPartition(h.Priority), ReadyKey(h), ct), ct);
        await IndexAsync("take it off the active list", h.RunKey, () => _store.DeleteAsync(_names, ActivePartition, h.RunKey, ct), ct);
    }

    private Task RecordFinishedAsync(RunHeader h, CancellationToken ct)
    {
        var done = (h.CompletedUtc ?? DateTime.UtcNow).Ticks.ToString("D19", CultureInfo.InvariantCulture);
        return _store.UpsertAsync(_finished, new StoreRow("F", $"{done}|{h.RunKey}") { Properties = { ["RunKey"] = h.RunKey } }, ct);
    }

    private static readonly TimeSpan[] IndexRetryDelays =
        [TimeSpan.FromMilliseconds(200), TimeSpan.FromMilliseconds(500), TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(4)];

    /// <summary>Index writes that failed for good; a scheduler repairs the indexes when this moves.</summary>
    public int IndexFailures => Volatile.Read(ref _indexFailures);
    private int _indexFailures;

    /// <summary>Delays between index-write retries; tests shorten them.</summary>
    internal TimeSpan[] IndexRetries { get; set; } = IndexRetryDelays;

    /// <summary>
    /// An index write that follows a committed change to a run. Retried through a short storage blip; if it
    /// still fails the change stands, the in-process <see cref="RunChanged"/> event keeps this instance
    /// scheduling correctly, and <see cref="RepairIndexesAsync"/> (run at startup, or on demand) puts the
    /// index right. Logged as an error naming the run, so a run missing from a listing has an explanation.
    /// </summary>
    private async Task IndexAsync(string what, string runKey, Func<Task> write, CancellationToken ct)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                await write();
                return;
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                if (attempt >= IndexRetries.Length)
                {
                    Interlocked.Increment(ref _indexFailures);
                    _logger.LogError(ex, "[WorkStore] Could not {What} for run {Run}; the index repair (startup, or RepairIndexes) fixes it",
                        what, runKey);
                    return;
                }
                await Task.Delay(IndexRetries[attempt], ct);
            }
        }
    }

    /// <summary>Remove a stale Ready entry (its run is gone or finished).</summary>
    public Task DropReadyAsync(ReadyEntry e, CancellationToken ct = default) =>
        _store.DeleteAsync(_ready, ReadyPartition(e.Band), $"{e.StartedUtc.Ticks.ToString("D19", CultureInfo.InvariantCulture)}|{e.RunKey}", ct);

    // ── children ──

    /// <summary>
    /// Make <paramref name="parentKey"/> wait for a child run. Only while the parent is still running its tasks:
    /// a run queued from an aggregation is not a child.
    /// </summary>
    public async Task<bool> AddChildAsync(string parentKey, string childKey, CancellationToken ct = default)
    {
        for (var attempt = 0; attempt < ConflictRetries; attempt++)
        {
            var headerRow = await _store.GetAsync(_work, parentKey, HeaderKey, ct);
            if (headerRow == null) return false;
            var header = RunHeader.FromRow(headerRow);
            if (header.Phase != RunPhase.Tasks) return false;
            header.Total++;
            var ops = new List<StoreOp>
            {
                StoreOp.Insert(new StoreRow(parentKey, $"C|{childKey}") { Properties = { ["Child"] = childKey } }),
                StoreOp.Replace(header.ToRow(headerRow.ETag)),
            };
            if (await _store.TrySubmitAsync(_work, parentKey, ops, ct))
            {
                Changed(parentKey);
                return true;
            }
        }
        return false;
    }

    // ── cancel ──

    /// <summary>Cancel every pending task of a run. Running tasks finish; the barrier then fires as usual.</summary>
    private readonly ConcurrentDictionary<string, int> _cancelling = new(StringComparer.Ordinal);

    /// <summary>Whether a full cancel of the run is in progress in this process (the scheduler leaves it alone).</summary>
    public bool IsCancelling(string runKey) => _cancelling.ContainsKey(runKey);

    /// <param name="maxPages">Pages of up to 49 to cancel before returning; the scheduler does one per visit to finish
    /// a cancel that was interrupted. A full cancel (no bound) marks the run as being cancelled while it works.</param>
    public async Task<(int Cancelled, FinishOutcome? Outcome)> CancelPendingAsync(string runKey,
        string reason = "Cancelled by user", int maxPages = int.MaxValue, CancellationToken ct = default)
    {
        if (maxPages != int.MaxValue) return await CancelPagesAsync(runKey, reason, maxPages, ct);
        _cancelling.AddOrUpdate(runKey, 1, (_, n) => n + 1);
        try
        {
            return await CancelPagesAsync(runKey, reason, maxPages, ct);
        }
        finally
        {
            if (_cancelling.AddOrUpdate(runKey, 0, (_, n) => n - 1) <= 0) _cancelling.TryRemove(runKey, out _);
        }
    }

    private async Task<(int Cancelled, FinishOutcome? Outcome)> CancelPagesAsync(string runKey, string reason, int maxPages,
        CancellationToken ct)
    {
        var cancelled = 0;
        var idle = 0;
        FinishOutcome? outcome = null;
        for (var pages = 0; pages < maxPages; pages++)
        {
            var page = new List<Finish>();
            var rows = new Dictionary<int, StoreRow>();
            await foreach (var r in Range(runKey, 'P', MaxPerTransaction, ct: ct))
            {
                var seq = SeqOf(r.RowKey);
                if (seq == AggregateSeq) continue;
                page.Add(new Finish(seq, "Cancelled", reason));
                rows[seq] = r;
            }
            if (page.Count == 0) return (cancelled, outcome);
            var result = await FinishChunkAsync(runKey, page, 'P', ct, rows);
            if (result == null) return (cancelled, outcome);
            // Nothing applied means another writer cancelled (or claimed) this page first, not that none is left:
            // only an empty pending range ends the loop. Bounded, so rows that somehow cannot be cancelled do not spin.
            if (result.Applied == 0)
            {
                if (++idle >= 3) return (cancelled, outcome);
                continue;
            }
            idle = 0;
            cancelled += result.Applied;
            outcome = result;
        }
        return (cancelled, outcome);
    }

    // ── the instance lock ──

    private const string LockPartition = "$instance", LockKey = "lock";

    /// <summary>Who holds the instance lock, and until when (null when nobody does).</summary>
    public sealed record InstanceLock(string Owner, DateTimeOffset LeaseUntil, DateTimeOffset AcquiredUtc);

    public async Task<InstanceLock?> GetInstanceLockAsync(CancellationToken ct = default)
    {
        await InitializeAsync(ct);
        var row = await _store.GetAsync(_names, LockPartition, LockKey, ct);
        return row?.GetString("Owner") is { } owner
            ? new InstanceLock(owner, row.GetDateTimeOffset("LeaseUntil") ?? DateTimeOffset.MinValue,
                row.GetDateTimeOffset("AcquiredUtc") ?? DateTimeOffset.MinValue)
            : null;
    }

    /// <summary>
    /// Take or keep the instance lock: the one row that says which process works the queue. Succeeds when
    /// nobody holds it, its lease has run out, or <paramref name="owner"/> already holds it (a renewal). Guarded
    /// by the row's ETag, so two processes racing for it cannot both win. Returns the holder afterwards.
    /// </summary>
    public async Task<(bool Held, InstanceLock? Holder)> TryHoldInstanceLockAsync(string owner, TimeSpan lease,
        CancellationToken ct = default)
    {
        await InitializeAsync(ct);
        var now = DateTimeOffset.UtcNow;
        var row = await _store.GetAsync(_names, LockPartition, LockKey, ct);
        var holder = row?.GetString("Owner");
        var until = row?.GetDateTimeOffset("LeaseUntil") ?? DateTimeOffset.MinValue;
        if (row != null && holder != owner && until > now)
            return (false, new InstanceLock(holder!, until, row.GetDateTimeOffset("AcquiredUtc") ?? DateTimeOffset.MinValue));

        var acquired = holder == owner ? row!.GetDateTimeOffset("AcquiredUtc") ?? now : now;
        var next = new StoreRow(LockPartition, LockKey)
        {
            ETag = row?.ETag,
            Properties = { ["Owner"] = owner, ["LeaseUntil"] = now.Add(lease), ["AcquiredUtc"] = acquired },
        };
        var ok = await _store.TrySubmitAsync(_names, LockPartition, [row == null ? StoreOp.Insert(next) : StoreOp.Replace(next)], ct);
        return ok ? (true, new InstanceLock(owner, now.Add(lease), acquired)) : (false, await GetInstanceLockAsync(ct));
    }

    /// <summary>Give the instance lock up, if <paramref name="owner"/> still holds it, so a successor starts at once.
    /// False when <paramref name="owner"/> still held it and the delete lost a race.</summary>
    public async Task<bool> ReleaseInstanceLockAsync(string owner, CancellationToken ct = default)
    {
        var row = await _store.GetAsync(_names, LockPartition, LockKey, ct);
        return row?.GetString("Owner") != owner || await _store.TrySubmitAsync(_names, LockPartition, [StoreOp.Delete(row)], ct);
    }

    // ── index repair ──

    /// <summary>What a repair pass found and did.</summary>
    public sealed record RepairResult(int Active, int Relisted, int Retired, int Removed, int Young);

    /// <summary>
    /// Rebuild the indexes from the active-run list, which is written before a run's other rows and removed
    /// after them. For each run on it: a run that finished is retired (Finished row, off Ready, off the list);
    /// a run still going gets its Ready entry rewritten; a run whose header never landed (its creation was
    /// interrupted) is removed once it is older than <paramref name="abandonAfter"/>. Every step is
    /// idempotent, so this is safe while the queue is being worked. One point read per active run.
    /// </summary>
    public async Task<RepairResult> RepairIndexesAsync(TimeSpan abandonAfter, CancellationToken ct = default)
    {
        await InitializeAsync(ct);
        var rows = new List<StoreRow>();
        await foreach (var row in _store.QueryPartitionAsync(_names, ActivePartition, ct)) rows.Add(row);

        int relisted = 0, retired = 0, removed = 0, young = 0;
        var cutoff = DateTimeOffset.UtcNow - abandonAfter;
        foreach (var row in rows)
        {
            var runKey = row.RowKey;
            var header = await GetRunAsync(runKey, ct);
            if (header == null)
            {
                if ((row.GetDateTimeOffset("StartedUtc") ?? DateTimeOffset.MinValue) > cutoff) { young++; continue; }
                _logger.LogWarning("[WorkStore] Removing run {Run}: its creation never finished", runKey);
                await _store.DeletePartitionAsync(_work, runKey, ct);
                await _store.DeletePartitionAsync(_results, runKey, ct);
                await _store.DeleteAsync(_names, ActivePartition, runKey, ct);
                removed++;
            }
            else if (header.IsFinished)
            {
                await RecordFinishedAsync(header, ct);
                await _store.DeleteAsync(_ready, ReadyPartition(header.Priority), ReadyKey(header), ct);
                await _store.DeleteAsync(_names, ActivePartition, runKey, ct);
                retired++;
            }
            else
            {
                await PublishReadyAsync(header, ct);
                Changed(runKey);
                relisted++;
            }
        }
        return new RepairResult(rows.Count, relisted, retired, removed, young);
    }

    // ── inspection ──

    /// <summary>Whether the run has its entry on the Ready list (the scheduler only sees runs that do).</summary>
    public async Task<bool> IsListedAsync(RunHeader h, CancellationToken ct = default) =>
        await _store.GetAsync(_ready, ReadyPartition(h.Priority), ReadyKey(h), ct) != null;

    /// <summary>The run's child placeholders: runs it is waiting for (at most <paramref name="max"/>).</summary>
    public async Task<List<string>> GetChildWaitsAsync(string runKey, int max = 1000, CancellationToken ct = default)
    {
        var children = new List<string>();
        await foreach (var r in Range(runKey, 'C', max, ct)) children.Add(r.RowKey[2..]);
        return children;
    }

    // ── retention ──

    /// <summary>Delete runs that finished before the retention cutoff: their Work and Results partitions and
    /// index rows. Reads the Finished index oldest first and stops at the cutoff.</summary>
    public async Task<int> SweepFinishedAsync(TimeSpan retention, CancellationToken ct = default)
    {
        var cutoff = (DateTime.UtcNow - retention).Ticks.ToString("D19", CultureInfo.InvariantCulture);
        var expired = new List<StoreRow>();
        await foreach (var row in _store.QueryRowKeyRangeAsync(_finished, "F", "", cutoff, ct: ct))
            expired.Add(row);

        foreach (var row in expired)
        {
            var runKey = row.GetString("RunKey") ?? row.RowKey[20..];
            await DeleteRunAsync(runKey, ct);
            await _store.DeleteAsync(_finished, "F", row.RowKey, ct);
        }
        return expired.Count;
    }

    /// <summary>Delete a run's partitions and its name entry if it still points at this run.</summary>
    public async Task DeleteRunAsync(string runKey, CancellationToken ct = default)
    {
        var header = await GetRunAsync(runKey, ct);
        await _store.DeletePartitionAsync(_work, runKey, ct);
        await _store.DeletePartitionAsync(_results, runKey, ct);
        await _store.DeleteAsync(_names, ActivePartition, runKey, ct);
        if (header == null) return;
        var name = await _store.GetAsync(_names, "N", header.Name, ct);
        if (name?.GetString("RunKey") == runKey) await _store.DeleteAsync(_names, "N", header.Name, ct);
        await _store.DeleteAsync(_ready, ReadyPartition(header.Priority), ReadyKey(header), ct);
    }

    /// <summary>Delete the tables of the previous orchestration design. Their in-flight work is dropped; the timers
    /// that created it create it again.</summary>
    public async Task DropLegacyTablesAsync(CancellationToken ct = default)
    {
        foreach (var t in _legacyTables)
        {
            try { await _store.DeleteTableAsync(t, ct); }
            catch (Exception ex) { _logger.LogWarning(ex, "[WorkStore] Could not drop legacy table {Table}", t); }
        }
    }

    /// <summary>Flag a run as cancelled, so its running tasks and a sequential driver stop at their next step.</summary>
    public Task RequestCancelAsync(string runKey, CancellationToken ct = default) =>
        UpdateHeaderAsync(runKey, h => { h.CancelRequested = true; return true; }, ct);

    /// <summary>Move a run to another priority band (its Ready entry moves with it).</summary>
    public async Task<bool> SetPriorityAsync(string runKey, int priority, CancellationToken ct = default)
    {
        RunHeader? before = null;
        var ok = await UpdateHeaderAsync(runKey, h =>
        {
            before ??= new RunHeader { RunKey = h.RunKey, Name = h.Name, Priority = h.Priority, StartedUtc = h.StartedUtc };
            if (h.IsFinished) return false;
            h.Priority = priority;
            return true;
        }, ct);
        if (!ok || before == null) return false;
        await _store.DeleteAsync(_ready, ReadyPartition(before.Priority), ReadyKey(before), ct);
        if (await GetRunAsync(runKey, ct) is { IsFinished: false } after) await PublishReadyAsync(after, ct);
        Changed(runKey);
        return true;
    }

    private async Task<bool> UpdateHeaderAsync(string runKey, Func<RunHeader, bool> change, CancellationToken ct)
    {
        for (var attempt = 0; attempt < ConflictRetries; attempt++)
        {
            var row = await _store.GetAsync(_work, runKey, HeaderKey, ct);
            if (row == null) return false;
            var header = RunHeader.FromRow(row);
            if (!change(header)) return false;
            if (await _store.TrySubmitAsync(_work, runKey, [StoreOp.Replace(header.ToRow(row.ETag))], ct)) return true;
        }
        return false;
    }
}

public enum RunPhase { Tasks, Aggregate, Done }

/// <summary>A run's header row: identity, options and counts.</summary>
public sealed class RunHeader
{
    internal static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    public string RunKey { get; init; } = "";
    public string Name { get; init; } = "";
    public string Status { get; set; } = "Running";
    public RunPhase Phase { get; set; } = RunPhase.Tasks;
    public int Priority { get; set; } = 4;
    public DateTime StartedUtc { get; init; }
    public DateTime? CompletedUtc { get; set; }
    public string? TaskScriptName { get; init; }
    public string? PostExecFunctionName { get; init; }
    /// <summary>Set on creation only; read back with <see cref="WorkStore.GetPostExecParametersAsync"/>.</summary>
    public string? PostExecParametersJson { get; init; }
    public string? PostExecStatus { get; set; }
    public string? ParentRunKey { get; init; }

    /// <summary>The placeholder this run fills in its parent (<c>C|{key}</c>), completed when this run finishes.</summary>
    public string? ParentChildKey { get; init; }
    public string? Reference { get; init; }

    /// <summary>Sequential runs: the worker driving the run, and until when. One driver at a time.</summary>
    public string? DriverOwner { get; set; }
    public DateTimeOffset? DriverLease { get; set; }
    public bool Sequential { get; init; }

    /// <summary>At most this many of the run's tasks run at once; 0 is no limit. Not used with <see cref="Sequential"/>.</summary>
    public int MaxConcurrency { get; init; }

    /// <summary>Sequential runs: the first failed step cancels the steps after it instead of carrying on.</summary>
    public bool StopOnFailure { get; init; }
    public bool CancelRequested { get; set; }
    public int Total { get; set; }
    public int Done { get; set; }
    public int Failed { get; set; }
    public int Cancelled { get; set; }

    public bool HasPostExec => !string.IsNullOrEmpty(PostExecFunctionName);
    public bool IsFinished => Phase == RunPhase.Done;

    public StoreRow ToRow(string? etag = null) => new(RunKey, WorkStore.HeaderKey)
    {
        ETag = etag,
        Properties =
        {
            ["Name"] = Name,
            ["Status"] = Status,
            ["Phase"] = Phase.ToString(),
            ["Priority"] = Priority,
            ["StartedUtc"] = new DateTimeOffset(DateTime.SpecifyKind(StartedUtc, DateTimeKind.Utc)),
            ["CompletedUtc"] = CompletedUtc is { } c ? new DateTimeOffset(DateTime.SpecifyKind(c, DateTimeKind.Utc)) : (DateTimeOffset?)null,
            ["TaskScriptName"] = TaskScriptName,
            ["PostExecFunctionName"] = PostExecFunctionName,
            ["PostExecStatus"] = PostExecStatus,
            ["ParentRunKey"] = ParentRunKey,
            ["ParentChildKey"] = ParentChildKey,
            ["Reference"] = Reference,
            ["DriverOwner"] = DriverOwner,
            ["DriverLease"] = DriverLease,
            ["Sequential"] = Sequential ? 1 : 0,
            ["MaxConcurrency"] = MaxConcurrency,
            ["StopOnFailure"] = StopOnFailure ? 1 : 0,
            ["CancelRequested"] = CancelRequested ? 1 : 0,
            ["Total"] = Total,
            ["Done"] = Done,
            ["Failed"] = Failed,
            ["Cancelled"] = Cancelled,
        }
    };

    public static RunHeader FromRow(StoreRow r) => new()
    {
        RunKey = r.PartitionKey,
        Name = r.GetString("Name") ?? r.PartitionKey,
        Status = r.GetString("Status") ?? "Running",
        Phase = Enum.TryParse<RunPhase>(r.GetString("Phase"), out var phase) ? phase : RunPhase.Tasks,
        Priority = r.GetInt32("Priority") ?? 4,
        StartedUtc = r.GetDateTimeOffset("StartedUtc")?.UtcDateTime ?? DateTime.UnixEpoch,
        CompletedUtc = r.GetDateTimeOffset("CompletedUtc")?.UtcDateTime,
        TaskScriptName = r.GetString("TaskScriptName"),
        PostExecFunctionName = r.GetString("PostExecFunctionName"),
        PostExecStatus = r.GetString("PostExecStatus"),
        ParentRunKey = r.GetString("ParentRunKey"),
        ParentChildKey = r.GetString("ParentChildKey"),
        Reference = r.GetString("Reference"),
        DriverOwner = r.GetString("DriverOwner"),
        DriverLease = r.GetDateTimeOffset("DriverLease"),
        Sequential = r.GetInt32("Sequential") == 1,
        MaxConcurrency = r.GetInt32("MaxConcurrency") ?? 0,
        StopOnFailure = r.GetInt32("StopOnFailure") == 1,
        CancelRequested = r.GetInt32("CancelRequested") == 1,
        Total = r.GetInt32("Total") ?? 0,
        Done = r.GetInt32("Done") ?? 0,
        Failed = r.GetInt32("Failed") ?? 0,
        Cancelled = r.GetInt32("Cancelled") ?? 0,
    };
}

/// <summary>
/// Keeps each partition under Azure's ~2,000 entities/s target: a token bucket per partition at
/// <see cref="PerSecond"/>, refilled continuously. Callers await their cost before touching the partition.
/// </summary>
public sealed class PartitionRateLimiter(int perSecond = 1_900)
{
    public int PerSecond { get; } = perSecond;
    private readonly ConcurrentDictionary<string, Bucket> _buckets = new(StringComparer.Ordinal);

    public void Forget(string partition) => _buckets.TryRemove(partition, out _);

    public async Task TakeAsync(string partition, int cost, CancellationToken ct = default)
    {
        var bucket = _buckets.GetOrAdd(partition, _ => new Bucket(PerSecond));
        while (true)
        {
            var wait = bucket.TryTake(Math.Min(cost, PerSecond), PerSecond);
            if (wait <= TimeSpan.Zero) return;
            await Task.Delay(wait, ct);
        }
    }

    private sealed class Bucket(double tokens)
    {
        private double _tokens = tokens;
        private long _stamp = Environment.TickCount64;

        public TimeSpan TryTake(int cost, int perSecond)
        {
            lock (this)
            {
                var now = Environment.TickCount64;
                _tokens = Math.Min(perSecond, _tokens + (now - _stamp) * perSecond / 1000.0);
                _stamp = now;
                if (_tokens >= cost) { _tokens -= cost; return TimeSpan.Zero; }
                return TimeSpan.FromMilliseconds(Math.Ceiling((cost - _tokens) * 1000.0 / perSecond));
            }
        }
    }
}

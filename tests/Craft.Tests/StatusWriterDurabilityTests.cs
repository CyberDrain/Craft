using System.Runtime.CompilerServices;
using Craft.Configuration;
using Craft.Orchestration;
using Craft.Storage;
using Microsoft.Extensions.Logging.Abstractions;

namespace Craft.Tests;

/// <summary>
/// The status writer sits on the critical path between a task being dispatched and that task checking
/// out a worker. Production wedged twice in one morning — 101 and 75 minutes — with all 8 limiter slots
/// held by tasks blocked on its barrier, every one of the 8 BG workers idle, 1,919 jobs queued and the
/// heap at 24% of its cap. The worker-health snapshot read Jobs.Running=8 / BgPool.BusyCount=0, which is
/// only reachable if the jobs never got as far as PowerShell.
///
/// These tests pin the two properties that failure needed:
///   LIVENESS  — no wait here is unbounded, and the drain loop cannot die.
///   DURABILITY — nothing bounded is ever dropped. A write that does not persist is retried; a task that
///                cannot be marked Running is deferred, never failed, and stays Pending in storage.
/// </summary>
public class StatusWriterDurabilityTests
{
    /// <summary>An in-memory store whose writes can be made to hang or fail on demand.</summary>
    private sealed class ControllableStore : ICraftTableStore
    {
        private readonly Dictionary<string, Dictionary<(string, string), StoreRow>> _tables = new();
        private readonly object _sync = new();

        public ManualResetEventSlim BatchGate { get; } = new(initialState: true);
        public volatile bool FailBatches;

        /// <summary>Simulated round-trip latency. Without it a batch finishes inside its own
        /// continuation and no two ever overlap, which measures the thread pool rather than the code.</summary>
        public int BatchDelayMs;

        public int BatchCalls;
        public int MaxConcurrentBatches;
        private int _inFlight;

        /// <summary>Single-row upserts. Counted separately from batches because a flush that writes N rows
        /// one at a time costs N round-trips no matter how fast each one is — the cost the batch path exists
        /// to avoid.</summary>
        public int SingleUpsertCalls;

        public List<StoreRow> Rows(string table)
        {
            lock (_sync) return _tables.TryGetValue(table, out var t) ? t.Values.ToList() : new List<StoreRow>();
        }

        public Task PingAsync(CancellationToken ct = default) => Task.CompletedTask;

        public Task EnsureTableAsync(string table, CancellationToken ct = default)
        {
            lock (_sync) { if (!_tables.ContainsKey(table)) _tables[table] = new(); }
            return Task.CompletedTask;
        }

        public async Task UpsertAsync(string table, StoreRow row, CancellationToken ct = default)
        {
            Interlocked.Increment(ref SingleUpsertCalls);
            // Same simulated round-trip as the batch path: without it a sequential loop of N writes
            // completes in microseconds and the test cannot distinguish it from a single batch.
            if (BatchDelayMs > 0) await Task.Delay(BatchDelayMs, ct);
            lock (_sync)
            {
                if (!_tables.ContainsKey(table)) _tables[table] = new();
                _tables[table][(row.PartitionKey, row.RowKey)] = row;
            }
        }

        public async Task UpsertBatchAsync(string table, string partitionKey, IReadOnlyList<StoreRow> rows,
            CancellationToken ct = default)
        {
            await Task.Yield();   // a real storage call never completes synchronously
            Interlocked.Increment(ref BatchCalls);
            var now = Interlocked.Increment(ref _inFlight);
            InterlockedMax(ref MaxConcurrentBatches, now);
            try
            {
                // Block here to model a stalled storage call, honouring cancellation so a flush timeout works.
                while (!BatchGate.IsSet)
                {
                    ct.ThrowIfCancellationRequested();
                    await Task.Delay(5, ct);
                }
                if (BatchDelayMs > 0) await Task.Delay(BatchDelayMs, ct);
                if (FailBatches) throw new InvalidOperationException("storage unavailable");

                lock (_sync)
                {
                    if (!_tables.ContainsKey(table)) _tables[table] = new();
                    foreach (var r in rows) _tables[table][(r.PartitionKey, r.RowKey)] = r;
                }
            }
            finally
            {
                Interlocked.Decrement(ref _inFlight);
            }
        }

        public Task<StoreRow?> GetAsync(string table, string partitionKey, string rowKey, CancellationToken ct = default)
        {
            lock (_sync)
                return Task.FromResult(_tables.TryGetValue(table, out var t)
                    && t.TryGetValue((partitionKey, rowKey), out var r) ? r : null);
        }

        public async IAsyncEnumerable<StoreRow> QueryPartitionAsync(string table, string partitionKey,
            [EnumeratorCancellation] CancellationToken ct = default)
        {
            List<StoreRow> snapshot;
            lock (_sync)
                snapshot = _tables.TryGetValue(table, out var t)
                    ? t.Where(k => k.Key.Item1 == partitionKey).Select(k => k.Value).ToList()
                    : new List<StoreRow>();
            foreach (var r in snapshot) { yield return r; await Task.Yield(); }
        }

        public async IAsyncEnumerable<StoreRow> QueryTableAsync(string table,
            [EnumeratorCancellation] CancellationToken ct = default)
        {
            List<StoreRow> snapshot;
            lock (_sync) snapshot = _tables.TryGetValue(table, out var t) ? t.Values.ToList() : new List<StoreRow>();
            foreach (var r in snapshot) { yield return r; await Task.Yield(); }
        }

        public Task DeleteAsync(string table, string partitionKey, string rowKey, CancellationToken ct = default)
        {
            lock (_sync) { if (_tables.TryGetValue(table, out var t)) t.Remove((partitionKey, rowKey)); }
            return Task.CompletedTask;
        }

        public Task DeletePartitionAsync(string table, string partitionKey, CancellationToken ct = default)
        {
            lock (_sync)
            {
                if (!_tables.TryGetValue(table, out var t)) return Task.CompletedTask;
                foreach (var k in t.Keys.Where(k => k.Item1 == partitionKey).ToList()) t.Remove(k);
            }
            return Task.CompletedTask;
        }

        private static void InterlockedMax(ref int target, int value)
        {
            int cur;
            while (value > (cur = Volatile.Read(ref target)))
                if (Interlocked.CompareExchange(ref target, value, cur) == cur) return;
        }
    }

    private static (OrchestratorStatusWriter Writer, ControllableStore Backing) NewWriter(
        int barrierTimeoutSec = 2, int flushTimeoutSec = 1, int concurrency = 8)
    {
        var settings = new CraftSettings();
        settings.Orchestrator.RunningBarrierTimeoutSeconds = barrierTimeoutSec;
        settings.Orchestrator.StatusFlushTimeoutSeconds = flushTimeoutSec;
        settings.Orchestrator.StatusFlushConcurrency = concurrency;

        var backing = new ControllableStore();
        var store = new OrchestratorTableStore(NullLogger<OrchestratorTableStore>.Instance, settings, backing);
        var writer = new OrchestratorStatusWriter(store, NullLogger<OrchestratorStatusWriter>.Instance, settings);
        return (writer, backing);
    }

    private static OrchestratorTaskItem Task1(string id = "t1") =>
        new() { Id = id, Status = "Running", Parameters = new Dictionary<string, object> { ["TenantFilter"] = "x.com" } };

    // ── LIVENESS ────────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// THE regression guard. A stalled storage write must not hold the caller forever — that wait is what
    /// consumed all 8 slots while every worker sat idle.
    /// </summary>
    [Fact]
    public async Task MarkRunning_TimesOut_WhenStorageStalls_RatherThanHangingForever()
    {
        var (writer, backing) = NewWriter(barrierTimeoutSec: 2, flushTimeoutSec: 1);
        using var _ = writer;

        backing.BatchGate.Reset();               // storage stalls from here on
        await writer.QueueRunWarmup();           // ensure the drain loop is running

        var sw = System.Diagnostics.Stopwatch.StartNew();
        await Assert.ThrowsAsync<MarkerNotPersistedException>(() => writer.MarkRunningAsync("run", Task1()));
        sw.Stop();

        backing.BatchGate.Set();
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(30),
            $"MarkRunningAsync took {sw.Elapsed.TotalSeconds:F1}s — the barrier is not bounded");
    }

    /// <summary>A stalled flush must not stop LATER work once storage recovers — the loop has to survive.</summary>
    [Fact]
    public async Task DrainLoop_KeepsWorking_AfterAFlushTimesOut()
    {
        var (writer, backing) = NewWriter(barrierTimeoutSec: 2, flushTimeoutSec: 1);
        using var _ = writer;

        backing.BatchGate.Reset();
        await Assert.ThrowsAsync<MarkerNotPersistedException>(() => writer.MarkRunningAsync("run", Task1("stalled")));

        backing.BatchGate.Set();                 // storage recovers

        // A brand-new marker must now succeed, proving the loop is still alive.
        await writer.MarkRunningAsync("run", Task1("after-recovery"));
    }

    /// <summary>FlushAsync is the other barrier consumer — no run could finalize while it hung.</summary>
    [Fact]
    public async Task FlushAsync_ReturnsWithinTheBound_WhenStorageStalls()
    {
        var (writer, backing) = NewWriter(barrierTimeoutSec: 2, flushTimeoutSec: 1);
        using var _ = writer;

        backing.BatchGate.Reset();
        writer.QueueTask("run", Task1());

        var sw = System.Diagnostics.Stopwatch.StartNew();
        await writer.FlushAsync();               // must not throw and must not hang
        sw.Stop();

        backing.BatchGate.Set();
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(30),
            $"FlushAsync took {sw.Elapsed.TotalSeconds:F1}s — it is not bounded");
    }

    // ── DURABILITY ──────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// The write that could not be persisted must be retried, not dropped. Dropping it — the previous
    /// behaviour on ANY exception — silently lost terminal task states, leaving finished tasks looking
    /// Pending forever and re-run by the next recovery pass.
    /// </summary>
    [Fact]
    public async Task WritesThatFail_AreRetried_NotLost()
    {
        var (writer, backing) = NewWriter(barrierTimeoutSec: 2, flushTimeoutSec: 1);
        using var _ = writer;

        backing.FailBatches = true;
        writer.QueueTask("run", new OrchestratorTaskItem { Id = "t1", Status = "Completed" });

        await Task.Delay(400);                                   // several failing flushes
        Assert.Empty(backing.Rows("OrchestratorTasks"));         // nothing persisted yet

        backing.FailBatches = false;                             // storage recovers

        var deadline = Environment.TickCount64 + 5000;
        while (Environment.TickCount64 < deadline && backing.Rows("OrchestratorTasks").Count == 0)
            await Task.Delay(20);

        var rows = backing.Rows("OrchestratorTasks");
        Assert.Single(rows);
        Assert.Equal("Completed", rows[0].GetString("Status"));  // the state survived the outage
    }

    /// <summary>A newer state for the same task must not be clobbered by a retry of an older snapshot.</summary>
    [Fact]
    public async Task Retry_DoesNotOverwrite_NewerStateForTheSameTask()
    {
        var (writer, backing) = NewWriter(barrierTimeoutSec: 2, flushTimeoutSec: 1);
        using var _ = writer;

        backing.FailBatches = true;
        writer.QueueTask("run", new OrchestratorTaskItem { Id = "t1", Status = "Running" });
        await Task.Delay(200);

        writer.QueueTask("run", new OrchestratorTaskItem { Id = "t1", Status = "Completed" });
        backing.FailBatches = false;

        var deadline = Environment.TickCount64 + 5000;
        while (Environment.TickCount64 < deadline && backing.Rows("OrchestratorTasks").Count == 0)
            await Task.Delay(20);

        var rows = backing.Rows("OrchestratorTasks");
        Assert.Single(rows);
        Assert.Equal("Completed", rows[0].GetString("Status"));
    }

    /// <summary>Shutdown must still push everything pending to storage.</summary>
    [Fact]
    public async Task Dispose_DrainsPendingWrites()
    {
        var (writer, backing) = NewWriter(barrierTimeoutSec: 2, flushTimeoutSec: 5);

        writer.QueueTask("run", new OrchestratorTaskItem { Id = "t1", Status = "Completed" });
        writer.Dispose();                        // final drain runs here
        await Task.Delay(50);

        Assert.Single(backing.Rows("OrchestratorTasks"));
    }

    // ── THROUGHPUT SHAPE ────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Writes group by run because a batch shares a partition key. CIPP's workload is ~600 runs of ONE
    /// task each, so sequential groups meant hundreds of round-trips per flush with the whole process
    /// waiting. They must overlap.
    /// </summary>
    [Fact]
    public async Task ManySingleTaskRuns_AreWrittenConcurrently_NotOneAtATime()
    {
        var settings = new CraftSettings();
        var backing = new ControllableStore { BatchDelayMs = 15 };   // stand in for storage latency
        var store = new OrchestratorTableStore(NullLogger<OrchestratorTableStore>.Instance, settings, backing);

        // The shape that broke production: one run per tenant, one task each.
        var writes = Enumerable.Range(0, 200)
            .Select(i => new TaskStatusWrite($"AuditLogIngestV2-tenant{i:D3}.dk", "t1", "Completed", "{}", 0, null, null, null))
            .ToList();

        var sw = System.Diagnostics.Stopwatch.StartNew();
        var failed = await store.WriteTaskStatusBatchAsync(writes, maxConcurrency: 8);
        sw.Stop();

        Assert.Empty(failed);
        Assert.Equal(200, backing.BatchCalls);
        Assert.True(backing.MaxConcurrentBatches > 1,
            $"peak concurrent batch writes was {backing.MaxConcurrentBatches} — per-run writes are still serialized");
        Assert.True(backing.MaxConcurrentBatches <= 8,
            $"peak concurrency {backing.MaxConcurrentBatches} exceeded the requested cap of 8");
        // Serialized, 200 x 15ms would be >= 3s. Bounded parallelism must beat that comfortably.
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(2),
            $"200 single-task runs took {sw.Elapsed.TotalSeconds:F1}s — still effectively sequential");
    }

    /// <summary>One run's failure must not discard the other 199.</summary>
    [Fact]
    public async Task OneFailingRun_DoesNotDiscardTheRest()
    {
        var settings = new CraftSettings();
        var backing = new FlakyStore(failFor: "AuditLogIngestV2-tenant005.dk");
        var store = new OrchestratorTableStore(NullLogger<OrchestratorTableStore>.Instance, settings, backing);

        var writes = Enumerable.Range(0, 20)
            .Select(i => new TaskStatusWrite($"AuditLogIngestV2-tenant{i:D3}.dk", "t1", "Completed", "{}", 0, null, null, null))
            .ToList();

        var failed = await store.WriteTaskStatusBatchAsync(writes, maxConcurrency: 4);

        Assert.Equal(["AuditLogIngestV2-tenant005.dk"], failed);
        Assert.Equal(19, backing.Written);
    }

    private sealed class FlakyStore : ICraftTableStore
    {
        private readonly string _failFor;
        public int Written;
        public FlakyStore(string failFor) => _failFor = failFor;

        public Task PingAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task EnsureTableAsync(string table, CancellationToken ct = default) => Task.CompletedTask;
        public Task UpsertAsync(string table, StoreRow row, CancellationToken ct = default) => Task.CompletedTask;

        public Task UpsertBatchAsync(string table, string partitionKey, IReadOnlyList<StoreRow> rows,
            CancellationToken ct = default)
        {
            if (partitionKey == _failFor) throw new InvalidOperationException("partition unavailable");
            Interlocked.Increment(ref Written);
            return Task.CompletedTask;
        }

        public Task<StoreRow?> GetAsync(string t, string p, string r, CancellationToken ct = default) => Task.FromResult<StoreRow?>(null);
        public async IAsyncEnumerable<StoreRow> QueryPartitionAsync(string t, string p,
            [EnumeratorCancellation] CancellationToken ct = default)
        {
            await Task.CompletedTask;
            yield break;
        }

        public async IAsyncEnumerable<StoreRow> QueryTableAsync(string t,
            [EnumeratorCancellation] CancellationToken ct = default)
        {
            await Task.CompletedTask;
            yield break;
        }

        public Task DeleteAsync(string t, string p, string r, CancellationToken ct = default) => Task.CompletedTask;
        public Task DeletePartitionAsync(string t, string p, CancellationToken ct = default) => Task.CompletedTask;
    }

    // ── RUN-ROW WRITE COST ──────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Run rows all share the constant "Run" partition key, so a flush carrying N of them can persist
    /// them in ceil(N/100) transactions. Writing them one at a time instead costs N round-trips inside a
    /// flush that is bounded by StatusFlushTimeoutSeconds — the cost that pushed real flushes past 30s,
    /// then past the 90s barrier, deferring every waiting task until it was abandoned as Pending.
    ///
    /// Guards the write SHAPE, not wall-clock: a timing assertion here would be flaky on a loaded agent.
    /// </summary>
    [Fact]
    public async Task RunRows_ArePersistedInBatches_NotOnePerRoundTrip()
    {
        var (writer, backing) = NewWriter(barrierTimeoutSec: 30, flushTimeoutSec: 20);
        using var _ = writer;

        const int runCount = 150;
        for (var i = 0; i < runCount; i++)
            writer.QueueRun(new OrchestratorRun { Name = $"run-{i:D3}", Status = "Running" });

        await writer.FlushAsync();

        Assert.Equal(runCount, backing.Rows("OrchestratorRuns").Count);

        // 150 rows in one partition = 2 transactions of 100 + 50. Allow generous headroom for the
        // warmup row and flush-cycle boundaries, but nothing close to one call per row.
        Assert.True(backing.SingleUpsertCalls <= 10,
            $"run rows were written with {backing.SingleUpsertCalls} single upserts for {runCount} runs — " +
            "they share one partition key and should be batched");
    }

    /// <summary>
    /// A batch transaction fails atomically, so one bad row would take out the other 99. The write path
    /// must fall back to per-row writes for that chunk rather than reporting all of them unwritten.
    /// </summary>
    [Fact]
    public async Task RunRows_FallBackToIndividualWrites_WhenABatchFails()
    {
        var (writer, backing) = NewWriter(barrierTimeoutSec: 30, flushTimeoutSec: 20);
        using var _ = writer;

        backing.FailBatches = true;              // every batch transaction rejects

        for (var i = 0; i < 5; i++)
            writer.QueueRun(new OrchestratorRun { Name = $"run-{i}", Status = "Running" });

        await writer.FlushAsync();

        // Batching failed, so each row must still have reached storage individually.
        Assert.Equal(5, backing.Rows("OrchestratorRuns").Count);
    }
}

internal static class StatusWriterTestExtensions
{
    /// <summary>Nudge the drain loop so it is definitely running before a test manipulates storage.</summary>
    public static Task QueueRunWarmup(this OrchestratorStatusWriter writer)
    {
        writer.QueueRun(new OrchestratorRun { Name = "warmup", Status = "Running" });
        return Task.Delay(60);
    }
}

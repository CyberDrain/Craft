using Craft.Configuration;
using Craft.Orchestration;
using Craft.PowerShellHost;
using Craft.Storage;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace Craft.Tests;

/// <summary>
/// What a status refresh COSTS, as opposed to what it reports.
///
/// The status snapshot is polled — the stats sampler on its timer, the worker-health page at a few
/// hertz, the perf harness at 4 Hz — and its scan is proportional to the backlog. Two properties kept
/// that from being sustainable on a large instance, and neither is visible from the values returned:
///
///   1. The snapshot did a per-run counter read while aggregating, so a backlog spanning 12,028 runs
///      cost 12,028 point reads per refresh — on every poll, for data only the run-summaries listing
///      ever read.
///   2. The snapshot was stamped with the time the refresh STARTED, so a scan slower than the TTL
///      returned something already expired and the next poll immediately started another. Measured on
///      a 743,000-row queue: continuous back-to-back full scans, gated only by the single-flight lock.
///
/// Both are cost properties, so both are asserted by counting storage calls and by reading the
/// snapshot's own age — not by checking the numbers it reports, which were correct throughout.
/// </summary>
public class JobQueueStatusRefreshCostTests
{
    /// <summary>Counts reads and can make the queue scan take a controllable amount of time.</summary>
    private sealed class CountingStore(ICraftTableStore inner, string queueTable) : ICraftTableStore
    {
        private int _pointReads;
        private int _tableScans;

        /// <summary>Point reads (GetAsync) issued since the last <see cref="Reset"/>.</summary>
        public int PointReads => Volatile.Read(ref _pointReads);

        /// <summary>Full scans of the queue table since the last <see cref="Reset"/>.</summary>
        public int QueueScans => Volatile.Read(ref _tableScans);

        /// <summary>Injected per-scan delay, to model a backlog large enough to outlast the TTL.</summary>
        public TimeSpan ScanDelay { get; set; } = TimeSpan.Zero;

        public void Reset() { Volatile.Write(ref _pointReads, 0); Volatile.Write(ref _tableScans, 0); }

        public Task PingAsync(CancellationToken ct = default) => inner.PingAsync(ct);
        public Task EnsureTableAsync(string table, CancellationToken ct = default) => inner.EnsureTableAsync(table, ct);
        public Task UpsertAsync(string table, StoreRow row, CancellationToken ct = default) => inner.UpsertAsync(table, row, ct);
        public Task UpsertBatchAsync(string table, string pk, IReadOnlyList<StoreRow> rows, CancellationToken ct = default)
            => inner.UpsertBatchAsync(table, pk, rows, ct);
        public Task<bool> TryReplaceBatchAsync(string table, string pk, IReadOnlyList<StoreRow> rows, CancellationToken ct = default)
            => inner.TryReplaceBatchAsync(table, pk, rows, ct);
        public Task DeleteAsync(string table, string pk, string rk, CancellationToken ct = default) => inner.DeleteAsync(table, pk, rk, ct);
        public Task DeletePartitionAsync(string table, string pk, CancellationToken ct = default) => inner.DeletePartitionAsync(table, pk, ct);

        public Task<StoreRow?> GetAsync(string table, string pk, string rk, CancellationToken ct = default)
        {
            Interlocked.Increment(ref _pointReads);
            return inner.GetAsync(table, pk, rk, ct);
        }

        public IAsyncEnumerable<StoreRow> QueryPartitionAsync(string table, string pk, CancellationToken ct = default)
            => inner.QueryPartitionAsync(table, pk, ct);

        public async IAsyncEnumerable<StoreRow> QueryTableAsync(string table,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
        {
            if (table == queueTable)
            {
                Interlocked.Increment(ref _tableScans);
                if (ScanDelay > TimeSpan.Zero) await Task.Delay(ScanDelay, ct);
            }
            await foreach (var row in inner.QueryTableAsync(table, ct)) yield return row;
        }

        public IAsyncEnumerable<StoreRow> QueryTableAsync(string table, string? filter, CancellationToken ct = default)
            => QueryTableAsync(table, ct);
    }

    private sealed class Fixture
    {
        public required CountingStore Counting { get; init; }
        public required JobQueueStore Queue { get; init; }
        public required OrchestratorTableStore Store { get; init; }
        public required JobQueueStatusReader Reader { get; init; }
    }

    private static async Task<Fixture> NewFixtureAsync()
    {
        var settings = new CraftSettings();
        settings.Worker.BgPoolSize = 2;
        var config = new ConfigurationBuilder().AddInMemoryCollection([]).Build();
        var repo = new ScriptRepository(NullLogger<ScriptRepository>.Instance, settings);
        var pool = new PowerShellWorkerPool(repo, NullLogger<PowerShellWorkerPool>.Instance, config, settings);
        var limiter = new BackgroundTaskLimiter(NullLogger<BackgroundTaskLimiter>.Instance, config, settings, pool);
        var jobs = new JobManager(NullLogger<JobManager>.Instance, settings, limiter);

        var counting = new CountingStore(new RunRemainingCounterTests.ConditionalStore(),
            $"{settings.Orchestrator.TablePrefix}Queue");
        var queue = new JobQueueStore(NullLogger<JobQueueStore>.Instance, settings, counting);
        var store = new OrchestratorTableStore(NullLogger<OrchestratorTableStore>.Instance, settings, counting);
        await queue.InitializeAsync();
        await store.InitializeAsync();

        return new Fixture
        {
            Counting = counting,
            Queue = queue,
            Store = store,
            Reader = new JobQueueStatusReader(NullLogger<JobQueueStatusReader>.Instance, jobs, queue, store),
        };
    }

    private static DateTime At(int minute) => new(2026, 8, 12, 3, minute, 0, DateTimeKind.Utc);

    [Fact]
    public async Task SnapshotCostIsOneScan_RegardlessOfHowManyRunsTheBacklogSpans()
    {
        var f = await NewFixtureAsync();

        // 40 distinct runs, each with a counter row — the shape that used to cost 40 point reads per
        // refresh, and 12,028 on the instance that motivated this.
        for (var i = 0; i < 40; i++)
        {
            await f.Store.InitRemainingAsync($"run-{i}", 2);
            await f.Queue.EnqueueBatchAsync($"run-{i}", [("t0", 4), ("t1", 4)], At(0));
        }

        f.Counting.Reset();
        var snap = await f.Reader.GetAsync(TimeSpan.Zero);

        Assert.NotNull(snap);
        Assert.Equal(40, snap!.ByRun.Count);
        Assert.Equal(1, f.Counting.QueueScans);

        // The point of the change: aggregating the backlog reads nothing per run.
        Assert.Equal(0, f.Counting.PointReads);
    }

    [Fact]
    public async Task ASlowScanDoesNotReturnAnAlreadyExpiredSnapshot()
    {
        var f = await NewFixtureAsync();
        await f.Queue.EnqueueBatchAsync("run", [("t0", 4)], At(0));

        // A scan that takes far longer than the age the caller asked for. Stamped on entry, the
        // returned snapshot would be born ~400ms old and instantly past a 100ms TTL, so the next poll
        // starts another — the loop this guards against.
        f.Counting.ScanDelay = TimeSpan.FromMilliseconds(400);

        var snap = await f.Reader.GetAsync(TimeSpan.FromMilliseconds(100));

        Assert.NotNull(snap);
        Assert.True(snap!.AgeSeconds < 0.2,
            $"snapshot was born {snap.AgeSeconds:N3}s old — TakenUtc is being stamped before the scan");
    }

    [Fact]
    public async Task TheRefreshIntervalBacksOffToWhatTheScanActuallyCosts()
    {
        var f = await NewFixtureAsync();
        await f.Queue.EnqueueBatchAsync("run", [("t0", 4)], At(0));

        f.Counting.ScanDelay = TimeSpan.FromMilliseconds(400);
        await f.Reader.GetAsync(TimeSpan.FromMilliseconds(50));   // records the cost

        f.Counting.Reset();

        // Older than the 50ms the caller wants, well inside the 400ms the scan actually costs. Without
        // the floor this re-scans; with it the cached snapshot stands.
        await Task.Delay(120);
        var again = await f.Reader.GetAsync(TimeSpan.FromMilliseconds(50));

        Assert.NotNull(again);
        Assert.Equal(0, f.Counting.QueueScans);
    }

    [Fact]
    public async Task AFastScanStillRefreshesOnTheOrdinaryTtl()
    {
        // The backoff must not become a permanent cache on a healthy instance, where the scan is
        // milliseconds and the floor should collapse back to the caller's requested age.
        var f = await NewFixtureAsync();
        await f.Queue.EnqueueBatchAsync("run", [("t0", 4)], At(0));

        await f.Reader.GetAsync(TimeSpan.Zero);
        f.Counting.Reset();

        await Task.Delay(60);
        await f.Reader.GetAsync(TimeSpan.FromMilliseconds(20));

        Assert.Equal(1, f.Counting.QueueScans);
    }

    [Fact]
    public async Task RunSummariesStillSizeRunsFromTheirCounter()
    {
        // The counter reads moved out of the snapshot and into the one caller that reads them; this is
        // the behaviour that must survive the move.
        var f = await NewFixtureAsync();
        await f.Store.InitRemainingAsync("run", 10);
        await f.Store.DecrementRemainingAsync("run", 1);
        await f.Queue.EnqueueBatchAsync("run",
            Enumerable.Range(0, 9).Select(i => ($"task-{i}", 4)).ToList(), At(0));

        f.Counting.Reset();
        var summary = Assert.Single(await f.Reader.GetRunSummariesAsync(), s => s.Name == "run");

        Assert.Equal(10, summary.Total);
        Assert.Equal(1, summary.Completed);

        // Resolved here, and only here: one point read for the one active run.
        Assert.Equal(1, f.Counting.PointReads);
    }
}

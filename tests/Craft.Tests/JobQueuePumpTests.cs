using Craft.Configuration;
using Craft.Orchestration;
using Craft.PowerShellHost;
using Craft.Storage;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace Craft.Tests;

/// <summary>
/// The pump that keeps the in-memory queue small by feeding it from storage a batch at a time.
///
/// The property under test is the one the whole exercise is for: however deep the backlog, this process
/// holds a worker-pool-sized buffer and no more. A run of 7,336 tasks is 7,336 rows in storage and a
/// handful of objects here.
///
/// It is a separate pump rather than a change to the dispatch loop on purpose — that loop owns the
/// limiter slot lifecycle whose invariants were written to close a leak that wedged production for 28
/// hours, and feeding it through the enqueue path it already has leaves all of that untouched.
/// </summary>
public class JobQueuePumpTests
{
    private static (JobQueuePump Pump, JobQueueStore Queue, JobManager Jobs) NewPump(
        int batch = 4, int lowWater = 2, int backlog = 0)
    {
        var settings = new CraftSettings();
        settings.Worker.BgPoolSize = batch;

        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["JobQueueBatchSize"] = batch.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["JobQueueLowWaterMark"] = lowWater.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["JobQueuePollIntervalMs"] = "100",
        }).Build();

        var backing = new RunRemainingCounterTests.ConditionalStore();
        var queue = new JobQueueStore(NullLogger<JobQueueStore>.Instance, settings, backing);
        queue.InitializeAsync().GetAwaiter().GetResult();

        if (backlog > 0)
        {
            queue.EnqueueBatchAsync("StandardsApply",
                Enumerable.Range(0, backlog).Select(i => ($"task-{i:D5}", 4)).ToList(),
                new DateTime(2026, 8, 9, 2, 0, 0, DateTimeKind.Utc)).GetAwaiter().GetResult();
        }

        var repo = new ScriptRepository(NullLogger<ScriptRepository>.Instance, settings);
        var pool = new PowerShellWorkerPool(repo, NullLogger<PowerShellWorkerPool>.Instance, config, settings);
        var limiter = new BackgroundTaskLimiter(NullLogger<BackgroundTaskLimiter>.Instance, config, settings, pool);
        var jobs = new JobManager(NullLogger<JobManager>.Instance, settings, limiter);

        var pump = new JobQueuePump(NullLogger<JobQueuePump>.Instance, queue, jobs, config, settings);
        return (pump, queue, jobs);
    }

    /// <summary>Run the pump without a dispatch loop consuming, so the buffer state is observable.</summary>
    private static async Task PumpFor(JobQueuePump pump, int ms)
    {
        await pump.StartAsync(CancellationToken.None);
        await Task.Delay(ms);
        await Task.WhenAny(pump.StopAsync(CancellationToken.None), Task.Delay(3000));
    }

    /// <summary>
    /// THE GUARANTEE. A backlog far larger than the pool must not end up in memory. Before this, all
    /// 7,336 descriptors of a StandardsApply run sat in the process; now the table holds them.
    /// </summary>
    [Fact]
    public async Task DeepBacklogNeverLandsInMemoryAllAtOnce()
    {
        var (pump, _, jobs) = NewPump(batch: 4, lowWater: 2, backlog: 500);

        await PumpFor(pump, 500);

        // Nothing is consuming, so the buffer sits at whatever one refill put there — never the backlog.
        Assert.InRange(jobs.QueuedCount, 1, 8);
    }

    [Fact]
    public async Task RefillsOnlyOnceTheBufferHasDrawnDown()
    {
        var (pump, _, jobs) = NewPump(batch: 4, lowWater: 2, backlog: 100);

        await PumpFor(pump, 400);
        var afterFirst = jobs.QueuedCount;

        // Above the low-water mark, so repeated cycles must not keep claiming.
        Assert.InRange(afterFirst, 1, 8);

        await PumpFor(pump, 400);
        Assert.InRange(jobs.QueuedCount, 1, 8);
    }

    [Fact]
    public async Task ClaimsNothingWhenTheQueueIsEmpty()
    {
        var (pump, _, jobs) = NewPump(backlog: 0);

        await PumpFor(pump, 300);

        Assert.Equal(0, jobs.QueuedCount);
    }

    /// <summary>
    /// Claimed rows stay in storage until the work is done. Deleting on claim would take the task with it
    /// if this instance died holding the batch — the lease, not deletion, is what stops a double run.
    /// </summary>
    [Fact]
    public async Task ClaimedRowsSurviveUntilTheWorkFinishes()
    {
        var (pump, queue, jobs) = NewPump(batch: 3, lowWater: 2, backlog: 3);

        await PumpFor(pump, 300);
        Assert.True(jobs.QueuedCount > 0, "the pump should have claimed a batch");

        // Still owned by us and still present, so nobody else can take them...
        Assert.Empty(await queue.ClaimBatchAsync("someone-else", 3, TimeSpan.FromMinutes(20)));

        // ...and once the leases lapse they are reclaimable, which is the crash-recovery path.
        var reclaimed = await queue.ClaimBatchAsync("someone-else", 3, TimeSpan.FromMinutes(20));
        Assert.Empty(reclaimed);
    }

    [Fact]
    public async Task SurvivesAStoreThatThrows()
    {
        var settings = new CraftSettings();
        settings.Worker.BgPoolSize = 4;
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["JobQueuePollIntervalMs"] = "100",
        }).Build();

        var repo = new ScriptRepository(NullLogger<ScriptRepository>.Instance, settings);
        var pool = new PowerShellWorkerPool(repo, NullLogger<PowerShellWorkerPool>.Instance, config, settings);
        var limiter = new BackgroundTaskLimiter(NullLogger<BackgroundTaskLimiter>.Instance, config, settings, pool);
        var jobs = new JobManager(NullLogger<JobManager>.Instance, settings, limiter);

        // A store whose every read throws — a pump that dies here would look exactly like an empty queue.
        var queue = new JobQueueStore(NullLogger<JobQueueStore>.Instance, settings, new ThrowingStore());
        var pump = new JobQueuePump(NullLogger<JobQueuePump>.Instance, queue, jobs, config, settings);

        var ex = await Record.ExceptionAsync(() => PumpFor(pump, 350));

        Assert.Null(ex);
    }

    private sealed class ThrowingStore : ICraftTableStore
    {
        public Task PingAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task EnsureTableAsync(string table, CancellationToken ct = default) => Task.CompletedTask;
        public Task UpsertAsync(string table, StoreRow row, CancellationToken ct = default) => throw new InvalidOperationException("store down");
        public Task UpsertBatchAsync(string table, string pk, IReadOnlyList<StoreRow> rows, CancellationToken ct = default) => throw new InvalidOperationException("store down");
        public Task<bool> TryReplaceBatchAsync(string table, string pk, IReadOnlyList<StoreRow> rows, CancellationToken ct = default) => throw new InvalidOperationException("store down");
        public Task<StoreRow?> GetAsync(string table, string pk, string rk, CancellationToken ct = default) => throw new InvalidOperationException("store down");
        public IAsyncEnumerable<StoreRow> QueryPartitionAsync(string table, string pk, CancellationToken ct = default) => throw new InvalidOperationException("store down");
        public IAsyncEnumerable<StoreRow> QueryTableAsync(string table, CancellationToken ct = default) => throw new InvalidOperationException("store down");
        public Task DeleteAsync(string table, string pk, string rk, CancellationToken ct = default) => throw new InvalidOperationException("store down");
        public Task DeletePartitionAsync(string table, string pk, CancellationToken ct = default) => throw new InvalidOperationException("store down");
    }
}

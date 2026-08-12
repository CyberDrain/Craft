using Craft.Configuration;
using Craft.Orchestration;
using Craft.PowerShellHost;
using Craft.Storage;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace Craft.Tests;

/// <summary>
/// The table-backed status view. Since task ownership moved into the queue table, the in-memory
/// JobManager holds only a worker-pool-sized buffer — so every status consumer that reads it as "the
/// queue" reports a 7,000-task fan-out as eight queued jobs. These tests pin the merge semantics: the
/// durable backlog is counted and listed, claimed rows are never double-counted against the local
/// records that represent them, and run sizes come from the counter row rather than from whatever
/// slice this instance happened to claim.
/// </summary>
public class JobQueueStatusReaderTests
{
    private static readonly TimeSpan Lease = TimeSpan.FromMinutes(20);
    private static readonly TimeSpan Fresh = TimeSpan.Zero;

    private sealed class Fixture
    {
        public required RunRemainingCounterTests.ConditionalStore Backing { get; init; }
        public required JobQueueStore Queue { get; init; }
        public required OrchestratorTableStore Store { get; init; }
        public required JobManager Jobs { get; init; }
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

        var backing = new RunRemainingCounterTests.ConditionalStore();
        var queue = new JobQueueStore(NullLogger<JobQueueStore>.Instance, settings, backing);
        var store = new OrchestratorTableStore(NullLogger<OrchestratorTableStore>.Instance, settings, backing);
        await queue.InitializeAsync();
        await store.InitializeAsync();

        return new Fixture
        {
            Backing = backing,
            Queue = queue,
            Store = store,
            Jobs = jobs,
            Reader = new JobQueueStatusReader(NullLogger<JobQueueStatusReader>.Instance, jobs, queue, store),
        };
    }

    private static DateTime At(int minute) => new(2026, 8, 12, 3, minute, 0, DateTimeKind.Utc);

    [Fact]
    public async Task SummaryCountsTheDurableBacklog_NotJustTheLocalBuffer()
    {
        var f = await NewFixtureAsync();
        await f.Queue.EnqueueBatchAsync("StandardsApply",
            Enumerable.Range(0, 120).Select(i => ($"std-{i}", 4)).ToList(), At(5));

        var summary = await f.Reader.GetSummaryAsync();

        Assert.Equal(120, summary.Queued);
        Assert.Equal(120, summary.QueuedDurable);
        Assert.Equal(0, summary.QueuedLocal);
        Assert.Equal(At(5), summary.OldestQueuedUtc);
    }

    [Fact]
    public async Task ClaimedRowsAreNotCountedAsQueued()
    {
        var f = await NewFixtureAsync();
        await f.Queue.EnqueueBatchAsync("run",
            Enumerable.Range(0, 10).Select(i => ($"task-{i}", 4)).ToList(), At(0));
        var claimed = await f.Queue.ClaimBatchAsync("worker-a", 4, Lease);
        Assert.Equal(4, claimed.Count);

        var summary = await f.Reader.GetSummaryAsync();

        // The four claimed rows are some instance's buffer — represented by its local records, not by
        // the backlog count.
        Assert.Equal(6, summary.QueuedDurable);
        Assert.Equal(6, summary.Queued);
    }

    [Fact]
    public async Task JobDetails_ListTheBacklog_WithoutDuplicatingLocallyClaimedWork()
    {
        var f = await NewFixtureAsync();
        await f.Queue.EnqueueAsync("run", "claimed-here", 4, At(0));
        await f.Queue.EnqueueAsync("run", "claimed-elsewhere", 4, At(1));
        await f.Queue.EnqueueAsync("run", "waiting", 4, At(2));

        // "claimed-here" is what the pump does: claim the row, enqueue the descriptor locally.
        var mine = await f.Queue.ClaimBatchAsync("this-node", 1, Lease);
        Assert.Equal("claimed-here", Assert.Single(mine).TaskId);
        f.Jobs.Enqueue(new JobDescriptor("run", "claimed-here", 4), "run-claimed-here");

        // Another instance's claim: a row under lease with no local record at all.
        var theirs = await f.Queue.ClaimBatchAsync("other-node", 1, Lease);
        Assert.Equal("claimed-elsewhere", Assert.Single(theirs).TaskId);

        var details = await f.Reader.GetJobDetailsAsync();

        // claimed-here once (the local record), waiting once (durable), claimed-elsewhere not at all —
        // it is running inside another instance and its records live there.
        Assert.Equal(2, details.Count);
        Assert.Single(details, d => d.Id == "run-claimed-here");
        var waiting = Assert.Single(details, d => d.Id == "run-waiting");
        Assert.Equal("Queued", waiting.Status);
        Assert.Equal(At(2), waiting.QueuedUtc);
        Assert.True(waiting.WaitSeconds > 0);
    }

    [Fact]
    public async Task JobDetails_RespectStatusFilterAndLimit()
    {
        var f = await NewFixtureAsync();
        await f.Queue.EnqueueBatchAsync("run",
            Enumerable.Range(0, 5).Select(i => ($"task-{i}", 4)).ToList(), At(0));

        // A non-Queued filter describes claimed work, which only local records know about.
        Assert.Empty(await f.Reader.GetJobDetailsAsync(status: "Running"));

        var queuedOnly = await f.Reader.GetJobDetailsAsync(status: "Queued");
        Assert.Equal(5, queuedOnly.Count);
        Assert.All(queuedOnly, d => Assert.Equal("Queued", d.Status));

        Assert.Equal(3, (await f.Reader.GetJobDetailsAsync(limit: 3)).Count);
    }

    [Fact]
    public async Task RunSummaries_SizeARunFromItsCounter_NotFromTheClaimedSlice()
    {
        var f = await NewFixtureAsync();

        // A 10-task run: one durably finished, one claimed into this instance, eight still queued.
        await f.Store.InitRemainingAsync("run", 10);
        await f.Store.DecrementRemainingAsync("run", 1);
        await f.Queue.EnqueueBatchAsync("run",
            Enumerable.Range(0, 9).Select(i => ($"task-{i}", 4)).ToList(), At(0));
        var claimed = await f.Queue.ClaimBatchAsync("this-node", 1, Lease);
        f.Jobs.Enqueue(new JobDescriptor("run", claimed[0].TaskId, 4), $"run-{claimed[0].TaskId}");

        var summary = Assert.Single(await f.Reader.GetRunSummariesAsync(), s => s.Name == "run");

        Assert.Equal(10, summary.Total);
        Assert.Equal(9, summary.Queued);      // 8 unclaimed + 1 buffered locally
        Assert.Equal(1, summary.Completed);   // Total − Remaining, durable across restarts
    }

    [Fact]
    public async Task RunSummaries_SynthesizeARunTheJobManagerHasNeverSeen()
    {
        var f = await NewFixtureAsync();
        await f.Store.InitRemainingAsync("cold-run", 50);
        await f.Queue.EnqueueBatchAsync("cold-run",
            Enumerable.Range(0, 50).Select(i => ($"task-{i}", 3)).ToList(), At(0));

        var summary = Assert.Single(await f.Reader.GetRunSummariesAsync(), s => s.Name == "cold-run");

        Assert.Equal(50, summary.Total);
        Assert.Equal(50, summary.Queued);
        Assert.Equal(3, summary.Priority);
        Assert.Equal(0, summary.Running);
    }

    [Fact]
    public async Task AFailedRefreshServesThePreviousSnapshot()
    {
        var f = await NewFixtureAsync();
        await f.Queue.EnqueueAsync("run", "task-0", 4, At(0));

        var first = await f.Reader.GetAsync(Fresh);
        Assert.NotNull(first);
        Assert.Equal(1, first!.Unclaimed);

        f.Backing.OnBeforeQuery = () => throw new InvalidOperationException("storage is down");
        var second = await f.Reader.GetAsync(Fresh);

        // Stale data with an honest timestamp, not an exception on a health endpoint.
        Assert.Same(first, second);
    }

    [Fact]
    public async Task SnapshotAggregatesPerRun()
    {
        var f = await NewFixtureAsync();
        await f.Queue.EnqueueBatchAsync("run-a",
            Enumerable.Range(0, 3).Select(i => ($"a-{i}", 4)).ToList(), At(0));
        await f.Queue.EnqueueBatchAsync("run-b",
            Enumerable.Range(0, 2).Select(i => ($"b-{i}", 1)).ToList(), At(1));

        var snap = await f.Reader.GetAsync(Fresh);

        Assert.NotNull(snap);
        Assert.Equal(5, snap!.Total);
        Assert.Equal(3, snap.ByRun["run-a"].Unclaimed);
        Assert.Equal(2, snap.ByRun["run-b"].Unclaimed);
        Assert.Equal(1, snap.ByRun["run-b"].MinPriority);
        Assert.Equal(At(0), snap.OldestUnclaimedUtc);
    }
}

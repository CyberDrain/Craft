using Craft.Configuration;
using Craft.Storage;
using Microsoft.Extensions.Logging.Abstractions;

namespace Craft.Tests;

/// <summary>
/// The index table answers "does this run still have queued work" from a single partition, which is
/// what keeps the re-drive and finalize paths off a full-table scan. But the index can OUTLIVE the
/// queue rows it points at, and when it does it lies: it reports a task queued that no pump will ever
/// claim, so an orphan re-drive that trusts the index skips it and the run stalls indefinitely with
/// the task Pending — resume logs "Dispatched 0 tasks (N already queued)" and the orphan watchdog
/// never fires, because GetQueuedTaskIdsAsync (index-only) keeps reporting the task queued while the
/// queue table has no runnable row for it. Clearing the stale index row is the only thing that lets
/// the watchdog see it as orphaned again.
///
/// <see cref="JobQueueStore.GetDispatchableTaskIdsAsync"/> is the fix: it verifies each candidate
/// against the queue TABLE, returning only tasks the pump can actually still claim. These tests pin
/// the three divergence shapes it has to catch.
/// </summary>
public class JobQueueDispatchableTests
{
    private static readonly TimeSpan Lease = TimeSpan.FromMinutes(20);
    private const string QueueTable = "OrchestratorQueue";

    private static (JobQueueStore Queue, RunRemainingCounterTests.ConditionalStore Backing) NewQueue()
    {
        var backing = new RunRemainingCounterTests.ConditionalStore();
        var queue = new JobQueueStore(NullLogger<JobQueueStore>.Instance, new CraftSettings(), backing);
        return (queue, backing);
    }

    [Fact]
    public async Task NormallyQueuedTasksAreAllDispatchable()
    {
        var (queue, _) = NewQueue();
        await queue.InitializeAsync();
        await queue.EnqueueBatchAsync("R", [("a", 4), ("b", 4), ("c", 4)], DateTime.UtcNow);

        var dispatchable = await queue.GetDispatchableTaskIdsAsync("R", ["a", "b", "c"]);

        Assert.Equal(3, dispatchable.Count);
        Assert.Contains("a", dispatchable);
        Assert.Contains("b", dispatchable);
        Assert.Contains("c", dispatchable);
    }

    [Fact]
    public async Task AnIndexRowWithNoQueueRowIsNotDispatchable_ButTheIndexStillListsIt()
    {
        var (queue, backing) = NewQueue();
        await queue.InitializeAsync();
        await queue.EnqueueBatchAsync("R", [("a", 4), ("b", 4)], DateTime.UtcNow);

        // Delete ONLY b's queue row, leaving its index row — the exact divergence a crash between the
        // two deletes, or a run carried in from a pre-pump build, leaves behind.
        await backing.DeleteAsync(QueueTable, JobQueueStore.Bucket(4), JobQueueStore.BuildRowKey("R", "b"));

        // The index — what the old re-drive trusted — still reports both as queued.
        var indexView = await queue.GetQueuedTaskIdsAsync("R");
        Assert.Contains("a", indexView);
        Assert.Contains("b", indexView);

        // The queue-verified view sees b for the ghost it is.
        var dispatchable = await queue.GetDispatchableTaskIdsAsync("R", ["a", "b"]);
        Assert.Contains("a", dispatchable);
        Assert.DoesNotContain("b", dispatchable);
    }

    [Fact]
    public async Task AQueueRowOwnedWithNoLeaseIsNotDispatchable()
    {
        var (queue, backing) = NewQueue();
        await queue.InitializeAsync();
        await queue.EnqueueBatchAsync("R", [("a", 4)], DateTime.UtcNow);

        // Owned with no LeaseUntil: neither "Owner eq ''" nor "LeaseUntil lt now", so the claim filter
        // can never match it and the pump will never dispatch it — a ghost as surely as a missing row.
        var key = JobQueueStore.BuildRowKey("R", "a");
        var row = await backing.GetAsync(QueueTable, JobQueueStore.Bucket(4), key);
        Assert.NotNull(row);
        row!["Owner"] = "dead-instance";
        row["LeaseUntil"] = (DateTimeOffset?)null;
        await backing.UpsertAsync(QueueTable, row);

        var dispatchable = await queue.GetDispatchableTaskIdsAsync("R", ["a"]);
        Assert.Empty(dispatchable);
    }

    [Fact]
    public async Task AQueueRowUnderALeaseStaysDispatchable()
    {
        var (queue, _) = NewQueue();
        await queue.InitializeAsync();
        await queue.EnqueueBatchAsync("R", [("a", 4)], DateTime.UtcNow);

        // A live claim: owned under a lease. The pump (or its lapse) will run it, so the re-drive must
        // NOT treat it as orphaned — doing so would reset a row a worker is actively holding and could
        // run the task a second time.
        var claimed = await queue.ClaimBatchAsync("worker-a", 8, Lease);
        Assert.Single(claimed);

        var dispatchable = await queue.GetDispatchableTaskIdsAsync("R", ["a"]);
        Assert.Contains("a", dispatchable);
    }
}

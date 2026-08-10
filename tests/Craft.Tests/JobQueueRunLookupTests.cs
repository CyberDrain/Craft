using Craft.Configuration;
using Craft.Storage;
using Microsoft.Extensions.Logging.Abstractions;

namespace Craft.Tests;

/// <summary>
/// The lookup that tells a re-drive "waiting" from "lost".
///
/// RedrivePendingTasks used to decide a Pending task was orphaned when the JobManager did not have it
/// queued or running. Under the pump that is what a BACKLOG is — the pump buffers a worker-pool-sized
/// slice and leaves the rest in storage — so the re-drive re-queued the whole un-started backlog every
/// 60 seconds. Queue RowKeys are prefixed with the enqueue timestamp, so each pass added another row
/// for the same task instead of updating the first, and every copy was independently claimable.
///
/// Measured live before the fix, on one 124-task run: re-drives of 92, 60, 60, 52, 44, 36 tasks on
/// consecutive ticks, six queue rows for a single task exactly 60s apart, and that task executing six
/// times.
/// </summary>
public class JobQueueRunLookupTests
{
    private static JobQueueStore NewQueue()
    {
        var settings = new CraftSettings();
        var queue = new JobQueueStore(NullLogger<JobQueueStore>.Instance, settings,
            new RunRemainingCounterTests.ConditionalStore());
        queue.InitializeAsync().GetAwaiter().GetResult();
        return queue;
    }

    private static DateTime At(int minute) => new(2026, 8, 9, 2, minute, 0, DateTimeKind.Utc);

    [Fact]
    public async Task QueuedTaskIds_ReportsWaitingWork_SoABacklogIsNotMistakenForOrphans()
    {
        var queue = NewQueue();
        await queue.EnqueueBatchAsync("run-a",
            [("task-0", 4), ("task-1", 4), ("task-2", 4)], At(0));
        await queue.EnqueueBatchAsync("other-run", [("task-9", 4)], At(0));

        var ids = await queue.GetQueuedTaskIdsAsync("run-a");

        Assert.Equal(["task-0", "task-1", "task-2"], ids.OrderBy(x => x, StringComparer.Ordinal));
        Assert.DoesNotContain("task-9", ids);
    }

    [Fact]
    public async Task AClaimedTaskIsStillReported_BecauseItIsRunning_NotLost()
    {
        // The re-drive must not re-queue work that has been handed to a worker: it is the case that
        // produced duplicate rows for tasks already executing.
        var queue = NewQueue();
        await queue.EnqueueBatchAsync("run-a", [("task-0", 4), ("task-1", 4)], At(0));

        var claimed = await queue.ClaimBatchAsync("worker-a", 1, TimeSpan.FromMinutes(20));
        Assert.Single(claimed);

        var ids = await queue.GetQueuedTaskIdsAsync("run-a");

        Assert.Contains(claimed[0].TaskId, ids);
        Assert.Equal(2, ids.Count);
    }

    [Fact]
    public async Task ATaskWhoseRowIsGone_IsNotReported_SoARealOrphanIsStillRecoverable()
    {
        // The guard has to stay specific — a task whose row genuinely vanished must still be re-driven,
        // which is the whole reason the re-drive exists.
        var queue = NewQueue();
        await queue.EnqueueBatchAsync("run-a", [("task-0", 4), ("task-1", 4)], At(0));

        var claimed = await queue.ClaimBatchAsync("worker-a", 2, TimeSpan.FromMinutes(20));
        await queue.RemoveAsync(claimed.Single(c => c.TaskId == "task-0"));

        var ids = await queue.GetQueuedTaskIdsAsync("run-a");

        Assert.DoesNotContain("task-0", ids);
        Assert.Contains("task-1", ids);
    }

    [Fact]
    public async Task ARunWithNothingQueued_ReportsNothing()
    {
        var queue = NewQueue();
        Assert.Empty(await queue.GetQueuedTaskIdsAsync("run-with-no-rows"));
    }

    // ── Releasing the claims a crashed process was holding ────────────────────────────────────────

    /// <summary>
    /// After a crash the dead process's claims are still live as far as storage is concerned, so nothing
    /// can pick those rows up until the lease lapses — up to 30 minutes by default. Since re-dispatch
    /// now declines to write duplicate rows for tasks that already have one, the run simply stalls.
    /// Recovery has to hand the claims back.
    /// </summary>
    [Fact]
    public async Task ReleasingARunsClaims_MakesItsRowsClaimableAgain()
    {
        var queue = NewQueue();
        await queue.EnqueueBatchAsync("crashed-run", [("task-0", 4), ("task-1", 4)], At(0));

        // A long lease, as the pump takes: without a release these are untouchable for its full duration.
        var held = await queue.ClaimBatchAsync("dead-worker", 2, TimeSpan.FromMinutes(30));
        Assert.Equal(2, held.Count);
        Assert.Empty(await queue.ClaimBatchAsync("new-worker", 2, TimeSpan.FromMinutes(30)));

        var released = await queue.ReleaseRunClaimsAsync("crashed-run");

        Assert.Equal(2, released);
        Assert.Equal(2, (await queue.ClaimBatchAsync("new-worker", 2, TimeSpan.FromMinutes(30))).Count);
    }

    /// <summary>Releasing frees the existing rows rather than adding more — the duplicate-row bug again.</summary>
    [Fact]
    public async Task ReleasingClaims_DoesNotCreateExtraRows()
    {
        var queue = NewQueue();
        await queue.EnqueueBatchAsync("crashed-run", [("task-0", 4), ("task-1", 4)], At(0));
        await queue.ClaimBatchAsync("dead-worker", 2, TimeSpan.FromMinutes(30));

        await queue.ReleaseRunClaimsAsync("crashed-run");

        var ids = await queue.GetQueuedTaskIdsAsync("crashed-run");
        Assert.Equal(["task-0", "task-1"], ids.OrderBy(x => x, StringComparer.Ordinal));
        Assert.Equal(2, (await queue.ClaimBatchAsync("new-worker", 10, TimeSpan.FromMinutes(30))).Count);
    }

    /// <summary>Another run's claims are left alone — recovery is per run.</summary>
    [Fact]
    public async Task ReleasingOneRunsClaims_LeavesOtherRunsHeld()
    {
        var queue = NewQueue();
        await queue.EnqueueBatchAsync("crashed-run", [("task-0", 4)], At(0));
        await queue.EnqueueBatchAsync("healthy-run", [("task-9", 4)], At(1));
        await queue.ClaimBatchAsync("worker", 2, TimeSpan.FromMinutes(30));

        Assert.Equal(1, await queue.ReleaseRunClaimsAsync("crashed-run"));

        var reclaimed = await queue.ClaimBatchAsync("new-worker", 10, TimeSpan.FromMinutes(30));
        Assert.Equal("task-0", Assert.Single(reclaimed).TaskId);
    }
}

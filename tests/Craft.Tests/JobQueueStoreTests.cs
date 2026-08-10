using Craft.Configuration;
using Craft.Storage;
using Microsoft.Extensions.Logging.Abstractions;

namespace Craft.Tests;

/// <summary>
/// The durable job queue that lets the dispatch side hold a worker-pool-sized buffer instead of the
/// whole backlog, and makes the row in storage — not the in-memory copy — the thing that actually runs.
///
/// Two properties carry the design:
///
///   Ordering. Rows sort by partition key then row key, so a single read yields the highest-priority,
///   oldest-first work across every run. That is what the in-memory PriorityQueue gives today, and it
///   has to survive the move or a P0 audit-log task ends up behind thousands of P4 standards.
///
///   Exclusivity. A claim is one conditional transaction, so two workers cannot take the same task —
///   the loser is rejected and retries rather than forcing the write. This is the property the
///   28-hour deadlock's redrive heuristic was standing in for.
/// </summary>
public class JobQueueStoreTests
{
    private static readonly TimeSpan Lease = TimeSpan.FromMinutes(20);

    private static (JobQueueStore Queue, RunRemainingCounterTests.ConditionalStore Backing) NewQueue()
    {
        var backing = new RunRemainingCounterTests.ConditionalStore();
        var queue = new JobQueueStore(NullLogger<JobQueueStore>.Instance, new CraftSettings(), backing);
        return (queue, backing);
    }

    private static DateTime At(int minute) => new(2026, 8, 9, 2, minute, 0, DateTimeKind.Utc);

    [Fact]
    public async Task ClaimsHighestPriorityFirstAcrossRuns()
    {
        var (queue, _) = NewQueue();
        await queue.InitializeAsync();

        // Queued in the least helpful order: the low-priority bulk first, the urgent one last.
        await queue.EnqueueBatchAsync("StandardsApply",
            Enumerable.Range(0, 5).Select(i => ($"std-{i}", 4)).ToList(), At(0));
        await queue.EnqueueAsync("AuditLogIngest", "audit-0", 0, At(5));

        var claimed = await queue.ClaimBatchAsync("worker-a", 2, Lease);

        Assert.Single(claimed);
        Assert.Equal("audit-0", claimed[0].TaskId);
        Assert.Equal("AuditLogIngest", claimed[0].RunName);
    }

    [Fact]
    public async Task ClaimsOldestFirstWithinAPriority()
    {
        var (queue, _) = NewQueue();
        await queue.InitializeAsync();

        await queue.EnqueueAsync("run", "newer", 4, At(10));
        await queue.EnqueueAsync("run", "older", 4, At(1));

        var claimed = await queue.ClaimBatchAsync("worker-a", 1, Lease);

        Assert.Equal("older", Assert.Single(claimed).TaskId);
    }

    /// <summary>
    /// THE GUARANTEE. Two workers claiming at once must not both get the same task. The batch is one
    /// conditional transaction, so the loser comes back empty and retries.
    /// </summary>
    [Fact]
    public async Task TwoWorkersNeverClaimTheSameTask()
    {
        var (queue, backing) = NewQueue();
        await queue.InitializeAsync();
        await queue.EnqueueBatchAsync("run", Enumerable.Range(0, 4).Select(i => ($"task-{i}", 4)).ToList(), At(0));

        // Land a competing claim in the window between our read and our write.
        IReadOnlyList<JobQueueStore.ClaimedJob> competitor = [];
        backing.OnBeforeConditionalWrite = () =>
        {
            backing.OnBeforeConditionalWrite = null;
            competitor = queue.ClaimBatchAsync("worker-b", 4, Lease).GetAwaiter().GetResult();
        };

        var mine = await queue.ClaimBatchAsync("worker-a", 4, Lease);

        Assert.Equal(4, competitor.Count);
        Assert.Empty(mine);

        // And nothing is left claimable, rather than the rows being double-owned.
        Assert.Empty(await queue.ClaimBatchAsync("worker-c", 4, Lease));
    }

    [Fact]
    public async Task ClaimedWorkIsNotHandedOutAgainWhileTheLeaseHolds()
    {
        var (queue, _) = NewQueue();
        await queue.InitializeAsync();
        await queue.EnqueueBatchAsync("run", Enumerable.Range(0, 3).Select(i => ($"task-{i}", 4)).ToList(), At(0));

        var first = await queue.ClaimBatchAsync("worker-a", 2, Lease);
        var second = await queue.ClaimBatchAsync("worker-b", 2, Lease);

        Assert.Equal(2, first.Count);
        Assert.Single(second);
        Assert.Empty(first.Select(j => j.TaskId).Intersect(second.Select(j => j.TaskId)));
    }

    /// <summary>
    /// A worker that dies holding a claim must give the work back on its own. This is what replaces the
    /// age-based re-drive: nothing has to notice the worker is gone.
    /// </summary>
    [Fact]
    public async Task ExpiredLeaseMakesWorkClaimableAgain()
    {
        var (queue, _) = NewQueue();
        await queue.InitializeAsync();
        await queue.EnqueueAsync("run", "task-0", 4, At(0));

        var held = await queue.ClaimBatchAsync("worker-a", 1, TimeSpan.FromMilliseconds(40));
        Assert.Single(held);

        Assert.Empty(await queue.ClaimBatchAsync("worker-b", 1, Lease));

        await Task.Delay(120);

        var reclaimed = await queue.ClaimBatchAsync("worker-b", 1, Lease);
        Assert.Equal("task-0", Assert.Single(reclaimed).TaskId);
    }

    [Fact]
    public async Task RenewingKeepsTheClaimAndFailsOnceItHasLapsed()
    {
        var (queue, _) = NewQueue();
        await queue.InitializeAsync();
        await queue.EnqueueAsync("run", "task-0", 4, At(0));

        var held = await queue.ClaimBatchAsync("worker-a", 1, TimeSpan.FromMilliseconds(40));

        Assert.True(await queue.RenewAsync(held, "worker-a", Lease));
        Assert.Empty(await queue.ClaimBatchAsync("worker-b", 1, Lease));

        // Someone else now owns it — renewal must report that rather than taking it back.
        await queue.RemoveAsync(held[0]);
        await queue.EnqueueAsync("run", "task-0", 4, At(0));
        await queue.ClaimBatchAsync("worker-b", 1, Lease);

        Assert.False(await queue.RenewAsync(held, "worker-a", Lease));
    }

    [Fact]
    public async Task RemovedWorkIsGoneFromTheQueue()
    {
        var (queue, _) = NewQueue();
        await queue.InitializeAsync();
        await queue.EnqueueBatchAsync("run", [("task-0", 4), ("task-1", 4)], At(0));

        var claimed = await queue.ClaimBatchAsync("worker-a", 2, Lease);
        foreach (var job in claimed) await queue.RemoveAsync(job);

        // Nothing is left, even once the leases would have lapsed.
        Assert.Empty(await queue.ClaimBatchAsync("worker-b", 2, TimeSpan.Zero));
    }

    [Fact]
    public async Task RemovingARunClearsOnlyThatRun()
    {
        var (queue, _) = NewQueue();
        await queue.InitializeAsync();
        await queue.EnqueueBatchAsync("doomed", [("a", 4), ("b", 4)], At(0));
        await queue.EnqueueAsync("survivor", "c", 4, At(1));

        await queue.RemoveRunAsync("doomed");

        var left = await queue.ClaimBatchAsync("worker-a", 10, Lease);
        Assert.Equal("survivor", Assert.Single(left).RunName);
    }

    [Fact]
    public void PriorityBucketsSortNumericallyNotLexically()
    {
        // "P4" vs "P10" would order 10 before 4 as strings, quietly inverting priority.
        Assert.Equal("P00", JobQueueStore.Bucket(0));
        Assert.Equal("P04", JobQueueStore.Bucket(4));
        Assert.Equal("P10", JobQueueStore.Bucket(10));
        Assert.True(string.CompareOrdinal(JobQueueStore.Bucket(4), JobQueueStore.Bucket(10)) < 0);
    }

    [Fact]
    public void RowKeysSortByQueueTimeAcrossTickDigitBoundaries()
    {
        var early = JobQueueStore.BuildRowKey(new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc), "r", "t");
        var later = JobQueueStore.BuildRowKey(new DateTime(2026, 1, 1, 0, 0, 1, DateTimeKind.Utc), "r", "t");

        Assert.True(string.CompareOrdinal(early, later) < 0);
        Assert.Equal(19, early.IndexOf('-', StringComparison.Ordinal));
    }

    [Fact]
    public async Task EmptyQueueClaimsNothingRatherThanBlocking()
    {
        var (queue, _) = NewQueue();
        await queue.InitializeAsync();

        Assert.Empty(await queue.ClaimBatchAsync("worker-a", 8, Lease));
    }
}

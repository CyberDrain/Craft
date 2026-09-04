using Craft.Configuration;
using Craft.Storage;
using Microsoft.Extensions.Logging.Abstractions;

namespace Craft.Tests;

/// <summary>
/// The one-time build of the run index, for a queue table that predates it.
///
/// This is the only part of the index change that touches a queue nobody wrote through the new enqueue
/// path, and it runs against the worst queue in the estate: the instance that motivated the change was
/// carrying ~743,000 rows across 12,028 runs. Two properties matter and neither is observable from the
/// happy path.
///
///   1. It actually indexes what is already there. Without this every pre-existing run reads as having
///      no queued tasks, and the orphan re-drive re-queues a backlog that already has rows — the
///      duplicate-execution failure the queue exists to prevent.
///   2. It runs exactly once, ever. It is a full pass over the queue table, awaited before the service
///      serves traffic, so a version that re-ran on every start would add a multi-minute stall to every
///      restart of the largest instances.
/// </summary>
public class JobQueueIndexBackfillTests
{
    private static DateTime At(int minute) => new(2026, 8, 9, 2, minute, 0, DateTimeKind.Utc);

    private static (JobQueueStore Queue, RunRemainingCounterTests.ConditionalStore Store, string QueueTable) NewQueue()
    {
        var settings = new CraftSettings();
        var store = new RunRemainingCounterTests.ConditionalStore();
        var queue = new JobQueueStore(NullLogger<JobQueueStore>.Instance, settings, store);
        return (queue, store, $"{settings.Orchestrator.TablePrefix}Queue");
    }

    /// <summary>
    /// A queue row exactly as the v1 code wrote it: the legacy time-prefixed key
    /// (<c>{ticks:D19}-{run}-{task}</c>), no <c>QueuedUtc</c> property, no index row anywhere. This is what
    /// the v2 migration must re-key.
    /// </summary>
    private static Task SeedLegacyRowAsync(RunRemainingCounterTests.ConditionalStore store, string queueTable,
        string runName, string taskId, int priority, DateTime queuedUtc)
    {
        var legacyKey = $"{queuedUtc.Ticks.ToString("D19", System.Globalization.CultureInfo.InvariantCulture)}-{runName}-{taskId}";
        return store.UpsertAsync(queueTable,
            new StoreRow(JobQueueStore.Bucket(priority), legacyKey)
            {
                Properties =
                {
                    ["RunName"] = runName,
                    ["TaskId"] = taskId,
                    ["Priority"] = priority,
                    ["Owner"] = "",
                    ["LeaseUntil"] = (DateTimeOffset?)null,
                }
            });
    }

    [Fact]
    public async Task BuildsTheIndexForAQueueThatPredatesIt()
    {
        var (queue, store, queueTable) = NewQueue();

        await SeedLegacyRowAsync(store, queueTable, "run-a", "task-0", 4, At(0));
        await SeedLegacyRowAsync(store, queueTable, "run-a", "task-1", 4, At(0));
        await SeedLegacyRowAsync(store, queueTable, "run-b", "task-9", 0, At(1));

        await queue.InitializeAsync();

        Assert.Equal(["task-0", "task-1"],
            (await queue.GetQueuedTaskIdsAsync("run-a")).OrderBy(x => x, StringComparer.Ordinal));
        Assert.Equal(["task-9"], await queue.GetQueuedTaskIdsAsync("run-b"));
    }

    /// <summary>
    /// v2 re-keys a legacy time-prefixed row to the deterministic <c>{run}|{task}</c> scheme, carrying its
    /// enqueue time into the QueuedUtc property, deleting the old row, and leaving the task claimable once.
    /// </summary>
    [Fact]
    public async Task MigratesLegacyRowsToTheDeterministicKeyScheme()
    {
        var (queue, store, queueTable) = NewQueue();
        await SeedLegacyRowAsync(store, queueTable, "run-a", "task-0", 4, At(3));

        await queue.InitializeAsync();

        var rows = new List<StoreRow>();
        await foreach (var row in store.QueryTableAsync(queueTable)) rows.Add(row);

        // The legacy row is gone; one row remains, keyed deterministically and carrying QueuedUtc.
        var only = Assert.Single(rows);
        Assert.Equal(JobQueueStore.BuildRowKey("run-a", "task-0"), only.RowKey);
        Assert.Equal(new DateTimeOffset(At(3), TimeSpan.Zero), only.GetDateTimeOffset("QueuedUtc"));

        // And it is still claimable, exactly once.
        Assert.Equal("task-0",
            Assert.Single(await queue.ClaimBatchAsync("w", 8, TimeSpan.FromMinutes(20))).TaskId);
    }

    [Fact]
    public async Task RunsOnceAndNeverAgain()
    {
        var (queue, store, queueTable) = NewQueue();

        await SeedLegacyRowAsync(store, queueTable, "run-a", "task-0", 4, At(0));
        await queue.InitializeAsync();
        Assert.Equal(["task-0"], await queue.GetQueuedTaskIdsAsync("run-a"));

        // A second un-indexed row, then a fresh store instance over the SAME tables so the in-process
        // _initialized latch cannot be what short-circuits it. Only the persisted marker can.
        await SeedLegacyRowAsync(store, queueTable, "run-a", "task-1", 4, At(2));

        var second = new JobQueueStore(NullLogger<JobQueueStore>.Instance, new CraftSettings(), store);
        await second.InitializeAsync();

        // Deliberately asserting the un-indexed row is NOT picked up: that is what proves the backfill
        // short-circuited rather than silently re-running. A rebuild would report both tasks.
        Assert.Equal(["task-0"], await second.GetQueuedTaskIdsAsync("run-a"));
    }

    [Fact]
    public async Task IndexedRowsSurviveARemoveRun()
    {
        var (queue, store, queueTable) = NewQueue();

        await SeedLegacyRowAsync(store, queueTable, "run-a", "task-0", 4, At(0));
        await SeedLegacyRowAsync(store, queueTable, "run-b", "task-9", 4, At(0));
        await queue.InitializeAsync();

        await queue.RemoveRunAsync("run-a");

        Assert.Empty(await queue.GetQueuedTaskIdsAsync("run-a"));
        Assert.Equal(["task-9"], await queue.GetQueuedTaskIdsAsync("run-b"));

        // The backfilled queue rows must be gone too, not just their index entries — otherwise a
        // cancelled run's work stays claimable.
        var left = new List<string>();
        await foreach (var row in store.QueryTableAsync(queueTable))
        {
            if (row.GetString("RunName") == "run-a") left.Add(row.RowKey);
        }
        Assert.Empty(left);
    }

    /// <summary>
    /// Run names carry a user-supplied scheduled-task name, and Azure Tables rejects '/', '\', '#', '?'
    /// and control characters in a key. A real one in production is
    /// "UserTaskOrchestrator_AllTenants: Alert on Huntress Rogue Apps detected-{guid}"; nothing stops the
    /// next one containing a slash. Unescaped, the index write throws and the run has no index at all.
    /// </summary>
    [Theory]
    [InlineData("run/with/slashes")]
    [InlineData("run?with=query")]
    [InlineData(@"run\with\backslash")]
    [InlineData("run#with-hash")]
    [InlineData("run%already-escaped")]
    [InlineData("UserTaskOrchestrator_AllTenants: Alert on 100% CPU? detected-abc123")]
    public async Task RunNamesWithKeyIllegalCharactersRoundTrip(string runName)
    {
        var (queue, _, _) = NewQueue();
        await queue.InitializeAsync();

        await queue.EnqueueBatchAsync(runName, [("task-0", 4)], At(0));
        await queue.EnqueueBatchAsync("run-plain", [("task-9", 4)], At(0));

        var partition = JobQueueStore.IndexPartition(runName);
        Assert.DoesNotContain(partition, c => c is '/' or '\\' or '#' or '?' || char.IsControl(c));

        Assert.Equal(["task-0"], await queue.GetQueuedTaskIdsAsync(runName));
        Assert.Equal(["task-9"], await queue.GetQueuedTaskIdsAsync("run-plain"));
    }

    /// <summary>Distinct run names must not collide once escaped, or one run's cleanup drops another's.</summary>
    [Fact]
    public async Task EscapingDoesNotCollideAcrossRunNames()
    {
        // "a/b" escapes to "a%2Fb"; a run literally named "a%2Fb" must land somewhere else, which is why
        // '%' is itself escaped.
        Assert.NotEqual(JobQueueStore.IndexPartition("a/b"), JobQueueStore.IndexPartition("a%2Fb"));

        var (queue, _, _) = NewQueue();
        await queue.InitializeAsync();

        await queue.EnqueueBatchAsync("a/b", [("slash", 4)], At(0));
        await queue.EnqueueBatchAsync("a%2Fb", [("literal", 4)], At(0));

        Assert.Equal(["slash"], await queue.GetQueuedTaskIdsAsync("a/b"));
        Assert.Equal(["literal"], await queue.GetQueuedTaskIdsAsync("a%2Fb"));
    }

    [Fact]
    public void IndexRowKeySplitsAtTheBucketBoundary_EvenWithAPipeInTheRowKey()
    {
        // The queue row key embeds the run name, so a '|' can appear inside it. Splitting on the first
        // '|' rather than at the fixed bucket width would address the wrong queue row.
        const string QueueRowKey = "0000000638000000000000000-run|odd-task-0";
        var split = JobQueueStore.SplitIndexRowKey(JobQueueStore.IndexRowKey("P04", QueueRowKey));

        Assert.NotNull(split);
        Assert.Equal("P04", split!.Value.Bucket);
        Assert.Equal(QueueRowKey, split.Value.QueueRowKey);
    }
}

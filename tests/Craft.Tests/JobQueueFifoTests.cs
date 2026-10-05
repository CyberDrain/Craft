using Craft.Configuration;
using Craft.Storage;
using Microsoft.Extensions.Logging.Abstractions;

namespace Craft.Tests;

/// <summary>
/// Schema v3: within a priority bucket the queue drains oldest run first. v2 keyed rows <c>{run}|{task}</c>,
/// so a bucket drained alphabetically by run name and a run whose name sorted late never ran while
/// earlier-sorting runs kept arriving (MailboxRules/UpdatePermissions starved for 16 days behind a steady
/// stream of AuditLog/DomainAnalyser runs). These pin the order, that the key stays one row per task, and
/// the migration of a live v2 backlog.
/// </summary>
public class JobQueueFifoTests
{
    private static readonly TimeSpan Lease = TimeSpan.FromMinutes(20);
    private static DateTime At(int minute) => new(2026, 10, 5, 2, minute, 0, DateTimeKind.Utc);

    private sealed record Q(JobQueueStore Queue, RunRemainingCounterTests.ConditionalStore Store, string QueueTable, string IndexTable);

    private static Q NewQueue()
    {
        var settings = new CraftSettings();
        var store = new RunRemainingCounterTests.ConditionalStore();
        var queue = new JobQueueStore(NullLogger<JobQueueStore>.Instance, settings, store);
        return new Q(queue, store, $"{settings.Orchestrator.TablePrefix}Queue", $"{settings.Orchestrator.TablePrefix}QueueIndex");
    }

    private static async Task<List<string>> DrainOrderAsync(JobQueueStore queue)
    {
        var order = new List<string>();
        while (true)
        {
            var claimed = await queue.ClaimBatchAsync("w", 1, Lease);
            if (claimed.Count == 0) return order;
            order.Add($"{claimed[0].RunName}/{claimed[0].TaskId}");
            await queue.RemoveAsync(claimed[0]);
        }
    }

    private static async Task<List<StoreRow>> RowsAsync(Q q, string table)
    {
        var rows = new List<StoreRow>();
        await foreach (var r in q.Store.QueryTableAsync(table)) rows.Add(r);
        return rows;
    }

    [Fact]
    public async Task ABucketDrainsOldestRunFirst_NotAlphabetically()
    {
        var q = NewQueue();
        await q.Queue.InitializeAsync();

        await q.Queue.EnqueueBatchAsync("UpdatePermissions-1", [("u0", 4), ("u1", 4)], At(0));
        await q.Queue.EnqueueBatchAsync("AuditLog-2", [("a0", 4)], At(5));
        await q.Queue.EnqueueBatchAsync("DomainAnalyser-3", [("d0", 4)], At(9));

        Assert.Equal(["UpdatePermissions-1/u0", "UpdatePermissions-1/u1", "AuditLog-2/a0", "DomainAnalyser-3/d0"],
            await DrainOrderAsync(q.Queue));
    }

    [Fact]
    public async Task PriorityStillBeatsAge()
    {
        var q = NewQueue();
        await q.Queue.InitializeAsync();

        await q.Queue.EnqueueBatchAsync("Old", [("o0", 4)], At(0));
        await q.Queue.EnqueueBatchAsync("Urgent", [("x0", 1)], At(30));

        Assert.Equal(["Urgent/x0", "Old/o0"], await DrainOrderAsync(q.Queue));
    }

    [Fact]
    public async Task AReEnqueueHoursLater_KeepsTheRunsPlaceAndOneRow()
    {
        var q = NewQueue();
        await q.Queue.InitializeAsync();

        await q.Queue.EnqueueBatchAsync("Zeta", [("z0", 4), ("z1", 4)], At(0));
        await q.Queue.EnqueueBatchAsync("Alpha", [("a0", 4)], At(10));
        // The re-drive re-queues a task with the time it noticed, long after the run started.
        await q.Queue.EnqueueAsync("Zeta", "z1", 4, At(59));

        Assert.Equal(3, (await RowsAsync(q, q.QueueTable)).Count);
        Assert.Equal(["Zeta/z0", "Zeta/z1", "Alpha/a0"], await DrainOrderAsync(q.Queue));
    }

    [Fact]
    public async Task TheEpochRowIsInvisibleToIndexReaders_AndGoesWithTheRun()
    {
        var q = NewQueue();
        await q.Queue.InitializeAsync();
        await q.Queue.EnqueueBatchAsync("R", [("a", 4)], At(0));

        Assert.Equal(["a"], await q.Queue.GetQueuedTaskIdsAsync("R"));
        Assert.Equal(["a"], await q.Queue.GetDispatchableTaskIdsAsync("R", ["a"]));
        Assert.Equal(0, await q.Queue.ReleaseRunClaimsAsync("R"));

        await q.Queue.RemoveRunAsync("R");

        Assert.Empty(await RowsAsync(q, q.QueueTable));
        Assert.DoesNotContain(await RowsAsync(q, q.IndexTable), r => r.PartitionKey == "R");
    }

    [Fact]
    public async Task AnEpochSurvivesTheSecondsRoundTrip()
    {
        var q = NewQueue();
        await q.Queue.InitializeAsync();
        var odd = At(0).AddTicks(1_234_567);

        await q.Queue.EnqueueBatchAsync("R", [("a", 4)], odd);
        await q.Queue.EnqueueAsync("R", "a", 4, odd);

        var row = Assert.Single(await RowsAsync(q, q.QueueTable));
        Assert.Equal(JobQueueStore.BuildRowKey(At(0), "R", "a"), row.RowKey);
    }

    // ── migration ────────────────────────────────────────────────────────────────────────────────

    /// <summary>A queue row and its index entry exactly as v2 wrote them, plus the v2 schema marker.</summary>
    private static async Task SeedV2Async(Q q, string run, string task, DateTime queuedUtc, int priority = 4,
        string owner = "", DateTimeOffset? lease = null)
    {
        var bucket = JobQueueStore.Bucket(priority);
        var key = $"{run}|{task}";
        await q.Store.UpsertAsync(q.QueueTable, new StoreRow(bucket, key)
        {
            Properties =
            {
                ["RunName"] = run, ["TaskId"] = task, ["Priority"] = priority, ["Owner"] = owner,
                ["LeaseUntil"] = lease, ["QueuedUtc"] = new DateTimeOffset(queuedUtc, TimeSpan.Zero),
            }
        });
        await q.Store.UpsertAsync(q.IndexTable, new StoreRow(run, $"{bucket}|{key}")
        {
            Properties = { ["TaskId"] = task, ["RunName"] = run }
        });
        await q.Store.UpsertAsync(q.IndexTable, new StoreRow("$schema", "queue-index") { Properties = { ["Version"] = 2 } });
    }

    [Fact]
    public async Task MigratesAV2Backlog_ToOldestRunFirst_KeepingClaimsAndTheIndex()
    {
        var q = NewQueue();
        // Alphabetically the v2 order was AuditLog, MailboxRules; by age MailboxRules is 16 days older.
        await SeedV2Async(q, "MailboxRules_t1", "b1", At(0).AddDays(-16));
        await SeedV2Async(q, "MailboxRules_t1", "b2", At(0).AddDays(-16));
        await SeedV2Async(q, "AuditLog_t1", "s1", At(0));
        var leaseUntil = DateTimeOffset.UtcNow.AddMinutes(10);
        await SeedV2Async(q, "AuditLog_t1", "s2", At(0), owner: "w-other", lease: leaseUntil);

        await q.Queue.InitializeAsync();

        var queueRows = await RowsAsync(q, q.QueueTable);
        Assert.Equal(4, queueRows.Count);
        Assert.All(queueRows, r => Assert.NotNull(JobQueueStore.ParseEpoch(r.RowKey)));
        var claimedRow = Assert.Single(queueRows, r => r.GetString("TaskId") == "s2");
        Assert.Equal("w-other", claimedRow.GetString("Owner"));
        Assert.Equal(leaseUntil, claimedRow.GetDateTimeOffset("LeaseUntil"));

        // Index entries point at the new keys: every run-scoped read still sees its tasks as dispatchable.
        Assert.Equal(["b1", "b2"], (await q.Queue.GetDispatchableTaskIdsAsync("MailboxRules_t1", ["b1", "b2"])).Order());
        Assert.Equal(["s1", "s2"], (await q.Queue.GetDispatchableTaskIdsAsync("AuditLog_t1", ["s1", "s2"])).Order());
        Assert.Equal(6, (await RowsAsync(q, q.IndexTable)).Count(r => r.PartitionKey != "$schema"));

        // A later enqueue of a migrated task lands on its existing row, not a second one.
        await q.Queue.EnqueueAsync("MailboxRules_t1", "b2", 4, DateTime.UtcNow);
        Assert.Equal(4, (await RowsAsync(q, q.QueueTable)).Count);

        Assert.Equal(["MailboxRules_t1/b1", "MailboxRules_t1/b2", "AuditLog_t1/s1"], await DrainOrderAsync(q.Queue));
    }

    [Fact]
    public async Task AMigrationInterruptedPartWay_ConvergesOnRerun()
    {
        var q = NewQueue();
        await SeedV2Async(q, "Run", "t1", At(0));
        await SeedV2Async(q, "Run", "t2", At(0));
        // t1 already re-keyed by a pass that crashed before deleting its old row or finishing t2.
        var epoch = At(0).AddMinutes(-3);
        var v3Key = JobQueueStore.BuildRowKey(epoch, "Run", "t1");
        await q.Store.UpsertAsync(q.QueueTable, new StoreRow("P04", v3Key)
        {
            Properties = { ["RunName"] = "Run", ["TaskId"] = "t1", ["Priority"] = 4, ["Owner"] = "", ["QueuedUtc"] = new DateTimeOffset(At(0), TimeSpan.Zero) }
        });

        await q.Queue.InitializeAsync();

        var rows = await RowsAsync(q, q.QueueTable);
        Assert.Equal(2, rows.Count);
        Assert.All(rows, r => Assert.Equal(epoch, JobQueueStore.ParseEpoch(r.RowKey)));
    }
}

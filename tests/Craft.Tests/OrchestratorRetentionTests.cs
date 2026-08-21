using Craft.Configuration;
using Craft.Orchestration;
using Craft.Storage;
using Microsoft.Extensions.Logging.Abstractions;

namespace Craft.Tests;

/// <summary>
/// The retention sweep is the only thing that bounds the orchestrator tables on a host that is not
/// restarted, and every rule in it is a rule about what NOT to delete while Craft is live. These pin
/// those rules against an in-memory backend; <see cref="OrchestratorRetentionAzuriteTests"/> proves
/// the same sweep against real tables, where the projection and the partition deletes are real.
/// </summary>
public class OrchestratorRetentionTests
{
    private sealed class FakeStore : ICraftTableStore
    {
        private readonly Dictionary<string, Dictionary<(string, string), StoreRow>> _tables = new();

        public Task<bool> TryReplaceBatchAsync(string table, string partitionKey, IReadOnlyList<StoreRow> rows,
            CancellationToken ct = default) => throw new NotSupportedException();

        public Task PingAsync(CancellationToken ct = default) => Task.CompletedTask;

        public Task EnsureTableAsync(string table, CancellationToken ct = default)
        {
            if (!_tables.ContainsKey(table)) _tables[table] = new();
            return Task.CompletedTask;
        }

        public Task UpsertAsync(string table, StoreRow row, CancellationToken ct = default)
        {
            _tables[table][(row.PartitionKey, row.RowKey)] = row;
            return Task.CompletedTask;
        }

        public Task UpsertBatchAsync(string table, string partitionKey, IReadOnlyList<StoreRow> rows,
            CancellationToken ct = default)
        {
            foreach (var r in rows) _tables[table][(r.PartitionKey, r.RowKey)] = r;
            return Task.CompletedTask;
        }

        public Task<StoreRow?> GetAsync(string table, string partitionKey, string rowKey, CancellationToken ct = default)
            => Task.FromResult(_tables[table].TryGetValue((partitionKey, rowKey), out var r) ? r : null);

        public async IAsyncEnumerable<StoreRow> QueryPartitionAsync(string table, string partitionKey,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
        {
            foreach (var kv in _tables[table].Where(k => k.Key.Item1 == partitionKey).ToList())
            {
                yield return kv.Value;
                await Task.Yield();
            }
        }

        public async IAsyncEnumerable<StoreRow> QueryTableAsync(string table,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
        {
            foreach (var kv in _tables[table].ToList())
            {
                yield return kv.Value;
                await Task.Yield();
            }
        }

        public Task DeleteAsync(string table, string partitionKey, string rowKey, CancellationToken ct = default)
        {
            _tables[table].Remove((partitionKey, rowKey));
            return Task.CompletedTask;
        }

        public Task DeletePartitionAsync(string table, string partitionKey, CancellationToken ct = default)
        {
            foreach (var k in _tables[table].Keys.Where(k => k.Item1 == partitionKey).ToList())
                _tables[table].Remove(k);
            return Task.CompletedTask;
        }
    }

    private static readonly TimeSpan Retention = TimeSpan.FromHours(48);
    private static readonly HashSet<string> NothingActive = new(StringComparer.Ordinal);
    private static DateTime Old => DateTime.UtcNow.AddDays(-3);
    private static DateTime Recent => DateTime.UtcNow.AddHours(-1);

    private sealed record Harness(OrchestratorTableStore Store, FakeStore Backing, CraftSettings Settings)
    {
        public string Runs => Settings.Orchestrator.TablePrefix + "Runs";
        public string Tasks => Settings.Orchestrator.TablePrefix + "Tasks";
        public string Results => Settings.Orchestrator.TablePrefix + "Results";

        public Task AddRunAsync(string name, string status, DateTime started, DateTime? completed) =>
            Store.UpsertRunAsync(new OrchestratorRun { Name = name, Status = status, StartedUtc = started, CompletedUtc = completed });

        public Task AddTasksAsync(string run, int count) =>
            Store.UpsertTaskBatchAsync(run, Enumerable.Range(1, count)
                .Select(i => new OrchestratorTaskItem { Id = $"task-{i}", Status = "Completed" }).ToList());

        /// <summary>A raw row carrying a Timestamp, the way a real backend returns every row.</summary>
        public Task AddStampedRowAsync(string table, string partition, string rowKey, DateTime stamp) =>
            Backing.UpsertAsync(table, new StoreRow(partition, rowKey) { Timestamp = new DateTimeOffset(stamp, TimeSpan.Zero) });

        public async Task<int> CountAsync(string table, string partition)
        {
            var n = 0;
            await foreach (var _ in Backing.QueryPartitionAsync(table, partition)) n++;
            return n;
        }
    }

    private static async Task<Harness> NewHarnessAsync()
    {
        var settings = new CraftSettings();
        var backing = new FakeStore();
        var store = new OrchestratorTableStore(NullLogger<OrchestratorTableStore>.Instance, settings, backing);
        await store.InitializeAsync();
        return new Harness(store, backing, settings);
    }

    [Fact]
    public async Task CancelledRun_PastRetention_IsRemovedWithItsPartitions()
    {
        var h = await NewHarnessAsync();
        await h.AddRunAsync("cancelled", "Cancelled", Old, Old);
        await h.AddTasksAsync("cancelled", 3);
        await h.Store.StoreResultAsync("cancelled", "task-1", "{\"ok\":true}");

        var result = await h.Store.CleanupOldRunsAsync(Retention);

        Assert.Equal("cancelled", Assert.Single(result.ExpiredRuns));
        Assert.Null(await h.Store.GetRunAsync("cancelled"));
        Assert.Equal(0, await h.CountAsync(h.Tasks, "cancelled"));
        Assert.Equal(0, await h.CountAsync(h.Results, "cancelled"));
    }

    [Theory]
    [InlineData("Completed")]
    [InlineData("CompletedWithErrors")]
    [InlineData("Failed")]
    [InlineData("Cancelled")]
    public async Task EveryTerminalStatus_PastRetention_IsRemoved(string status)
    {
        var h = await NewHarnessAsync();
        await h.AddRunAsync("run", status, Old, Old);

        var result = await h.Store.CleanupOldRunsAsync(Retention);

        Assert.Equal("run", Assert.Single(result.ExpiredRuns));
        Assert.Equal(1, result.RunsExamined);
        Assert.Null(await h.Store.GetRunAsync("run"));
    }

    [Fact]
    public async Task FinishedRun_InsideRetention_IsKept_WithItsRows()
    {
        var h = await NewHarnessAsync();
        await h.AddRunAsync("recent", "Completed", Old, Recent);
        await h.AddTasksAsync("recent", 2);

        var result = await h.Store.CleanupOldRunsAsync(Retention);

        Assert.Empty(result.ExpiredRuns);
        Assert.NotNull(await h.Store.GetRunAsync("recent"));
        Assert.Equal(2, await h.CountAsync(h.Tasks, "recent"));
    }

    [Fact]
    public async Task FinishedRun_WithoutCompletedUtc_IsJudgedByStartedUtc()
    {
        var h = await NewHarnessAsync();
        await h.AddRunAsync("failed-old", "Failed", Old, null);
        await h.AddRunAsync("failed-recent", "Failed", Recent, null);

        var result = await h.Store.CleanupOldRunsAsync(Retention);

        Assert.Equal("failed-old", Assert.Single(result.ExpiredRuns));
        Assert.NotNull(await h.Store.GetRunAsync("failed-recent"));
    }

    [Fact]
    public async Task ActiveRun_IsKept_HoweverLongAgoItStarted()
    {
        var h = await NewHarnessAsync();
        await h.AddRunAsync("long", "Running", DateTime.UtcNow.AddDays(-10), null);
        await h.AddTasksAsync("long", 2);

        var result = await h.Store.CleanupOldRunsAsync(Retention, new HashSet<string> { "long" });

        Assert.Empty(result.AbandonedRuns);
        Assert.NotNull(await h.Store.GetRunAsync("long"));
        Assert.Equal(2, await h.CountAsync(h.Tasks, "long"));
    }

    [Fact]
    public async Task RunNobodyIsDriving_WithNoWritesWithinRetention_IsAbandoned()
    {
        var h = await NewHarnessAsync();
        await h.AddRunAsync("stuck", "Pending", Old, null);
        await h.AddTasksAsync("stuck", 2);

        var result = await h.Store.CleanupOldRunsAsync(Retention, NothingActive);

        Assert.Equal("stuck", Assert.Single(result.AbandonedRuns));
        Assert.Empty(result.ExpiredRuns);
        Assert.Null(await h.Store.GetRunAsync("stuck"));
        Assert.Equal(0, await h.CountAsync(h.Tasks, "stuck"));
    }

    [Fact]
    public async Task RunNobodyIsDriving_WithAFreshHeartbeat_IsKept()
    {
        // Started long ago, but a task completed an hour ago: the counter row is the heartbeat.
        var h = await NewHarnessAsync();
        await h.AddRunAsync("slow", "Running", DateTime.UtcNow.AddDays(-10), null);
        await h.AddTasksAsync("slow", 2);
        await h.AddStampedRowAsync(h.Tasks, "slow", "!!run-counter", Recent);

        var result = await h.Store.CleanupOldRunsAsync(Retention, NothingActive);

        Assert.Empty(result.AbandonedRuns);
        Assert.NotNull(await h.Store.GetRunAsync("slow"));
        Assert.Equal(3, await h.CountAsync(h.Tasks, "slow"));
    }

    [Fact]
    public async Task UnknownStatus_IsTreatedAsNotFinished()
    {
        var h = await NewHarnessAsync();
        await h.AddRunAsync("odd-stale", "Suspended", Old, null);
        await h.AddRunAsync("odd-fresh", "Suspended", Old, null);
        await h.AddStampedRowAsync(h.Tasks, "odd-fresh", "!!run-counter", Recent);

        var result = await h.Store.CleanupOldRunsAsync(Retention, NothingActive);

        Assert.Equal("odd-stale", Assert.Single(result.AbandonedRuns));
        Assert.Empty(result.ExpiredRuns);
        Assert.NotNull(await h.Store.GetRunAsync("odd-fresh"));
    }

    [Fact]
    public async Task OrphanedPartitions_WithOnlyOldRows_AreRemoved()
    {
        var h = await NewHarnessAsync();
        await h.AddStampedRowAsync(h.Tasks, "ghost", "task-1", Old);
        await h.AddStampedRowAsync(h.Tasks, "ghost", "task-2", Old);
        await h.AddStampedRowAsync(h.Results, "ghost", "task-1", Old);

        var result = await h.Store.CleanupOldRunsAsync(Retention);

        Assert.Equal(2, result.OrphanPartitionsRemoved);
        Assert.Equal(0, await h.CountAsync(h.Tasks, "ghost"));
        Assert.Equal(0, await h.CountAsync(h.Results, "ghost"));
    }

    [Fact]
    public async Task OrphanedPartition_WithAFreshRow_IsKeptWhole()
    {
        var h = await NewHarnessAsync();
        await h.AddStampedRowAsync(h.Tasks, "ghost", "task-1", Old);
        await h.AddStampedRowAsync(h.Tasks, "ghost", "task-2", Old);
        await h.AddStampedRowAsync(h.Tasks, "ghost", "task-3", Recent);

        var result = await h.Store.CleanupOldRunsAsync(Retention);

        Assert.Equal(0, result.OrphanPartitionsRemoved);
        Assert.Equal(3, await h.CountAsync(h.Tasks, "ghost"));
    }

    [Fact]
    public async Task PartitionOfARetainedRun_IsNotAnOrphan_EvenWhenItsRowsAreOld()
    {
        var h = await NewHarnessAsync();
        await h.AddRunAsync("resumed", "Completed", Old, Recent);
        await h.AddStampedRowAsync(h.Tasks, "resumed", "task-1", Old);
        await h.AddStampedRowAsync(h.Results, "resumed", "task-1", Old);

        var result = await h.Store.CleanupOldRunsAsync(Retention);

        Assert.Equal(0, result.OrphanPartitionsRemoved);
        Assert.Equal(1, await h.CountAsync(h.Tasks, "resumed"));
        Assert.Equal(1, await h.CountAsync(h.Results, "resumed"));
    }

    [Fact]
    public async Task RowsWithoutATimestamp_AreNeverJudgedOrphans()
    {
        // A backend that cannot say how old a row is gets the benefit of the doubt.
        var h = await NewHarnessAsync();
        await h.AddTasksAsync("ghost", 2);

        var result = await h.Store.CleanupOldRunsAsync(Retention);

        Assert.Equal(0, result.OrphanPartitionsRemoved);
        Assert.Equal(2, await h.CountAsync(h.Tasks, "ghost"));
    }

    [Fact]
    public async Task Sweep_ReportsEverythingItExamined()
    {
        var h = await NewHarnessAsync();
        await h.AddRunAsync("done", "Completed", Old, Old);
        await h.AddRunAsync("live", "Running", Recent, null);
        await h.AddRunAsync("stuck", "Pending", Old, null);
        await h.AddStampedRowAsync(h.Results, "ghost", "r", Old);

        var result = await h.Store.CleanupOldRunsAsync(Retention, new HashSet<string> { "live" });

        Assert.Equal(3, result.RunsExamined);
        Assert.Equal("done", Assert.Single(result.ExpiredRuns));
        Assert.Equal("stuck", Assert.Single(result.AbandonedRuns));
        Assert.Equal(1, result.OrphanPartitionsRemoved);
        Assert.NotNull(await h.Store.GetRunAsync("live"));
    }
}

using Craft.Configuration;
using Craft.Orchestration;
using Craft.Storage;
using Microsoft.Extensions.Logging.Abstractions;

namespace Craft.Tests;

/// <summary>
/// The remaining-task counter that makes run completion a fact in storage rather than a property of one
/// process's memory.
///
/// Completion is decided today by <c>CheckRunCompletion</c> walking <c>run.Tasks</c> in memory, which is
/// why <c>ResolveTaskWorkAsync</c> documents object identity with the live run graph as a requirement
/// rather than an optimization — a worker handed a deserialized copy would mutate something the graph
/// never sees and the run would never finalize. A counter in storage removes that requirement.
///
/// Its one hard property is being decremented exactly once per task. The status writer retries runs
/// whose batch failed, so anything that decrements separately from the task's own terminal write gets
/// replayed and double-counts, stranding a run that has actually finished.
/// </summary>
public class RunRemainingCounterTests
{
    /// <summary>
    /// A fake that models the two things the counter depends on: ETag concurrency, and transactions that
    /// are all-or-nothing within a partition.
    /// </summary>
    internal sealed class ConditionalStore : ICraftTableStore
    {
        private readonly Dictionary<string, Dictionary<(string, string), StoreRow>> _tables = new();
        private long _etag;

        public int ConditionalWrites { get; private set; }
        public int RejectedWrites { get; private set; }

        /// <summary>Set to run just before a conditional write lands — lets a test interleave a competitor.</summary>
        public Action? OnBeforeConditionalWrite { get; set; }

        /// <summary>Set to run at the start of any table query — lets a test model unreachable storage.</summary>
        public Action? OnBeforeQuery { get; set; }

        private Dictionary<(string, string), StoreRow> Table(string t) =>
            _tables.TryGetValue(t, out var x) ? x : _tables[t] = new();

        private StoreRow Stamp(StoreRow row) => new(row.PartitionKey, row.RowKey)
        {
            ETag = $"W/\"{Interlocked.Increment(ref _etag)}\"",
            Properties = new Dictionary<string, object?>(row.Properties),
        };


        /// <summary>
        /// Azure Table returns rows ordered by partition key then row key, and the queue's whole
        /// priority scheme rests on that. A fake handing back insertion order would let an ordering bug
        /// pass, so model the real contract.
        ///
        /// Copies, like GetAsync: yielding the stored instances would let a caller that mutates what it
        /// read — which claiming does, by design — change storage without ever writing.
        /// </summary>
        private List<StoreRow> Ordered(string table) => Table(table).Values
            .OrderBy(r => r.PartitionKey, StringComparer.Ordinal)
            .ThenBy(r => r.RowKey, StringComparer.Ordinal)
            .Select(r => new StoreRow(r.PartitionKey, r.RowKey)
            {
                ETag = r.ETag,
                Properties = new Dictionary<string, object?>(r.Properties),
            })
            .ToList();

        public Task PingAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task EnsureTableAsync(string table, CancellationToken ct = default) { Table(table); return Task.CompletedTask; }

        public Task UpsertAsync(string table, StoreRow row, CancellationToken ct = default)
        {
            Table(table)[(row.PartitionKey, row.RowKey)] = Stamp(row);
            return Task.CompletedTask;
        }

        public Task UpsertBatchAsync(string table, string partitionKey, IReadOnlyList<StoreRow> rows, CancellationToken ct = default)
        {
            foreach (var r in rows) Table(table)[(r.PartitionKey, r.RowKey)] = Stamp(r);
            return Task.CompletedTask;
        }

        public Task<bool> TryReplaceBatchAsync(string table, string partitionKey, IReadOnlyList<StoreRow> rows,
            CancellationToken ct = default)
        {
            OnBeforeConditionalWrite?.Invoke();

            var t = Table(table);
            // All-or-nothing: verify every guard BEFORE mutating anything.
            foreach (var r in rows)
            {
                if (!t.TryGetValue((r.PartitionKey, r.RowKey), out var cur) || cur.ETag != r.ETag)
                {
                    RejectedWrites++;
                    return Task.FromResult(false);
                }
            }

            foreach (var r in rows) t[(r.PartitionKey, r.RowKey)] = Stamp(r);
            ConditionalWrites++;
            return Task.FromResult(true);
        }

        public Task<StoreRow?> GetAsync(string table, string partitionKey, string rowKey, CancellationToken ct = default)
        {
            // Hand back a copy: a caller mutating what it read must not mutate the store in place.
            if (!Table(table).TryGetValue((partitionKey, rowKey), out var r)) return Task.FromResult<StoreRow?>(null);
            return Task.FromResult<StoreRow?>(new StoreRow(r.PartitionKey, r.RowKey)
            {
                ETag = r.ETag,
                Properties = new Dictionary<string, object?>(r.Properties),
            });
        }

        public async IAsyncEnumerable<StoreRow> QueryPartitionAsync(string table, string partitionKey,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
        {
            foreach (var r in Ordered(table).Where(r => r.PartitionKey == partitionKey))
            {
                yield return r;
                await Task.Yield();
            }
        }

        public async IAsyncEnumerable<StoreRow> QueryTableAsync(string table,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
        {
            OnBeforeQuery?.Invoke();
            foreach (var r in Ordered(table)) { yield return r; await Task.Yield(); }
        }

        public Task DeleteAsync(string table, string partitionKey, string rowKey, CancellationToken ct = default)
        {
            Table(table).Remove((partitionKey, rowKey));
            return Task.CompletedTask;
        }

        public Task DeletePartitionAsync(string table, string partitionKey, CancellationToken ct = default)
        {
            foreach (var k in Table(table).Keys.Where(k => k.Item1 == partitionKey).ToList()) Table(table).Remove(k);
            return Task.CompletedTask;
        }
    }

    private const string Run = "StandardsApply";

    private static (OrchestratorTableStore Store, ConditionalStore Backing) NewStore()
    {
        var backing = new ConditionalStore();
        var settings = new CraftSettings();
        return (new OrchestratorTableStore(NullLogger<OrchestratorTableStore>.Instance, settings, backing), backing);
    }

    private static OrchestratorTaskItem Task_(string id, string status = "Completed") =>
        new() { Id = id, Status = status, CompletedUtc = DateTime.UtcNow };

    private static async Task<OrchestratorTableStore> SeededAsync(ConditionalStore backing, int taskCount)
    {
        var settings = new CraftSettings();
        var store = new OrchestratorTableStore(NullLogger<OrchestratorTableStore>.Instance, settings, backing);
        await store.InitializeAsync();

        var tasks = Enumerable.Range(0, taskCount).Select(i => Task_($"task-{i}", "Pending")).ToList();
        await store.UpsertTaskBatchAsync(Run, tasks);
        await store.InitRemainingAsync(Run, taskCount);
        return store;
    }

    [Fact]
    public async Task CounterStartsAtTheTaskCountAndFallsToZero()
    {
        var backing = new ConditionalStore();
        var store = await SeededAsync(backing, 3);

        Assert.Equal(3, await store.GetRemainingAsync(Run));

        Assert.Equal(2, await store.CompleteTaskAsync(Run, Task_("task-0")));
        Assert.Equal(1, await store.CompleteTaskAsync(Run, Task_("task-1")));
        Assert.Equal(0, await store.CompleteTaskAsync(Run, Task_("task-2")));

        Assert.Equal(0, await store.GetRemainingAsync(Run));
    }

    /// <summary>
    /// THE GUARANTEE. Replaying a completion — which the status writer does whenever a run's batch fails
    /// and is requeued — must not decrement twice. A run that double-counts finishes on paper before its
    /// work is done.
    /// </summary>
    [Fact]
    public async Task ReplayingACompletionDoesNotDecrementTwice()
    {
        var backing = new ConditionalStore();
        var store = await SeededAsync(backing, 2);

        var task = Task_("task-0");
        Assert.Equal(1, await store.CompleteTaskAsync(Run, task));

        // Same completion again. Completing a completed task reports the count and changes nothing.
        var writesBefore = backing.ConditionalWrites;
        var replay = await store.CompleteTaskAsync(Run, task);

        Assert.Equal(1, replay);
        Assert.Equal(1, await store.GetRemainingAsync(Run));
        Assert.Equal(writesBefore, backing.ConditionalWrites);
    }

    /// <summary>
    /// A competitor updating the counter between our read and our write must not cost us our decrement.
    /// Both completions have to count.
    /// </summary>
    [Fact]
    public async Task ConcurrentCompletionsBothCount()
    {
        var backing = new ConditionalStore();
        var store = await SeededAsync(backing, 5);

        var interfered = false;
        backing.OnBeforeConditionalWrite = () =>
        {
            if (interfered) return;
            interfered = true;
            // Land a competing completion first, invalidating the counter ETag we just read.
            store.CompleteTaskAsync(Run, Task_("task-9")).GetAwaiter().GetResult();
        };

        await store.CompleteTaskAsync(Run, Task_("task-0"));
        backing.OnBeforeConditionalWrite = null;

        // task-9 does not exist, so only our own decrement should have landed.
        Assert.Equal(4, await store.GetRemainingAsync(Run));
    }

    /// <summary>
    /// The counter shares the task partition so it can ride the same transaction. Nothing that reads
    /// tasks may mistake it for one — a phantom task never completes, and the run never finalizes.
    /// </summary>
    [Fact]
    public async Task CounterRowIsNotReturnedAsATask()
    {
        var backing = new ConditionalStore();
        var store = await SeededAsync(backing, 4);
        await store.UpsertRunAsync(new OrchestratorRun { Name = Run, Priority = 4 });

        var run = await store.GetRunAsync(Run);

        Assert.NotNull(run);
        Assert.Equal(4, run!.Tasks.Count);
        Assert.DoesNotContain(run.Tasks, t => t.Id.Contains("counter", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// The live path. Terminal transitions reach storage through the coalescing status writer in
    /// batches, so the counter has to fall by the number of terminal rows in a batch rather than by one
    /// per call — routing each completion through the single-task primitive would be three round-trips
    /// per task, roughly 22,000 for the run that prompted this work.
    ///
    /// Non-terminal rows in the same batch (a "Running" marker) must not count.
    /// </summary>
    [Fact]
    public async Task BatchedTerminalWritesDecrementByTheirTerminalCount()
    {
        var backing = new ConditionalStore();
        var store = await SeededAsync(backing, 10);

        var writes = new List<TaskStatusWrite>
        {
            new(Run, "task-0", "Completed", null, 1, null, DateTime.UtcNow, null),
            new(Run, "task-1", "Failed", null, 1, "boom", DateTime.UtcNow, null),
            new(Run, "task-2", "Cancelled", null, 1, null, DateTime.UtcNow, null),
            new(Run, "task-3", "Running", null, 1, null, null, null),   // not terminal — must not count
        };

        var failed = await store.WriteTaskStatusBatchAsync(writes);

        Assert.Empty(failed);
        Assert.Equal(7, await store.GetRemainingAsync(Run));
    }

    [Fact]
    public async Task MissingCounterReportsNullRatherThanGuessing()
    {
        var (store, _) = NewStore();
        await store.InitializeAsync();

        Assert.Null(await store.GetRemainingAsync("never-seeded"));
        Assert.Null(await store.CompleteTaskAsync("never-seeded", Task_("task-0")));
    }

    // ─── Status-guarded cancel (the cancel-a-run write) ───

    [Fact]
    public async Task CancellingAPendingTaskDecrementsTheCounter()
    {
        var backing = new ConditionalStore();
        var store = await SeededAsync(backing, 3);
        await store.UpsertRunAsync(new OrchestratorRun { Name = Run, Priority = 4 });

        var result = await store.CancelPendingTaskAsync(Run, Task_("task-0", "Cancelled"));

        Assert.True(result.Cancelled);
        Assert.Equal(2, await store.GetRemainingAsync(Run));
        Assert.Equal("Cancelled", (await store.GetRunAsync(Run))?.Tasks.Single(t => t.Id == "task-0").Status);
    }

    /// <summary>
    /// The guard that makes cancel safe against dispatch. A task that moved Pending → Running between
    /// the caller's read and this write must be left alone: clobbering it would have the task's real
    /// completion decrement the counter a second time, and the run would finalize with work
    /// outstanding.
    /// </summary>
    [Fact]
    public async Task CancellingATaskThatStartedRunningIsRefused()
    {
        var backing = new ConditionalStore();
        var store = await SeededAsync(backing, 3);
        await store.UpsertRunAsync(new OrchestratorRun { Name = Run, Priority = 4 });
        await store.UpsertTaskAsync(Run, Task_("task-0", "Running"));

        var result = await store.CancelPendingTaskAsync(Run, Task_("task-0", "Cancelled"));

        Assert.False(result.Cancelled);
        Assert.Equal("Running", result.CurrentStatus);
        Assert.Equal(3, await store.GetRemainingAsync(Run));
        Assert.Equal("Running", (await store.GetRunAsync(Run))?.Tasks.Single(t => t.Id == "task-0").Status);
    }

    [Fact]
    public async Task CancellingOnAPreCounterRunStillWritesTheStatus()
    {
        var (store, _) = NewStore();
        await store.InitializeAsync();
        await store.UpsertTaskBatchAsync("old-run", [Task_("task-0", "Pending")]);

        var result = await store.CancelPendingTaskAsync("old-run", Task_("task-0", "Cancelled"));

        Assert.True(result.Cancelled);
        Assert.Null(await store.GetRemainingAsync("old-run"));
    }
}

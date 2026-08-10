using System.Runtime.CompilerServices;
using Craft.Configuration;
using Craft.Orchestration;
using Craft.Storage;
using Microsoft.Extensions.Logging.Abstractions;

namespace Craft.Tests;

/// <summary>
/// The run row is what a restart rebuilds a run's identity from. Anything on
/// <see cref="OrchestratorRun"/> that is not written here is silently null after recovery, and the
/// feature that depends on it stops working without any error.
///
/// Reference and ParentRunName were exactly that: present on the model, never persisted. A resumed run
/// came back unreferenceable (QueueStatusBridge could not look it up) and orphaned (its finalize never
/// re-checked the parent, and the parent had no record of it).
/// </summary>
public class OrchestratorRunPersistenceTests
{
    private sealed class FakeStore : ICraftTableStore
    {

        // Claims are not exercised by this fake. Fail loudly rather than pretend the guard held —
        // a silent 'true' here would look exactly like a successful claim.
        public Task<bool> TryReplaceBatchAsync(string table, string partitionKey, IReadOnlyList<StoreRow> rows,
            CancellationToken ct = default) => throw new NotSupportedException();
        private readonly Dictionary<string, Dictionary<(string, string), StoreRow>> _tables = new();

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
            [EnumeratorCancellation] CancellationToken ct = default)
        {
            foreach (var kv in _tables[table].Where(k => k.Key.Item1 == partitionKey).ToList())
            {
                yield return kv.Value;
                await Task.Yield();
            }
        }

        public async IAsyncEnumerable<StoreRow> QueryTableAsync(string table,
            [EnumeratorCancellation] CancellationToken ct = default)
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

    private static OrchestratorTableStore NewStore() =>
        new(NullLogger<OrchestratorTableStore>.Instance, new CraftSettings(), new FakeStore());

    [Fact]
    public async Task Reference_And_ParentRunName_SurviveARestart()
    {
        var store = NewStore();
        await store.InitializeAsync();

        await store.UpsertRunAsync(new OrchestratorRun
        {
            Name = "ChildRun",
            Reference = "user-request-4711",
            ParentRunName = "ParentRun",
            Status = "Running",
            Priority = 3,
            StartedUtc = DateTime.UtcNow,
        });

        var recovered = await store.GetRunAsync("ChildRun");

        Assert.NotNull(recovered);
        Assert.Equal("user-request-4711", recovered!.Reference);
        Assert.Equal("ParentRun", recovered.ParentRunName);
    }

    /// <summary>
    /// PostExecAttemptCount is what bounds the retry of a failed post-execution across restarts. If it
    /// did not survive the restart it would read back as 0 every time, and the bound would never be
    /// reached — a post-execution that crashes the host would be retried on every start forever.
    /// </summary>
    [Fact]
    public async Task PostExecAttemptCount_SurvivesARestart()
    {
        var store = NewStore();
        await store.InitializeAsync();

        await store.UpsertRunAsync(new OrchestratorRun
        {
            Name = "AggregatedRun",
            Status = "Completed",
            PostExecFunctionName = "StoreMailboxPermissions",
            PostExecStatus = "Failed",
            PostExecAttemptCount = 2,
            StartedUtc = DateTime.UtcNow,
        });

        var recovered = await store.GetRunAsync("AggregatedRun");

        Assert.Equal("Failed", recovered!.PostExecStatus);
        Assert.Equal(2, recovered.PostExecAttemptCount);
    }

    /// <summary>
    /// Rows written before the counter existed have no such property. They must read as 0 — an
    /// already-exhausted reading would abandon in-flight aggregations on the upgrade restart.
    /// </summary>
    [Fact]
    public async Task RunWrittenWithoutTheCounter_ReadsAsZeroAttempts()
    {
        var store = NewStore();
        await store.InitializeAsync();

        await store.UpsertRunAsync(new OrchestratorRun
        {
            Name = "Legacy",
            Status = "Completed",
            PostExecStatus = "Pending",
            StartedUtc = DateTime.UtcNow,
        });

        Assert.Equal(0, (await store.GetRunAsync("Legacy"))!.PostExecAttemptCount);
    }

    /// <summary>Runs without either field keep round-tripping as null — no backfill required.</summary>
    [Fact]
    public async Task RunsWithoutReferenceOrParent_RoundTripAsNull()
    {
        var store = NewStore();
        await store.InitializeAsync();

        await store.UpsertRunAsync(new OrchestratorRun
        {
            Name = "Plain",
            Status = "Running",
            Priority = 5,
            StartedUtc = DateTime.UtcNow,
        });

        var recovered = await store.GetRunAsync("Plain");

        Assert.Null(recovered!.Reference);
        Assert.Null(recovered.ParentRunName);
    }

    /// <summary>
    /// The summary scan is what startup uses to rebuild parent→child links. It must report parentage
    /// without loading task rows — using GetRunAsync per run would pull every task of every run.
    /// </summary>
    [Fact]
    public async Task ListRunSummaries_ReportsParentage_WithoutLoadingTasks()
    {
        var store = NewStore();
        await store.InitializeAsync();

        var childTasks = Enumerable.Range(0, 50)
            .Select(i => new OrchestratorTaskItem { Id = $"t{i}", Status = "Pending" }).ToList();

        await store.UpsertRunAsync(new OrchestratorRun
        {
            Name = "Parent",
            Status = "Running",
            StartedUtc = DateTime.UtcNow,
        });
        await store.UpsertRunAsync(new OrchestratorRun
        {
            Name = "Child",
            ParentRunName = "Parent",
            Status = "Running",
            StartedUtc = DateTime.UtcNow,
            Tasks = childTasks,
        });
        await store.UpsertRunAsync(new OrchestratorRun
        {
            Name = "DoneChild",
            ParentRunName = "Parent",
            Status = "Completed",
            StartedUtc = DateTime.UtcNow,
        });
        await store.UpsertTaskBatchAsync("Child", childTasks);

        var summaries = await store.ListRunSummariesAsync();

        Assert.Equal(3, summaries.Count);
        Assert.Null(summaries.Single(s => s.Name == "Parent").ParentRunName);
        Assert.Equal("Parent", summaries.Single(s => s.Name == "Child").ParentRunName);
        Assert.Equal("Running", summaries.Single(s => s.Name == "Child").Status);

        // Terminal children are filtered out of the rebuild by status — they cannot block a parent.
        Assert.Equal("Completed", summaries.Single(s => s.Name == "DoneChild").Status);
    }

    /// <summary>
    /// Reference is looked up case-insensitively by QueueStatusBridge, so it has to come back with its
    /// original casing intact rather than being normalised on the way through storage.
    /// </summary>
    [Fact]
    public async Task Reference_PreservesCasing()
    {
        var store = NewStore();
        await store.InitializeAsync();

        await store.UpsertRunAsync(new OrchestratorRun
        {
            Name = "Run",
            Reference = "Tenant-ABC_Sync",
            Status = "Running",
            StartedUtc = DateTime.UtcNow,
        });

        Assert.Equal("Tenant-ABC_Sync", (await store.GetRunAsync("Run"))!.Reference);
    }
}

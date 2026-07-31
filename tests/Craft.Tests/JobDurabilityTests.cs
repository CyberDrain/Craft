using Craft.Auth;
using Craft.Configuration;
using Craft.Orchestration;
using Craft.PowerShellHost;
using Craft.Storage;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace Craft.Tests;

/// <summary>
/// Covers the state that has to survive a container restart. Production restarts nine times in eleven
/// hours, so "in-memory only" and "lost" are the same thing here: recovery re-queues whatever the task
/// table still reports as Pending, at whatever priority it reports.
/// </summary>
public class JobDurabilityTests
{
    private sealed class FakeStore : ICraftTableStore
    {
        private readonly Dictionary<string, Dictionary<(string, string), StoreRow>> _tables = new();

        public Task PingAsync(CancellationToken ct = default) => Task.CompletedTask;

        public Task EnsureTableAsync(string table, CancellationToken ct = default)
        {
            if (!_tables.ContainsKey(table)) _tables[table] = new();
            return Task.CompletedTask;
        }

        public Task UpsertAsync(string table, StoreRow row, CancellationToken ct = default)
        {
            // Mirrors TableUpdateMode.Replace — the whole row is overwritten.
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

    private static OrchestratorTableStore NewStore(out FakeStore backing)
    {
        backing = new FakeStore();
        return new OrchestratorTableStore(NullLogger<OrchestratorTableStore>.Instance, new CraftSettings(), backing);
    }

    private static JobManager NewJobManager()
    {
        var settings = new CraftSettings();
        settings.Worker.BgPoolSize = 8;
        var config = new ConfigurationBuilder().AddInMemoryCollection([]).Build();
        var repo = new ScriptRepository(NullLogger<ScriptRepository>.Instance, settings);
        var pool = new PowerShellWorkerPool(repo, NullLogger<PowerShellWorkerPool>.Instance, config, settings);
        var limiter = new BackgroundTaskLimiter(NullLogger<BackgroundTaskLimiter>.Instance, config, settings, pool);
        return new JobManager(NullLogger<JobManager>.Instance, settings, limiter);
    }

    /// <summary>Records what the JobManager reports, standing in for OrchestratorService.</summary>
    private sealed class RecordingSink : IJobDescriptorStateWriter
    {
        public readonly List<(JobDescriptor Descriptor, int Priority)> Priorities = new();
        public readonly List<JobDescriptor> Cancellations = new();

        public void PriorityChanged(JobDescriptor descriptor, int newPriority)
        {
            lock (Priorities) Priorities.Add((descriptor, newPriority));
        }

        public void Cancelled(JobDescriptor descriptor)
        {
            lock (Cancellations) Cancellations.Add(descriptor);
        }
    }

    // ── Per-task priority ───────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task TaskPriority_RoundTripsThroughStorage()
    {
        var store = NewStore(out _);
        await store.InitializeAsync();

        var tasks = new List<OrchestratorTaskItem>
        {
            new() { Id = "inherits", Status = "Pending" },                  // null ⇒ run priority
            new() { Id = "overridden", Status = "Pending", Priority = 0 },  // escalated by an operator
        };
        await store.UpsertRunAsync(new OrchestratorRun
        {
            Name = "run",
            Status = "Running",
            Priority = 5,
            StartedUtc = DateTime.UtcNow,
            Tasks = tasks,
        });
        await store.UpsertTaskBatchAsync("run", tasks);

        var recovered = await store.GetRunAsync("run");

        Assert.Equal(5, recovered!.Priority);
        Assert.Null(recovered.Tasks.Single(t => t.Id == "inherits").Priority);
        Assert.Equal(0, recovered.Tasks.Single(t => t.Id == "overridden").Priority);
    }

    /// <summary>
    /// The Replace-mode trap: the batched status writer rewrites the WHOLE row, so a column it does not
    /// carry is erased. If Priority ever drops out of TaskStatusWrite, the override survives exactly
    /// until the task moves to Running — this fails the moment that regresses.
    /// </summary>
    [Fact]
    public async Task StatusWrite_PreservesTaskPriority_RatherThanErasingIt()
    {
        var store = NewStore(out _);
        await store.InitializeAsync();

        var task = new OrchestratorTaskItem { Id = "t1", Status = "Pending", Priority = 0 };
        await store.UpsertTaskBatchAsync("run", [task]);
        await store.UpsertRunAsync(new OrchestratorRun { Name = "run", Status = "Running", Priority = 5 });

        // A status transition through the coalescing writer's path.
        await store.WriteTaskStatusBatchAsync(
            [new TaskStatusWrite("run", "t1", "Running", "{}", 0, null, null, task.Priority)]);

        var recovered = await store.GetRunAsync("run");
        var reloaded = recovered!.Tasks.Single();

        Assert.Equal("Running", reloaded.Status);
        Assert.Equal(0, reloaded.Priority);
    }

    // ── Durable operator actions ────────────────────────────────────────────────────────────────────

    [Fact]
    public void ChangePriority_ReportsTheDescriptor_ForDurablePersistence()
    {
        var jobs = NewJobManager();
        var sink = new RecordingSink();
        jobs.SetDescriptorStateWriter(sink);

        var id = jobs.Enqueue(new JobDescriptor("run", "task1", 5), "run-task1");
        Assert.True(jobs.ChangePriority(id, 0));

        var (descriptor, priority) = Assert.Single(sink.Priorities);
        Assert.Equal("run", descriptor.RunName);
        Assert.Equal("task1", descriptor.TaskId);
        Assert.Equal(0, priority);
    }

    [Fact]
    public void CancelJob_ReportsTheDescriptor_SoRecoveryDoesNotReQueueIt()
    {
        var jobs = NewJobManager();
        var sink = new RecordingSink();
        jobs.SetDescriptorStateWriter(sink);

        var id = jobs.Enqueue(new JobDescriptor("run", "task1", 5), "run-task1");
        Assert.True(jobs.CancelJob(id));

        var descriptor = Assert.Single(sink.Cancellations);
        Assert.Equal("task1", descriptor.TaskId);
    }

    [Fact]
    public void CancelRun_ReportsEveryQueuedDescriptor()
    {
        var jobs = NewJobManager();
        var sink = new RecordingSink();
        jobs.SetDescriptorStateWriter(sink);

        for (var i = 0; i < 25; i++)
            jobs.Enqueue(new JobDescriptor("run", $"task{i}", 5), $"run-task{i}");
        jobs.Enqueue(new JobDescriptor("other", "task0", 5), "other-task0");

        Assert.Equal(25, jobs.CancelRun("run"));
        Assert.Equal(25, sink.Cancellations.Count);
        Assert.All(sink.Cancellations, d => Assert.Equal("run", d.RunName));
        Assert.Equal(25, sink.Cancellations.Select(d => d.TaskId).Distinct().Count());
    }

    /// <summary>
    /// Closure jobs (simple scheduled scripts, post-execution) carry no descriptor and are never
    /// recovered from storage, so there is nothing to persist and nothing should be reported.
    /// </summary>
    [Fact]
    public void ClosureJobs_AreNotReported_HavingNothingToPersist()
    {
        var jobs = NewJobManager();
        var sink = new RecordingSink();
        jobs.SetDescriptorStateWriter(sink);

        var id = jobs.Enqueue("Start-CIPPDBCache", 5, _ => Task.CompletedTask);
        Assert.True(jobs.ChangePriority(id, 0));
        Assert.True(jobs.CancelJob(id));

        Assert.Empty(sink.Priorities);
        Assert.Empty(sink.Cancellations);
    }

    /// <summary>A sink that throws must not fail the operator's action — the queue change already took effect.</summary>
    [Fact]
    public void SinkFailure_DoesNotFailTheOperatorAction()
    {
        var jobs = NewJobManager();
        jobs.SetDescriptorStateWriter(new ThrowingSink());

        var id = jobs.Enqueue(new JobDescriptor("run", "task1", 5), "run-task1");

        Assert.True(jobs.ChangePriority(id, 0));
        Assert.True(jobs.CancelJob(id));
        Assert.Equal("Cancelled", jobs.GetJobs().Single().Status);
    }

    private sealed class ThrowingSink : IJobDescriptorStateWriter
    {
        public void PriorityChanged(JobDescriptor descriptor, int newPriority) => throw new InvalidOperationException("storage down");
        public void Cancelled(JobDescriptor descriptor) => throw new InvalidOperationException("storage down");
    }

    // ── Host shutdown ───────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// AuthService.Dispose threw a leftover NotImplementedException. DI disposes singletons in reverse
    /// creation order and does not catch, so it aborted the chain — every service created before it was
    /// skipped, and the host exited with an unhandled exception on every shutdown.
    /// </summary>
    [Fact]
    public void AuthService_Dispose_DoesNotThrow()
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection([]).Build();
        var auth = new AuthService(NullLogger<AuthService>.Instance, config, new CraftSettings(), new FakeStore());

        auth.Dispose();          // must not throw — this is what broke host shutdown
        auth.Dispose();          // and must stay safe if the container disposes twice
    }
}

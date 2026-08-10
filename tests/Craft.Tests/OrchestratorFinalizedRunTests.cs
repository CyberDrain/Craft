using System.Collections.Concurrent;
using System.Reflection;
using System.Runtime.CompilerServices;
using Craft.Configuration;
using Craft.Orchestration;
using Craft.Storage;
using Microsoft.Extensions.Logging.Abstractions;

namespace Craft.Tests;

/// <summary>
/// A finalized run must stay finalized.
///
/// It did not. A run's queue rows were only dropped at finalize when it had NO post-execution, so a run
/// with one kept its rows from finalize until the post-execution succeeded. The pump re-claimed them in
/// that window, and the resolver — finding the run absent from _activeRuns because it had FINISHED —
/// rehydrated it from storage and put it back in the live graph. From there the next completion
/// re-finalized it and dispatched the aggregation again.
///
/// Measured on a live 16-tenant instance before the fix, for one 13-task run:
///   7x  "Run MailboxRules_7ngn50... finalized: Completed (13/0/0/13)"
///   7x  "Dispatching PostExecution Push-StoreMailboxRules"
///   and individual tasks re-executed up to 4 times each, because a coalesced terminal write had not
///   landed yet and the rehydrated task still read Pending.
///
/// Idempotent Push-* consumers absorbed this silently. Push-ScheduledTaskPostExecution does not — it
/// advances a recurring task by ScheduledTime + recurrence and writes it back, so every extra
/// invocation pushes the next run out by another interval.
///
/// This pins the resolver half: a descriptor belonging to an already-finished run is dropped rather
/// than rehydrated. The guard has to be specific — an unfinished run absent from memory must still be
/// rehydrated, which is what crash recovery depends on — so both directions are asserted.
/// </summary>
public class OrchestratorFinalizedRunTests
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

    /// <summary>
    /// An OrchestratorService with only the fields the resolver reads. Building the real one would drag
    /// in the PowerShell runner and the job manager for what is, on this path, a storage read plus a
    /// status check.
    /// </summary>
    private static (OrchestratorService Svc, OrchestratorTableStore Store) NewService()
    {
        var settings = new CraftSettings { Orchestrator = { TablePrefix = "fin" + Guid.NewGuid().ToString("N")[..6] } };
        var store = new OrchestratorTableStore(NullLogger<OrchestratorTableStore>.Instance, settings, new FakeStore());

        var svc = (OrchestratorService)RuntimeHelpers.GetUninitializedObject(typeof(OrchestratorService));
        Set(svc, "_logger", NullLogger<OrchestratorService>.Instance);
        Set(svc, "_store", store);
        Set(svc, "_activeRuns", new ConcurrentDictionary<string, OrchestratorRun>());
        Set(svc, "_taskScriptPaths", new ConcurrentDictionary<string, string>());
        // Field initializers do not run on an uninitialized object; the resolver locks this to read
        // task status once it gets past the finished-run guard.
        Set(svc, "_lock", new object());
        return (svc, store);
    }

    private static void Set(object target, string field, object value) =>
        typeof(OrchestratorService).GetField(field, BindingFlags.NonPublic | BindingFlags.Instance)!
            .SetValue(target, value);

    private static async Task<object?> ResolveAsync(OrchestratorService svc, string runName, string taskId)
    {
        var mi = typeof(OrchestratorService).GetMethod("ResolveTaskWorkAsync",
            BindingFlags.NonPublic | BindingFlags.Instance)!;
        try
        {
            var task = (Task)mi.Invoke(svc, [new JobDescriptor(runName, taskId, 4), CancellationToken.None])!;
            await task;
            return task.GetType().GetProperty("Result")!.GetValue(task);
        }
        catch (TargetInvocationException ex)
        {
            throw ex.InnerException ?? ex;
        }
    }

    private static async Task SeedRunAsync(OrchestratorTableStore store, string name, string status)
    {
        await store.InitializeAsync();
        await store.UpsertRunAsync(new OrchestratorRun
        {
            Name = name,
            Status = status,
            Priority = 4,
            StartedUtc = DateTime.UtcNow,
            // Pending on purpose: this is the state a coalesced terminal write has not caught up with,
            // and the state that had the resolver hand back work for a task that had already run.
            Tasks = [new OrchestratorTaskItem { Id = "task-0", Status = "Pending" }]
        });
        await store.UpsertTaskAsync(name, new OrchestratorTaskItem { Id = "task-0", Status = "Pending" });
    }

    [Theory]
    [InlineData("Completed")]
    [InlineData("CompletedWithErrors")]
    public async Task DescriptorForAFinishedRun_IsDropped_AndTheRunIsNotPutBackInTheLiveGraph(string status)
    {
        var (svc, store) = NewService();
        await SeedRunAsync(store, "finished-run", status);
        ((ConcurrentDictionary<string, string>)typeof(OrchestratorService)
            .GetField("_taskScriptPaths", BindingFlags.NonPublic | BindingFlags.Instance)!
            .GetValue(svc)!)["finished-run"] = "Invoke-CraftTask";

        var work = await ResolveAsync(svc, "finished-run", "task-0");

        Assert.Null(work);

        // The resurrection is the actual defect: once a finalized run is back in _activeRuns, the next
        // completion re-finalizes it and dispatches its post-execution again.
        var active = (ConcurrentDictionary<string, OrchestratorRun>)typeof(OrchestratorService)
            .GetField("_activeRuns", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(svc)!;
        Assert.False(active.ContainsKey("finished-run"),
            "a finalized run was rehydrated back into the live graph — it will finalize again");
    }

    // ── The finalize-once claim, and the paths that must not strand a run ──────────────────────────

    private static ConcurrentDictionary<string, bool> Claims(OrchestratorService svc) =>
        (ConcurrentDictionary<string, bool>)typeof(OrchestratorService)
            .GetField("_finalizingRuns", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(svc)!;

    /// <summary>
    /// A second finalize is refused. This is the guard that keeps a run's Push-* aggregation to one
    /// invocation; without it the observed run ran its aggregation seven times.
    /// </summary>
    [Fact]
    public async Task FinalizeRun_RefusesASecondEntry()
    {
        var (svc, _) = NewService();
        Set(svc, "_finalizingRuns", new ConcurrentDictionary<string, bool>());

        // Pre-claim, as a first finalize would. The second entry must return before touching anything —
        // this instance has no writer or queue, so getting past the guard would throw rather than pass.
        Claims(svc)["run"] = true;

        var run = new OrchestratorRun { Name = "run", Status = "Running", StartedUtc = DateTime.UtcNow };
        await InvokeFinalizeAsync(svc, run);

        // Untouched: a refused finalize must not restamp the run's status or completion time.
        Assert.Equal("Running", run.Status);
        Assert.Null(run.CompletedUtc);
    }

    /// <summary>
    /// The stranding risk the guard introduces: if a claim outlived a failed finalize, nothing would
    /// ever finalize that run again — worse than the duplicate it prevents. A throw must release it.
    /// </summary>
    [Fact]
    public async Task FinalizeRun_ReleasesTheClaim_WhenItThrows()
    {
        var (svc, _) = NewService();
        Set(svc, "_finalizingRuns", new ConcurrentDictionary<string, bool>());
        // _writer is left null, so the core finalize throws once past the guard.

        var run = new OrchestratorRun { Name = "run", Status = "Running", StartedUtc = DateTime.UtcNow };

        await Assert.ThrowsAnyAsync<Exception>(() => InvokeFinalizeAsync(svc, run));

        Assert.False(Claims(svc).ContainsKey("run"),
            "a failed finalize kept its claim — this run can never finalize again");
    }

    /// <summary>
    /// Run names recur within one process (CIPPDBCacheOrchestrator, ProcessDeltaQueries fire on a
    /// timer). Dispatching a run again is what makes it finalizable again.
    /// </summary>
    [Fact]
    public async Task DispatchingARunAgain_ClearsAPreviousFinalizeClaim()
    {
        var (svc, _) = NewService();
        Set(svc, "_finalizingRuns", new ConcurrentDictionary<string, bool>());
        Set(svc, "_runStatusTimers", new ConcurrentDictionary<string, Timer>());
        Claims(svc)["recurring-run"] = true;

        var run = new OrchestratorRun { Name = "recurring-run", Status = "Running", StartedUtc = DateTime.UtcNow };

        // Only the bookkeeping prologue is exercised; the enqueue that follows needs a live queue.
        try
        {
            var mi = typeof(OrchestratorService).GetMethod("DispatchPendingTasksAsync",
                BindingFlags.NonPublic | BindingFlags.Instance)!;
            await (Task)mi.Invoke(svc, [run, "Invoke-CraftTask", 4, CancellationToken.None])!;
        }
        catch { /* expected: no queue on this instance */ }

        Assert.False(Claims(svc).ContainsKey("recurring-run"),
            "a recurring run kept its previous claim — its next outing would never finalize");
    }

    private static async Task InvokeFinalizeAsync(OrchestratorService svc, OrchestratorRun run)
    {
        var mi = typeof(OrchestratorService).GetMethod("FinalizeRunAsync",
            BindingFlags.NonPublic | BindingFlags.Instance)!;
        try
        {
            await (Task)mi.Invoke(svc, [run])!;
        }
        catch (TargetInvocationException ex)
        {
            throw ex.InnerException ?? ex;
        }
    }

    // ── A task already executing must not be started a second time ────────────────────────────────

    /// <summary>
    /// The crash-recovery race, at the resolver.
    ///
    /// A queue RowKey embeds the enqueue timestamp, so recovery re-dispatching a Pending task writes a
    /// SECOND row rather than updating the surviving one. Most duplicates are harmless — by the time the
    /// extra row is claimed the task has finished and it is dropped as terminal. But a claim that lands
    /// while the task is still RUNNING used to pass the guard and start another copy: on a killed
    /// 140-task fanout, a five-minute Intune collection was re-claimed four minutes in and ran twice.
    /// </summary>
    [Fact]
    public async Task DescriptorForATaskThatIsAlreadyRunning_IsDropped()
    {
        var (svc, store) = NewService();
        await store.InitializeAsync();

        var run = new OrchestratorRun
        {
            Name = "live-run",
            Status = "Running",
            Priority = 4,
            StartedUtc = DateTime.UtcNow,
            Tasks = [new OrchestratorTaskItem { Id = "task-0", Status = "Running" }]
        };
        await store.UpsertRunAsync(run);
        await store.UpsertTaskAsync("live-run", run.Tasks[0]);

        // In the live graph, mid-flight — the state a duplicate row races.
        ((ConcurrentDictionary<string, OrchestratorRun>)typeof(OrchestratorService)
            .GetField("_activeRuns", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(svc)!)["live-run"] = run;
        ((ConcurrentDictionary<string, string>)typeof(OrchestratorService)
            .GetField("_taskScriptPaths", BindingFlags.NonPublic | BindingFlags.Instance)!
            .GetValue(svc)!)["live-run"] = "Invoke-CraftTask";

        Assert.Null(await ResolveAsync(svc, "live-run", "task-0"));
    }

    /// <summary>
    /// The guard must not block genuine recovery. ResumeInterruptedRunsAsync flips interrupted tasks
    /// from Running back to Pending before re-dispatching, so a task that really does need re-running
    /// arrives here as Pending — and must resolve to work.
    /// </summary>
    [Fact]
    public async Task ATaskResetToPendingByRecovery_StillResolvesToWork()
    {
        var (svc, store) = NewService();
        await SeedRunAsync(store, "recovered-run", "Running");
        ((ConcurrentDictionary<string, string>)typeof(OrchestratorService)
            .GetField("_taskScriptPaths", BindingFlags.NonPublic | BindingFlags.Instance)!
            .GetValue(svc)!)["recovered-run"] = "Invoke-CraftTask";

        Assert.NotNull(await ResolveAsync(svc, "recovered-run", "task-0"));
    }

    [Fact]
    public async Task DescriptorForAnUnfinishedRun_IsStillRehydrated()
    {
        // The guard must not swallow crash recovery: a Running run absent from memory is exactly what
        // the rehydrate path exists for.
        var (svc, store) = NewService();
        await SeedRunAsync(store, "live-run", "Running");
        ((ConcurrentDictionary<string, string>)typeof(OrchestratorService)
            .GetField("_taskScriptPaths", BindingFlags.NonPublic | BindingFlags.Instance)!
            .GetValue(svc)!)["live-run"] = "Invoke-CraftTask";

        var work = await ResolveAsync(svc, "live-run", "task-0");

        Assert.NotNull(work);

        var active = (ConcurrentDictionary<string, OrchestratorRun>)typeof(OrchestratorService)
            .GetField("_activeRuns", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(svc)!;
        Assert.True(active.ContainsKey("live-run"),
            "an unfinished run was not re-established in the live graph — sibling completion tracking breaks");
    }
}

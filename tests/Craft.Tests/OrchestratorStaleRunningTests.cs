using System.Collections.Concurrent;
using System.Reflection;
using Craft.Configuration;
using Craft.Orchestration;
using Craft.PowerShellHost;
using Craft.Services;
using Craft.Storage;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace Craft.Tests;

/// <summary>
/// A "Running" status this process did not write must not be trusted as "a worker here has it".
///
/// The resolver drops a descriptor whose task reads Running, which is right for a duplicate queue row
/// claimed while THIS process is executing the task. But Running also reaches the live graph from
/// storage: the durable pre-invoke marker another process wrote before it died. Two production paths
/// put it there, both seen on a hosted instance across an App Service container swap (old and new
/// containers overlap for tens of seconds to minutes, sharing one storage account):
///
///   A. The old container creates or advances a run after the new container's startup recovery has
///      already passed. It dies holding claims whose tasks are marked Running. The new container's pump
///      claims one of the run's rows, <c>ResolveTaskWorkAsync</c> rehydrates the run from storage with
///      those markers, and once the dead claims' leases lapse and those rows are claimed, the resolver
///      drops each one as "already running" — Skipped, row deleted. Nothing ever re-drives a Running
///      task, and the run can never finalize (observed: 505/508 for 31 hours).
///
///   B. The pump starts claiming at host start, before <c>ResumeInterruptedRunsAsync</c> (which waits
///      for the worker pool). Its rehydrated copy goes into <c>_activeRuns</c> first; recovery then
///      loads its OWN copy, flips Running→Pending on that copy and in storage, releases the dead claims
///      and calls DispatchPendingTasksAsync, whose <c>_activeRuns.TryAdd</c> loses to the pump's copy.
///      The live graph keeps the stale Running; the released row is claimed and dropped exactly as in A.
///
/// Holding the queue claim is the ownership proof: only this process's pump enqueues descriptor jobs,
/// and it only does so for a row it has claimed. So a Running task that no worker in this process
/// started is an interrupted task, and is handled the way recovery handles one — attempt counted
/// (poison bound kept) and run.
/// </summary>
public class OrchestratorStaleRunningTests
{
    private const string TaskFunc = "Invoke-CraftTask";

    private sealed record Harness(OrchestratorService Svc, OrchestratorTableStore Store, JobQueueStore Queue,
        ConcurrentDictionary<string, OrchestratorRun> Active);

    private static async Task<Harness> NewHarnessAsync()
    {
        var settings = new CraftSettings { Orchestrator = { TablePrefix = "stale" + Guid.NewGuid().ToString("N")[..8] } };
        settings.Orchestrator.BatchStatusWrites = false;
        var config = new ConfigurationBuilder().AddInMemoryCollection([]).Build();

        var backing = new RunRemainingCounterTests.ConditionalStore();
        var store = new OrchestratorTableStore(NullLogger<OrchestratorTableStore>.Instance, settings, backing);
        var queue = new JobQueueStore(NullLogger<JobQueueStore>.Instance, settings, backing);
        await store.InitializeAsync();
        await queue.InitializeAsync();
        var writer = new OrchestratorStatusWriter(store, NullLogger<OrchestratorStatusWriter>.Instance, settings);

        var repo = new ScriptRepository(NullLogger<ScriptRepository>.Instance, settings);
        typeof(ScriptRepository).GetField("_moduleFunctionNames", BindingFlags.NonPublic | BindingFlags.Instance)!
            .SetValue(repo, new HashSet<string>([TaskFunc], StringComparer.OrdinalIgnoreCase));
        var pool = new PowerShellWorkerPool(repo, NullLogger<PowerShellWorkerPool>.Instance, config, settings);
        var runner = new PowerShellRunnerService(NullLogger<PowerShellRunnerService>.Instance, pool, repo, settings);

        var svc = (OrchestratorService)System.Runtime.CompilerServices.RuntimeHelpers
            .GetUninitializedObject(typeof(OrchestratorService));
        var active = new ConcurrentDictionary<string, OrchestratorRun>();
        Set(svc, "_logger", NullLogger<OrchestratorService>.Instance);
        Set(svc, "_store", store);
        Set(svc, "_queue", queue);
        Set(svc, "_writer", writer);
        Set(svc, "_psRunner", runner);
        Set(svc, "_settings", settings);
        Set(svc, "_lock", new object());
        Set(svc, "_activeRuns", active);
        Set(svc, "_taskScriptPaths", new ConcurrentDictionary<string, string>());
        Set(svc, "_finalizingRuns", new ConcurrentDictionary<string, bool>());
        Set(svc, "_finalizeDeferrals", new ConcurrentDictionary<string, int>());
        Set(svc, "_childRuns", new ConcurrentDictionary<string, ConcurrentBag<string>>());
        Set(svc, "_recoveringChildren", new ConcurrentDictionary<string, bool>());
        Set(svc, "_pendingChildRuns", new ConcurrentDictionary<string, int>());
        Set(svc, "_cancelledRuns", new ConcurrentDictionary<string, bool>());
        Set(svc, "_activeSequentialDrivers", new ConcurrentDictionary<string, bool>());
        Set(svc, "_requeueFailures", new ConcurrentDictionary<string, int>());
        Set(svc, "_deferrals", NewFieldValue(svc, "_deferrals"));
        Set(svc, "_redriveBackoff", NewFieldValue(svc, "_redriveBackoff"));
        Set(svc, "_redriveInFlight", NewFieldValue(svc, "_redriveInFlight"));
        Set(svc, "_redriveSlots", new SemaphoreSlim(8, 8));
        Set(svc, "_shedParameters", false);
        var jm = (JobManager)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(JobManager));
        var jobsField = typeof(JobManager).GetField("_jobs", BindingFlags.NonPublic | BindingFlags.Instance)!;
        jobsField.SetValue(jm, Activator.CreateInstance(jobsField.FieldType));
        Set(svc, "_jobManager", jm);
        return new Harness(svc, store, queue, active);
    }

    private static object NewFieldValue(object svc, string field) =>
        Activator.CreateInstance(typeof(OrchestratorService)
            .GetField(field, BindingFlags.NonPublic | BindingFlags.Instance)!.FieldType)!;

    private static void Set(object target, string field, object? value) =>
        typeof(OrchestratorService).GetField(field, BindingFlags.NonPublic | BindingFlags.Instance)!
            .SetValue(target, value);

    private static async Task<object?> ResolveAsync(OrchestratorService svc, string run, string taskId)
    {
        var mi = typeof(OrchestratorService).GetMethod("ResolveTaskWorkAsync", BindingFlags.NonPublic | BindingFlags.Instance)!;
        try
        {
            var task = (Task)mi.Invoke(svc, [new JobDescriptor(run, taskId, 4), CancellationToken.None])!;
            await task;
            return task.GetType().GetProperty("Result")!.GetValue(task);
        }
        catch (TargetInvocationException ex) { throw ex.InnerException ?? ex; }
    }

    /// <summary>Storage as another process left it: its in-flight task carries the durable Running marker.</summary>
    private static async Task SeedAsync(Harness h, string name, int staleAttempts = 0)
    {
        var tasks = new List<OrchestratorTaskItem>
        {
            new() { Id = "t-stale", Status = "Running", AttemptCount = staleAttempts, Parameters = new() { ["FunctionName"] = "Push-Noop" } },
            new() { Id = "t-next", Status = "Pending", Parameters = new() { ["FunctionName"] = "Push-Noop" } },
            new() { Id = "t-other", Status = "Pending", Parameters = new() { ["FunctionName"] = "Push-Noop" } },
        };
        await h.Store.UpsertRunAsync(new OrchestratorRun
        {
            Name = name,
            Status = "Running",
            Priority = 4,
            StartedUtc = DateTime.UtcNow,
            TaskScriptName = TaskFunc,
            Tasks = tasks
        });
        foreach (var t in tasks) await h.Store.UpsertTaskAsync(name, t);
    }

    // ── Mode A ─────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task ModeA_RunningMarkerFromADeadProcess_IsRunWhenThisProcessClaimsItsRow()
    {
        var h = await NewHarnessAsync();
        await SeedAsync(h, "run-a");

        // The dead process's lease lapsed; this process claimed the row. Previously: null (dropped forever).
        var work = await ResolveAsync(h.Svc, "run-a", "t-stale");

        Assert.NotNull(work);
        var task = h.Active["run-a"].Tasks.Single(t => t.Id == "t-stale");
        Assert.Equal(1, task.AttemptCount);   // the interrupted attempt is counted, as recovery counts it
    }

    [Fact]
    public async Task ModeA_PoisonBoundHolds_AThirdInterruptedAttemptFailsTheTask()
    {
        var h = await NewHarnessAsync();
        await SeedAsync(h, "run-poison", staleAttempts: 2);

        Assert.Null(await ResolveAsync(h.Svc, "run-poison", "t-stale"));

        var task = h.Active["run-poison"].Tasks.Single(t => t.Id == "t-stale");
        Assert.Equal("Failed", task.Status);
    }

    // ── Mode B ─────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task ModeB_PumpClaimBeforeRecovery_LeavesAStaleRunningInTheLiveGraph_WhichMustStillRun()
    {
        var h = await NewHarnessAsync();
        await SeedAsync(h, "run-b");

        // 1. Host start: the pump claims a sibling row before recovery has run. The rehydrated copy —
        //    carrying the dead process's Running marker — becomes the live graph.
        Assert.NotNull(await ResolveAsync(h.Svc, "run-b", "t-next"));
        var live = h.Active["run-b"];

        // 2. Recovery runs on its own copy: flips t-stale to Pending in storage, releases the claims and
        //    re-dispatches — but its TryAdd loses to the pump's copy.
        await h.Svc.ResumeInterruptedRunsAsync(CancellationToken.None);
        Assert.Same(live, h.Active["run-b"]);

        // 3. The released row is claimed. Previously: dropped as "already running", never run again.
        Assert.NotNull(await ResolveAsync(h.Svc, "run-b", "t-stale"));
    }

    // ── The guard's real job is kept ───────────────────────────────────────────────────────────────

    [Fact]
    public async Task ADuplicateRowForATaskThisProcessIsExecuting_IsStillDropped()
    {
        var h = await NewHarnessAsync();
        await SeedAsync(h, "run-dup");
        await h.Store.UpsertTaskAsync("run-dup",
            new OrchestratorTaskItem { Id = "t-stale", Status = "Pending", Parameters = new() { ["FunctionName"] = "Push-Noop" } });

        // Start the task the way dispatch does, up to the point it is running on a worker here.
        var work = (Func<CancellationToken, Task>)(await ResolveAsync(h.Svc, "run-dup", "t-stale"))!;
        var task = h.Active["run-dup"].Tasks.Single(t => t.Id == "t-stale");
        _ = Task.Run(() => work(CancellationToken.None));   // marks Running, then blocks on worker checkout
        for (var i = 0; i < 200 && task.Status != "Running"; i++) await Task.Delay(10);
        Assert.Equal("Running", task.Status);

        // A second row for the same task, claimed mid-flight, must not start another copy.
        Assert.Null(await ResolveAsync(h.Svc, "run-dup", "t-stale"));
    }
}

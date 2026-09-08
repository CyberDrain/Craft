using System.Collections.Concurrent;
using System.Reflection;
using Craft.Configuration;
using Craft.Orchestration;
using Craft.Storage;
using Microsoft.Extensions.Logging.Abstractions;

namespace Craft.Tests;

/// <summary>
/// Pins SEQUENTIAL orchestrator mode.
///
/// A run marked <see cref="OrchestratorRun.Sequential"/> runs its tasks ONE AT A TIME, in ascending
/// <see cref="OrchestratorTaskItem.Sequence"/> (payload) order: only the current task is ever enqueued,
/// and the next is enqueued when the current one reaches a terminal state. It runs on any free worker —
/// the durable queue simply never holds more than one of the run's tasks. The default (false) is the
/// existing fan-out: every task enqueued up front and drained in parallel.
///
/// These fix the contract at every seam it touches:
///   - persistence  — the flag and the per-task order survive a round trip AND a status-write rewrite
///                     (Replace mode erases any column the write omits), so a resumed run keeps its order;
///   - dispatch     — a sequential run enqueues only its current task; fan-out enqueues all;
///   - advance      — the next task (by Sequence) is enqueued on terminal, past failures too;
///   - re-drive     — the watchdog never re-enqueues the not-yet-reached tasks (which deliberately have no
///                     queue row), yet still recovers the current task if its own row is lost.
/// </summary>
public class OrchestratorSequentialTests
{
    private const string TaskFunc = "Invoke-CraftTask";

    // ─── harness ────────────────────────────────────────────────────────────────────────────────────

    private sealed record Harness(OrchestratorService Svc, OrchestratorTableStore Store, JobQueueStore Queue);

    private static async Task<Harness> NewHarnessAsync()
    {
        var settings = new CraftSettings
        {
            Orchestrator = { TablePrefix = "seq" + Guid.NewGuid().ToString("N")[..8] }
        };
        var backing = new RunRemainingCounterTests.ConditionalStore();
        var store = new OrchestratorTableStore(NullLogger<OrchestratorTableStore>.Instance, settings, backing);
        var queue = new JobQueueStore(NullLogger<JobQueueStore>.Instance, settings, backing);
        await store.InitializeAsync();
        await queue.InitializeAsync();

        // Build the service without its constructor (which drags in the PowerShell runner and the worker
        // pool) and set only the fields the dispatch / advance / re-drive paths read — the same shape the
        // other orchestrator unit tests use.
        var svc = (OrchestratorService)System.Runtime.CompilerServices.RuntimeHelpers
            .GetUninitializedObject(typeof(OrchestratorService));
        Set(svc, "_logger", NullLogger<OrchestratorService>.Instance);
        Set(svc, "_store", store);
        Set(svc, "_queue", queue);
        Set(svc, "_lock", new object());
        Set(svc, "_activeRuns", new ConcurrentDictionary<string, OrchestratorRun>());
        Set(svc, "_taskScriptPaths", new ConcurrentDictionary<string, string>());
        Set(svc, "_finalizingRuns", new ConcurrentDictionary<string, bool>());
        Set(svc, "_requeueFailures", new ConcurrentDictionary<string, int>());
        Set(svc, "_deferrals", NewFieldDict(svc, "_deferrals"));
        Set(svc, "_redriveBackoff", NewFieldDict(svc, "_redriveBackoff"));
        Set(svc, "_shedParameters", false);
        Set(svc, "_redriveBackoffEnabled", false); // pin the sequential logic, not the backoff timing
        Set(svc, "_redriveBase", TimeSpan.FromSeconds(60));
        Set(svc, "_settings", settings);

        // A JobManager with an empty job map, so IsQueuedOrRunning answers false (nothing dispatched here).
        var jm = (JobManager)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(JobManager));
        var jobsField = typeof(JobManager).GetField("_jobs", BindingFlags.NonPublic | BindingFlags.Instance)!;
        jobsField.SetValue(jm, Activator.CreateInstance(jobsField.FieldType));
        Set(svc, "_jobManager", jm);

        return new Harness(svc, store, queue);
    }

    private static object NewFieldDict(object svc, string field)
    {
        var t = svc.GetType().GetField(field, BindingFlags.NonPublic | BindingFlags.Instance)!.FieldType;
        return Activator.CreateInstance(t)!;
    }

    private static void Set(object target, string field, object? value) =>
        target.GetType().GetField(field, BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(target, value);

    private static Task Invoke(OrchestratorService svc, string method, params object[] args) =>
        (Task)typeof(OrchestratorService).GetMethod(method, BindingFlags.NonPublic | BindingFlags.Instance)!
            .Invoke(svc, args)!;

    private static Task Dispatch(Harness h, OrchestratorRun run) =>
        Invoke(h.Svc, "DispatchPendingTasksAsync", run, TaskFunc, run.Priority, CancellationToken.None, false);
    private static Task Advance(Harness h, OrchestratorRun run) => Invoke(h.Svc, "AdvanceSequentialAsync", run);
    private static Task Redrive(Harness h, OrchestratorRun run) => Invoke(h.Svc, "RedrivePendingTasksAsync", run);

    private static OrchestratorRun MakeRun(string name, int count, bool sequential)
    {
        var run = new OrchestratorRun
        {
            Name = name,
            Status = "Running",
            Priority = 4,
            Sequential = sequential,
            StartedUtc = DateTime.UtcNow,
            TaskScriptName = TaskFunc
        };
        for (var i = 0; i < count; i++)
            run.Tasks.Add(new OrchestratorTaskItem
            {
                Id = $"{name}_t{i}",
                Status = "Pending",
                Sequence = i,
                Parameters = new Dictionary<string, object> { ["FunctionName"] = "Push-Noop", ["idx"] = i }
            });
        return run;
    }

    private static async Task<List<string>> QueuedAsync(Harness h, string run) =>
        (await h.Queue.GetQueuedTaskIdsAsync(run)).OrderBy(x => x, StringComparer.Ordinal).ToList();

    /// <summary>Re-drive re-enqueues via a fire-and-forget Task.Run, so poll for the row to land.</summary>
    private static async Task<List<string>> WaitQueuedAsync(Harness h, string run, int expected)
    {
        for (var i = 0; i < 100; i++)
        {
            var ids = await h.Queue.GetQueuedTaskIdsAsync(run);
            if (ids.Count >= expected) return ids.OrderBy(x => x, StringComparer.Ordinal).ToList();
            await Task.Delay(20);
        }
        return await QueuedAsync(h, run);
    }

    // ─── persistence ────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Sequential_And_Sequence_SurviveCreationRoundTrip()
    {
        var h = await NewHarnessAsync();
        var run = MakeRun("persist-seq", 3, sequential: true);
        await h.Store.UpsertRunAsync(run);
        await h.Store.UpsertTaskBatchAsync(run.Name, run.Tasks);

        var loaded = await h.Store.GetRunAsync(run.Name);

        Assert.NotNull(loaded);
        Assert.True(loaded!.Sequential);
        Assert.Equal([0, 1, 2],
            loaded.Tasks.OrderBy(t => t.Sequence).Select(t => t.Sequence).ToArray());
    }

    [Fact]
    public async Task FanOutRun_RoundTripsSequentialFalse()
    {
        var h = await NewHarnessAsync();
        var run = MakeRun("persist-fanout", 2, sequential: false);
        await h.Store.UpsertRunAsync(run);
        await h.Store.UpsertTaskBatchAsync(run.Name, run.Tasks);

        var loaded = await h.Store.GetRunAsync(run.Name);

        Assert.False(loaded!.Sequential);
    }

    [Fact]
    public async Task Sequence_SurvivesStatusWriteRewrite()
    {
        // The status writer rewrites a task row on every transition with Replace semantics, so Sequence
        // must be part of that write or a resumed run would read every task as Sequence 0 and lose order.
        var h = await NewHarnessAsync();
        var run = MakeRun("persist-statuswrite", 2, sequential: true);
        await h.Store.UpsertRunAsync(run);
        await h.Store.UpsertTaskBatchAsync(run.Name, run.Tasks);

        // Simulate a terminal status write (as the coalescing writer does) for the second task.
        var t1 = run.Tasks[1];
        await h.Store.WriteTaskStatusBatchAsync(
        [
            new TaskStatusWrite(run.Name, t1.Id, "Completed", "{}", 0, null, DateTime.UtcNow, null, t1.Sequence)
        ]);

        var loaded = await h.Store.GetRunAsync(run.Name);
        var reloaded = loaded!.Tasks.Single(t => t.Id == t1.Id);
        Assert.Equal(1, reloaded.Sequence);       // not erased to 0 by the Replace write
        Assert.Equal("Completed", reloaded.Status);
    }

    // ─── dispatch gate ──────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Sequential_Dispatch_EnqueuesOnlyTheFirstTask()
    {
        var h = await NewHarnessAsync();
        var run = MakeRun("disp-seq", 5, sequential: true);

        await Dispatch(h, run);

        var queued = await QueuedAsync(h, run.Name);
        Assert.Equal(["disp-seq_t0"], queued);   // only Sequence 0, despite five pending tasks
    }

    [Fact]
    public async Task FanOut_Dispatch_EnqueuesEveryTask()
    {
        var h = await NewHarnessAsync();
        var run = MakeRun("disp-fanout", 5, sequential: false);

        await Dispatch(h, run);

        var queued = await QueuedAsync(h, run.Name);
        Assert.Equal(5, queued.Count);
    }

    [Fact]
    public async Task Sequential_Dispatch_OnResume_EnqueuesCurrentOnly_NeverTheNext()
    {
        // Resume shape: tasks 0,1 done; task 2 is the reached task and its queue row SURVIVED the restart;
        // task 3 has not been reached. Dispatch must not enqueue task 3 (that would put two in flight) — it
        // must leave the already-queued current task alone.
        var h = await NewHarnessAsync();
        var run = MakeRun("disp-resume", 4, sequential: true);
        run.Tasks[0].Status = "Completed";
        run.Tasks[1].Status = "Completed";
        await h.Queue.EnqueueBatchAsync(run.Name, [(run.Tasks[2].Id, 4)], DateTime.UtcNow);

        await Dispatch(h, run);

        var queued = await QueuedAsync(h, run.Name);
        Assert.Equal(["disp-resume_t2"], queued);   // task 3 NOT enqueued
    }

    [Fact]
    public async Task Sequential_Dispatch_OnResume_ReEnqueuesReachedTask_WhenItsRowIsGone()
    {
        // Reached task's queue row was lost (advance never landed). Dispatch re-enqueues exactly it.
        var h = await NewHarnessAsync();
        var run = MakeRun("disp-resume-gone", 3, sequential: true);
        run.Tasks[0].Status = "Completed";

        await Dispatch(h, run);

        var queued = await QueuedAsync(h, run.Name);
        Assert.Equal(["disp-resume-gone_t1"], queued);
    }

    // ─── advance ────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Advance_EnqueuesNextPendingBySequence()
    {
        var h = await NewHarnessAsync();
        var run = MakeRun("adv-next", 4, sequential: true);
        run.Tasks[0].Status = "Completed";

        await Advance(h, run);

        Assert.Equal(["adv-next_t1"], await QueuedAsync(h, run.Name));
    }

    [Fact]
    public async Task Advance_ProceedsPastAFailedTask()
    {
        var h = await NewHarnessAsync();
        var run = MakeRun("adv-failed", 3, sequential: true);
        run.Tasks[0].Status = "Failed";   // a failed step must not strand the rest

        await Advance(h, run);

        Assert.Equal(["adv-failed_t1"], await QueuedAsync(h, run.Name));
    }

    [Fact]
    public async Task Advance_EnqueuesNothing_WhenAllTasksTerminal()
    {
        var h = await NewHarnessAsync();
        var run = MakeRun("adv-done", 3, sequential: true);
        run.Tasks[0].Status = "Completed";
        run.Tasks[1].Status = "Failed";
        run.Tasks[2].Status = "Completed";

        await Advance(h, run);

        Assert.Empty(await QueuedAsync(h, run.Name));
    }

    // ─── re-drive restriction ───────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Redrive_Sequential_DoesNothing_WhileATaskIsRunning()
    {
        // The current task is Running; the not-yet-reached tasks deliberately have no queue row. The
        // watchdog must not treat them as orphaned and enqueue them — that would put multiple in flight.
        var h = await NewHarnessAsync();
        var run = MakeRun("rd-running", 3, sequential: true);
        run.Tasks[0].Status = "Running";

        await Redrive(h, run);
        await Task.Delay(100); // give any (erroneous) fire-and-forget requeue time to land

        Assert.Empty(await QueuedAsync(h, run.Name));
    }

    [Fact]
    public async Task Redrive_Sequential_NoOp_WhenCurrentTaskStillHasItsRow()
    {
        // Nothing running, current task (Sequence 0) is queued and waiting; later tasks have no row. The
        // current is dispatchable (row present) so not orphaned, and the later tasks must not be touched.
        var h = await NewHarnessAsync();
        var run = MakeRun("rd-waiting", 3, sequential: true);
        await h.Queue.EnqueueBatchAsync(run.Name, [(run.Tasks[0].Id, 4)], DateTime.UtcNow);

        await Redrive(h, run);
        await Task.Delay(100);

        Assert.Equal(["rd-waiting_t0"], await QueuedAsync(h, run.Name)); // unchanged; t1/t2 not enqueued
    }

    [Fact]
    public async Task Redrive_Sequential_ReDrivesOnlyTheCurrentTask_WhenStalled()
    {
        // Chain stalled: current task (Sequence 0) has no queue row and nothing is running. The watchdog
        // re-enqueues exactly the current task — and none of the not-yet-reached tasks.
        var h = await NewHarnessAsync();
        var run = MakeRun("rd-stalled", 3, sequential: true);

        await Redrive(h, run);

        var queued = await WaitQueuedAsync(h, run.Name, 1);
        Assert.Equal(["rd-stalled_t0"], queued); // only the current; t1/t2 stay unqueued
    }

    [Fact]
    public async Task Redrive_FanOut_ReEnqueuesAllOrphanedTasks()
    {
        // The default fan-out behaviour is unchanged: every orphaned (Pending, no queue row) task is
        // re-driven, not just the first.
        var h = await NewHarnessAsync();
        var run = MakeRun("rd-fanout", 3, sequential: false);

        await Redrive(h, run);

        var queued = await WaitQueuedAsync(h, run.Name, 3);
        Assert.Equal(3, queued.Count);
    }
}

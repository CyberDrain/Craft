using System.Collections.Concurrent;
using System.Reflection;
using Craft.Configuration;
using Craft.Orchestration;
using Craft.PowerShellHost;
using Craft.Services;
using Craft.Storage;
using Microsoft.Extensions.Logging.Abstractions;

namespace Craft.Tests;

/// <summary>
/// Pins SEQUENTIAL orchestrator mode.
///
/// A run marked <see cref="OrchestratorRun.Sequential"/> runs its tasks ONE AT A TIME, in ascending
/// <see cref="OrchestratorTaskItem.Sequence"/> (payload) order, ON A SINGLE PINNED WORKER: one entry row
/// is dispatched, that one claim drives the WHOLE run inline (checkout one worker → run every step on it →
/// reclaim once), and the not-yet-reached steps never get their own queue row. The default (false) is the
/// existing fan-out: every task enqueued up front and drained in parallel by the pool.
///
/// These fix the contract at every seam it touches:
///   - persistence  — the flag and the per-task order survive a round trip AND a status-write rewrite
///                     (Replace mode erases any column the write omits), so a resumed run keeps its order;
///   - dispatch     — a sequential run enqueues only its single entry row; fan-out enqueues all;
///   - driver       — every step runs, in Sequence order, on ONE worker; a failing step does not strand the
///                     rest (best-effort); a cancelled run marks the remaining steps Cancelled; a duplicate
///                     entry row is a no-op while a driver is already active;
///   - re-drive     — the watchdog leaves a run alone while its driver is active (or its entry job is still
///                     queued/running), yet still restarts the driver if the entry row is lost.
///
/// The driver's loop logic is exercised through <see cref="SeqDriver"/>, a subclass that overrides the
/// three PowerShell seams (checkout / run-step / reclaim), so these tests need no worker pool. The actual
/// pinning to a live worker is proven separately by live validation against the dev backend.
/// </summary>
public class OrchestratorSequentialTests
{
    private const string TaskFunc = "Invoke-CraftTask";

    // ─── seam subclass ──────────────────────────────────────────────────────────────────────────────
    // Overrides only the three PowerShell interactions. Everything else — ordering, best-effort, marker
    // writes, completion, cancellation — runs the real OrchestratorService code.

    private sealed class SeqDriver : OrchestratorService
    {
        // OrchestratorService has only a parameterized constructor; a subclass must chain to it to compile.
        // Never actually invoked — the harness builds this via GetUninitializedObject — so the nulls are safe.
        private SeqDriver() : base(null!, null!, null!, null!, null!, null!, null!, null!) { }

        public List<string> Order = new();          // step ids in the order the driver ran them
        public HashSet<string> FailIds = new();     // ids whose step throws (best-effort test)
        public int Checkouts;                        // must be exactly 1 for a run that starts
        public int Reclaims;                         // must pair 1:1 with Checkouts
        public string StepOutput = string.Empty;     // what a step "returns" (PostExecution capture test)
        public Action<OrchestratorTaskItem>? OnStep; // test hook, runs synchronously inside a step

        internal override PowerShellWorker? CheckoutSequentialWorker(CancellationToken ct)
        {
            Checkouts++;
            return null; // step execution is faked, so the worker handle is never dereferenced
        }

        internal override void ReclaimSequentialWorker(PowerShellWorker? worker, bool faulted) => Reclaims++;

        internal override Task<string> RunSequentialStepAsync(
            OrchestratorRun run, OrchestratorTaskItem task, string taskPath, PowerShellWorker? worker)
        {
            Order.Add(task.Id);
            OnStep?.Invoke(task);
            if (FailIds.Contains(task.Id))
                throw new InvalidOperationException("boom " + task.Id);
            return Task.FromResult(StepOutput);
        }
    }

    // ─── harness ────────────────────────────────────────────────────────────────────────────────────

    private sealed record Harness(SeqDriver Svc, OrchestratorTableStore Store, JobQueueStore Queue, JobManager Jobs);

    private static async Task<Harness> NewHarnessAsync()
    {
        var settings = new CraftSettings
        {
            Orchestrator = { TablePrefix = "seq" + Guid.NewGuid().ToString("N")[..8] }
        };
        // Direct, synchronous status writes — no batching barrier or drain loop to stand up in a unit test.
        settings.Orchestrator.BatchStatusWrites = false;

        var backing = new RunRemainingCounterTests.ConditionalStore();
        var store = new OrchestratorTableStore(NullLogger<OrchestratorTableStore>.Instance, settings, backing);
        var queue = new JobQueueStore(NullLogger<JobQueueStore>.Instance, settings, backing);
        await store.InitializeAsync();
        await queue.InitializeAsync();
        var writer = new OrchestratorStatusWriter(store, NullLogger<OrchestratorStatusWriter>.Instance, settings);

        // Build the service without its constructor (which drags in the PowerShell runner and the worker
        // pool) and set only the fields the dispatch / driver / re-drive paths read — the same shape the
        // other orchestrator unit tests use.
        var svc = (SeqDriver)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(SeqDriver));
        svc.Order = new();
        svc.FailIds = new();
        Set(svc, "_logger", NullLogger<OrchestratorService>.Instance);
        Set(svc, "_store", store);
        Set(svc, "_queue", queue);
        Set(svc, "_writer", writer);
        Set(svc, "_lock", new object());
        Set(svc, "_activeRuns", new ConcurrentDictionary<string, OrchestratorRun>());
        Set(svc, "_taskScriptPaths", new ConcurrentDictionary<string, string>());
        Set(svc, "_finalizingRuns", new ConcurrentDictionary<string, bool>());
        Set(svc, "_finalizeDeferrals", new ConcurrentDictionary<string, int>());
        Set(svc, "_childRuns", new ConcurrentDictionary<string, ConcurrentBag<string>>());
        Set(svc, "_cancelledRuns", new ConcurrentDictionary<string, bool>());
        Set(svc, "_activeSequentialDrivers", new ConcurrentDictionary<string, bool>());
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

        return new Harness(svc, store, queue, jm);
    }

    private static object NewFieldDict(object svc, string field)
    {
        var t = typeof(OrchestratorService).GetField(field, BindingFlags.NonPublic | BindingFlags.Instance)!.FieldType;
        return Activator.CreateInstance(t)!;
    }

    private static void Set(object target, string field, object? value) =>
        typeof(OrchestratorService).GetField(field, BindingFlags.NonPublic | BindingFlags.Instance)!
            .SetValue(target, value);

    private static T Get<T>(object target, string field) =>
        (T)typeof(OrchestratorService).GetField(field, BindingFlags.NonPublic | BindingFlags.Instance)!
            .GetValue(target)!;

    private static Task Invoke(OrchestratorService svc, string method, params object[] args) =>
        (Task)typeof(OrchestratorService).GetMethod(method, BindingFlags.NonPublic | BindingFlags.Instance)!
            .Invoke(svc, args)!;

    private static Task Dispatch(Harness h, OrchestratorRun run) =>
        Invoke(h.Svc, "DispatchPendingTasksAsync", run, TaskFunc, run.Priority, CancellationToken.None, false);
    private static Task Redrive(Harness h, OrchestratorRun run) => Invoke(h.Svc, "RedrivePendingTasksAsync", run);

    /// <summary>Run the sequential driver to completion. Pre-seeds _finalizingRuns so the background
    /// finalize the last step schedules cannot mutate the run under the test's assertions.</summary>
    private static async Task DriveAsync(Harness h, OrchestratorRun run, string taskPath = TaskFunc,
        CancellationToken ct = default)
    {
        Get<ConcurrentDictionary<string, bool>>(h.Svc, "_finalizingRuns")[run.Name] = true;
        var mi = typeof(OrchestratorService).GetMethod("BuildSequentialRunWork",
            BindingFlags.NonPublic | BindingFlags.Instance)!;
        var work = (Func<CancellationToken, Task>)mi.Invoke(h.Svc, [run, taskPath])!;
        try { await work(ct); }
        catch (TargetInvocationException ex) { throw ex.InnerException ?? ex; }
    }

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

    private static string St(OrchestratorRun run, int i) => run.Tasks.Single(t => t.Sequence == i).Status;

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
    public async Task Sequential_Dispatch_EnqueuesOnlyTheEntryRow()
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
    public async Task Sequential_Dispatch_OnResume_EnqueuesEntryOnly_NeverASecondRow()
    {
        // Resume shape: tasks 0,1 done; task 2 is the reached task and its queue row SURVIVED the restart;
        // task 3 has not been reached. Dispatch must not enqueue task 3 (a second row would let a second
        // driver start) — it must leave the already-queued entry alone.
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
    public async Task Sequential_Dispatch_OnResume_ReEnqueuesCurrentStep_WhenItsRowIsGone()
    {
        // The reached step's queue row was lost. Dispatch re-enqueues exactly it, to restart the driver.
        var h = await NewHarnessAsync();
        var run = MakeRun("disp-resume-gone", 3, sequential: true);
        run.Tasks[0].Status = "Completed";

        await Dispatch(h, run);

        var queued = await QueuedAsync(h, run.Name);
        Assert.Equal(["disp-resume-gone_t1"], queued);
    }

    // ─── driver ─────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Driver_RunsEveryStepInSequenceOrder_OnExactlyOneWorker()
    {
        var h = await NewHarnessAsync();
        var run = MakeRun("drv-order", 4, sequential: true);
        run.Tasks.Reverse();  // insertion order 3,2,1,0 — only the Sequence sort can recover 0,1,2,3

        await DriveAsync(h, run);

        Assert.Equal(["drv-order_t0", "drv-order_t1", "drv-order_t2", "drv-order_t3"], h.Svc.Order);
        Assert.All(run.Tasks, t => Assert.Equal("Completed", t.Status));
        Assert.Equal(1, h.Svc.Checkouts);   // one worker for the whole run
        Assert.Equal(1, h.Svc.Reclaims);    // reclaimed once, at the end
    }

    [Fact]
    public async Task Driver_BestEffort_ContinuesPastAFailingStep()
    {
        var h = await NewHarnessAsync();
        var run = MakeRun("drv-besteffort", 4, sequential: true);
        h.Svc.FailIds.Add("drv-besteffort_t1");   // the second step throws

        await DriveAsync(h, run);

        // Every step is still attempted, in order, and the failure does not strand the rest.
        Assert.Equal(["drv-besteffort_t0", "drv-besteffort_t1", "drv-besteffort_t2", "drv-besteffort_t3"],
            h.Svc.Order);
        Assert.Equal("Completed", St(run, 0));
        Assert.Equal("Failed", St(run, 1));
        Assert.Equal("Completed", St(run, 2));
        Assert.Equal("Completed", St(run, 3));
        Assert.Equal(1, h.Svc.Checkouts);   // still one worker — a step failure does not re-grab a worker
        Assert.Equal(1, h.Svc.Reclaims);
    }

    [Fact]
    public async Task Driver_Cancellation_MarksRemainingStepsCancelled_AndStops()
    {
        var h = await NewHarnessAsync();
        var run = MakeRun("drv-cancel", 4, sequential: true);
        var cancelled = Get<ConcurrentDictionary<string, bool>>(h.Svc, "_cancelledRuns");
        h.Svc.OnStep = t => { if (t.Sequence == 1) cancelled[run.Name] = true; }; // cancel while step 1 runs

        await DriveAsync(h, run);

        // Steps 0 and 1 completed; the driver noticed the cancellation before step 2 and stopped there.
        Assert.Equal(["drv-cancel_t0", "drv-cancel_t1"], h.Svc.Order);
        Assert.Equal("Completed", St(run, 0));
        Assert.Equal("Completed", St(run, 1));
        Assert.Equal("Cancelled", St(run, 2));
        Assert.Equal("Cancelled", St(run, 3));
        Assert.Equal(1, h.Svc.Reclaims);    // the pinned worker is still reclaimed on the way out
    }

    [Fact]
    public async Task Driver_DuplicateEntry_IsANoOp_WhileAnotherDriverIsActive()
    {
        // A duplicate entry row for a run that already has an active driver must not start a second one.
        var h = await NewHarnessAsync();
        var run = MakeRun("drv-dup", 3, sequential: true);
        Get<ConcurrentDictionary<string, bool>>(h.Svc, "_activeSequentialDrivers")[run.Name] = true;

        await DriveAsync(h, run);

        Assert.Empty(h.Svc.Order);                 // ran nothing
        Assert.Equal(0, h.Svc.Checkouts);          // never grabbed a worker
        Assert.All(run.Tasks, t => Assert.Equal("Pending", t.Status)); // left for the real driver
    }

    [Fact]
    public async Task Driver_ClearsItsRegistration_WhenDone()
    {
        var h = await NewHarnessAsync();
        var run = MakeRun("drv-cleanup", 2, sequential: true);

        await DriveAsync(h, run);

        // The run must not stay registered, or its re-drive would be suppressed forever.
        Assert.False(Get<ConcurrentDictionary<string, bool>>(h.Svc, "_activeSequentialDrivers")
            .ContainsKey(run.Name));
    }

    [Fact]
    public async Task Driver_PostExecutionRun_CapturesAndStoresStepOutput()
    {
        var h = await NewHarnessAsync();
        var run = MakeRun("drv-postexec", 2, sequential: true);
        run.PostExecFunctionName = "Push-Aggregate";   // capture path
        h.Svc.StepOutput = "{\"ok\":true}";

        await DriveAsync(h, run);

        var results = await h.Store.GetResultsAsync(run.Name);
        Assert.Equal(2, results.Length);
        Assert.All(results, r => Assert.Contains("ok", r));
    }

    [Fact]
    public async Task ResolveTaskWork_ForSequentialRun_ReturnsAndRunsTheDriver()
    {
        // Routing: a claimed entry row for a sequential run resolves to the pinned driver (not the parallel
        // per-task work), and invoking it drives the whole run on one worker.
        var h = await NewHarnessAsync();
        var run = MakeRun("resolve-seq", 3, sequential: true);
        Get<ConcurrentDictionary<string, OrchestratorRun>>(h.Svc, "_activeRuns")[run.Name] = run;
        Get<ConcurrentDictionary<string, string>>(h.Svc, "_taskScriptPaths")[run.Name] = TaskFunc;
        Get<ConcurrentDictionary<string, bool>>(h.Svc, "_finalizingRuns")[run.Name] = true;

        var mi = typeof(OrchestratorService).GetMethod("ResolveTaskWorkAsync",
            BindingFlags.NonPublic | BindingFlags.Instance)!;
        var task = (Task)mi.Invoke(h.Svc, [new JobDescriptor(run.Name, run.Tasks[0].Id, 4), CancellationToken.None])!;
        await task;
        var work = (Func<CancellationToken, Task>?)task.GetType().GetProperty("Result")!.GetValue(task);

        Assert.NotNull(work);
        await work!(CancellationToken.None);

        Assert.Equal(["resolve-seq_t0", "resolve-seq_t1", "resolve-seq_t2"], h.Svc.Order);
        Assert.Equal(1, h.Svc.Checkouts);
    }

    // ─── re-drive restriction ───────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Redrive_Sequential_DoesNothing_WhileADriverIsActive()
    {
        // The driver runs every step inline; the not-yet-reached steps deliberately have no queue row. The
        // watchdog must not treat them as orphaned and enqueue them — that would spawn a second driver.
        var h = await NewHarnessAsync();
        var run = MakeRun("rd-driver", 3, sequential: true);
        Get<ConcurrentDictionary<string, bool>>(h.Svc, "_activeSequentialDrivers")[run.Name] = true;

        await Redrive(h, run);
        await Task.Delay(100); // give any (erroneous) fire-and-forget requeue time to land

        Assert.Empty(await QueuedAsync(h, run.Name));
    }

    [Fact]
    public async Task Redrive_Sequential_DoesNothing_WhileTheEntryJobIsStillQueued()
    {
        // No driver registered yet, but the entry job is still Queued/Running in the JobManager (the window
        // between the pump claiming the entry row and the driver registering). The watchdog must still leave
        // the run alone.
        var h = await NewHarnessAsync();
        var run = MakeRun("rd-entryjob", 3, sequential: true);
        var jobs = Get<ConcurrentDictionary<string, JobRecord>>(h.Jobs, "_jobs");
        jobs[$"{run.Name}-{run.Tasks[0].Id}"] = new JobRecord
        {
            Id = $"{run.Name}-{run.Tasks[0].Id}",
            Name = TaskFunc,
            RunName = run.Name,
            Priority = 4,
            Status = "Running",
            QueuedUtc = DateTime.UtcNow
        };

        await Redrive(h, run);
        await Task.Delay(100);

        Assert.Empty(await QueuedAsync(h, run.Name));
    }

    private static T Get<T>(JobManager jm, string field) =>
        (T)typeof(JobManager).GetField(field, BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(jm)!;

    [Fact]
    public async Task Redrive_Sequential_NoOp_WhenTheEntryStepStillHasItsRow()
    {
        // Nothing running, entry step (Sequence 0) is queued and waiting; later steps have no row. The entry
        // is dispatchable (row present) so not orphaned, and the later steps must not be touched.
        var h = await NewHarnessAsync();
        var run = MakeRun("rd-waiting", 3, sequential: true);
        await h.Queue.EnqueueBatchAsync(run.Name, [(run.Tasks[0].Id, 4)], DateTime.UtcNow);

        await Redrive(h, run);
        await Task.Delay(100);

        Assert.Equal(["rd-waiting_t0"], await QueuedAsync(h, run.Name)); // unchanged; t1/t2 not enqueued
    }

    [Fact]
    public async Task Redrive_Sequential_ReDrivesOnlyTheCurrentStep_WhenStalled()
    {
        // Driver gone and the entry row lost: current step (Sequence 0) has no queue row and no driver is
        // active. The watchdog re-enqueues exactly the current step — and none of the not-yet-reached ones —
        // so a fresh driver resumes the run.
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

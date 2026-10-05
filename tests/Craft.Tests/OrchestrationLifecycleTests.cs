using System.Text.RegularExpressions;
using Craft.Configuration;
using Craft.Orchestration;
using Craft.PowerShellHost;
using Craft.Storage;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Craft.Tests;

/// <summary>
/// Everything around a run that is not its scheduling mode: leases, shutdown, operator actions, startup cleanup,
/// result cleanup, and the log lines operators and the health tooling read.
/// </summary>
public class OrchestrationLifecycleTests
{
    private static string Batch(int n, string prefix) => OrchestrationHarness.Batch(n, prefix);

    private static JobManager NewJobs()
    {
        var settings = new CraftSettings();
        settings.Worker.BgPoolSize = 4;
        var config = new ConfigurationBuilder().AddInMemoryCollection([]).Build();
        var repo = new ScriptRepository(NullLogger<ScriptRepository>.Instance, settings);
        var pool = new PowerShellWorkerPool(repo, NullLogger<PowerShellWorkerPool>.Instance, config, settings);
        var limiter = new BackgroundTaskLimiter(NullLogger<BackgroundTaskLimiter>.Instance, config, settings, pool);
        return new JobManager(NullLogger<JobManager>.Instance, settings, limiter);
    }

    private static (WorkStore Store, WorkPump Pump, JobManager Jobs) NewIdlePump(int leaseSeconds = 1800)
    {
        var settings = new CraftSettings();
        settings.Worker.BgPoolSize = 4;
        var store = new WorkStore(NullLogger<WorkStore>.Instance, settings, new MemoryTableStore());
        var jobs = NewJobs();
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["JobQueueLeaseSeconds"] = leaseSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture),
        }).Build();
        return (store, new WorkPump(NullLogger<WorkPump>.Instance, store, jobs, config, settings), jobs);
    }

    private static async Task<RunHeader> CreateAsync(WorkStore s, string name, int tasks)
    {
        var started = DateTime.UtcNow;
        return await s.CreateRunAsync(new RunHeader
        {
            RunKey = WorkStore.RunKeyFor(name, started),
            Name = name,
            StartedUtc = started,
            TaskScriptName = "Invoke-CraftTask",
        }, Enumerable.Range(0, tasks).Select(i => new WorkStore.NewTask($"t{i}", [])).ToList());
    }

    // ── leases and shutdown ──

    [Fact]
    public async Task AClaimHeldPastTwoThirdsOfItsLease_IsRenewed_SoALongTaskNeverLosesIt()
    {
        var (store, pump, _) = NewIdlePump(leaseSeconds: 60);
        var run = await CreateAsync(store, "Long", 2);
        var now = DateTime.UtcNow;
        pump.Clock = () => now;
        Assert.Equal(2, await pump.RefillAsync(CancellationToken.None));
        var before = (await store.GetTasksAsync(run.RunKey, 'R')).Select(t => t.LeaseUntil!.Value).Min();

        now = now.AddSeconds(30);                 // half way: not yet due
        await Task.Delay(20);
        await pump.RenewAsync(CancellationToken.None);
        Assert.Equal(before, (await store.GetTasksAsync(run.RunKey, 'R')).Select(t => t.LeaseUntil!.Value).Min());

        now = now.AddSeconds(15);                 // three quarters: renewed
        await pump.RenewAsync(CancellationToken.None);
        Assert.All(await store.GetTasksAsync(run.RunKey, 'R'), t => Assert.True(t.LeaseUntil > before));
    }

    [Fact]
    public async Task OnShutdown_ClaimsThatNeverStarted_AreHandedBack_WithTheirAttemptRefunded()
    {
        var (store, pump, _) = NewIdlePump();
        var run = await CreateAsync(store, "Stopping", 6);
        Assert.Equal(4, await pump.RefillAsync(CancellationToken.None));

        await pump.StopAsync(CancellationToken.None);

        Assert.Empty(await store.GetTasksAsync(run.RunKey, 'R'));
        var pending = await store.GetTasksAsync(run.RunKey, 'P');
        Assert.Equal(6, pending.Count);
        Assert.All(pending, t => Assert.Equal(0, t.Attempt));
    }

    /// <summary>
    /// Depending on timing, the cancel lands while the job is still queued or just after the dispatcher has
    /// dequeued it. The second case once dropped the state-writer notification: the claim was never finished,
    /// lapsed half an hour later and ran after all. Either way the task must end up Cancelled.
    /// </summary>
    [Fact]
    public async Task CancellingABufferedJob_RecordsItsTaskCancelled_SoTheClaimCannotLapseAndRunIt()
    {
        await using var h = await OrchestrationHarness.CreateAsync(poolSize: 1);
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        h.Svc.BeforeRun = t => FakeOrchestrator.IdOf(t) == "b0" ? gate.Task : Task.CompletedTask;
        Assert.True(await h.Start("Buffered", Batch(3, "b")));
        var run = (await h.Store.GetRunByNameAsync("Buffered"))!;

        Assert.True(await h.DriveUntil(() => Task.FromResult(h.Jobs.GetJobs(status: "Queued").Count > 0)), "no job was ever buffered");
        var queued = h.Jobs.GetJobs(status: "Queued").First();
        Assert.True(h.Jobs.CancelJob(queued.Id));
        gate.SetResult();

        var finished = await h.DriveUntilFinished("Buffered");
        var state = string.Join(",", (await h.Store.GetTasksAsync(run.RunKey)).Select(t => $"{t.TaskId}:{t.State}:{t.Status}:{t.Owner}"));
        Assert.True(finished, $"run did not finish; started={string.Join(",", h.Svc.Started)} tasks={state} cancelled={queued.Name}");
        var done = await h.Store.GetTasksAsync(run.RunKey, 'D');
        Assert.Single(done, t => t.Status == "Cancelled");
        Assert.Equal(2, h.Svc.Started.Count);
    }

    // ── operator actions ──

    [Fact]
    public async Task Reprioritizing_MovesTheWholeRunToItsNewBand()
    {
        await using var h = await OrchestrationHarness.CreateAsync();
        Assert.True(await h.Start("First", Batch(2, "f"), priority: 4));
        Assert.True(await h.Start("Second", Batch(2, "s"), priority: 4));

        Assert.True(await h.Svc.ReprioritizeRunAsync("Second", 1));

        Assert.Equal(["Second", "First"], await h.ReadyNamesAsync());
        Assert.Equal(1, (await h.Store.GetRunByNameAsync("Second"))!.Priority);
    }

    [Fact]
    public async Task CancellingOneQueuedTaskByName_FindsItInWhicheverStackedRunHoldsIt()
    {
        await using var h = await OrchestrationHarness.CreateAsync();
        Assert.True(await h.Start("Twin", Batch(2, "a")));
        Assert.True(await h.Start("Twin", Batch(2, "b")));

        Assert.True(await h.Svc.TryCancelQueuedTaskAsync("Twin", "Job_b1"));
        Assert.False(await h.Svc.TryCancelQueuedTaskAsync("Twin", "Job_b1"));
        Assert.False(await h.Svc.TryCancelQueuedTaskAsync("Twin", "Job_nope"));
    }

    [Fact]
    public async Task ClearingTheQueue_CancelsEveryPendingTaskOfEveryRun()
    {
        await using var h = await OrchestrationHarness.CreateAsync();
        Assert.True(await h.Start("A", Batch(3, "a")));
        Assert.True(await h.Start("B", Batch(4, "b"), priority: 9));

        Assert.Equal(7, await h.Svc.ClearQueueAsync());
        Assert.Empty(await h.ReadyNamesAsync());
    }

    [Fact]
    public async Task AReadyEntryWhoseRunIsGone_IsDroppedByThePump()
    {
        await using var h = await OrchestrationHarness.CreateAsync();
        Assert.True(await h.Start("Vanished", Batch(2, "v")));
        var run = (await h.Store.GetRunByNameAsync("Vanished"))!;
        await h.Tables.DeletePartitionAsync("OrchestratorWork", run.RunKey);

        await h.Pump.RefillAsync(CancellationToken.None);
        Assert.Empty(await h.ReadyNamesAsync());
    }

    // ── bulk cancel ──

    /// <summary>
    /// Cancelling a big backlog while the pump is working. The pump once kept claiming the run's tasks during the
    /// cancel (each cancelled page woke it), so the two fought over the same rows and the run header: the call took
    /// tens of seconds, under-reported, and could give up on lost races. Now the pump leaves a run being
    /// cancelled alone, and the cancel reuses the rows it has just read.
    /// </summary>
    [Fact]
    public async Task CancellingABigBacklogWithThePumpRunning_CancelsEveryPendingTask_AndReportsTheTrueCount()
    {
        await using var h = await OrchestrationHarness.CreateAsync(poolSize: 4);
        h.Svc.HoldMs = 200;
        h.Svc.MarkRecoveryDone();
        await h.Pump.StartAsync(CancellationToken.None);
        try
        {
            Assert.True(await h.Start("Backlog", Batch(5000, "b")));
            Assert.True(await h.DriveUntil(() => Task.FromResult(h.Svc.Started.Count >= 4)));

            var sw = System.Diagnostics.Stopwatch.StartNew();
            var (found, cancelled) = await h.Svc.CancelRunAsync("Backlog");
            sw.Stop();

            var run = (await h.Store.GetRunByNameAsync("Backlog"))!;
            Assert.True(found);
            Assert.Equal(run.Cancelled, cancelled);
            Assert.Empty(await h.Store.GetTasksAsync(run.RunKey, 'P'));
            Assert.True(cancelled >= 5000 - h.Svc.Started.Count - 8, $"cancelled {cancelled}, started {h.Svc.Started.Count}");
            Assert.True(sw.Elapsed < TimeSpan.FromSeconds(20), $"cancelling 5,000 took {sw.Elapsed.TotalSeconds:F1}s");
        }
        finally
        {
            await h.Pump.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task ACancelInterruptedAfterItsFlag_IsFinishedByThePump()
    {
        await using var h = await OrchestrationHarness.CreateAsync(poolSize: 4);
        Assert.True(await h.Start("Interrupted", Batch(300, "i"), "Agg"));
        var run = (await h.Store.GetRunByNameAsync("Interrupted"))!;
        await h.Store.RequestCancelAsync(run.RunKey);             // the process died before cancelling anything

        Assert.True(await h.DriveUntilFinished("Interrupted", 30_000));
        var after = (await h.Store.GetRunByNameAsync("Interrupted"))!;
        Assert.Equal(300, after.Cancelled);
        Assert.Empty(h.Svc.Started);
        Assert.Single(h.Svc.PostExecs);
    }

    [Fact]
    public async Task ABulkCancel_ReusesTheRowsItReads_RatherThanReadingEachAgain()
    {
        var count = new CountingTableStore(new MemoryTableStore());
        await using var h = await OrchestrationHarness.CreateAsync(tables: count);
        Assert.True(await h.Start("Cheap", Batch(4900, "c")));
        count.Reset();

        Assert.Equal(4900, (await h.Svc.CancelRunAsync("Cheap")).cancelledCount);

        // 100 pages: the header before and after each, never one read per cancelled task.
        Assert.InRange(count.For("OrchestratorWork").PointReads, 0, 400);
    }

    // ── startup and cleanup ──

    [Fact]
    public async Task Startup_DropsThePreviousDesignsTables()
    {
        var tables = new MemoryTableStore();
        await using var h = await OrchestrationHarness.CreateAsync(tables: tables);

        Assert.Equal(["OrchestratorQueue", "OrchestratorQueueIndex", "OrchestratorTasks", "OrchestratorRuns", "OrchestratorResults"],
            tables.DroppedTables);
    }

    [Fact]
    public async Task ACompletedRunsResults_AreDeleted_OnceItsAggregationHasReadThem()
    {
        var tables = new MemoryTableStore();
        await using var h = await OrchestrationHarness.CreateAsync(tables: tables);
        Assert.True(await h.Start("Results", Batch(5, "r"), "Agg"));

        Assert.True(await h.DriveUntilFinished("Results"));
        Assert.Equal(5, Assert.Single(h.Svc.PostExecs).Lines.Length);
        Assert.Empty(tables.All("OrchestratorTaskResults"));
    }

    // ── what operators and tooling read ──

    [Fact]
    public async Task TheLogLines_HealthToolingParses_KeepTheirShape()
    {
        await using var h = await OrchestrationHarness.CreateAsync(poolSize: 1);
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        h.Svc.BeforeRun = t => FakeOrchestrator.IdOf(t) == "l0" ? gate.Task : Task.CompletedTask;
        h.Svc.Body = t => FakeOrchestrator.IdOf(t) == "l2" ? throw new InvalidOperationException("boom") : "{}";
        Assert.True(await h.Start("Logged", Batch(3, "l"), "Agg", priority: 6));
        Assert.True(await h.DriveUntil(() => Task.FromResult(h.Svc.Started.Contains("l0"))));
        await h.Svc.LogRunStatusAsync(CancellationToken.None);
        gate.SetResult();
        Assert.True(await h.DriveUntilFinished("Logged"));

        var lines = h.Log.Lines.Select(l => l.Message).ToList();
        // The patterns Get-CraftInstanceStatus.ps1 matches; change them together or not at all.
        Assert.Contains(lines, l => Regex.IsMatch(l, @"\] Run Logged created with 3 tasks at P6"));
        Assert.Contains(lines, l => Regex.IsMatch(l, @"\] Run Logged T\+[\d.]+min: \d+/3 done 1 running 2 pending 0 failed"));
        Assert.Contains(lines, l => l.Contains("Dispatching PostExecution") && Regex.IsMatch(l, @"for run Logged\b"));
        Assert.Contains(lines, l => Regex.IsMatch(l, @"\] Run Logged finalized: CompletedWithErrors \(2/1/0/3\)"));
        Assert.Contains(h.Log.Lines, l => l.Level == LogLevel.Debug && l.Message.StartsWith("[Scheduler] Task completed: ", StringComparison.Ordinal));
    }

    [Fact]
    public async Task APostExecutionThatGivesUp_SaysSoInTheLineTheToolingMatches()
    {
        await using var h = await OrchestrationHarness.CreateAsync();
        h.Svc.PostExecBody = () => throw new InvalidOperationException("aggregate down");
        Assert.True(await h.Start("GivesUp", Batch(1, "g"), "Agg"));

        Assert.True(await h.DriveUntil(async () =>
        {
            h.Pump.ForgetBackoff();
            return await h.Store.GetRunByNameAsync("GivesUp") is { IsFinished: true };
        }));
        Assert.Contains(h.Log.Lines, l => l.Message.Contains("giving up and cleaning up results")
            && Regex.IsMatch(l.Message, @"PostExecution for GivesUp\b"));
    }
}

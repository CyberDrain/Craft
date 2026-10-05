using Craft.Configuration;
using Craft.Orchestration;
using Craft.PowerShellHost;
using Craft.Storage;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace Craft.Tests;

/// <summary>
/// The guarantees that replace the old recovery machinery, each driven to its failure point: one process works
/// the queue (the instance lock), a stopped process's claims are taken back on sight, a failed index write never
/// strands a run, a crash at any write boundary is repaired from the active-run list, a finish that cannot be
/// written is retried rather than lost, and <see cref="OrchestratorService.InspectRunAsync"/> explains a stuck run.
/// </summary>
public class OrchestrationRecoveryTests
{
    private static readonly TimeSpan Lease = TimeSpan.FromMinutes(30);

    private static WorkStore NewStore(ICraftTableStore tables) =>
        new(NullLogger<WorkStore>.Instance, new CraftSettings(), tables) { IndexRetries = [TimeSpan.Zero] };

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

    private static WorkPump NewPump(WorkStore store, int lockSeconds = 5)
    {
        var settings = new CraftSettings();
        settings.Worker.BgPoolSize = 4;
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["InstanceLockSeconds"] = lockSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture),
        }).Build();
        return new WorkPump(NullLogger<WorkPump>.Instance, store, NewJobs(), config, settings);
    }

    private static Task<RunHeader> CreateAsync(WorkStore s, string name, int tasks, bool sequential = false, string? postExec = null)
    {
        var started = DateTime.UtcNow;
        return s.CreateRunAsync(new RunHeader
        {
            RunKey = WorkStore.RunKeyFor(name, started),
            Name = name,
            StartedUtc = started,
            TaskScriptName = "Invoke-CraftTask",
            Sequential = sequential,
            PostExecFunctionName = postExec,
        }, Enumerable.Range(0, tasks).Select(i => new WorkStore.NewTask($"t{i}", [])).ToList());
    }

    // ── the instance lock ──

    [Fact]
    public async Task OneProcessHoldsTheLock_TheNextGetsItOnceReleased()
    {
        var s = NewStore(new MemoryTableStore());

        Assert.True((await s.TryHoldInstanceLockAsync("a", Lease)).Held);
        var (held, holder) = await s.TryHoldInstanceLockAsync("b", Lease);
        Assert.False(held);
        Assert.Equal("a", holder!.Owner);
        Assert.True((await s.TryHoldInstanceLockAsync("a", Lease)).Held);      // renewal

        await s.ReleaseInstanceLockAsync("b");                                   // not the holder: no effect
        Assert.Equal("a", (await s.GetInstanceLockAsync())!.Owner);
        await s.ReleaseInstanceLockAsync("a");
        Assert.True((await s.TryHoldInstanceLockAsync("b", Lease)).Held);
    }

    [Fact]
    public async Task ALapsedLock_IsTakenOver()
    {
        var s = NewStore(new MemoryTableStore());
        Assert.True((await s.TryHoldInstanceLockAsync("crashed", TimeSpan.FromMilliseconds(30))).Held);
        await Task.Delay(60);

        Assert.True((await s.TryHoldInstanceLockAsync("successor", Lease)).Held);
    }

    [Fact]
    public async Task TwoProcessesRacingForAFreeLock_OnlyOneGetsIt()
    {
        var mem = new MemoryTableStore();
        var s = NewStore(mem);
        await s.InitializeAsync();
        var raced = false;
        (bool Held, WorkStore.InstanceLock? Holder) rival = default;
        mem.BeforeSubmit = async () =>
        {
            if (raced) return;
            raced = true;
            mem.BeforeSubmit = null;
            rival = await s.TryHoldInstanceLockAsync("rival", Lease);
        };

        var mine = await s.TryHoldInstanceLockAsync("me", Lease);

        Assert.True(rival.Held);
        Assert.False(mine.Held);
        Assert.Equal("rival", (await s.GetInstanceLockAsync())!.Owner);
    }

    [Fact]
    public async Task APumpWaitsForTheLock_AndStartsOnceItsPredecessorShutsDown()
    {
        var s = NewStore(new MemoryTableStore());
        var first = NewPump(s);
        var second = NewPump(s);
        await first.AcquireLockAsync(CancellationToken.None);

        var waiting = second.AcquireLockAsync(CancellationToken.None);
        await Task.Delay(300);
        Assert.False(waiting.IsCompleted);
        Assert.False(second.HoldsLock);

        await first.StopAsync(CancellationToken.None);
        await waiting.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(second.HoldsLock);
    }

    [Fact]
    public async Task APumpTakesTheLock_FromAPredecessorThatCrashed_OnceItsLeaseRunsOut()
    {
        var s = NewStore(new MemoryTableStore());
        await s.TryHoldInstanceLockAsync("crashed/1/abc", TimeSpan.FromSeconds(2));
        var pump = NewPump(s);

        var started = DateTime.UtcNow;
        await pump.AcquireLockAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(10));

        Assert.True(DateTime.UtcNow - started >= TimeSpan.FromSeconds(1.5));
        Assert.True(pump.HoldsLock);
    }

    [Fact]
    public async Task APumpThatLosesTheLock_StopsClaiming()
    {
        var s = NewStore(new MemoryTableStore());
        var pump = NewPump(s, lockSeconds: 6);
        await pump.AcquireLockAsync(CancellationToken.None);
        await s.ReleaseInstanceLockAsync((await s.GetInstanceLockAsync())!.Owner);
        Assert.True((await s.TryHoldInstanceLockAsync("usurper", Lease)).Held);

        await Task.Delay(TimeSpan.FromSeconds(2.1));             // past a renewal interval (lease / 3)
        Assert.False(await pump.KeepLockAsync(CancellationToken.None));
        Assert.False(pump.HoldsLock);
    }

    // ── a stopped process's claims ──

    [Fact]
    public async Task HoldingTheLock_AStoppedProcesssLiveClaimsAreTakenBackAtOnce()
    {
        var s = NewStore(new MemoryTableStore());
        var run = await CreateAsync(s, "Inherited", 3);
        Assert.Equal(3, (await s.ClaimAsync(run.RunKey, 3, "old-host/7/aaaa", Lease, false)).Count);
        var pump = NewPump(s);

        Assert.Equal(0, await pump.RefillAsync(CancellationToken.None));     // not holding the lock: left alone
        pump.ForgetBackoff();
        await pump.AcquireLockAsync(CancellationToken.None);
        Assert.Equal(3, await pump.RefillAsync(CancellationToken.None));
        Assert.All(await s.GetTasksAsync(run.RunKey, 'R'), t => Assert.Equal(2, t.Attempt));
    }

    [Fact]
    public async Task HoldingTheLock_AStoppedProcesssSequentialDriver_IsTakenOverAtOnce()
    {
        var s = NewStore(new MemoryTableStore());
        var run = await CreateAsync(s, "SeqInherited", 3, sequential: true);
        Assert.NotNull(await s.ClaimSequentialAsync(run.RunKey, "old-host/7/aaaa", Lease));
        var pump = NewPump(s);
        await pump.AcquireLockAsync(CancellationToken.None);

        Assert.Equal(1, await pump.RefillAsync(CancellationToken.None));
        var step = Assert.Single(await s.GetTasksAsync(run.RunKey, 'R'));
        Assert.Equal((0, 2), (step.Seq, step.Attempt));
    }

    // ── index writes that fail ──

    [Fact]
    public async Task AFailedReadyUpdate_DoesNotStrandARun_ThisProcessIsWorking()
    {
        var faulty = new FaultyTableStore(new MemoryTableStore());
        await using var h = await OrchestrationHarness.CreateAsync(tables: faulty);
        h.Store.IndexRetries = [TimeSpan.Zero];
        Assert.True(await h.Start("Stranded", OrchestrationHarness.Batch(2, "s"), "Agg"));
        faulty.FailUpsert = (table, _, _) => table == "OrchestratorReady";    // every later Ready update fails

        Assert.True(await h.DriveUntilFinished("Stranded"));
        Assert.Single(h.Svc.PostExecs);
    }

    [Fact]
    public async Task ARunWhoseReadyEntryNeverLanded_IsRelistedByTheRepairTheFailureTriggers()
    {
        var faulty = new FaultyTableStore(new MemoryTableStore());
        await using var h = await OrchestrationHarness.CreateAsync(tables: faulty);
        h.Store.IndexRetries = [TimeSpan.Zero];
        faulty.FailUpsert = (table, _, _) => table == "OrchestratorReady";
        Assert.True(await h.Start("Unlisted", OrchestrationHarness.Batch(2, "u")));
        faulty.FailUpsert = null;

        Assert.Empty(await h.ReadyNamesAsync());
        Assert.Contains((await h.Svc.InspectRunAsync("Unlisted")).Runs[0].Diagnosis, d => d.StartsWith("Not on the Ready list", StringComparison.Ordinal));

        await h.Pump.RefillAsync(CancellationToken.None);                     // sees the failure, repairs
        await h.Pump.LastRepair!;
        Assert.True(await h.DriveUntilFinished("Unlisted"));
    }

    // ── a crash at a write boundary, repaired from the active-run list ──

    [Fact]
    public async Task ACreationThatDiedBeforeItsHeader_IsRemoved_OnceItIsOldEnough()
    {
        var faulty = new FaultyTableStore(new MemoryTableStore());
        var s = NewStore(faulty);
        faulty.FailUpsert = (table, _, rk) => table == "OrchestratorWork" && rk == WorkStore.HeaderKey;
        await Assert.ThrowsAsync<InvalidOperationException>(() => CreateAsync(s, "HalfMade", 3));
        faulty.FailUpsert = null;
        var runKey = Assert.Single(await ActiveKeysAsync(faulty));

        var young = await s.RepairIndexesAsync(TimeSpan.FromHours(1));
        Assert.Equal((1, 0), (young.Young, young.Removed));

        var old = await s.RepairIndexesAsync(TimeSpan.Zero);
        Assert.Equal(1, old.Removed);
        Assert.Empty(await ActiveKeysAsync(faulty));
        Assert.Empty(await s.GetTasksAsync(runKey));
    }

    [Fact]
    public async Task ARunThatFinishedButWasNotRetired_IsRetiredByTheRepair()
    {
        var faulty = new FaultyTableStore(new MemoryTableStore());
        var s = NewStore(faulty);
        var run = await CreateAsync(s, "Unretired", 1);
        var claim = await s.ClaimAsync(run.RunKey, 1, "w", Lease, false);
        faulty.FailDelete = (table, _) => table is "OrchestratorReady" or "OrchestratorNames";
        await s.FinishAsync(run.RunKey, [new WorkStore.Finish(claim[0].Seq, "Completed", Owner: "w")]);
        faulty.FailDelete = null;
        Assert.Single(await ActiveKeysAsync(faulty));
        Assert.True(s.IndexFailures > 0);

        var r = await s.RepairIndexesAsync(TimeSpan.FromMinutes(10));

        Assert.Equal(1, r.Retired);
        Assert.Empty(await ActiveKeysAsync(faulty));
        Assert.Empty(await NamesAsync(s));
        Assert.Equal(1, await s.SweepFinishedAsync(TimeSpan.Zero));             // retention still finds it
    }

    internal static async Task<List<string>> ActiveKeysAsync(FaultyTableStore t)
    {
        var keys = new List<string>();
        await foreach (var r in t.QueryPartitionAsync("OrchestratorNames", "A")) keys.Add(r.RowKey);
        return keys;
    }

    private static async Task<List<string>> NamesAsync(WorkStore s)
    {
        var names = new List<string>();
        await foreach (var e in s.ReadReadyAsync()) names.Add(e.Name);
        return names;
    }

    // ── finishes that cannot be written ──

    [Fact]
    public async Task AFinishThatCannotBeWritten_IsRetried_AndTheTaskIsNotRunAgain()
    {
        var faulty = new FaultyTableStore(new MemoryTableStore());
        await using var h = await OrchestrationHarness.CreateAsync(tables: faulty);
        var failures = 0;
        faulty.FailSubmit = (table, ops) => table == "OrchestratorWork" && ops.Any(o => o.Row.RowKey.StartsWith("D|", StringComparison.Ordinal))
            && Interlocked.Increment(ref failures) <= 3;
        Assert.True(await h.Start("Flaky", OrchestrationHarness.Batch(4, "f")));

        Assert.True(await h.DriveUntilFinished("Flaky", 30_000));
        Assert.True(failures >= 3);
        Assert.Equal(4, h.Svc.Started.Count);
    }

    [Fact]
    public async Task ATaskFinishedTwiceInOneBatch_IsAppliedOnce()
    {
        var s = NewStore(new MemoryTableStore());
        var run = await CreateAsync(s, "Twice", 2);
        var claim = (await s.ClaimAsync(run.RunKey, 1, "w", Lease, false))[0];

        var outcome = await s.FinishAsync(run.RunKey,
            [new WorkStore.Finish(claim.Seq, "Completed", Owner: "w"), new WorkStore.Finish(claim.Seq, "Cancelled", Owner: "w")]);

        Assert.Equal(1, outcome!.Applied);
        Assert.Equal("Completed", Assert.Single(await s.GetTasksAsync(run.RunKey, 'D')).Status);
    }

    // ── explaining a stuck run ──

    [Fact]
    public async Task Inspection_SaysWhyARunIsNotMoving()
    {
        await using var h = await OrchestrationHarness.CreateAsync();

        Assert.True(await h.Start("Capped", OrchestrationHarness.Batch(5, "c"), maxConcurrency: 2));
        var capped = (await h.Store.GetRunByNameAsync("Capped"))!;
        Assert.Equal(2, (await h.Store.ClaimAsync(capped.RunKey, 2, h.Svc.Owner, Lease, false)).Count);
        var cappedView = (await h.Svc.InspectRunAsync("Capped")).Runs.Single();
        Assert.Equal(2, cappedView.Running.Count(r => r.HeldHere));
        Assert.Contains(cappedView.Diagnosis, d => d.StartsWith("At its concurrency limit of 2", StringComparison.Ordinal));
        Assert.Equal("3", cappedView.Pending);

        Assert.True(await h.Start("Orphaned", OrchestrationHarness.Batch(1, "o")));
        var orphaned = (await h.Store.GetRunByNameAsync("Orphaned"))!;
        await h.Store.ClaimAsync(orphaned.RunKey, 1, "gone-host/9/beef", Lease, false);
        await h.Store.TryHoldInstanceLockAsync(h.Svc.Owner, Lease);
        Assert.Contains((await h.Svc.InspectRunAsync("Orphaned")).Runs.Single().Diagnosis,
            d => d.Contains("held by a process that no longer works the queue (gone-host/9/beef)"));

        Assert.True(await h.Start("Parent", OrchestrationHarness.Batch(1, "p")));
        var parent = (await h.Store.GetRunByNameAsync("Parent"))!;
        Assert.NotNull(h.Svc.RegisterPendingChild(parent.RunKey, "Kid"));
        Assert.Contains((await h.Svc.InspectRunAsync("Parent")).Runs.Single().Diagnosis, d => d.StartsWith("Waiting for 1 child run(s): Kid", StringComparison.Ordinal));

        Assert.True(await h.Start("Quick", OrchestrationHarness.Batch(1, "q")));
        Assert.True(await h.DriveUntilFinished("Quick"));
        Assert.Contains((await h.Svc.InspectRunAsync("Quick")).Runs.Single().Diagnosis, d => d.StartsWith("Finished Completed", StringComparison.Ordinal));
    }

    // ── bounded reads ──

    [Fact]
    public async Task OnAHugeRun_StatusClaimAndInspection_ReadOnlyWhatTheyNeed()
    {
        var count = new CountingTableStore(new MemoryTableStore());
        await using var h = await OrchestrationHarness.CreateAsync(tables: count);
        Assert.True(await h.Start("Huge", OrchestrationHarness.Batch(20_000, "h")));
        var reader = new JobQueueStatusReader(NullLogger<JobQueueStatusReader>.Instance, h.Jobs, h.Store);
        count.Reset();

        await reader.GetAsync();
        Assert.InRange(count.For("OrchestratorWork").Rows, 0, JobQueueStatusReader.HeadRows);
        Assert.Equal(0, count.For("OrchestratorWork").UnboundedRanges);

        count.Reset();
        var run = (await h.Store.GetRunByNameAsync("Huge"))!;
        await h.Store.ClaimAsync(run.RunKey, 49, "w", Lease, reclaimExpired: true, new WorkStore.ClaimProbe());
        Assert.Equal(0, count.For("OrchestratorWork").UnboundedRanges);

        count.Reset();
        await h.Svc.InspectRunAsync("Huge");
        Assert.Equal(0, count.For("OrchestratorWork").UnboundedRanges);
        Assert.InRange(count.For("OrchestratorWork").Rows, 0, 1001 + 500);
    }
}

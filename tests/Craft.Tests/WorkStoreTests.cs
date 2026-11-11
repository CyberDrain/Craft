using Craft.Configuration;
using Craft.Storage;
using Microsoft.Extensions.Logging.Abstractions;

namespace Craft.Tests;

/// <summary>
/// The storage model: a task's state is one row in its run's partition, and every transition is one
/// transaction that also moves the run's counts. These pin the transitions and the guarantees that replace
/// the old reconciliation machinery: no task is claimed twice, a lost lease cannot finish someone else's
/// task, the counts reach the barrier exactly once, and a task that keeps dying is failed rather than retried
/// forever.
/// </summary>
public class WorkStoreTests
{
    private static readonly TimeSpan Lease = TimeSpan.FromMinutes(30);

    private static (WorkStore Store, MemoryTableStore Mem) New()
    {
        var mem = new MemoryTableStore();
        return (new WorkStore(NullLogger<WorkStore>.Instance, new CraftSettings(), mem), mem);
    }

    private static RunHeader Header(string name, int minute = 0, int priority = 4, string? postExec = null) => new()
    {
        RunKey = WorkStore.RunKeyFor(name, At(minute)),
        Name = name,
        Priority = priority,
        StartedUtc = At(minute),
        TaskScriptName = "Invoke-CraftTask",
        PostExecFunctionName = postExec,
    };

    private static DateTime At(int minute) => new(2026, 10, 5, 3, minute, 0, DateTimeKind.Utc);

    private static List<WorkStore.NewTask> Tasks(int n) =>
        Enumerable.Range(0, n).Select(i => new WorkStore.NewTask($"t{i}", new() { ["i"] = i })).ToList();

    private static async Task<RunHeader> CreateAsync(WorkStore s, string name, int tasks, int minute = 0,
        int priority = 4, string? postExec = null) =>
        await s.CreateRunAsync(Header(name, minute, priority, postExec), Tasks(tasks));

    private static List<WorkStore.Finish> Done(IEnumerable<WorkStore.ClaimedTask> claims, string owner = "w") =>
        claims.Select(c => new WorkStore.Finish(c.Seq, "Completed", Owner: owner)).ToList();

    [Fact]
    public async Task ARunsTasksAreClaimedOnce_AndTheLastFinishCompletesTheRun()
    {
        var (s, _) = New();
        var run = await CreateAsync(s, "R", 3);

        var first = await s.ClaimAsync(run.RunKey, 2, "w", Lease, false);
        var second = await s.ClaimAsync(run.RunKey, 2, "w", Lease, false);
        Assert.Equal([0, 1], first.Select(c => c.Seq));
        Assert.Equal([2], second.Select(c => c.Seq));
        Assert.Empty(await s.ClaimAsync(run.RunKey, 2, "w", Lease, false));

        var partial = await s.FinishAsync(run.RunKey, Done(first));
        Assert.False(partial!.Completed);
        Assert.Equal(2, partial.Header.Done);

        var last = await s.FinishAsync(run.RunKey, Done(second));
        Assert.True(last!.ReachedBarrier);
        Assert.True(last.Completed);
        Assert.Equal("Completed", last.Header.Status);
        Assert.Empty(await ReadyAsync(s));
    }

    [Fact]
    public async Task TheBarrierQueuesTheAggregation_WhichCompletesTheRun()
    {
        var (s, _) = New();
        var run = await CreateAsync(s, "R", 1, postExec: "StoreThings");

        var task = await s.ClaimAsync(run.RunKey, 8, "w", Lease, false);
        var barrier = await s.FinishAsync(run.RunKey, Done(task));
        Assert.True(barrier!.ReachedBarrier);
        Assert.False(barrier.Completed);
        Assert.Equal(RunPhase.Aggregate, barrier.Header.Phase);

        var agg = Assert.Single(await s.ClaimAsync(run.RunKey, 8, "w", Lease, false));
        Assert.Equal(WorkStore.AggregateSeq, agg.Seq);
        var done = await s.FinishAsync(run.RunKey, Done([agg]));
        Assert.True(done!.Completed);
        Assert.Equal("Completed", done.Header.PostExecStatus);
        Assert.Equal(1, done.Header.Done);
    }

    [Fact]
    public async Task AFinishFromAWorkerThatLostItsLease_IsIgnored()
    {
        var (s, _) = New();
        var run = await CreateAsync(s, "R", 1);
        var claim = await s.ClaimAsync(run.RunKey, 1, "old", TimeSpan.FromMilliseconds(1), false);
        await Task.Delay(20);
        Assert.Single(await s.ClaimAsync(run.RunKey, 1, "new", Lease, reclaimExpired: true));

        var stale = await s.FinishAsync(run.RunKey, Done(claim, "old"));

        Assert.Equal(0, stale!.Applied);
        Assert.Equal(0, (await s.GetRunAsync(run.RunKey))!.Done);
    }

    [Fact]
    public async Task ATaskThatKeepsDying_IsFailedAfterThreeAttempts()
    {
        var (s, _) = New();
        var run = await CreateAsync(s, "R", 1);

        for (var i = 0; i < 3; i++)
        {
            Assert.Single(await s.ClaimAsync(run.RunKey, 1, $"w{i}", TimeSpan.FromMilliseconds(1), reclaimExpired: true));
            await Task.Delay(20);
        }
        Assert.Empty(await s.ClaimAsync(run.RunKey, 1, "w4", Lease, reclaimExpired: true));

        var header = (await s.GetRunAsync(run.RunKey))!;
        Assert.Equal(1, header.Failed);
        Assert.Equal("CompletedWithErrors", header.Status);
    }

    private static Task<RunHeader> CreateModeAsync(WorkStore s, string name, int tasks, bool sequential = false,
        bool stopOnFailure = false, string? postExec = null)
    {
        var h = Header(name);
        return s.CreateRunAsync(new RunHeader
        {
            RunKey = h.RunKey,
            Name = h.Name,
            StartedUtc = h.StartedUtc,
            TaskScriptName = h.TaskScriptName,
            Sequential = sequential,
            StopOnFailure = stopOnFailure,
            PostExecFunctionName = postExec,
        }, Tasks(tasks));
    }

    // ── a sequential step: finish it and claim the next in one transaction ──

    [Fact]
    public async Task FinishingASequentialStep_ClaimsTheNextOne_WithItsPayload_UnderTheDriverLease()
    {
        var (s, _) = New();
        var run = await CreateModeAsync(s, "Step", 3, sequential: true);
        var first = (await s.ClaimSequentialAsync(run.RunKey, "w", Lease))!;

        var r = await s.FinishStepAsync(run.RunKey, new WorkStore.Finish(first.Seq, "Completed", Owner: "w"), "w", Lease);

        Assert.Equal((1, "t1", 1), (r.Next!.Seq, r.Next.TaskId, r.Next.Attempt));
        Assert.Equal("1", r.Payload!["i"].ToString());
        Assert.Equal("Completed", Assert.Single(await s.GetTasksAsync(run.RunKey, 'D')).Status);
        Assert.Equal("w", Assert.Single(await s.GetTasksAsync(run.RunKey, 'R')).Owner);
        var header = (await s.GetRunAsync(run.RunKey))!;
        Assert.Equal(("w", 1), (header.DriverOwner, header.Done));
        Assert.Equal(header.Done, r.Outcome!.Header.Done);
    }

    [Fact]
    public async Task FinishingTheLastSequentialStep_ClaimsTheAggregationDirectly()
    {
        var (s, _) = New();
        var run = await CreateModeAsync(s, "StepAgg", 2, sequential: true, postExec: "Agg");
        var step = (await s.ClaimSequentialAsync(run.RunKey, "w", Lease))!;
        step = (await s.FinishStepAsync(run.RunKey, new WorkStore.Finish(step.Seq, "Completed", Owner: "w"), "w", Lease)).Next!;

        var r = await s.FinishStepAsync(run.RunKey, new WorkStore.Finish(step.Seq, "Failed", "x", "w"), "w", Lease);

        Assert.Equal(WorkStore.AggregateSeq, r.Next!.Seq);
        Assert.Null(r.Payload);
        Assert.True(r.Outcome!.ReachedBarrier);
        Assert.Empty(await s.GetTasksAsync(run.RunKey, 'P'));
        Assert.Equal(WorkStore.AggregateSeq, Assert.Single(await s.GetTasksAsync(run.RunKey, 'R')).Seq);
        var header = (await s.GetRunAsync(run.RunKey))!;
        Assert.Equal((RunPhase.Aggregate, "Pending", 1), (header.Phase, header.PostExecStatus, header.Failed));
    }

    [Fact]
    public async Task FinishingTheLastSequentialStep_WithoutAggregation_CompletesTheRun_AndClaimsNothing()
    {
        var (s, _) = New();
        var run = await CreateModeAsync(s, "StepLast", 1, sequential: true);
        var step = (await s.ClaimSequentialAsync(run.RunKey, "w", Lease))!;

        var r = await s.FinishStepAsync(run.RunKey, new WorkStore.Finish(step.Seq, "Completed", Owner: "w"), "w", Lease);

        Assert.Null(r.Next);
        Assert.True(r.Outcome!.Completed);
        Assert.Equal("Completed", (await s.GetRunAsync(run.RunKey))!.Status);
    }

    [Fact]
    public async Task ACancelRequest_StopsTheNextStepBeingClaimed()
    {
        var (s, _) = New();
        var run = await CreateModeAsync(s, "StepCancel", 3, sequential: true);
        var step = (await s.ClaimSequentialAsync(run.RunKey, "w", Lease))!;
        await s.RequestCancelAsync(run.RunKey);

        var r = await s.FinishStepAsync(run.RunKey, new WorkStore.Finish(step.Seq, "Completed", Owner: "w"), "w", Lease);

        Assert.Null(r.Next);
        Assert.True(r.Outcome!.Header.CancelRequested);
        Assert.Equal(2, (await s.GetTasksAsync(run.RunKey, 'P')).Count);
        Assert.Empty(await s.GetTasksAsync(run.RunKey, 'R'));
    }

    [Fact]
    public async Task AStepOutlivingItsDriverLease_IsNotTakenBack_WhileItsOwnLeaseIsRenewed()
    {
        var (s, _) = New();
        var run = await CreateModeAsync(s, "LongStep", 2, sequential: true);
        var step = (await s.ClaimSequentialAsync(run.RunKey, "w", TimeSpan.FromMilliseconds(1)))!;
        Assert.Empty(await s.RenewAsync([step], "w", Lease));
        await Task.Delay(20);

        Assert.Null(await s.ClaimSequentialAsync(run.RunKey, "w", Lease));
        Assert.Equal(1, Assert.Single(await s.GetTasksAsync(run.RunKey, 'R')).Attempt);
    }

    [Fact]
    public async Task AStepTakenOverByAnotherDriver_IsNeitherFinishedNorFollowed_ByItsFormerOwner()
    {
        var (s, _) = New();
        var run = await CreateModeAsync(s, "StepLost", 3, sequential: true);
        Assert.NotNull(await s.ClaimSequentialAsync(run.RunKey, "old", TimeSpan.FromMilliseconds(1)));
        await Task.Delay(20);
        var taken = (await s.ClaimSequentialAsync(run.RunKey, "new", Lease))!;

        var r = await s.FinishStepAsync(run.RunKey, new WorkStore.Finish(taken.Seq, "Completed", Owner: "old"), "old", Lease);

        Assert.Null(r.Next);
        Assert.Equal(0, r.Outcome!.Applied);
        Assert.Equal("new", Assert.Single(await s.GetTasksAsync(run.RunKey, 'R')).Owner);
        Assert.Equal("new", (await s.GetRunAsync(run.RunKey))!.DriverOwner);
    }

    [Fact]
    public async Task ASequentialStepThatKeepsDying_StopsAStopOnFailureRun()
    {
        var (s, _) = New();
        var run = await CreateModeAsync(s, "SeqPoison", 4, sequential: true, stopOnFailure: true);
        for (var i = 0; i < 3; i++)
        {
            Assert.NotNull(await s.ClaimSequentialAsync(run.RunKey, $"w{i}", TimeSpan.FromMilliseconds(1)));
            await Task.Delay(20);
        }

        Assert.Null(await s.ClaimSequentialAsync(run.RunKey, "w9", Lease));
        var done = (await s.GetTasksAsync(run.RunKey, 'D')).OrderBy(t => t.Seq).ToList();
        Assert.Equal(["Failed", "Cancelled", "Cancelled", "Cancelled"], done.Select(t => t.Status));
        Assert.True((await s.GetRunAsync(run.RunKey))!.IsFinished);
    }

    [Fact]
    public async Task ASequentialStepThatKeepsDying_IsFailed_AndTheRunCarriesOn_ByDefault()
    {
        var (s, _) = New();
        var run = await CreateModeAsync(s, "SeqPoisonCarry", 3, sequential: true);
        for (var i = 0; i < 3; i++)
        {
            Assert.NotNull(await s.ClaimSequentialAsync(run.RunKey, $"w{i}", TimeSpan.FromMilliseconds(1)));
            await Task.Delay(20);
        }

        var next = await s.ClaimSequentialAsync(run.RunKey, "w9", Lease);
        Assert.Equal(1, next!.Seq);
        Assert.Equal("Failed", Assert.Single(await s.GetTasksAsync(run.RunKey, 'D')).Status);
    }

    [Fact]
    public async Task TwoClaimersRacingForTheSameRows_NeverBothWin()
    {
        var (s, mem) = New();
        var run = await CreateAsync(s, "R", 2);
        var raced = false;
        mem.BeforeSubmit = async () =>
        {
            if (raced) return;
            raced = true;
            mem.BeforeSubmit = null;
            await s.ClaimAsync(run.RunKey, 2, "rival", Lease, false);
        };

        Assert.Empty(await s.ClaimAsync(run.RunKey, 2, "me", Lease, false));
        Assert.All(await s.GetTasksAsync(run.RunKey, 'R'), t => Assert.Equal("rival", t.Owner));
    }

    [Fact]
    public async Task CancellingARun_CancelsPendingTasks_AndRunningOnesFinishTheRun()
    {
        var (s, _) = New();
        var run = await CreateAsync(s, "R", 3);
        var running = await s.ClaimAsync(run.RunKey, 1, "w", Lease, false);

        var (cancelled, _) = await s.CancelPendingAsync(run.RunKey);
        Assert.Equal(2, cancelled);
        Assert.False((await s.GetRunAsync(run.RunKey))!.IsFinished);

        var last = await s.FinishAsync(run.RunKey, Done(running));
        Assert.True(last!.Completed);
        Assert.Equal("CompletedWithErrors", last.Header.Status);
        Assert.Equal(2, last.Header.Cancelled);
    }

    [Fact]
    public async Task AParentWaitsForItsChild_ThenCompletes()
    {
        var (s, _) = New();
        var parent = await CreateAsync(s, "Parent", 1);
        var task = await s.ClaimAsync(parent.RunKey, 1, "w", Lease, false);

        Assert.True(await s.AddChildAsync(parent.RunKey, "Child~1"));
        var tasksDone = await s.FinishAsync(parent.RunKey, Done(task));
        Assert.False(tasksDone!.Completed);

        var childDone = await s.FinishAsync(parent.RunKey, [new WorkStore.Finish(0, "Completed", ChildKey: "Child~1")]);
        Assert.True(childDone!.Completed);
        Assert.Equal(2, childDone.Header.Done);
    }

    [Fact]
    public async Task ARunQueuedFromAnAggregation_IsNotAChild()
    {
        var (s, _) = New();
        var parent = await CreateAsync(s, "Parent", 1, postExec: "Agg");
        await s.FinishAsync(parent.RunKey, Done(await s.ClaimAsync(parent.RunKey, 1, "w", Lease, false)));

        Assert.False(await s.AddChildAsync(parent.RunKey, "Child~1"));
    }

    [Fact]
    public async Task ReadyListsTheBestBandFirst_ThenTheOldestRun()
    {
        var (s, _) = New();
        await CreateAsync(s, "Zeta", 1, minute: 0);
        await CreateAsync(s, "Alpha", 1, minute: 5);
        await CreateAsync(s, "Urgent", 1, minute: 9, priority: 1);

        Assert.Equal(["Urgent", "Zeta", "Alpha"], (await ReadyAsync(s)).Select(e => e.Name));
    }

    [Fact]
    public async Task AReleasedTaskGoesBackToPending_WithItsAttemptRefunded()
    {
        var (s, _) = New();
        var run = await CreateAsync(s, "R", 1);
        var claim = Assert.Single(await s.ClaimAsync(run.RunKey, 1, "w", Lease, false));

        Assert.True(await s.ReleaseAsync(run.RunKey, claim.Seq, "w", refundAttempt: true));

        var again = Assert.Single(await s.ClaimAsync(run.RunKey, 1, "w", Lease, false));
        Assert.Equal(1, again.Attempt);
    }

    [Fact]
    public async Task ARunStillUnfinishedPastTheRetention_IsAbandoned_SoNothingClaimsIt_AndAYoungRunIsLeft()
    {
        var (s, _) = New();
        var stale = await CreateAsync(s, "Stuck", 2, postExec: "Invoke-PostExec");
        var running = await s.ClaimAsync(stale.RunKey, 1, "w", Lease, false);
        Assert.NotEmpty(running);
        var now = DateTime.UtcNow;
        var young = await s.CreateRunAsync(new RunHeader
        {
            RunKey = WorkStore.RunKeyFor("Young", now),
            Name = "Young",
            StartedUtc = now,
            TaskScriptName = "Invoke-CraftTask",
        }, Tasks(2));

        // The periodic sweep leaves a run with a live claim alone; startup, whose claims are a stopped process's, does not.
        Assert.Equal(0, await s.AbandonStaleAsync(TimeSpan.FromHours(24), skipClaimed: true));
        Assert.NotNull(await s.GetRunAsync(stale.RunKey));
        Assert.Equal(1, await s.AbandonStaleAsync(TimeSpan.FromHours(24), skipClaimed: false));

        Assert.Null(await s.GetRunAsync(stale.RunKey));
        Assert.Null(await s.GetRunByNameAsync("Stuck"));
        var ready = new List<string>();
        await foreach (var e in s.ReadReadyAsync()) ready.Add(e.RunKey);
        Assert.Equal([young.RunKey], ready);
        Assert.Equal(0, await s.AbandonStaleAsync(TimeSpan.FromHours(24), skipClaimed: false));

        // A task still running elsewhere finishing later recreates nothing, so no post-execution is ever claimed.
        Assert.Null(await s.FinishAsync(stale.RunKey, Done(running)));
        Assert.Null(await s.GetRunAsync(stale.RunKey));
        Assert.Empty(await s.GetTasksAsync(stale.RunKey));
    }

    [Fact]
    public async Task ThePeriodicSweep_AbandonsAStaleRun_OnceItsClaimsHaveLapsed()
    {
        var (s, _) = New();
        var stale = await CreateAsync(s, "Lapsed", 2);
        Assert.NotEmpty(await s.ClaimAsync(stale.RunKey, 1, "w", TimeSpan.FromMilliseconds(1), false));
        await Task.Delay(20);

        Assert.Equal(1, await s.AbandonStaleAsync(TimeSpan.FromHours(24), skipClaimed: true));
        Assert.Null(await s.GetRunAsync(stale.RunKey));
    }

    /// <summary>
    /// What no index reaches goes: a Work partition with no header, Results and a name row for a run that has none.
    /// A live run's rows, a run on the active list (still being created) and anything newer than the minimum age stay.
    /// </summary>
    [Fact]
    public async Task TheOrphanSweep_DropsWhatNoIndexReaches_AndKeepsLiveYoungAndListedRuns()
    {
        var (s, mem) = New();
        var p = new CraftSettings().Orchestrator.TablePrefix;
        var live = await CreateAsync(s, "Live", 2);
        await mem.UpsertAsync($"{p}TaskResults", new StoreRow(live.RunKey, "0"));
        await mem.UpsertAsync($"{p}Work", new StoreRow("Gone~1", "D|0000000"));
        await mem.UpsertAsync($"{p}TaskResults", new StoreRow("Gone~2", "0"));
        await mem.UpsertAsync($"{p}Names", new StoreRow("N", "Gone") { ["RunKey"] = "Gone~2" });
        await mem.UpsertAsync($"{p}Names", new StoreRow("A", "Creating~3"));
        await mem.UpsertAsync($"{p}Work", new StoreRow("Creating~3", "P|0000000"));

        Assert.Equal(new WorkStore.OrphanResult(0, 0, 0), await s.SweepOrphansAsync(TimeSpan.FromHours(1)));
        Assert.Equal(new WorkStore.OrphanResult(1, 1, 1), await s.SweepOrphansAsync(TimeSpan.Zero));

        Assert.DoesNotContain(mem.All($"{p}Work"), r => r.PartitionKey == "Gone~1");
        Assert.DoesNotContain(mem.All($"{p}TaskResults"), r => r.PartitionKey == "Gone~2");
        Assert.Contains(mem.All($"{p}TaskResults"), r => r.PartitionKey == live.RunKey);
        Assert.Contains(mem.All($"{p}Work"), r => r.PartitionKey == "Creating~3");
        Assert.NotNull(await s.GetRunAsync(live.RunKey));
        Assert.NotNull(await s.GetRunByNameAsync("Live"));
        Assert.Equal(new WorkStore.OrphanResult(0, 0, 0), await s.SweepOrphansAsync(TimeSpan.Zero));
    }

    [Fact]
    public async Task RetentionDeletesRunsFinishedBeforeTheCutoff()
    {
        var (s, mem) = New();
        var run = await CreateAsync(s, "R", 1);
        await s.FinishAsync(run.RunKey, Done(await s.ClaimAsync(run.RunKey, 1, "w", Lease, false)));

        Assert.Equal(0, await s.SweepFinishedAsync(TimeSpan.FromHours(1)));
        Assert.Equal(1, await s.SweepFinishedAsync(TimeSpan.Zero));
        Assert.Null(await s.GetRunAsync(run.RunKey));
        Assert.Null(await s.GetRunByNameAsync("R"));
        Assert.DoesNotContain(mem.All($"{new CraftSettings().Orchestrator.TablePrefix}Work"), r => r.PartitionKey == run.RunKey);
    }

    /// <summary>
    /// The header is rewritten in every finish transaction, and a transaction cannot split a row, so a
    /// property over Azure's 64 KiB limit there would fail every finish of the run. Checked on the row itself,
    /// which catches it without needing a real backend or a run large enough to hit the limit.
    /// </summary>
    [Fact]
    public async Task TheHeaderStaysSmall_HoweverLargeThePostExecutionParameters()
    {
        var (s, mem) = New();
        var big = new string('x', 200_000);
        var header = Header("Big", postExec: "Agg");
        await s.CreateRunAsync(new RunHeader
        {
            RunKey = header.RunKey,
            Name = header.Name,
            StartedUtc = header.StartedUtc,
            TaskScriptName = header.TaskScriptName,
            PostExecFunctionName = "Agg",
            PostExecParametersJson = big,
        }, Tasks(1));

        var row = mem.All("OrchestratorWork").Single(r => r.RowKey == WorkStore.HeaderKey);
        Assert.All(row.Properties.Values, v => Assert.True((v as string)?.Length is null or < 32_000));
        Assert.Equal(big, await s.GetPostExecParametersAsync(header.RunKey));
    }

    [Fact]
    public async Task APayloadRoundTripsToTheTaskThatWasClaimed()
    {
        var (s, _) = New();
        var run = await CreateAsync(s, "R", 2);
        var claim = (await s.ClaimAsync(run.RunKey, 2, "w", Lease, false))[1];

        var payload = await s.GetPayloadAsync(run.RunKey, claim.Seq);
        Assert.Equal("t1", claim.TaskId);
        Assert.Equal("1", payload!["i"].ToString());
    }

    private static async Task<List<WorkStore.ReadyEntry>> ReadyAsync(WorkStore s)
    {
        var list = new List<WorkStore.ReadyEntry>();
        await foreach (var e in s.ReadReadyAsync()) list.Add(e);
        return list;
    }
}

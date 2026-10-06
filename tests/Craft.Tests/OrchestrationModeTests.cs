using Craft.Storage;

namespace Craft.Tests;

/// <summary>
/// How a run's tasks are scheduled, end to end: fan-out (all at once), a concurrency limit (at most N at once,
/// workers released between tasks so other work interleaves), and sequential (one at a time on one pinned
/// worker, optionally stopping at the first failure).
/// </summary>
public class OrchestrationModeTests
{
    private static string Batch(int n, string prefix) => OrchestrationHarness.Batch(n, prefix);

    // ── concurrency limit ──

    [Fact]
    public async Task AConcurrencyLimit_IsNeverExceeded_AndIsUsedInFull()
    {
        await using var h = await OrchestrationHarness.CreateAsync(poolSize: 6);
        h.Svc.HoldMs = 40;
        Assert.True(await h.Start("Capped", Batch(24, "c"), maxConcurrency: 2));

        Assert.True(await h.DriveUntilFinished("Capped", 20_000));
        Assert.Equal(2, h.Svc.MaxActive);
        Assert.Equal(24, h.Svc.Started.Distinct().Count());
        Assert.Equal(24, h.Svc.Started.Count);
    }

    [Fact]
    public async Task NoLimit_UsesTheWholePool()
    {
        await using var h = await OrchestrationHarness.CreateAsync(poolSize: 4);
        h.Svc.HoldMs = 60;
        Assert.True(await h.Start("Open", Batch(16, "o")));

        Assert.True(await h.DriveUntilFinished("Open", 20_000));
        Assert.Equal(4, h.Svc.MaxActive);
    }

    [Fact]
    public async Task ALimitAboveThePoolSize_IsBoundedByThePool()
    {
        await using var h = await OrchestrationHarness.CreateAsync(poolSize: 3);
        h.Svc.HoldMs = 40;
        Assert.True(await h.Start("Roomy", Batch(12, "r"), maxConcurrency: 10));

        Assert.True(await h.DriveUntilFinished("Roomy", 20_000));
        Assert.Equal(3, h.Svc.MaxActive);
    }

    [Fact]
    public async Task ALimitOfOne_RunsTheTasksInPayloadOrder()
    {
        await using var h = await OrchestrationHarness.CreateAsync(poolSize: 4);
        h.Svc.HoldMs = 10;
        Assert.True(await h.Start("OneByOne", Batch(6, "s"), maxConcurrency: 1));

        Assert.True(await h.DriveUntilFinished("OneByOne"));
        Assert.Equal(["s0", "s1", "s2", "s3", "s4", "s5"], h.Svc.Started);
        Assert.Equal(1, h.Svc.MaxActive);
    }

    [Fact]
    public async Task ALimitOfOne_ReleasesTheWorkerBetweenTasks_SoUrgentWorkRunsInBetween()
    {
        Assert.Equal(["s0", "u0", "s1", "s2"], await InterleaveAsync(sequential: false));
    }

    [Fact]
    public async Task ASequentialRun_HoldsItsWorker_SoUrgentWorkWaitsForTheWholeRun()
    {
        Assert.Equal(["s0", "s1", "s2", "u0"], await InterleaveAsync(sequential: true));
    }

    /// <summary>One worker. A three-task run (limit 1, or sequential) is held on its first task while an urgent
    /// run is queued; the order everything then runs in shows whether the worker went back to the pool.</summary>
    private static async Task<List<string>> InterleaveAsync(bool sequential)
    {
        await using var h = await OrchestrationHarness.CreateAsync(poolSize: 1);
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        h.Svc.BeforeRun = t => FakeOrchestrator.IdOf(t) == "s0" ? gate.Task : Task.CompletedTask;
        Assert.True(await h.Start("Slow", Batch(3, "s"), priority: 5, sequential: sequential,
            maxConcurrency: sequential ? 0 : 1));

        Assert.True(await h.DriveUntil(() => Task.FromResult(h.Svc.Started.Contains("s0"))));
        Assert.True(await h.Start("Urgent", Batch(1, "u"), priority: 1));
        await h.DriveUntil(() => Task.FromResult(false), 200);
        gate.SetResult();

        Assert.True(await h.DriveUntilAllFinished());
        return [.. h.Svc.Started];
    }

    [Fact]
    public async Task ALimitedRun_StillRunsItsPostExecutionOnce_AfterEveryTask()
    {
        await using var h = await OrchestrationHarness.CreateAsync();
        Assert.True(await h.Start("CappedAgg", Batch(5, "a"), "Agg", maxConcurrency: 1));

        Assert.True(await h.DriveUntilFinished("CappedAgg"));
        var post = Assert.Single(h.Svc.PostExecs);
        Assert.Equal(5, post.Lines.Length);
    }

    [Fact]
    public async Task ALimitedRunWithFailures_StaysWithinItsLimit_AndFinishesWithErrors()
    {
        await using var h = await OrchestrationHarness.CreateAsync(poolSize: 6);
        h.Svc.HoldMs = 20;
        h.Svc.Body = t => int.Parse(FakeOrchestrator.IdOf(t)[1..], System.Globalization.CultureInfo.InvariantCulture) % 3 == 0 ? throw new InvalidOperationException("boom") : "{}";
        Assert.True(await h.Start("CappedFailing", Batch(12, "f"), maxConcurrency: 3));

        Assert.True(await h.DriveUntilFinished("CappedFailing", 20_000));
        var run = (await h.Store.GetRunByNameAsync("CappedFailing"))!;
        Assert.Equal(3, h.Svc.MaxActive);
        Assert.Equal("CompletedWithErrors", run.Status);
        Assert.Equal(4, run.Failed);
    }

    [Fact]
    public async Task TheLimitIsStored_SoAnotherProcessPickingUpTheRunHonoursIt()
    {
        var tables = new MemoryTableStore();
        await using (var first = await OrchestrationHarness.CreateAsync(tables: tables))
            Assert.True(await first.Start("Handover", Batch(10, "h"), maxConcurrency: 2));

        await using var second = await OrchestrationHarness.CreateAsync(poolSize: 6, tables: tables);
        second.Svc.HoldMs = 40;
        Assert.True(await second.DriveUntilFinished("Handover", 20_000));
        Assert.Equal(2, second.Svc.MaxActive);
        Assert.Equal(2, (await second.Store.GetRunByNameAsync("Handover"))!.MaxConcurrency);
    }

    [Fact]
    public async Task ALimitOnASequentialRun_IsDropped_AndANegativeLimitMeansNone()
    {
        await using var h = await OrchestrationHarness.CreateAsync();
        Assert.True(await h.Start("SeqCapped", Batch(2, "q"), sequential: true, maxConcurrency: 3));
        Assert.True(await h.Start("Negative", Batch(2, "n"), maxConcurrency: -5));

        var seq = (await h.Store.GetRunByNameAsync("SeqCapped"))!;
        Assert.True(seq.Sequential);
        Assert.Equal(0, seq.MaxConcurrency);
        Assert.Equal(0, (await h.Store.GetRunByNameAsync("Negative"))!.MaxConcurrency);
    }

    // ── sequential: carry on (default) or stop on failure ──

    [Fact]
    public async Task ASequentialRun_CarriesOnPastAFailedStep_ByDefault()
    {
        await using var h = await OrchestrationHarness.CreateAsync();
        h.Svc.Body = t => FakeOrchestrator.IdOf(t) == "s1" ? throw new InvalidOperationException("step down") : "{}";
        Assert.True(await h.Start("CarryOn", Batch(4, "s"), "Agg", sequential: true));

        Assert.True(await h.DriveUntilFinished("CarryOn"));
        Assert.Equal(["s0", "s1", "s2", "s3"], h.Svc.Started);
        var run = (await h.Store.GetRunByNameAsync("CarryOn"))!;
        Assert.Equal((1, 0, "CompletedWithErrors"), (run.Failed, run.Cancelled, run.Status));
        Assert.Equal(3, Assert.Single(h.Svc.PostExecs).Lines.Length);
    }

    [Fact]
    public async Task CancellingASequentialRun_StopsItAfterTheStepInHand()
    {
        await using var h = await OrchestrationHarness.CreateAsync();
        h.Svc.BeforeRun = async t =>
        {
            if (FakeOrchestrator.IdOf(t) == "s1") await h.Svc.CancelRunAsync("SeqCancel");
        };
        Assert.True(await h.Start("SeqCancel", Batch(5, "s"), "Agg", sequential: true));

        Assert.True(await h.DriveUntilFinished("SeqCancel"));
        Assert.Equal(["s0", "s1"], h.Svc.Started);
        var run = (await h.Store.GetRunByNameAsync("SeqCancel"))!;
        Assert.Equal((0, 3, "CompletedWithErrors"), (run.Failed, run.Cancelled, run.Status));
    }

    /// <summary>
    /// A step's finish and the claim of the next share one transaction. If that cannot be written the finish
    /// goes the ordinary way (retried in the background) and the next step is claimed on its own.
    /// </summary>
    [Fact]
    public async Task ASequentialStepWhoseCombinedWriteFails_IsRecordedOnItsOwn_AndTheRunCarriesOn()
    {
        var faulty = new FaultyTableStore(new MemoryTableStore());
        var failures = 0;
        faulty.FailSubmit = (_, ops) =>
            ops.Count(o => o.Row.RowKey.StartsWith("R|", StringComparison.Ordinal)) == 2
            && Interlocked.CompareExchange(ref failures, 1, 0) == 0;
        await using var h = await OrchestrationHarness.CreateAsync(tables: faulty);
        Assert.True(await h.Start("SeqFallback", Batch(4, "s"), "Agg", sequential: true));

        Assert.True(await h.DriveUntilFinished("SeqFallback"));
        Assert.Equal(1, failures);
        Assert.Equal(["s0", "s1", "s2", "s3"], h.Svc.Started);
        var run = (await h.Store.GetRunByNameAsync("SeqFallback"))!;
        Assert.Equal(("Completed", 4), (run.Status, run.Done));
        Assert.Contains(h.Log.Lines, l => l.Message.Contains("with its successor; recording it on its own", StringComparison.Ordinal));
    }

    [Fact]
    public async Task StopOnFailure_CancelsTheStepsAfterTheFirstFailure_AndStillAggregates()
    {
        await using var h = await OrchestrationHarness.CreateAsync();
        h.Svc.Body = t => FakeOrchestrator.IdOf(t) == "s2" ? throw new InvalidOperationException("step down") : "{}";
        Assert.True(await h.Start("Stopper", Batch(5, "s"), "Agg", sequential: true, stopOnFailure: true));

        Assert.True(await h.DriveUntilFinished("Stopper"));
        Assert.Equal(["s0", "s1", "s2"], h.Svc.Started);
        var run = (await h.Store.GetRunByNameAsync("Stopper"))!;
        Assert.Equal((1, 2, "CompletedWithErrors"), (run.Failed, run.Cancelled, run.Status));

        var done = (await h.Store.GetTasksAsync(run.RunKey, 'D')).OrderBy(t => t.Seq).ToList();
        Assert.Equal(["Completed", "Completed", "Failed", "Cancelled", "Cancelled"], done.Select(t => t.Status));
        Assert.All(done.Skip(3), t => Assert.Equal(WorkStore.StoppedReason("Job_s2"), t.LastError));
        Assert.Equal(2, Assert.Single(h.Svc.PostExecs).Lines.Length);
        Assert.Equal(1, h.Svc.Checkouts);
        Assert.Equal(1, h.Svc.Reclaims);
    }

    [Fact]
    public async Task StopOnFailure_WhenTheLastStepFails_HasNothingToCancel()
    {
        await using var h = await OrchestrationHarness.CreateAsync();
        h.Svc.Body = t => FakeOrchestrator.IdOf(t) == "s2" ? throw new InvalidOperationException("last") : "{}";
        Assert.True(await h.Start("StopLast", Batch(3, "s"), sequential: true, stopOnFailure: true));

        Assert.True(await h.DriveUntilFinished("StopLast"));
        var run = (await h.Store.GetRunByNameAsync("StopLast"))!;
        Assert.Equal((1, 0), (run.Failed, run.Cancelled));
    }

    [Fact]
    public async Task StopOnFailure_IsIgnoredForAFanOutRun_WhichAlwaysCarriesOn()
    {
        await using var h = await OrchestrationHarness.CreateAsync();
        h.Svc.Body = t => FakeOrchestrator.IdOf(t) == "f0" ? throw new InvalidOperationException("boom") : "{}";
        Assert.True(await h.Start("FanStop", Batch(4, "f"), stopOnFailure: true));

        Assert.True(await h.DriveUntilFinished("FanStop"));
        var run = (await h.Store.GetRunByNameAsync("FanStop"))!;
        Assert.False(run.StopOnFailure);
        Assert.Equal(4, h.Svc.Started.Count);
        Assert.Equal((1, 0), (run.Failed, run.Cancelled));
    }

    [Fact]
    public async Task TheRunsModeIsStoredOnItsHeader()
    {
        await using var h = await OrchestrationHarness.CreateAsync();
        Assert.True(await h.Start("ModeSeq", Batch(1, "a"), sequential: true, stopOnFailure: true));
        Assert.True(await h.Start("ModeCap", Batch(1, "b"), maxConcurrency: 7));

        var seq = (await h.Store.GetRunByNameAsync("ModeSeq"))!;
        var cap = (await h.Store.GetRunByNameAsync("ModeCap"))!;
        Assert.Equal((true, 0, true), (seq.Sequential, seq.MaxConcurrency, seq.StopOnFailure));
        Assert.Equal((false, 7, false), (cap.Sequential, cap.MaxConcurrency, cap.StopOnFailure));
    }
}

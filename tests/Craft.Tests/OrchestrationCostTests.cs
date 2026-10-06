using Craft.Configuration;
using Craft.Orchestration;
using Craft.PowerShellHost;
using Craft.Storage;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit.Abstractions;

namespace Craft.Tests;

/// <summary>
/// Storage cost pins. Each operation's table traffic is counted and held to a bound that does not grow with
/// the size of the queue, so a change that turns an O(batch) step into an O(queue) one fails here, long before
/// it shows up as a backlog on a large instance. Where a bound is a deliberate trade-off it says why.
/// </summary>
public class OrchestrationCostTests(ITestOutputHelper output)
{
    private const string Work = "OrchestratorWork", Ready = "OrchestratorReady", Names = "OrchestratorNames",
        Finished = "OrchestratorFinished";

    private static readonly TimeSpan Lease = TimeSpan.FromMinutes(30);

    private static (WorkStore Store, CountingTableStore Count) NewStore()
    {
        var count = new CountingTableStore(new MemoryTableStore());
        return (new WorkStore(NullLogger<WorkStore>.Instance, new CraftSettings(), count), count);
    }

    private static DateTime s_clock = new(2026, 10, 6, 0, 0, 0, DateTimeKind.Utc);
    private static DateTime NextStart() => s_clock = s_clock.AddTicks(1);

    private static Task<RunHeader> CreateAsync(WorkStore s, string name, int tasks, int maxConcurrency = 0,
        string? postExec = null)
    {
        var started = NextStart();
        return s.CreateRunAsync(new RunHeader
        {
            RunKey = WorkStore.RunKeyFor(name, started),
            Name = name,
            StartedUtc = started,
            TaskScriptName = "Invoke-CraftTask",
            MaxConcurrency = maxConcurrency,
            PostExecFunctionName = postExec,
        }, Enumerable.Range(0, tasks).Select(i => new WorkStore.NewTask($"t{i}", new() { ["i"] = i })).ToList());
    }

    private static JobManager NewJobs(int poolSize = 4)
    {
        var settings = new CraftSettings();
        settings.Worker.BgPoolSize = poolSize;
        var config = new ConfigurationBuilder().AddInMemoryCollection([]).Build();
        var repo = new ScriptRepository(NullLogger<ScriptRepository>.Instance, settings);
        var pool = new PowerShellWorkerPool(repo, NullLogger<PowerShellWorkerPool>.Instance, config, settings);
        var limiter = new BackgroundTaskLimiter(NullLogger<BackgroundTaskLimiter>.Instance, config, settings, pool);
        return new JobManager(NullLogger<JobManager>.Instance, settings, limiter);
    }

    private static WorkPump NewPump(WorkStore store, JobManager jobs, int batchSize = 4, int lowWater = 2)
    {
        var settings = new CraftSettings();
        settings.Worker.BgPoolSize = 4;
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["JobQueueBatchSize"] = batchSize.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["JobQueueLowWaterMark"] = lowWater.ToString(System.Globalization.CultureInfo.InvariantCulture),
        }).Build();
        return new WorkPump(NullLogger<WorkPump>.Instance, store, jobs, config, settings);
    }

    [Theory]
    [InlineData(10)]
    [InlineData(5_000)]
    public async Task CreatingARun_CostsAFixedNumberOfCalls_WhateverItsSize(int tasks)
    {
        var (s, c) = NewStore();
        await s.InitializeAsync();
        c.Reset();

        await CreateAsync(s, "Sized", tasks);

        output.WriteLine($"work: {c.For(Work)}  names: {c.For(Names)}  ready: {c.For(Ready)}");
        Assert.Equal(2, c.For(Work).BatchUpserts);   // payload rows, then pending rows (chunked by the backend)
        Assert.Equal(1, c.For(Work).Upserts);        // the header, last
        Assert.Equal(1, c.For(Work).PointReads);     // the created run read back
        Assert.Equal(2, c.For(Names).Upserts);       // latest-by-name and the active-outing row
        Assert.Equal(1, c.For(Ready).Upserts);
    }

    [Fact]
    public async Task Claiming_ReadsOnlyTheRowsItClaims_FromAnyRunSize()
    {
        var (s, c) = NewStore();
        var run = await CreateAsync(s, "Big", 10_000);
        c.Reset();

        var claims = await s.ClaimAsync(run.RunKey, 49, "w", Lease, reclaimExpired: false);

        Assert.Equal(49, claims.Count);
        var w = c.For(Work);
        output.WriteLine($"plain claim: {w}");
        Assert.Equal((1, 49, 1, 0), (w.Queries, w.Rows, w.Submits, w.PointReads));

        c.Reset();
        await s.ClaimAsync(run.RunKey, 49, "w", Lease, reclaimExpired: true);
        w = c.For(Work);
        output.WriteLine($"claim with lapsed-lease check: {w}");
        Assert.Equal(2, w.Queries);                  // running range, then pending range
        Assert.Equal(49 + 49, w.Rows);               // the 49 live claims are read to find lapsed ones
        Assert.Equal(1, w.Submits);
    }

    [Fact]
    public async Task ARunAtItsConcurrencyLimit_CostsNoStorageReads_AndThePumpNeverHoldsMoreThanTheLimit()
    {
        var (s, c) = NewStore();
        await CreateAsync(s, "Capped", 10_000, maxConcurrency: 3);
        var pump = NewPump(s, NewJobs(), batchSize: 49, lowWater: 1_000);

        Assert.Equal(3, await pump.RefillAsync(CancellationToken.None));
        c.Reset();
        for (var i = 0; i < 5; i++) Assert.Equal(0, await pump.RefillAsync(CancellationToken.None));

        output.WriteLine($"5 refills at the limit: work {c.For(Work)}  ready {c.For(Ready)}");
        Assert.Equal((0, 0, 0), (c.For(Work).PointReads, c.For(Work).Queries, c.For(Work).Submits));
        Assert.Equal(5, c.For(Ready).Queries);
    }

    [Fact]
    public async Task ASequentialStep_FinishesAndClaimsTheNext_InOneTransaction_FromAnyRunSize()
    {
        var (s, c) = NewStore();
        var started = NextStart();
        var run = await s.CreateRunAsync(new RunHeader
        {
            RunKey = WorkStore.RunKeyFor("Steps", started),
            Name = "Steps",
            StartedUtc = started,
            TaskScriptName = "Invoke-CraftTask",
            Sequential = true,
        }, Enumerable.Range(0, 10_000).Select(i => new WorkStore.NewTask($"t{i}", new() { ["i"] = i })).ToList());
        var step = (await s.ClaimSequentialAsync(run.RunKey, "w", Lease))!;
        c.Reset();

        var r = await s.FinishStepAsync(run.RunKey, new WorkStore.Finish(step.Seq, "Completed", Owner: "w"), "w", Lease);

        Assert.NotNull(r.Payload);
        var w = c.For(Work);
        output.WriteLine($"step: work {w}  ready {c.For(Ready)}");
        Assert.Equal(3, w.PointReads);              // header and step (with the next-step range, read together), payload
        Assert.Equal((1, 1, 0), (w.Queries, w.Rows, w.UnboundedRanges));
        Assert.Equal(1, w.Submits);
        Assert.Equal(1, c.For(Ready).Upserts);
    }

    [Fact]
    public async Task FinishingABatch_IsOneTransaction_AndOneReadyUpdate()
    {
        var (s, c) = NewStore();
        var run = await CreateAsync(s, "Fin", 10_000);
        var claims = await s.ClaimAsync(run.RunKey, 49, "w", Lease, false);
        c.Reset();

        await s.FinishAsync(run.RunKey, claims.Select(x => new WorkStore.Finish(x.Seq, "Completed", Owner: "w")).ToList());

        var w = c.For(Work);
        output.WriteLine($"finish 49: {w}  ready: {c.For(Ready)}");
        Assert.Equal(1, w.Submits);
        Assert.Equal(49 + 2, w.PointReads);          // each claimed row, the header before and after
        Assert.Equal(0, w.Queries);
        Assert.Equal(1, c.For(Ready).Upserts);
    }

    [Fact]
    public async Task ConcurrentFinishesOfOneRun_AreCoalescedIntoFewTransactions()
    {
        var (s, c) = NewStore();
        var run = await CreateAsync(s, "Coalesce", 100);
        var claims = await s.ClaimAsync(run.RunKey, 40, "w", Lease, false);
        var batcher = new FinishBatcher(s, NullLogger.Instance, TimeSpan.FromMilliseconds(30));
        c.Reset();

        await Task.WhenAll(claims.Select(x => Task.Run(() => batcher.FinishAsync(run.RunKey, new WorkStore.Finish(x.Seq, "Completed", Owner: "w")))));

        output.WriteLine($"40 concurrent finishes: {c.For(Work)}");
        Assert.InRange(c.For(Work).Submits, 1, 3);
        Assert.Equal(40, (await s.GetRunAsync(run.RunKey))!.Done);
    }

    [Fact]
    public async Task TheStatusSnapshot_ReadsTheReadyListOnce_AndOnlyTheHeadOfTheQueue()
    {
        var (s, c) = NewStore();
        for (var i = 0; i < 5_000; i++) await CreateAsync(s, $"Run{i}", 2);
        var reader = new JobQueueStatusReader(NullLogger<JobQueueStatusReader>.Instance, NewJobs(), s);
        c.Reset();

        var snap = (await reader.GetAsync())!;

        Assert.Equal(10_000, snap.Total);
        output.WriteLine($"snapshot over 5,000 runs: ready {c.For(Ready)}  work {c.For(Work)}");
        Assert.Equal((1, 5_000, 5), (c.For(Ready).Queries, c.For(Ready).Rows, c.For(Ready).Pages));
        Assert.InRange(c.For(Work).Queries, 0, 50);     // HeadRuns
        Assert.InRange(c.For(Work).Rows, 0, 100);
        Assert.Equal(0, c.For(Work).PointReads);
    }

    [Theory]
    [InlineData(10)]
    [InlineData(3_000)]
    public async Task ARefill_CostsTheSame_WhateverTheBacklogBehindTheHead(int runs)
    {
        var (s, c) = NewStore();
        for (var i = 0; i < runs; i++) await CreateAsync(s, $"Run{i}", 5);
        var pump = NewPump(s, NewJobs());
        c.Reset();

        var claimed = await pump.RefillAsync(CancellationToken.None);

        Assert.Equal(4, claimed);
        output.WriteLine($"refill with {runs} runs queued: ready {c.For(Ready)}  work {c.For(Work)}");
        Assert.Equal((1, 1), (c.For(Ready).Queries, c.For(Ready).Pages));
        Assert.Equal(1, c.For(Work).PointReads);        // the head run's header
        Assert.Equal(2, c.For(Work).Queries);           // its running range (first visit) and pending range
        Assert.Equal(1, c.For(Work).Submits);
    }

    [Fact]
    public async Task LookingUpActiveRunsByName_IsOneRangeQuery_PlusOneReadPerOuting()
    {
        var (s, c) = NewStore();
        for (var i = 0; i < 1_000; i++) await CreateAsync(s, $"Other{i}", 1);
        for (var i = 0; i < 3; i++) await CreateAsync(s, "Wanted", 1);
        c.Reset();

        Assert.Equal(3, (await s.GetActiveRunsAsync("Wanted")).Count);
        Assert.Equal((1, 3), (c.For(Names).Queries, c.For(Names).Rows));
        Assert.Equal(3, c.For(Work).PointReads);
    }

    [Fact]
    public async Task TheRetentionSweep_ReadsOnlyRunsPastTheCutoff()
    {
        var (s, c) = NewStore();
        var old = new List<RunHeader>();
        for (var i = 0; i < 20; i++) old.Add(await CreateAsync(s, $"Done{i}", 1));
        foreach (var run in old)
        {
            var claim = await s.ClaimAsync(run.RunKey, 1, "w", Lease, false);
            await s.FinishAsync(run.RunKey, [new WorkStore.Finish(claim[0].Seq, "Completed", Owner: "w")]);
        }
        c.Reset();

        Assert.Equal(0, await s.SweepFinishedAsync(TimeSpan.FromHours(1)));
        Assert.Equal((1, 0), (c.For(Finished).Queries, c.For(Finished).Rows));

        Assert.Equal(20, await s.SweepFinishedAsync(TimeSpan.Zero));
    }

    [Fact]
    public async Task AWholeFanOut_CostsAFewTableCallsPerTask_EndToEnd()
    {
        var count = new CountingTableStore(new MemoryTableStore());
        await using var h = await OrchestrationHarness.CreateAsync(poolSize: 8, tables: count);
        count.Reset();
        Assert.True(await h.Start("Throughput", OrchestrationHarness.Batch(1_000, "t"), "Agg"));

        Assert.True(await h.DriveUntilFinished("Throughput", 60_000));
        var t = count.Total();
        output.WriteLine($"1,000-task fan-out end to end: {t}  work {count.For(Work)}");
        // Measured: see the output line. Bounds leave headroom for scheduling noise, not for a new per-task call.
        Assert.InRange(t.Submits / 1000.0, 0, PerTaskSubmits);
        Assert.InRange(t.PointReads / 1000.0, 0, PerTaskPointReads);
        Assert.InRange(t.Queries / 1000.0, 0, PerTaskQueries);
        Assert.InRange((t.Upserts + t.BatchUpserts) / 1000.0, 0, PerTaskWrites);
    }

    [Fact]
    public async Task ManySmallRuns_CostAFewTableCallsPerRun_EndToEnd()
    {
        var count = new CountingTableStore(new MemoryTableStore());
        await using var h = await OrchestrationHarness.CreateAsync(poolSize: 8, tables: count);
        count.Reset();
        for (var i = 0; i < 300; i++) Assert.True(await h.Start($"Small{i}", OrchestrationHarness.Batch(1, $"s{i}_")));

        Assert.True(await h.DriveUntilAllFinished(60_000));
        var t = count.Total();
        output.WriteLine($"300 single-task runs end to end: {t}");
        Assert.InRange((t.Submits + t.PointReads + t.Queries + t.Upserts + t.BatchUpserts + t.Deletes) / 300.0, 0, PerSmallRunCalls);
    }

    // Measured 2026-10-06 (three runs, steady): per task 0.25 transactions (claims and finishes batched),
    // 3.66 point reads (header and payload at dispatch, the claimed row and header at finish), 0.39 queries and
    // 1.13 writes (each task's result row for the aggregation, plus a Ready update per finish batch); per
    // single-task run 21.5 calls in all. Bounds are about 1.5x.
    private const double PerTaskSubmits = 0.4, PerTaskPointReads = 5.5, PerTaskQueries = 0.6, PerTaskWrites = 1.7,
        PerSmallRunCalls = 32;

    /// <summary>
    /// The case that wedges a big instance: thousands of runs that have nothing to claim (waiting on children,
    /// or on claims held elsewhere) sit ahead of one run that does. The pump gives each run it looks at a
    /// storage read from a small per-refill budget, so if the blocked runs keep spending that budget the run
    /// at the back is starved. Simulates ten minutes at one refill a second and requires the run at the back to
    /// be reached quickly and then claimed from on nearly every refill.
    /// </summary>
    [Fact]
    public async Task TheRunAtTheBackOfAQueueOfBlockedRuns_IsReached_AndKeepsBeingClaimed()
    {
        var (s, c) = NewStore();
        for (var i = 0; i < 2_000; i++)
        {
            var blocked = await CreateAsync(s, $"Blocked{i}", 1);
            await s.ClaimAsync(blocked.RunKey, 1, "elsewhere", TimeSpan.FromDays(1), false);
        }
        await CreateAsync(s, "Tail", 100_000);

        var jobs = NewJobs();
        jobs.SetWorkResolver((_, _) => Task.FromResult<Func<CancellationToken, Task>?>(null));
        _ = Task.Run(() => jobs.StartAsync(CancellationToken.None));
        var pump = NewPump(s, jobs);
        var now = new DateTime(2026, 10, 6, 0, 0, 0, DateTimeKind.Utc);
        pump.Clock = () => now;

        var first = -1;
        var claimedRefills = new List<bool>();
        c.Reset();
        for (var second = 0; second < 600; second++)
        {
            now = now.AddSeconds(1);
            var claimed = await pump.RefillAsync(CancellationToken.None);
            if (claimed > 0 && first < 0) first = second;
            claimedRefills.Add(claimed > 0);
            for (var spin = 0; spin < 200 && jobs.QueuedCount > 0; spin++) await Task.Delay(1);
        }
        await jobs.StopAsync(CancellationToken.None);

        var lateShare = claimedRefills.Skip(300).Count(x => x) / 300.0;
        output.WriteLine($"first claim at refill {first}; claimed in {lateShare:P0} of the last 300 refills; work {c.For(Work)} ready {c.For(Ready)}");
        Assert.InRange(first, 0, 70);
        Assert.InRange(c.For(Ready).Pages, 0, 600 * 3);   // the whole Ready list each refill, 1,000 rows a page
        Assert.True(lateShare >= 0.9, $"the run at the back was claimed in only {lateShare:P0} of the last 300 refills");
    }
}

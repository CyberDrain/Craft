using Craft.Configuration;
using Craft.Orchestration;
using Craft.PowerShellHost;
using Craft.Storage;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace Craft.Tests;

/// <summary>
/// The status APIs read the durable backlog from the Ready list: every run is counted from its counts, only
/// the head is listed task by task, and work this process holds is not counted as waiting.
/// </summary>
public class JobQueueStatusReaderTests
{
    private static (JobQueueStatusReader Reader, WorkStore Store, JobManager Jobs) New()
    {
        var settings = new CraftSettings();
        var config = new ConfigurationBuilder().AddInMemoryCollection([]).Build();
        var repo = new ScriptRepository(NullLogger<ScriptRepository>.Instance, settings);
        var pool = new PowerShellWorkerPool(repo, NullLogger<PowerShellWorkerPool>.Instance, config, settings);
        var limiter = new BackgroundTaskLimiter(NullLogger<BackgroundTaskLimiter>.Instance, config, settings, pool);
        var jobs = new JobManager(NullLogger<JobManager>.Instance, settings, limiter);
        var store = new WorkStore(NullLogger<WorkStore>.Instance, settings, new MemoryTableStore());
        return (new JobQueueStatusReader(NullLogger<JobQueueStatusReader>.Instance, jobs, store), store, jobs);
    }

    private static Task<RunHeader> Create(WorkStore s, string name, int tasks, int minute)
    {
        var started = new DateTime(2026, 10, 5, 3, minute, 0, DateTimeKind.Utc);
        return s.CreateRunAsync(new RunHeader
        {
            RunKey = WorkStore.RunKeyFor(name, started),
            Name = name,
            StartedUtc = started,
            TaskScriptName = "Invoke-CraftTask",
        }, Enumerable.Range(0, tasks).Select(i => new WorkStore.NewTask($"t{i}", [])).ToList());
    }

    [Fact]
    public async Task EveryRunIsCounted_ButOnlyTheHeadIsListed()
    {
        var (reader, store, _) = New();
        for (var i = 0; i < 60; i++) await Create(store, $"Run{i:D2}", 50, i % 60);

        var snap = (await reader.GetAsync())!;

        Assert.Equal(3_000, snap.Total);
        Assert.Equal(3_000, snap.Unclaimed);
        Assert.Equal(60, snap.ByRun.Count);
        Assert.Equal(JobQueueStatusReader.HeadRows, snap.Rows.Count);
        Assert.Equal("Run00", snap.Rows[0].RunName);
    }

    [Fact]
    public async Task WorkThisProcessHolds_IsNotCountedAsWaiting()
    {
        var (reader, store, jobs) = New();
        var run = await Create(store, "Busy", 5, 0);
        foreach (var c in await store.ClaimAsync(run.RunKey, 2, "me", TimeSpan.FromMinutes(5), false))
            jobs.Enqueue(new JobDescriptor("Busy", c.TaskId, 4) { RunKey = c.RunKey, Seq = c.Seq }, $"Busy-{c.TaskId}");

        var snap = (await reader.GetAsync())!;

        Assert.Equal(5, snap.Total);
        Assert.Equal(3, snap.Unclaimed);
        Assert.Equal(3, snap.Rows.Count);
        var summary = await reader.GetSummaryAsync();
        Assert.Equal(3, summary.QueuedDurable);
    }

    /// <summary>
    /// The snapshot is cached for seconds while this process keeps claiming. Merged views once added the live
    /// local queue to the snapshot's own "unclaimed", which had already subtracted the claims held when it was
    /// taken, so every claim made since was counted twice (seen live: 202 queued on a 200-task run).
    /// </summary>
    [Fact]
    public async Task ClaimsMadeAfterTheSnapshot_AreNotCountedTwice()
    {
        var (reader, store, jobs) = New();
        var run = await Create(store, "Busy", 5, 0);
        void Hold(IEnumerable<WorkStore.ClaimedTask> claims)
        {
            foreach (var c in claims) jobs.Enqueue(new JobDescriptor("Busy", c.TaskId, 4) { RunKey = c.RunKey, Seq = c.Seq }, $"Busy-{c.TaskId}", $"{c.RunKey}|{c.Seq}");
        }
        Hold(await store.ClaimAsync(run.RunKey, 2, "me", TimeSpan.FromMinutes(5), false));
        await reader.GetAsync(TimeSpan.FromMinutes(5));                     // cached with 2 held
        Hold(await store.ClaimAsync(run.RunKey, 2, "me", TimeSpan.FromMinutes(5), false));

        var summary = await reader.GetSummaryAsync();
        Assert.Equal(1, summary.QueuedDurable);
        Assert.Equal(5, summary.Queued + summary.Running);

        var busy = Assert.Single(await reader.GetRunSummariesAsync(), r => r.Name == "Busy");
        Assert.Equal(5, busy.Total);
        Assert.True(busy.Queued + busy.Running + busy.Completed + busy.Failed <= busy.Total,
            $"queued {busy.Queued} running {busy.Running} completed {busy.Completed} failed {busy.Failed} over total {busy.Total}");
        Assert.Equal(5, busy.Queued + busy.Running);
    }

    [Fact]
    public async Task RunsSharingAName_AreSummedUnderThatName()
    {
        var (reader, store, _) = New();
        await Create(store, "Twin", 3, 0);
        await Create(store, "Twin", 4, 1);

        var info = (await reader.GetAsync())!.ByRun["Twin"];

        Assert.Equal(7, info.Total);
        Assert.Equal(7, info.Unclaimed);
    }

    [Fact]
    public async Task AFinishedRun_LeavesTheBacklog()
    {
        var (reader, store, _) = New();
        var run = await Create(store, "Quick", 2, 0);
        var claims = await store.ClaimAsync(run.RunKey, 2, "w", TimeSpan.FromMinutes(5), false);
        await store.FinishAsync(run.RunKey, claims.Select(c => new WorkStore.Finish(c.Seq, "Completed", Owner: "w")).ToList());

        var snap = (await reader.GetAsync())!;

        Assert.Equal(0, snap.Total);
        Assert.Empty(snap.ByRun);
    }
}

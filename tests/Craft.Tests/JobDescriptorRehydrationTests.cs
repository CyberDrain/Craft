using Craft.Configuration;
using Craft.Orchestration;
using Craft.PowerShellHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace Craft.Tests;

/// <summary>
/// Covers the descriptor queue's dispatch-time rehydration: what the resolver is handed, and what happens
/// when the descriptor has gone stale underneath it.
/// </summary>
public class JobDescriptorRehydrationTests
{
    private static JobManager NewHarness()
    {
        var settings = new CraftSettings();
        settings.Worker.BgPoolSize = 8;
        var config = new ConfigurationBuilder().AddInMemoryCollection([]).Build();
        var repo = new ScriptRepository(NullLogger<ScriptRepository>.Instance, settings);
        var pool = new PowerShellWorkerPool(repo, NullLogger<PowerShellWorkerPool>.Instance, config, settings);
        var limiter = new BackgroundTaskLimiter(NullLogger<BackgroundTaskLimiter>.Instance, config, settings, pool);
        var jobs = new JobManager(NullLogger<JobManager>.Instance, settings, limiter);
        return jobs;
    }

    private static Task Pump(JobManager jobs) => Task.Run(() => jobs.StartAsync(CancellationToken.None));

    private static async Task<bool> WaitUntil(Func<bool> condition, int timeoutMs = 5000)
    {
        var deadline = Environment.TickCount64 + timeoutMs;
        while (Environment.TickCount64 < deadline)
        {
            if (condition()) return true;
            await Task.Delay(10);
        }
        return condition();
    }

    /// <summary>The descriptor reaches the resolver intact — that is the whole contract of the queue.</summary>
    [Fact]
    public async Task Dispatch_HandsTheDescriptorToTheResolver()
    {
        var jobs = NewHarness();
        var seen = new List<JobDescriptor>();
        var done = 0;

        jobs.SetWorkResolver((d, _) =>
        {
            lock (seen) seen.Add(d);
            return Task.FromResult<Func<CancellationToken, Task>?>(
                _ => { Interlocked.Increment(ref done); return Task.CompletedTask; });
        });

        for (var i = 0; i < 25; i++)
            jobs.Enqueue(new JobDescriptor("CIPPDBCacheRun", $"Graph_tenant{i:D3}", 5), $"CIPPDBCacheRun-Graph_tenant{i:D3}");

        _ = Pump(jobs);
        Assert.True(await WaitUntil(() => Volatile.Read(ref done) == 25), $"only {done}/25 ran");
        await Task.WhenAny(jobs.StopAsync(CancellationToken.None), Task.Delay(5000));

        lock (seen)
        {
            Assert.Equal(25, seen.Count);
            Assert.All(seen, d => Assert.Equal("CIPPDBCacheRun", d.RunName));
            Assert.Equal(25, seen.Select(d => d.TaskId).Distinct().Count());
        }
    }

    /// <summary>
    /// A descriptor whose task has gone (finalized, cancelled, or cleaned up while it sat queued) is
    /// Skipped, not Failed, and must not take the dispatch loop down with it.
    /// </summary>
    [Fact]
    public async Task StaleDescriptor_IsSkipped_AndDispatchContinues()
    {
        var jobs = NewHarness();
        var ran = 0;

        jobs.SetWorkResolver((d, _) => Task.FromResult<Func<CancellationToken, Task>?>(
            d.TaskId == "gone"
                ? null                                            // stale — resolver declines
                : _ => { Interlocked.Increment(ref ran); return Task.CompletedTask; }));

        jobs.Enqueue(new JobDescriptor("run", "gone", 0), "run-gone");
        for (var i = 0; i < 10; i++)
            jobs.Enqueue(new JobDescriptor("run", $"live{i}", 5), $"run-live{i}");

        _ = Pump(jobs);
        Assert.True(await WaitUntil(() => Volatile.Read(ref ran) == 10), $"only {ran}/10 ran after a stale descriptor");

        var stale = jobs.GetJobs(status: "Skipped");
        await Task.WhenAny(jobs.StopAsync(CancellationToken.None), Task.Delay(5000));

        Assert.Single(stale);
        Assert.Equal("run-gone", stale[0].Name);
    }

    /// <summary>A descriptor with no resolver registered must fail loudly, not vanish.</summary>
    [Fact]
    public async Task DescriptorWithNoResolver_FailsTheJob_RatherThanDisappearing()
    {
        var jobs = NewHarness();
        jobs.Enqueue(new JobDescriptor("run", "task", 0), "run-task");

        _ = Pump(jobs);
        Assert.True(await WaitUntil(() => jobs.GetJobs(status: "Failed").Count == 1));

        var failed = jobs.GetJobs(status: "Failed").Single();
        await Task.WhenAny(jobs.StopAsync(CancellationToken.None), Task.Delay(5000));

        Assert.Contains("resolver", failed.LastError, StringComparison.OrdinalIgnoreCase);
    }

    // ── OWNERSHIP, as used by the Pending re-drive ───────────────────────────────────────────────────

    /// <summary>
    /// The re-drive re-queues tasks that are Pending with nothing owning them, and decides ownership from
    /// this. A queued job must report owned, or the re-drive would double-dispatch every task in the queue
    /// on its next 60s tick.
    /// </summary>
    [Fact]
    public void IsQueuedOrRunning_True_ForAQueuedJob()
    {
        var jobs = NewHarness();

        jobs.Enqueue(new JobDescriptor("run-a", "task-1", 4), name: "run-a-task-1");

        Assert.True(jobs.IsQueuedOrRunning("run-a-task-1"));
    }

    /// <summary>
    /// THE trap. Terminal records stay in the tracking dictionary up to MaxTrackedJobs so the status API
    /// can report them, so a presence-only check would answer true for a job that finished long ago — and
    /// a task left Pending by an exhausted deferral would then look owned forever and never be re-driven.
    /// That is precisely the three-day outage this guards.
    /// </summary>
    [Fact]
    public async Task IsQueuedOrRunning_False_OnceTheJobHasCompleted()
    {
        var jobs = NewHarness();
        _ = Pump(jobs);

        var ran = new TaskCompletionSource();
        jobs.Enqueue(name: "run-b-task-1", priority: 4, runName: "run-b",
            work: _ => { ran.TrySetResult(); return Task.CompletedTask; });

        await ran.Task;
        Assert.True(await WaitUntil(() => !jobs.IsQueuedOrRunning("run-b-task-1")),
            "a completed job still reports as queued/running — the re-drive would never fire");
    }

    /// <summary>A job id nothing knows about is not owned. This is the state a deferred task is left in.</summary>
    [Fact]
    public void IsQueuedOrRunning_False_ForAnUnknownJob()
    {
        var jobs = NewHarness();

        Assert.False(jobs.IsQueuedOrRunning("run-c-task-never-enqueued"));
    }
}

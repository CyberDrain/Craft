using System.Management.Automation;
using System.Management.Automation.Runspaces;
using Craft.Configuration;
using Craft.Hosting;
using Craft.Orchestration;
using Craft.PowerShellHost;
using Craft.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using JobManager = Craft.Orchestration.JobManager;

namespace Craft.Tests;

/// <summary>
/// Ambient priority: nested enqueues (Start-CIPPOrchestrator called from inside a running job) read
/// <see cref="OperationContext.Invocation.Priority"/> to inherit the enclosing run's priority.
///
/// Who exposes it is deliberate, not incidental:
///   - Descriptor jobs (orchestrator activities) expose their own queue priority — a child run
///     started from an activity belongs to the parent run's band.
///   - Closure jobs expose it ONLY when the enqueuer passed inheritPriority (post-exec does, with the
///     run's priority). A plain starter script must NOT donate its job priority: scheduler starters
///     run at CIPPTimers priorities (0-30) that order the starters themselves, and letting them leak
///     would silently reprioritize every fan-out they spawn.
/// </summary>
public class OperationContextPriorityTests
{
    private static JobManager NewManager(int concurrency = 2)
    {
        var settings = new CraftSettings();
        settings.Worker.BgPoolSize = concurrency;
        var config = new ConfigurationBuilder().AddInMemoryCollection([]).Build();
        var repo = new ScriptRepository(NullLogger<ScriptRepository>.Instance, settings);
        var pool = new PowerShellWorkerPool(repo, NullLogger<PowerShellWorkerPool>.Instance, config, settings);
        var limiter = new BackgroundTaskLimiter(NullLogger<BackgroundTaskLimiter>.Instance, config, settings, pool);
        return new JobManager(NullLogger<JobManager>.Instance, settings, limiter);
    }

    private static Task Start(JobManager jobs) => Task.Run(() => jobs.StartAsync(CancellationToken.None));

    private static async Task WaitUntilAsync(Func<bool> condition, string because)
    {
        for (var i = 0; i < 200 && !condition(); i++) await Task.Delay(25);
        Assert.True(condition(), because);
    }

    [Fact]
    public async Task DescriptorJob_ExposesItsQueuePriorityAmbiently()
    {
        var jobs = NewManager();
        int? observed = int.MinValue;
        var done = 0;

        jobs.SetWorkResolver((descriptor, _) => Task.FromResult<Func<CancellationToken, Task>?>(
            _ =>
            {
                observed = OperationContext.Current?.Priority;
                done = 1;
                return Task.CompletedTask;
            }));

        _ = Start(jobs);
        jobs.Enqueue(new JobDescriptor("run", "task-0", 3), "run-task-0");

        await WaitUntilAsync(() => Volatile.Read(ref done) == 1, "descriptor job never ran");
        Assert.Equal(3, observed);
        await jobs.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task ClosureJob_WithoutInheritPriority_ExposesNone()
    {
        var jobs = NewManager();
        int? observed = int.MinValue;
        var done = 0;

        _ = Start(jobs);
        // A starter script's own priority (here 1, like a CIPPTimers starter) must not leak to the
        // work it enqueues.
        jobs.Enqueue("starter", priority: 1, work: _ =>
        {
            observed = OperationContext.Current?.Priority;
            done = 1;
            return Task.CompletedTask;
        });

        await WaitUntilAsync(() => Volatile.Read(ref done) == 1, "closure job never ran");
        Assert.Null(observed);
        await jobs.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task ClosureJob_WithInheritPriority_ExposesIt()
    {
        var jobs = NewManager();
        int? observed = int.MinValue;
        var done = 0;

        _ = Start(jobs);
        // Post-exec shape: the job itself runs at the run's priority AND donates it to nested enqueues.
        jobs.Enqueue("run-PostExec", priority: 3, work: _ =>
        {
            observed = OperationContext.Current?.Priority;
            done = 1;
            return Task.CompletedTask;
        }, runName: "run", inheritPriority: 3);

        await WaitUntilAsync(() => Volatile.Read(ref done) == 1, "post-exec job never ran");
        Assert.Equal(3, observed);
        await jobs.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task ChangePriority_PreservesInheritPriority()
    {
        var jobs = NewManager(concurrency: 1);
        int? observed = int.MinValue;
        var done = 0;
        var release = new SemaphoreSlim(0);

        _ = Start(jobs);

        // Hold the only slot so the target stays queued long enough to reprioritize.
        jobs.Enqueue("blocker", priority: 0, work: async ct =>
            await release.WaitAsync(TimeSpan.FromSeconds(10), CancellationToken.None));

        var id = jobs.Enqueue("run-PostExec", priority: 3, work: _ =>
        {
            observed = OperationContext.Current?.Priority;
            done = 1;
            return Task.CompletedTask;
        }, runName: "run", inheritPriority: 3);

        // The reprioritized entry is a `with`-copy of the original — inheritPriority must ride along.
        Assert.True(jobs.ChangePriority(id, 1), "target was not queued when reprioritized");
        release.Release();

        await WaitUntilAsync(() => Volatile.Read(ref done) == 1, "reprioritized job never ran");
        Assert.Equal(3, observed);
        await jobs.StopAsync(CancellationToken.None);
    }

    [Fact]
    public void QueueBridge_TwoArgEnqueue_KeepsTheHistoricalDefault()
    {
        // Callers that predate priorities must keep landing at P5 — the 3-arg overload exists so
        // user-initiated starters can opt INTO a higher band, not to move everyone else.
        Assert.Equal(5, new QueueBridge.PendingQueueCommand("Start-Thing", "{}").Priority);
    }

    /// <summary>
    /// PS code cannot read OperationContext.Current: the pipeline runs on the runspace's reused
    /// thread (ReuseThread), whose ExecutionContext was frozen when the thread was created — at pool
    /// warmup, before any operation context existed. The worker therefore stamps the invocation into
    /// $global:CraftOperationContext from the calling thread, and that variable is the only carrier
    /// Start-CIPPOrchestrator's priority resolution can rely on.
    /// </summary>
    [Fact]
    public async Task InvokeStampsOperationContextIntoTheRunspace_AndCleanupRemovesIt()
    {
        var worker = new PowerShellWorker(97, InitialSessionState.CreateDefault2(), NullLogger.Instance);
        try
        {
            if (worker.Runspace.RunspaceStateInfo.State == RunspaceState.BeforeOpen)
                worker.Runspace.Open();

            // Pin the reused pipeline thread's ExecutionContext to one with NO operation context,
            // exactly like pool warmup does — anything the later invocation observes must have come
            // through the stamped variable, not through thread-creation context flow.
            await worker.InvokeScriptAsync(ScriptBlock.Create("$null"));

            var invocation = new OperationContext.Invocation("Test-Function")
            {
                RunName = "StampTestRun",
                Priority = 3,
                Category = "Job"
            };
            OperationContext.Invocation? observed;
            using (OperationContext.Set(invocation))
            {
                var results = await worker.InvokeScriptAsync(ScriptBlock.Create("$global:CraftOperationContext"));
                observed = Assert.Single(results).BaseObject as OperationContext.Invocation;
            }

            Assert.NotNull(observed);
            Assert.Equal("StampTestRun", observed.RunName);
            Assert.Equal(3, observed.Priority);
            Assert.Equal("Job", observed.Category);

            // Context disposed: the next invocation stamps null (and the post-invoke global-variable
            // sweep removed the previous value), so nothing stale leaks across checkouts.
            var after = await worker.InvokeScriptAsync(ScriptBlock.Create("$null -eq $global:CraftOperationContext"));
            Assert.True((bool)Assert.Single(after).BaseObject);
        }
        finally
        {
            worker.Dispose();
        }
    }
}

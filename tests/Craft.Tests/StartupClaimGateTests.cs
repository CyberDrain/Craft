using System.Reflection;
using System.Runtime.CompilerServices;
using Craft.Configuration;
using Craft.Endpoints;
using Craft.Orchestration;
using Craft.PowerShellHost;
using Craft.Services;
using Craft.Storage;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace Craft.Tests;

/// <summary>
/// The pump claims nothing until startup recovery is done, and every way startup can end opens the gate.
///
/// A claim taken before recovery rehydrates its run from storage — the previous process's Running markers
/// included — into the live graph. Recovery then resets those markers on its own copy, which loses the
/// <c>_activeRuns</c> race, so the live graph keeps the stale Running (see OrchestratorStaleRunningTests,
/// mode B). Gating the first claim on recovery removes the race; the resolver's ownership guard stays as
/// defense in depth.
/// </summary>
public class StartupClaimGateTests
{
    /// <summary>An orchestrator carrying only the gate. Field initializers do not run on an uninitialized
    /// object, so the gate is installed by hand; every other field stays null.</summary>
    private static OrchestratorService GatedOrchestrator()
    {
        var svc = (OrchestratorService)RuntimeHelpers.GetUninitializedObject(typeof(OrchestratorService));
        typeof(OrchestratorService).GetField("_recoveryDone", BindingFlags.NonPublic | BindingFlags.Instance)!
            .SetValue(svc, new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously));
        typeof(OrchestratorService).GetField("_logger", BindingFlags.NonPublic | BindingFlags.Instance)!
            .SetValue(svc, NullLogger<OrchestratorService>.Instance);
        return svc;
    }

    private static (JobQueuePump Pump, JobManager Jobs) NewPump(OrchestratorService orchestrator)
    {
        var settings = new CraftSettings();
        settings.Worker.BgPoolSize = 4;
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["JobQueuePollIntervalMs"] = "100",
        }).Build();

        var queue = new JobQueueStore(NullLogger<JobQueueStore>.Instance, settings, new RunRemainingCounterTests.ConditionalStore());
        queue.InitializeAsync().GetAwaiter().GetResult();
        queue.EnqueueBatchAsync("StandardsApply",
            Enumerable.Range(0, 20).Select(i => ($"task-{i:D3}", 4)).ToList(), DateTime.UtcNow).GetAwaiter().GetResult();

        var repo = new ScriptRepository(NullLogger<ScriptRepository>.Instance, settings);
        var pool = new PowerShellWorkerPool(repo, NullLogger<PowerShellWorkerPool>.Instance, config, settings);
        var limiter = new BackgroundTaskLimiter(NullLogger<BackgroundTaskLimiter>.Instance, config, settings, pool);
        var jobs = new JobManager(NullLogger<JobManager>.Instance, settings, limiter);
        return (new JobQueuePump(NullLogger<JobQueuePump>.Instance, queue, jobs, config, settings, orchestrator), jobs);
    }

    private static async Task<bool> WaitUntil(Func<bool> condition, int timeoutMs = 5000)
    {
        var deadline = Environment.TickCount64 + timeoutMs;
        while (Environment.TickCount64 < deadline)
        {
            if (condition()) return true;
            await Task.Delay(20);
        }
        return condition();
    }

    [Fact]
    public async Task PumpClaimsNothingBeforeRecovery_AndClaimsNormallyAfter()
    {
        var orchestrator = GatedOrchestrator();
        var (pump, jobs) = NewPump(orchestrator);

        await pump.StartAsync(CancellationToken.None);
        try
        {
            await Task.Delay(500);   // five poll intervals with rows waiting
            Assert.Equal(0, jobs.QueuedCount);

            orchestrator.MarkRecoveryDone();

            Assert.True(await WaitUntil(() => jobs.QueuedCount > 0), "the pump did not claim once recovery was done");
        }
        finally
        {
            await Task.WhenAny(pump.StopAsync(CancellationToken.None), Task.Delay(3000));
        }
    }

    private static SchedulerService NewScheduler(OrchestratorService orchestrator, bool poolReady)
    {
        var settings = new CraftSettings();
        var config = new ConfigurationBuilder().AddInMemoryCollection([]).Build();
        var repo = new ScriptRepository(NullLogger<ScriptRepository>.Instance, settings);
        var pool = new PowerShellWorkerPool(repo, NullLogger<PowerShellWorkerPool>.Instance, config, settings);
        if (poolReady) pool.Initialize(enableHttp: false, enableBg: false);   // signals ready, builds nothing
        var limiter = new BackgroundTaskLimiter(NullLogger<BackgroundTaskLimiter>.Instance, config, settings, pool);
        var runner = new PowerShellRunnerService(NullLogger<PowerShellRunnerService>.Instance, pool, repo, settings);
        var health = new StorageHealthMonitor(new RunRemainingCounterTests.ConditionalStore(), NullLogger<StorageHealthMonitor>.Instance);
        return new SchedulerService(NullLogger<SchedulerService>.Instance, runner, limiter, orchestrator,
            new JobManager(NullLogger<JobManager>.Instance, settings, limiter), settings, pool, health,
            NativeScheduledTasks.Empty, null!);
    }

    [Fact]
    public async Task ARecoveryThatThrows_StillOpensTheGate()
    {
        // The gated orchestrator has no store, so ResumeInterruptedRunsAsync throws on its first line.
        var orchestrator = GatedOrchestrator();
        var scheduler = NewScheduler(orchestrator, poolReady: true);
        using var cts = new CancellationTokenSource();

        await scheduler.StartAsync(cts.Token);
        try
        {
            Assert.True(await Task.WhenAny(orchestrator.RecoveryDone, Task.Delay(10_000)) == orchestrator.RecoveryDone,
                "a failed recovery left the claim gate shut — the pump would never claim");
        }
        finally
        {
            cts.Cancel();
            try { await scheduler.StopAsync(CancellationToken.None); } catch { /* the gutted orchestrator may fault the loop */ }
        }
    }

    [Fact]
    public async Task ShutdownBeforeTheWorkerPoolIsReady_StillOpensTheGate()
    {
        var orchestrator = GatedOrchestrator();
        var scheduler = NewScheduler(orchestrator, poolReady: false);
        using var cts = new CancellationTokenSource();

        await scheduler.StartAsync(cts.Token);
        cts.Cancel();
        try { await scheduler.StopAsync(CancellationToken.None); } catch { /* cancellation */ }

        Assert.True(await Task.WhenAny(orchestrator.RecoveryDone, Task.Delay(5_000)) == orchestrator.RecoveryDone);
    }
}

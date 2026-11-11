using Craft.Configuration;
using Craft.Hosting;
using Craft.Orchestration;
using Craft.PowerShellHost;
using Craft.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace Craft.Tests;

public class RetentionCountsTests
{
    [Fact]
    public async Task EveryHolderIsReported_WithItsCurrentCount()
    {
        await using var h = await OrchestrationHarness.CreateAsync();
        var settings = new CraftSettings();
        var config = new ConfigurationBuilder().AddInMemoryCollection([]).Build();
        var pool = new PowerShellWorkerPool(new ScriptRepository(NullLogger<ScriptRepository>.Instance, settings),
            NullLogger<PowerShellWorkerPool>.Instance, config, settings);
        var limiter = new BackgroundTaskLimiter(NullLogger<BackgroundTaskLimiter>.Instance, config, settings, pool);
        using var diagnostics = new HttpDiagnosticListener(NullLogger.Instance);
        WorkerMetricsBridge.Initialize(pool, limiter, h.Jobs, orchestrator: h.Svc, pump: h.Pump, httpDiagnostics: diagnostics);

        var release = new TaskCompletionSource();
        for (var i = 0; i < 3; i++) h.Jobs.Enqueue($"held{i}", 9, _ => release.Task);

        var counts = WorkerMetricsBridge.GetRetentionCounts();
        release.SetResult();

        Assert.Equal(3, counts["JobManager.jobs"]);
        Assert.Equal(3, counts["JobManager.jobs.Queued"] + counts.GetValueOrDefault("JobManager.jobs.Running"));
        foreach (var name in new[]
        {
            "JobManager.pendingWork", "JobManager.cancelledJobIds", "JobManager.lastStep",
            "Orchestrator.activePlanners", "Orchestrator.lastStatusLog", "FinishBatcher.pending",
            "WorkStore.cancelling", "WorkStore.rateBuckets", "WorkPump.skip", "WorkPump.inFlight", "WorkPump.changed",
            "HttpDiag.inflight", "HttpDiag.dnsInFlight", "HttpDiag.tlsInFlight", "HttpDiag.connectInFlight",
            "QueueStatus.labels", "Log.queueDepth", "Pool.workersBuilt",
        })
            Assert.True(counts.ContainsKey(name), $"{name} is not reported");
        Assert.True(counts["Process.dynamicAssemblies"] > 0);
    }
}

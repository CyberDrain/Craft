using System.Text.Json;
using Craft.Configuration;
using Craft.Hosting;
using Craft.Orchestration;
using Craft.PowerShellHost;
using Craft.Services;
using Craft.Storage;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace Craft.Tests;

/// <summary>
/// queue-telemetry.json is read by external monitoring straight off the file system, so it must sit beside the
/// egress ledger and carry the run counts from storage, not just what this process has claimed.
/// </summary>
public class QueueTelemetryFileTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "craft-queue-telemetry-" + Guid.NewGuid().ToString("N")[..8]);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* temp dir */ }
        GC.SuppressFinalize(this);
    }

    [Fact]
    public async Task Write_PutsMetricsAndDurableRunCountsBesideTheLedger()
    {
        var settings = new CraftSettings();
        settings.FileLogging.Directory = _dir;
        var config = new ConfigurationBuilder().AddInMemoryCollection([]).Build();
        var repo = new ScriptRepository(NullLogger<ScriptRepository>.Instance, settings);
        var pool = new PowerShellWorkerPool(repo, NullLogger<PowerShellWorkerPool>.Instance, config, settings);
        var limiter = new BackgroundTaskLimiter(NullLogger<BackgroundTaskLimiter>.Instance, config, settings, pool);
        var jobs = new JobManager(NullLogger<JobManager>.Instance, settings, limiter);
        var store = new WorkStore(NullLogger<WorkStore>.Instance, settings, new MemoryTableStore());
        var started = new DateTime(2026, 10, 22, 3, 0, 0, DateTimeKind.Utc);
        await store.CreateRunAsync(new RunHeader
        {
            RunKey = WorkStore.RunKeyFor("Standards-AllTenants", started),
            Name = "Standards-AllTenants",
            StartedUtc = started,
            TaskScriptName = "Invoke-CraftTask",
        }, Enumerable.Range(0, 3).Select(i => new WorkStore.NewTask($"t{i}", [])).ToList());
        var service = new StatsHistoryService(NullLogger<StatsHistoryService>.Instance, settings,
            new JobQueueStatusReader(NullLogger<JobQueueStatusReader>.Instance, jobs, store));

        await service.WriteQueueTelemetryAsync(new WorkerMetricsSnapshot { TimestampUtc = started, Jobs = new JobMetrics { QueuedDurable = 3 } }, CancellationToken.None);

        var path = Path.Combine(_dir, "queue-telemetry.json");
        Assert.False(File.Exists(path + ".tmp"));
        var root = JsonDocument.Parse(File.ReadAllText(path)).RootElement;
        Assert.Equal(1, root.GetProperty("version").GetInt32());
        Assert.Equal(3, root.GetProperty("metrics").GetProperty("jobs").GetProperty("queuedDurable").GetInt32());
        var run = Assert.Single(root.GetProperty("runs").EnumerateArray());
        Assert.Equal("Standards-AllTenants", run.GetProperty("name").GetString());
        Assert.Equal(3, run.GetProperty("total").GetInt32());
        Assert.Equal(3, run.GetProperty("queued").GetInt32());
    }
}

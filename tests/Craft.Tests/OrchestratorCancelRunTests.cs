using System.Collections.Concurrent;
using System.Reflection;
using Craft.Configuration;
using Craft.Orchestration;
using Craft.Storage;
using Microsoft.Extensions.Logging.Abstractions;

namespace Craft.Tests;

/// <summary>
/// Cancelling a run this node holds must cancel the live tasks, not a copy loaded from storage: the live
/// ones are what the re-drive and completion check read, so a cancelled copy left them Pending and the
/// re-drive kept putting them back on the queue.
/// </summary>
public class OrchestratorCancelRunTests
{
    [Fact]
    public async Task CancellingALiveRun_CancelsItsLiveTasks_AndDropsItsQueueRows()
    {
        var settings = new CraftSettings { Orchestrator = { TablePrefix = "cnl" + Guid.NewGuid().ToString("N")[..8] } };
        settings.Orchestrator.BatchStatusWrites = false;
        var backing = new RunRemainingCounterTests.ConditionalStore();
        var store = new OrchestratorTableStore(NullLogger<OrchestratorTableStore>.Instance, settings, backing);
        var queue = new JobQueueStore(NullLogger<JobQueueStore>.Instance, settings, backing);
        await store.InitializeAsync();
        await queue.InitializeAsync();
        var svc = NewService(settings, store, queue);

        var run = new OrchestratorRun
        {
            Name = "Mailbox_t1",
            Status = "Running",
            Priority = 4,
            StartedUtc = DateTime.UtcNow,
            Tasks = [Pending("a"), Pending("b"), Pending("c")]
        };
        await store.UpsertRunAsync(run);
        await store.UpsertTaskBatchAsync(run.Name, run.Tasks);
        await store.InitRemainingAsync(run.Name, run.Tasks.Count);
        await queue.EnqueueBatchAsync(run.Name, run.Tasks.Select(t => (t.Id, 4)).ToList(), run.StartedUtc);
        run.Tasks[0].Status = "Running";
        await store.UpsertTaskAsync(run.Name, run.Tasks[0]);
        Get<ConcurrentDictionary<string, OrchestratorRun>>(svc, "_activeRuns")[run.Name] = run;

        var (found, cancelled) = await svc.CancelRunAsync(run.Name);

        Assert.True(found);
        Assert.Equal(2, cancelled);
        Assert.Equal(["Running", "Cancelled", "Cancelled"], run.Tasks.Select(t => t.Status));
        Assert.Empty(await queue.GetQueuedTaskIdsAsync(run.Name));
    }

    private static OrchestratorTaskItem Pending(string id) => new() { Id = id, Status = "Pending" };

    private static OrchestratorService NewService(CraftSettings settings, OrchestratorTableStore store, JobQueueStore queue)
    {
        var svc = (OrchestratorService)System.Runtime.CompilerServices.RuntimeHelpers
            .GetUninitializedObject(typeof(OrchestratorService));
        Set(svc, "_logger", NullLogger<OrchestratorService>.Instance);
        Set(svc, "_store", store);
        Set(svc, "_queue", queue);
        Set(svc, "_writer", new OrchestratorStatusWriter(store, NullLogger<OrchestratorStatusWriter>.Instance, settings));
        Set(svc, "_settings", settings);
        Set(svc, "_lock", new object());
        Set(svc, "_activeRuns", new ConcurrentDictionary<string, OrchestratorRun>());
        Set(svc, "_cancelledRuns", new ConcurrentDictionary<string, bool>());
        Set(svc, "_finalizingRuns", new ConcurrentDictionary<string, bool>());
        return svc;
    }

    private static void Set(object target, string field, object? value) =>
        typeof(OrchestratorService).GetField(field, BindingFlags.NonPublic | BindingFlags.Instance)!
            .SetValue(target, value);

    private static T Get<T>(object target, string field) =>
        (T)typeof(OrchestratorService).GetField(field, BindingFlags.NonPublic | BindingFlags.Instance)!
            .GetValue(target)!;
}

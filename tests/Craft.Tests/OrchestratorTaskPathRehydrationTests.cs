using System.Collections.Concurrent;
using System.Reflection;
using Craft.Configuration;
using Craft.Orchestration;
using Craft.PowerShellHost;
using Craft.Services;
using Craft.Storage;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace Craft.Tests;

/// <summary>
/// The resolver must rebuild a task's script from the run record when the in-memory path cache misses,
/// instead of dropping the task.
///
/// The cache (_taskScriptPaths) is written only by DispatchPendingTasksAsync. The JobQueuePump, a
/// BackgroundService, starts claiming persisted queue rows at host start — before the scheduler has
/// even waited for the worker pool, let alone run ResumeInterruptedRunsAsync, which is what dispatches
/// (and so caches the path for) a resumed run. In that window every claimed row resolved to a cache
/// miss and the task was dropped: the resolver returned null, the JobManager marked the job Skipped,
/// and the pump deleted the queue row — permanently, for a task still Pending in a run the pump would
/// never see re-queued.
///
/// The run record persists TaskScriptName, and the ScriptRepository is fully loaded before the pump's
/// first claim, so the path is rebuildable from storage exactly as the resume path rebuilds it. These
/// tests pin that: a cache miss on a live run rehydrates and caches the path; only a run with no
/// resolvable script at all is still dropped.
/// </summary>
public class OrchestratorTaskPathRehydrationTests
{
    private static (OrchestratorService Svc, OrchestratorTableStore Store, ConcurrentDictionary<string, string> Paths)
        NewService(params string[] knownModuleFunctions)
    {
        var settings = new CraftSettings
        {
            Orchestrator = { TablePrefix = "rehyd" + Guid.NewGuid().ToString("N")[..6] }
        };
        var config = new ConfigurationBuilder().AddInMemoryCollection([]).Build();

        var repo = new ScriptRepository(NullLogger<ScriptRepository>.Instance, settings);
        // IsModuleFunction short-circuits on a non-null _moduleFunctionNames, so FindScript resolves
        // these names without a ScriptBlock or a module on disk.
        typeof(ScriptRepository).GetField("_moduleFunctionNames", BindingFlags.NonPublic | BindingFlags.Instance)!
            .SetValue(repo, new HashSet<string>(knownModuleFunctions, StringComparer.OrdinalIgnoreCase));

        var pool = new PowerShellWorkerPool(repo, NullLogger<PowerShellWorkerPool>.Instance, config, settings);
        var runner = new PowerShellRunnerService(NullLogger<PowerShellRunnerService>.Instance, pool, repo, settings);
        var store = new OrchestratorTableStore(NullLogger<OrchestratorTableStore>.Instance, settings,
            new RunRemainingCounterTests.ConditionalStore());
        var paths = new ConcurrentDictionary<string, string>();

        var svc = (OrchestratorService)System.Runtime.CompilerServices.RuntimeHelpers
            .GetUninitializedObject(typeof(OrchestratorService));
        Set(svc, "_logger", NullLogger<OrchestratorService>.Instance);
        Set(svc, "_store", store);
        Set(svc, "_psRunner", runner);
        Set(svc, "_activeRuns", new ConcurrentDictionary<string, OrchestratorRun>());
        Set(svc, "_taskScriptPaths", paths);      // deliberately EMPTY — the pump-before-resume window
        Set(svc, "_lock", new object());
        return (svc, store, paths);
    }

    private static void Set(object target, string field, object value) =>
        typeof(OrchestratorService).GetField(field, BindingFlags.NonPublic | BindingFlags.Instance)!
            .SetValue(target, value);

    private static async Task<object?> ResolveAsync(OrchestratorService svc, string runName, string taskId)
    {
        var mi = typeof(OrchestratorService).GetMethod("ResolveTaskWorkAsync",
            BindingFlags.NonPublic | BindingFlags.Instance)!;
        try
        {
            var task = (Task)mi.Invoke(svc, [new JobDescriptor(runName, taskId, 4), CancellationToken.None])!;
            await task;
            return task.GetType().GetProperty("Result")!.GetValue(task);
        }
        catch (TargetInvocationException ex)
        {
            throw ex.InnerException ?? ex;
        }
    }

    private static async Task SeedRunAsync(OrchestratorTableStore store, string name, string taskScriptName)
    {
        await store.InitializeAsync();
        await store.UpsertRunAsync(new OrchestratorRun
        {
            Name = name,
            Status = "Running",
            Priority = 4,
            StartedUtc = DateTime.UtcNow,
            TaskScriptName = taskScriptName,
            Tasks = [new OrchestratorTaskItem { Id = "task-0", Status = "Pending" }]
        });
        await store.UpsertTaskAsync(name, new OrchestratorTaskItem { Id = "task-0", Status = "Pending" });
    }

    [Fact]
    public async Task CacheMissOnALiveRun_RehydratesThePathFromTaskScriptName_AndCachesIt()
    {
        var (svc, store, paths) = NewService("Invoke-CraftTask");
        await SeedRunAsync(store, "AuditLogProcessV2-contoso.com", "Invoke-CraftTask");

        var work = await ResolveAsync(svc, "AuditLogProcessV2-contoso.com", "task-0");

        Assert.NotNull(work);   // previously: dropped, because _taskScriptPaths had no entry yet
        Assert.Equal("Invoke-CraftTask", paths["AuditLogProcessV2-contoso.com"]);
    }

    [Fact]
    public async Task ARunWithNoResolvableTaskScript_IsStillDropped()
    {
        // Empty TaskScriptName, and the naming-convention fallback (Invoke-<run>Task) resolves to nothing
        // because the repo knows no such function. This is the only case that should still drop.
        var (svc, store, _) = NewService(/* no known functions */);
        await SeedRunAsync(store, "mysteryrun", taskScriptName: "");

        var work = await ResolveAsync(svc, "mysteryrun", "task-0");

        Assert.Null(work);
    }
}

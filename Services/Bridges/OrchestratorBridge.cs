using System.Collections.Concurrent;
using Craft.Hosting;
using Craft.Orchestration;

// NAMESPACE PINNED — do not change.
// Downstream PowerShell reaches these types by fully-qualified name, e.g.
//   [Craft.Services.RealtimeBridge]::Publish($userId, $jobId, 'start', $data)
// Renaming the namespace compiles fine and then fails at runtime in the hosted app
// ("Unable to find type"). Type forwarding cannot help — it only works across assemblies.
// The folder is free to move; the namespace is a published contract.
namespace Craft.Services;

/// <summary>
/// Thread-safe bridge allowing PowerShell (Start-CIPPOrchestrator) to queue
/// orchestrator runs that get picked up by the C# OrchestratorService.
/// PS enqueues via QueueOrchestration(); C# drains via DrainPending().
/// </summary>
public static class OrchestratorBridge
{
    private static OrchestratorService? s_service;
    private static readonly ConcurrentQueue<PendingOrchestration> s_pending = new();

    public static void Initialize(OrchestratorService service) => s_service = service;

    public static void QueueOrchestration(string name, string batchJson, int priority,
        string? postExecFunctionName = null, string? postExecParametersJson = null,
        string? reference = null)
    {
        var parentRunName = OperationContext.Current?.RunName;
        s_pending.Enqueue(new PendingOrchestration(name, batchJson, priority,
            postExecFunctionName, postExecParametersJson, parentRunName, reference));
    }

    /// <summary>
    /// Synchronous drain — blocks until all pending orchestrations are started.
    /// Safe to call from any context (no SynchronizationContext on background workers).
    /// </summary>
    public static void DrainPending()
    {
        while (s_pending.TryDequeue(out var p))
        {
            try
            {
                if (s_service != null)
                {
                    s_service.StartFromBatchAsync(p.Name, p.BatchJson, p.Priority,
                        p.PostExecFunctionName, p.PostExecParametersJson, CancellationToken.None,
                        p.ParentRunName, p.Reference)
                        .GetAwaiter().GetResult();

                    // Register as child run if parent is still active
                    if (!string.IsNullOrEmpty(p.ParentRunName))
                        s_service.TryRegisterChildRun(p.ParentRunName, p.Name);
                }
            }
            catch (Exception ex)
            {
                s_service?._logger.LogError(ex, "[Orchestrator] DrainPending failed for {Name}", p.Name);
            }
        }
        DrainPendingPlanners();
    }

    /// <summary>
    /// Async drain — preferred from async call sites (PostExec lambdas, ExecuteScript).
    /// </summary>
    public static async Task DrainPendingAsync()
    {
        while (s_pending.TryDequeue(out var p))
        {
            try
            {
                if (s_service != null)
                {
                    await s_service.StartFromBatchAsync(p.Name, p.BatchJson, p.Priority,
                        p.PostExecFunctionName, p.PostExecParametersJson, CancellationToken.None,
                        p.ParentRunName, p.Reference);

                    // Register as child run if parent is still active
                    if (!string.IsNullOrEmpty(p.ParentRunName))
                        s_service.TryRegisterChildRun(p.ParentRunName, p.Name);
                }
            }
            catch (Exception ex)
            {
                s_service?._logger.LogError(ex, "[Orchestrator] DrainPending failed for {Name}", p.Name);
            }
        }
        await DrainPendingPlannersAsync();
    }

    public record PendingOrchestration(string Name, string BatchJson, int Priority,
        string? PostExecFunctionName, string? PostExecParametersJson, string? ParentRunName,
        string? Reference = null);

    private static readonly ConcurrentQueue<PendingPlannerRun> s_pendingPlanners = new();

    /// <summary>
    /// Queue a planner-based orchestrator run from PowerShell. The C# orchestrator
    /// runs the planner script on a background worker to build the task list, then
    /// dispatches tasks — same as the scheduler. Returns immediately.
    /// </summary>
    public static void QueuePlannerRun(string command, int priority)
    {
        s_pendingPlanners.Enqueue(new PendingPlannerRun(command, priority));
    }

    /// <summary>Drain queued planner runs. Called alongside DrainPending.</summary>
    internal static void DrainPendingPlanners()
    {
        while (s_pendingPlanners.TryDequeue(out var p))
        {
            if (s_service == null) continue;
            // Fire-and-forget: planner runs on BG worker, dispatches tasks
            _ = s_service.StartPlannerRunAsync(p.Command, p.Priority, CancellationToken.None);
        }
    }

    internal static Task DrainPendingPlannersAsync()
    {
        while (s_pendingPlanners.TryDequeue(out var p))
        {
            if (s_service == null) continue;
            _ = s_service.StartPlannerRunAsync(p.Command, p.Priority, CancellationToken.None);
        }
        return Task.CompletedTask;
    }

    public record PendingPlannerRun(string Command, int Priority);
}

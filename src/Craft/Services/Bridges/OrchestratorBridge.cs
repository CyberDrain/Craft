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
/// <remarks>
/// Uninitialized policy: mutating enqueue APIs throw; drain calls soft no-op
/// (empty work when the host has not wired the bridge yet).
/// </remarks>
public static class OrchestratorBridge
{
    private static OrchestratorService? s_service;

    public static void Initialize(OrchestratorService service) => s_service = service;

    public static void QueueOrchestration(string name, string batchJson, int priority,
        string? postExecFunctionName = null, string? postExecParametersJson = null,
        string? reference = null)
    {
        var service = s_service ?? throw new InvalidOperationException("OrchestratorBridge not initialized");
        var parentRunName = OperationContext.Current?.RunName;
        service.QueueOrchestration(name, batchJson, priority,
            postExecFunctionName, postExecParametersJson, parentRunName, reference);
    }

    /// <summary>
    /// Synchronous drain — blocks until all pending orchestrations are started.
    /// Safe to call from any context (no SynchronizationContext on background workers).
    /// </summary>
    public static void DrainPending() => s_service?.DrainPending();

    /// <summary>
    /// Async drain — preferred from async call sites (PostExec lambdas, ExecuteScript).
    /// </summary>
    public static Task DrainPendingAsync() =>
        s_service?.DrainPendingAsync() ?? Task.CompletedTask;

    public static void QueuePlannerRun(string command, int priority)
    {
        var service = s_service ?? throw new InvalidOperationException("OrchestratorBridge not initialized");
        service.QueuePlannerRun(command, priority);
    }
}

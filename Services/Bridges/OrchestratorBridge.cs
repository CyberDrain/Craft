using System.Collections.Concurrent;
using Craft.Hosting;
using Craft.Orchestration;
using Craft.Storage;

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
        string? reference = null, string? parentRunName = null)
    {
        // Sanitized here as well as at run creation so the child-run registration below
        // records the SAME name the service ends up creating — a raw name with a table-illegal
        // character would register a child link no live run ever matches.
        name = TableKeys.Sanitize(name);
        parentRunName = ResolveParentRunName(name, parentRunName);
        var gated = RegisterPendingChild(parentRunName, name);
        s_pending.Enqueue(new PendingOrchestration(name, batchJson, priority,
            postExecFunctionName, postExecParametersJson, parentRunName, reference,
            PendingChildRegistered: gated));
    }

    /// <summary>
    /// Queue a run whose batch is already on disk as JSON Lines — one task object per line.
    ///
    /// Prefer this to <see cref="QueueOrchestration"/> for any fan-out whose size depends on tenant
    /// data. That overload takes the batch as a single string, so the caller has to build the entire
    /// task array in memory (ConvertTo-Json) and it is then held again as a JsonDocument while it is
    /// parsed. Writing the file a task at a time and reading it a line at a time means neither side
    /// ever holds more than one task, whether the run has ten tasks or ten thousand.
    ///
    /// The file is owned by the orchestrator from this point: it is deleted once parsed.
    /// </summary>
    public static void QueueOrchestrationFromFile(string name, string batchFilePath, int priority,
        string? postExecFunctionName = null, string? postExecParametersJson = null,
        string? reference = null, string? parentRunName = null)
    {
        name = TableKeys.Sanitize(name);
        parentRunName = ResolveParentRunName(name, parentRunName);
        var gated = RegisterPendingChild(parentRunName, name);
        s_pending.Enqueue(new PendingOrchestration(name, string.Empty, priority,
            postExecFunctionName, postExecParametersJson, parentRunName, reference, batchFilePath,
            PendingChildRegistered: gated));
    }

    /// <summary>
    /// Resolve the parent run of a queued orchestration. The explicit argument wins — PowerShell
    /// callers MUST pass it (read from the stamped $global:CraftOperationContext), because the
    /// ambient fallback cannot work for them: the pipeline runs on the runspace's reused thread,
    /// whose frozen ExecutionContext never sees the per-invocation AsyncLocal (see
    /// PowerShellWorker.StampOperationContext), so reading OperationContext.Current here yields
    /// null and every PS-queued child run used to lose its lineage. The ambient read stays as the
    /// fallback for .NET callers and for older wrapper scripts, where it degrades to null rather
    /// than misattributing lineage. PowerShell marshals a $null argument to "" for string
    /// parameters, so empty means "not passed".
    /// </summary>
    private static string? ResolveParentRunName(string name, string? parentRunName)
    {
        if (string.IsNullOrEmpty(parentRunName))
            parentRunName = OperationContext.Current?.RunName;
        if (string.IsNullOrEmpty(parentRunName))
            return null;
        // Sanitized like the child name: the parent was created under its sanitized name, and the
        // registration below has to match that live run.
        parentRunName = TableKeys.Sanitize(parentRunName);
        // A run re-queued from inside its own context arrives with itself as parent (the
        // recurring-run pattern). Never a real child — linking it would gate its finalize on
        // itself; StartFromBatchAsync applies the same guard before persisting.
        return parentRunName == name ? null : parentRunName;
    }

    /// <summary>
    /// Register the child link at ENQUEUE time — while the parent task's script is still executing,
    /// so the parent cannot pass its completion check before the gate exists. Registering after
    /// StartFromBatchAsync (the old shape) loses that race for the parent's LAST task: the drain
    /// runs in a background Task.Run while the enqueuing task is marked terminal immediately, so
    /// the parent would finalize — and dispatch PostExecution — before its child was visible.
    /// Returns whether a gate was taken, so the drain releases exactly what was registered.
    /// </summary>
    private static bool RegisterPendingChild(string? parentRunName, string childName) =>
        !string.IsNullOrEmpty(parentRunName) &&
        s_service?.TryRegisterPendingChildRun(parentRunName, childName) == true;

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
                if (s_service == null) { DiscardUndispatchable(p); continue; }
                s_service.StartFromBatchAsync(p.Name, p.BatchJson, p.Priority,
                    p.PostExecFunctionName, p.PostExecParametersJson, CancellationToken.None,
                    p.ParentRunName, p.Reference, p.BatchFilePath)
                    .GetAwaiter().GetResult();
            }
            catch (Exception ex)
            {
                s_service?._logger.LogError(ex, "[Orchestrator] DrainPending failed for {Name}", p.Name);
            }
            finally
            {
                // The enqueue-time gate lifts on EVERY path once the start attempt is over: a
                // started child is in _activeRuns by now (which takes over blocking the parent),
                // and one that failed to start must stop blocking — a leaked gate would defer the
                // parent's finalize forever, re-checked every 60s for the process lifetime.
                if (p.PendingChildRegistered)
                    s_service?.ReleasePendingChildRun(p.Name);
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
                if (s_service == null) { DiscardUndispatchable(p); continue; }
                await s_service.StartFromBatchAsync(p.Name, p.BatchJson, p.Priority,
                    p.PostExecFunctionName, p.PostExecParametersJson, CancellationToken.None,
                    p.ParentRunName, p.Reference, p.BatchFilePath);
            }
            catch (Exception ex)
            {
                s_service?._logger.LogError(ex, "[Orchestrator] DrainPending failed for {Name}", p.Name);
            }
            finally
            {
                // See DrainPending: the gate lifts whatever the outcome of the start attempt.
                if (p.PendingChildRegistered)
                    s_service?.ReleasePendingChildRun(p.Name);
            }
        }
        await DrainPendingPlannersAsync();
    }

    /// <summary>
    /// Drop a queued run that cannot be dispatched because the orchestrator is not registered yet.
    ///
    /// It has already been dequeued at this point, so the run is lost either way — it was only ever
    /// held in this in-memory queue, and nothing has been written to storage for it. What must not be
    /// lost silently is the batch file: normally StartFromBatchAsync owns and deletes it, and if that
    /// is never reached it would sit in the container's temp directory for the life of the process.
    /// </summary>
    private static void DiscardUndispatchable(PendingOrchestration p)
    {
        if (!string.IsNullOrEmpty(p.BatchFilePath))
        {
            try { if (File.Exists(p.BatchFilePath)) File.Delete(p.BatchFilePath); }
            catch { /* best effort — the run is already lost; do not mask that with a delete failure */ }
        }
    }

    /// <summary>
    /// A queued run. Exactly one of <paramref name="BatchJson"/> and <paramref name="BatchFilePath"/>
    /// carries the batch; the file path wins when both are set.
    /// <paramref name="PendingChildRegistered"/> records whether enqueue took a pending-child gate on
    /// the orchestrator, so the drain releases exactly the gates that were taken — releasing on a
    /// refused registration could lift a gate held by ANOTHER queued entry of the same child name.
    /// </summary>
    public record PendingOrchestration(string Name, string BatchJson, int Priority,
        string? PostExecFunctionName, string? PostExecParametersJson, string? ParentRunName,
        string? Reference = null, string? BatchFilePath = null,
        bool PendingChildRegistered = false);

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

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

    /// <param name="allowCollision">True (the default) lets runs of one name stack up; false skips this run
    /// while another run of the same name is unfinished.</param>
    /// <param name="maxConcurrency">At most this many of the run's tasks run at once; 0 (the default) is no
    /// limit. Ignored for a sequential run.</param>
    /// <param name="stopOnFailure">Sequential runs only: the first failed step cancels the rest instead of the
    /// run carrying on (the default).</param>
    public static void QueueOrchestration(string name, string batchJson, int priority,
        string? postExecFunctionName = null, string? postExecParametersJson = null,
        string? reference = null, string? parentRunName = null, bool sequential = false, bool allowCollision = true,
        int maxConcurrency = 0, bool stopOnFailure = false)
    {
        // Sanitized here as well as at run creation so the child-run registration below
        // records the SAME name the service ends up creating — a raw name with a table-illegal
        // character would register a child link no live run ever matches.
        name = TableKeys.Sanitize(name);
        parentRunName = ResolveParentRunName(name, parentRunName);
        var child = RegisterPendingChild(parentRunName, name);
        s_pending.Enqueue(new PendingOrchestration(name, batchJson, priority,
            postExecFunctionName, postExecParametersJson, parentRunName, reference,
            Sequential: sequential, ParentRunKey: child?.ParentRunKey, ChildKey: child?.ChildKey,
            AllowCollision: allowCollision, MaxConcurrency: maxConcurrency, StopOnFailure: stopOnFailure));
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
    /// <param name="allowCollision">True (the default) lets runs of one name stack up; false skips this run
    /// while another run of the same name is unfinished.</param>
    /// <param name="maxConcurrency">At most this many of the run's tasks run at once; 0 (the default) is no
    /// limit. Ignored for a sequential run.</param>
    /// <param name="stopOnFailure">Sequential runs only: the first failed step cancels the rest instead of the
    /// run carrying on (the default).</param>
    public static void QueueOrchestrationFromFile(string name, string batchFilePath, int priority,
        string? postExecFunctionName = null, string? postExecParametersJson = null,
        string? reference = null, string? parentRunName = null, bool sequential = false, bool allowCollision = true,
        int maxConcurrency = 0, bool stopOnFailure = false)
    {
        name = TableKeys.Sanitize(name);
        parentRunName = ResolveParentRunName(name, parentRunName);
        var child = RegisterPendingChild(parentRunName, name);
        s_pending.Enqueue(new PendingOrchestration(name, string.Empty, priority,
            postExecFunctionName, postExecParametersJson, parentRunName, reference, batchFilePath,
            Sequential: sequential, ParentRunKey: child?.ParentRunKey, ChildKey: child?.ChildKey,
            AllowCollision: allowCollision, MaxConcurrency: maxConcurrency, StopOnFailure: stopOnFailure));
    }

    /// <summary>
    /// Resolve the parent run of a queued orchestration: a run key (exact — runs of one name can overlap) or a
    /// run name (the newest outing). The explicit argument wins — PowerShell
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
            parentRunName = OperationContext.Current?.RunKey ?? OperationContext.Current?.RunName;
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
    /// Make the parent wait for this child, at ENQUEUE time — while the parent's task is still executing, so
    /// the parent cannot reach its barrier first. The returned keys travel with the queued run: the child
    /// fills the placeholder when it finishes, and a child that is never created releases it on the drain.
    /// </summary>
    private static (string ParentRunKey, string ChildKey)? RegisterPendingChild(string? parentRunName, string childName) =>
        string.IsNullOrEmpty(parentRunName) ? null : s_service?.RegisterPendingChild(parentRunName, childName);

    /// <summary>
    /// Whether a run of this name is unfinished or already queued here to start. Lets a caller that does not
    /// want overlapping runs (<c>allowCollision: false</c>) skip, and say so, before building the batch. The
    /// start itself checks again, so a run that appears in between is still skipped.
    /// PS usage: <c>[Craft.Services.OrchestratorBridge]::IsRunActive($name)</c>.
    /// </summary>
    public static bool IsRunActive(string name)
    {
        name = TableKeys.Sanitize(name);
        if (s_pending.Any(p => p.Name == name)) return true;
        return s_service != null && Task.Run(() => s_service.IsRunActiveAsync(name)).GetAwaiter().GetResult();
    }

    private static readonly System.Text.Json.JsonSerializerOptions s_inspectJson = new() { WriteIndented = true };

    /// <summary>
    /// Why a run is or is not moving, as JSON: counts and mode, whether the scheduler can see it, its claims
    /// and who holds them, child runs it waits for, its aggregation, the instance lock, and a diagnosis.
    /// Takes a run key, or a run name (every unfinished run of it, else the latest).
    /// PS usage: <c>[Craft.Services.OrchestratorBridge]::InspectRun('MailboxRules_contoso.com')</c>.
    /// </summary>
    public static string InspectRun(string nameOrKey) => s_service == null
        ? "{\"error\":\"orchestrator not initialised\"}"
        : System.Text.Json.JsonSerializer.Serialize(Task.Run(() => s_service.InspectRunAsync(nameOrKey)).GetAwaiter().GetResult(), s_inspectJson);

    /// <summary>
    /// Rebuild the Ready and Finished indexes from the active-run list now, as the pump does at startup: relists
    /// unfinished runs, retires finished ones, removes runs whose creation never finished. Returns a JSON summary.
    /// PS usage: <c>[Craft.Services.OrchestratorBridge]::RepairIndexes()</c>.
    /// </summary>
    public static string RepairIndexes() => s_service == null
        ? "{\"error\":\"orchestrator not initialised\"}"
        : System.Text.Json.JsonSerializer.Serialize(Task.Run(() => s_service.RepairIndexesAsync()).GetAwaiter().GetResult(), s_inspectJson);

    /// <summary>Synchronous drain — blocks until all pending orchestrations are started.</summary>
    public static void DrainPending()
    {
        while (s_pending.TryDequeue(out var p))
            Task.Run(() => StartAsync(p)).GetAwaiter().GetResult();
        DrainPendingPlanners();
    }

    /// <summary>Async drain — preferred from async call sites (PostExec, ExecuteScript).</summary>
    public static async Task DrainPendingAsync()
    {
        while (s_pending.TryDequeue(out var p))
            await StartAsync(p);
        await DrainPendingPlannersAsync();
    }

    private static async Task StartAsync(PendingOrchestration p)
    {
        var created = false;
        try
        {
            if (s_service == null) { DiscardUndispatchable(p); return; }
            created = await s_service.StartFromBatchAsync(p.Name, p.BatchJson, p.Priority,
                p.PostExecFunctionName, p.PostExecParametersJson, CancellationToken.None,
                p.ParentRunName, p.Reference, p.BatchFilePath, p.Sequential, p.ParentRunKey, p.ChildKey, p.AllowCollision,
                p.MaxConcurrency, p.StopOnFailure);
        }
        catch (Exception ex)
        {
            s_service?._logger.LogError(ex, "[Orchestrator] DrainPending failed for {Name}", p.Name);
        }
        finally
        {
            if (!created && p.ParentRunKey != null && p.ChildKey != null && s_service != null)
            {
                try { await s_service.AbandonPendingChildAsync(p.ParentRunKey, p.ChildKey); }
                catch (Exception ex) { s_service._logger.LogWarning(ex, "[Orchestrator] Could not release {Name} from its parent", p.Name); }
            }
        }
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
    /// carries the batch; the file path wins when both are set. <paramref name="ParentRunKey"/> and
    /// <paramref name="ChildKey"/> are set when the parent was made to wait for this run.
    /// </summary>
    public record PendingOrchestration(string Name, string BatchJson, int Priority,
        string? PostExecFunctionName, string? PostExecParametersJson, string? ParentRunName,
        string? Reference = null, string? BatchFilePath = null, bool Sequential = false,
        string? ParentRunKey = null, string? ChildKey = null, bool AllowCollision = true, int MaxConcurrency = 0,
        bool StopOnFailure = false)
    {
        public bool PendingChildRegistered => ChildKey != null;
    }

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

using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Serialization;

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

/// <summary>
/// Thread-safe bridge allowing PowerShell (Add-CippQueueMessage) to queue
/// background commands that get dispatched on a background worker.
/// Replaces Azure Storage Queue on CIPPNG — purely in-process.
/// </summary>
public static class QueueBridge
{
    private static PowerShellRunnerService? s_runner;
    private static JobManager? s_jobManager;
    private static string? s_queueTaskFunction;
    private static readonly ConcurrentQueue<PendingQueueCommand> s_pending = new();

    public static void Initialize(PowerShellRunnerService runner, JobManager jobManager, string queueTaskFunction)
    {
        s_runner = runner;
        s_jobManager = jobManager;
        s_queueTaskFunction = queueTaskFunction;
    }

    public static void Enqueue(string cmdlet, string parametersJson)
    {
        s_pending.Enqueue(new PendingQueueCommand(cmdlet, parametersJson));
    }

    public static void DrainPending()
    {
        if (string.IsNullOrEmpty(s_queueTaskFunction)) return;

        while (s_pending.TryDequeue(out var cmd))
        {
            var scriptPath = s_runner?.FindScript(s_queueTaskFunction);
            if (scriptPath == null || s_runner == null || s_jobManager == null)
                continue;

            var captured = cmd;
            s_jobManager.Enqueue(
                name: $"Queue-{captured.Cmdlet}",
                priority: 5,
                runName: $"Queue-{captured.Cmdlet}-{Guid.NewGuid():N}",
                id: $"Queue-{Guid.NewGuid():N}",
                work: async (ct) =>
                {
                    var parameters = new Dictionary<string, object>
                    {
                        { "Cmdlet", captured.Cmdlet },
                        { "ParametersJson", captured.ParametersJson }
                    };
                    await s_runner.ExecuteScript(scriptPath, parameters);

                    // Queued commands may trigger orchestrators
                    await OrchestratorBridge.DrainPendingAsync();
                }
            );
        }
    }

    public record PendingQueueCommand(string Cmdlet, string ParametersJson);
}

// OrchestrationResults removed — results are now persisted to the
// CippOrchestratorResults Azure Table via OrchestratorTableStore.

/// <summary>
/// Lightweight replacement for Azure Durable Functions orchestration.
/// Manages fan-out/fan-in runs with crash-resilient task tracking.
///
/// Flow:
///   1. Scheduler triggers StartOrResumeRun with a planner script
///   2. Planner runs on bg pool, returns JSON array of tasks
///   3. Each task is dispatched through JobManager with priority ordering
///   4. State is persisted to Azure Table Storage after every state change
///   5. On restart, interrupted tasks resume from where they left off
///   6. After 3 interruptions (host crash/reboot), a task is marked Failed
///   7. PostExecStatus tracks PostExecution lifecycle for crash resilience
/// </summary>
public class OrchestratorService
{
    internal readonly ILogger<OrchestratorService> _logger;
    private readonly PowerShellRunnerService _psRunner;
    private readonly BackgroundTaskLimiter _limiter;
    private readonly JobManager _jobManager;
    private readonly OrchestratorTableStore _store;
    private readonly OrchestratorStatusWriter _writer;
    private readonly CraftSettings _settings;
    private readonly object _lock = new();
    private readonly ConcurrentDictionary<string, bool> _activePlanners = new();
    private readonly ConcurrentDictionary<string, OrchestratorRun> _activeRuns = new();
    private readonly ConcurrentDictionary<string, ConcurrentBag<string>> _childRuns = new();
    private readonly ConcurrentDictionary<string, Timer> _runStatusTimers = new();
    private readonly ConcurrentDictionary<string, bool> _cancelledRuns = new();

    /// <summary>
    /// Get the Reference for a given run name, or null if not found/no reference set.
    /// </summary>
    public string? GetRunReference(string runName)
    {
        return _activeRuns.TryGetValue(runName, out var run) ? run.Reference : null;
    }

    /// <summary>
    /// Find a run name by its Reference value (exact match).
    /// </summary>
    public string? FindRunByReference(string reference)
    {
        return _activeRuns.Values
            .FirstOrDefault(r => string.Equals(r.Reference, reference, StringComparison.OrdinalIgnoreCase))
            ?.Name;
    }

    private static readonly JsonSerializerOptions s_jsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public OrchestratorService(
        ILogger<OrchestratorService> logger,
        PowerShellRunnerService psRunner,
        BackgroundTaskLimiter limiter,
        JobManager jobManager,
        OrchestratorTableStore store,
        OrchestratorStatusWriter writer,
        CraftSettings settings)
    {
        _logger = logger;
        _psRunner = psRunner;
        _limiter = limiter;
        _jobManager = jobManager;
        _store = store;
        _writer = writer;
        _settings = settings;
    }

    /// <summary>
    /// Start a new run or resume an interrupted one.
    /// Called by the SchedulerService when an Orchestrator-type task fires.
    /// </summary>
    public async Task StartOrResumeRun(string name, string plannerPath, string taskPath, int priority, CancellationToken ct)
    {
        // Prevent duplicate concurrent planners for the same run
        if (!_activePlanners.TryAdd(name, true))
        {
            _logger.LogInformation("[Scheduler] Run {Name} already in progress, skipping", name);
            return;
        }

        try
        {
            await _store.InitializeAsync();
            var run = await _store.GetRunAsync(name);

            if (run != null && run.Status == "Running")
            {
                // If tasks are already dispatched for this run, skip
                if (_activeRuns.ContainsKey(name))
                {
                    _logger.LogInformation("[Scheduler] Run {Name} tasks already dispatched, skipping", name);
                    return;
                }

                // Recover interrupted tasks
                var recovered = 0;
                var tasksToUpdate = new List<OrchestratorTaskItem>();
                lock (_lock)
                {
                    foreach (var task in run.Tasks.Where(t => t.Status == "Running"))
                    {
                        task.AttemptCount++;
                        if (task.AttemptCount >= 3)
                        {
                            task.Status = "Failed";
                            task.LastError = $"Cancelled {task.AttemptCount} times by host interruption";
                            _logger.LogWarning("[Scheduler] Task {TaskId} permanently failed after {Attempts} cancellations",
                                task.Id, task.AttemptCount);
                        }
                        else
                        {
                            task.Status = "Pending";
                            _logger.LogInformation("[Scheduler] Resuming task {TaskId} attempt {Attempt}/3",
                                task.Id, task.AttemptCount + 1);
                        }
                        tasksToUpdate.Add(task);
                        recovered++;
                    }
                }

                var pendingCount = run.Tasks.Count(t => t.Status == "Pending");
                if (pendingCount > 0)
                {
                    if (recovered > 0)
                    {
                        foreach (var t in tasksToUpdate)
                            await _store.UpsertTaskAsync(run.Name, t);
                    }
                    _logger.LogInformation("[Scheduler] Resuming run {Name}: {Pending}/{Total} pending",
                        name, pendingCount, run.Tasks.Count);
                    DispatchPendingTasks(run, taskPath, run.Priority, ct);
                    return;
                }

                // All tasks finished — finalize
                await FinalizeRunAsync(run);
            }

            // Start a new run
            _logger.LogInformation("[Scheduler] Starting orchestrator: {Name}", name);

            string output;
            try
            {
                output = await _limiter.RunAsync(
                    () => _psRunner.ExecuteScriptWithOutput(plannerPath),
                    $"Planner-{name}", ct);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[Scheduler] Planner failed: {Name}", name);
                return;
            }

            var tasks = ParseTasksFromJson(output, name);
            if (tasks.Count == 0)
            {
                _logger.LogWarning("[Scheduler] Planner returned 0 tasks: {Name}. Output: {Output}", name,
                    output?.Length > 1000 ? output[..1000] + "..." : output);
                return;
            }

            run = new OrchestratorRun
            {
                Name = name,
                Status = "Running",
                Priority = priority,
                StartedUtc = DateTime.UtcNow,
                Tasks = tasks,
                TaskScriptName = Path.GetFileNameWithoutExtension(taskPath)
            };

            await _store.UpsertRunAsync(run);
            await _store.UpsertTaskBatchAsync(name, tasks);
            _logger.LogInformation("[Scheduler] Run {Name} created with {Count} tasks at P{Priority}", name, tasks.Count, priority);
            DispatchPendingTasks(run, taskPath, priority, ct);
        }
        finally
        {
            _activePlanners.TryRemove(name, out _);
        }
    }

    /// <summary>
    /// Start a planner-based orchestrator run using the standard naming convention.
    /// Resolves planner and task scripts from the command name, then delegates to StartOrResumeRun.
    /// Called by OrchestratorBridge.QueuePlannerRun (fire-and-forget from PowerShell).
    /// </summary>
    public async Task StartPlannerRunAsync(string command, int priority, CancellationToken ct)
    {
        var plannerFunc = _psRunner.FindScript(command);
        var baseName = command.StartsWith("Start-", StringComparison.OrdinalIgnoreCase)
            ? command[6..]
            : command;
        var taskScriptName = $"Invoke-{baseName}Task";
        var taskFunc = _psRunner.FindScript(taskScriptName);

        if (plannerFunc != null && taskFunc != null)
        {
            _logger.LogInformation("[Orchestrator] Planner run queued: {Command} P{Priority}", command, priority);
            await StartOrResumeRun(command, plannerFunc, taskFunc, priority, ct);
        }
        else
        {
            _logger.LogWarning("[Orchestrator] Scripts not found for planner run: {Command} planner={Planner} task={Task}",
                command, command, taskScriptName);
        }
    }

    /// <summary>
    /// Resume any runs that were interrupted by a previous process crash.
    /// Called once on application startup.
    /// </summary>
    public async Task ResumeInterruptedRunsAsync(CancellationToken ct)
    {
        await _store.InitializeAsync();

        var runNames = await _store.ListRunsAsync();
        foreach (var runName in runNames)
        {
            try
            {
                var run = await _store.GetRunAsync(runName);
                if (run == null) continue;

                // Check for runs where PostExec was pending or running when we crashed
                if (run.Status is "Completed" or "CompletedWithErrors"
                    && run.PostExecStatus is "Pending" or "Running")
                {
                    _logger.LogInformation("[Scheduler] Resuming interrupted PostExecution for run: {Name} (PostExecStatus={Status})",
                        run.Name, run.PostExecStatus);
                    DispatchPostExecution(run);
                    continue;
                }

                if (run.Status != "Running") continue;

                _logger.LogInformation("[Scheduler] Found interrupted run: {Name}", run.Name);

                // Use the stored task script name, fall back to naming convention
                var taskPath = !string.IsNullOrEmpty(run.TaskScriptName)
                    ? _psRunner.FindScript(run.TaskScriptName)
                    : FindTaskScript(run.Name);
                if (taskPath == null)
                {
                    _logger.LogWarning("[Scheduler] Cannot resume {Name}: task script not found (tried {Script})",
                        run.Name, run.TaskScriptName ?? $"Invoke-{run.Name}Task");
                    continue;
                }

                // Recover interrupted tasks
                var tasksToUpdate = new List<OrchestratorTaskItem>();
                lock (_lock)
                {
                    foreach (var task in run.Tasks.Where(t => t.Status == "Running"))
                    {
                        task.AttemptCount++;
                        if (task.AttemptCount >= 3)
                        {
                            task.Status = "Failed";
                            task.LastError = $"Cancelled {task.AttemptCount} times by host interruption";
                        }
                        else
                        {
                            task.Status = "Pending";
                        }
                        tasksToUpdate.Add(task);
                    }
                }

                // Persist recovered task state changes
                foreach (var t in tasksToUpdate)
                    await _store.UpsertTaskAsync(run.Name, t);

                var pending = run.Tasks.Count(t => t.Status == "Pending");
                if (pending > 0)
                {
                    _logger.LogInformation("[Scheduler] Resuming interrupted run {Name}: {Pending} pending", run.Name, pending);
                    DispatchPendingTasks(run, taskPath, run.Priority, ct);
                }
                else
                {
                    await FinalizeRunAsync(run);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[Scheduler] Failed to process interrupted run: {Name}", runName);
            }
        }

        // Cleanup old runs (older than 7 days)
        try
        {
            await _store.CleanupOldRunsAsync(TimeSpan.FromDays(7));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[Scheduler] Failed to cleanup old runs");
        }
    }

    /// <summary>
    /// Start an orchestrator run from a pre-built batch JSON array.
    /// Called by OrchestratorBridge.DrainPending() when PowerShell's Start-CIPPOrchestrator
    /// queues a run on CIPPNG (bypassing the planner script phase).
    /// </summary>
    public async Task StartFromBatchAsync(string name, string batchJson, int priority,
        string? postExecFunctionName, string? postExecParametersJson, CancellationToken ct,
        string? parentRunName = null, string? reference = null)
    {
        if (!_activePlanners.TryAdd(name, true))
        {
            _logger.LogInformation("[Orchestrator] Run {Name} already in progress, skipping", name);
            return;
        }

        try
        {
            await _store.InitializeAsync();

            var existing = await _store.GetRunAsync(name);
            if (existing != null && existing.Status == "Running" && _activeRuns.ContainsKey(name))
            {
                _logger.LogInformation("[Orchestrator] Run {Name} already active, skipping", name);
                return;
            }

            var tasks = ParseTasksFromJson(batchJson, name);
            if (tasks.Count == 0)
            {
                _logger.LogWarning("[Orchestrator] Batch for {Name} produced 0 tasks", name);
                return;
            }

            var genericTaskFunc = _settings.Orchestrator.GenericTaskFunction;
            if (string.IsNullOrEmpty(genericTaskFunc))
            {
                _logger.LogError("[Orchestrator] Cannot start {Name}: App:Orchestrator:GenericTaskFunction not configured", name);
                return;
            }
            var taskPath = _psRunner.FindScript(genericTaskFunc);
            if (taskPath == null)
            {
                _logger.LogError("[Orchestrator] Cannot start {Name}: {Func} not found", name, genericTaskFunc);
                return;
            }

            var run = new OrchestratorRun
            {
                Name = name,
                Reference = reference,
                Status = "Running",
                Priority = priority,
                StartedUtc = DateTime.UtcNow,
                Tasks = tasks,
                TaskScriptName = genericTaskFunc,
                PostExecFunctionName = postExecFunctionName,
                PostExecParametersJson = postExecParametersJson,
                ParentRunName = parentRunName
            };

            await _store.UpsertRunAsync(run);
            await _store.UpsertTaskBatchAsync(name, tasks);
            _logger.LogInformation(
                "[Orchestrator] Run {Name} created from batch: {Count} tasks P{Priority}{PostExec}",
                name, tasks.Count, priority,
                postExecFunctionName != null ? $" (PostExec: Push-{postExecFunctionName})" : "");
            DispatchPendingTasks(run, taskPath, priority, ct);
        }
        finally
        {
            _activePlanners.TryRemove(name, out _);
        }
    }

    private void DispatchPendingTasks(OrchestratorRun run, string taskPath, int priority, CancellationToken ct)
    {
        _activeRuns.TryAdd(run.Name, run);

        // Start periodic status timer (every 60s) for this run
        if (!_runStatusTimers.ContainsKey(run.Name))
        {
            var timer = new Timer(_ => LogRunStatus(run), null, TimeSpan.FromSeconds(60), TimeSpan.FromSeconds(60));
            _runStatusTimers.TryAdd(run.Name, timer);
        }

        var pending = run.Tasks.Where(t => t.Status == "Pending").ToList();

        foreach (var task in pending)
        {
            DispatchSingleTask(run, task, taskPath, priority, ct);
        }

        _logger.LogInformation("[Scheduler] Dispatched {Count} tasks for {Name} at P{Priority}",
            pending.Count, run.Name, priority);
    }

    private void DispatchSingleTask(OrchestratorRun run, OrchestratorTaskItem task, string taskPath, int priority, CancellationToken ct)
    {
        _jobManager.Enqueue(
            name: $"{run.Name}-{task.Id}",
            priority: priority,
            runName: run.Name,
            work: async (jobCt) =>
            {
                // Check if run was cancelled while this job was queued
                if (_cancelledRuns.ContainsKey(run.Name))
                {
                    lock (_lock)
                    {
                        if (task.Status != "Cancelled")
                        {
                            task.Status = "Cancelled";
                            task.LastError = "Cancelled by user";
                            task.CompletedUtc = DateTime.UtcNow;
                            task.Parameters = null!;
                            CheckRunCompletion(run);
                        }
                    }
                    PersistTaskAndRunAsync(run, task);
                    return;
                }

                lock (_lock)
                {
                    task.Status = "Running";
                }
                // Pre-script "Running" write is awaited — the durability marker for crash recovery. Batched
                // across concurrently-starting tasks by the status writer, but still durable before the invoke.
                await _writer.MarkRunningAsync(run.Name, task);

                try
                {
                    var parameters = new Dictionary<string, object>
                    {
                        { "TaskJson", JsonSerializer.Serialize(task.Parameters, s_jsonOptions) }
                    };

                    if (!string.IsNullOrEmpty(run.PostExecFunctionName))
                    {
                        // Capture output so C# can store results for PostExecution
                        var output = await _psRunner.ExecuteScriptWithOutput(taskPath, parameters);
                        if (!string.IsNullOrEmpty(output))
                            await _store.StoreResultAsync(run.Name, task.Id, output);
                    }
                    else
                    {
                        await _psRunner.ExecuteScript(taskPath, parameters);
                    }

                    lock (_lock)
                    {
                        task.Status = "Completed";
                        task.CompletedUtc = DateTime.UtcNow;
                        // Release parameters — they are persisted in Table Storage and no longer
                        // needed in-memory. For 738-task runs this frees significant Gen2 memory.
                        task.Parameters = null!;
                        CheckRunCompletion(run);
                    }
                    // Post-script writes are fire-and-forget so the JobManager slot releases
                    // immediately and the dispatch loop can hand the worker to the next task.
                    // Crash recovery still works: the next startup re-reads task state from the
                    // table and re-runs anything not marked Completed (idempotent).
                    PersistTaskAndRunAsync(run, task);

                    _logger.LogDebug("[Scheduler] Task completed: {TaskId}", task.Id);
                }
                catch (OperationCanceledException) when (jobCt.IsCancellationRequested)
                {
                    // App shutting down — leave task as Running for resume on next startup
                    _logger.LogInformation("[Scheduler] Task {TaskId} interrupted by shutdown", task.Id);
                    throw; // Let JobManager mark as Cancelled
                }
                catch (Exception ex)
                {
                    lock (_lock)
                    {
                        task.Status = "Failed";
                        task.LastError = ex.Message;
                        task.CompletedUtc = DateTime.UtcNow;
                        // Release parameters on failure too — Table Storage has the full state
                        task.Parameters = null!;
                        CheckRunCompletion(run);
                    }
                    PersistTaskAndRunAsync(run, task);
                    _logger.LogError(ex, "[Scheduler] Task failed: {TaskId}", task.Id);
                    throw; // Let JobManager also track the failure
                }
            }
        );
    }

    /// <summary>
    /// Fire-and-forget persistence of task + run state. Callers do not await this — it lets the
    /// JobManager slot release immediately so the dispatch loop can hand the worker to the next
    /// task. Errors are logged; on host crash, ResumeInterruptedRunsAsync re-derives state from
    /// whatever made it to the table (writes are idempotent).
    /// </summary>
    private void PersistTaskAndRunAsync(OrchestratorRun run, OrchestratorTaskItem task)
    {
        // Non-blocking: the status writer coalesces these terminal task/run writes and flushes them in batches
        // (guaranteed flushed before the run finalizes). Previously two individual fire-and-forget writes.
        _writer.QueueTask(run.Name, task);
        _writer.QueueRun(run);
    }

    private void LogRunStatus(OrchestratorRun run)
    {
        var elapsed = DateTime.UtcNow - run.StartedUtc;
        int completed, failed, running, pending;
        lock (_lock)
        {
            completed = run.Tasks.Count(t => t.Status == "Completed");
            failed = run.Tasks.Count(t => t.Status == "Failed");
            running = run.Tasks.Count(t => t.Status == "Running");
            pending = run.Tasks.Count(t => t.Status == "Pending");
        }
        var memSnapshot = BackgroundTaskLimiter.GetMemorySnapshot();
        _logger.LogInformation(
            "[Scheduler] Run {Name} T+{Elapsed:F1}min: {Completed}/{Total} done {Running} running {Pending} pending {Failed} failed jobs={Active}a/{Queued}q {Memory}",
            run.Name, elapsed.TotalMinutes, completed, run.Tasks.Count, running, pending, failed,
            _jobManager.ActiveCount, _jobManager.QueuedCount,
            memSnapshot);
    }

    private void CheckRunCompletion(OrchestratorRun run)
    {
        // Already locked by caller
        if (run.Tasks.All(t => t.Status is "Completed" or "Failed" or "Cancelled"))
        {
            // Don't finalize if child runs (sub-orchestrators spawned by tasks) are still active
            if (!AllChildRunsComplete(run.Name))
            {
                _logger.LogInformation(
                    "[Scheduler] Run {Name} tasks complete but waiting for child runs to finish",
                    run.Name);
                return;
            }

            // Cannot await inside lock — schedule finalization
            _ = Task.Run(async () =>
            {
                try { await FinalizeRunAsync(run); }
                catch (Exception ex) { _logger.LogError(ex, "[Scheduler] FinalizeRun failed for {Name}", run.Name); }
            });
        }
    }

    /// <summary>
    /// Register a child run under a parent run. The parent will not finalize
    /// until all child runs complete. Only registers if the parent is still active.
    /// </summary>
    internal void TryRegisterChildRun(string parentRunName, string childRunName)
    {
        if (!_activeRuns.ContainsKey(parentRunName))
            return; // Parent no longer active (e.g. called from PostExec context)

        var children = _childRuns.GetOrAdd(parentRunName, _ => new ConcurrentBag<string>());
        children.Add(childRunName);
        _logger.LogInformation("[Orchestrator] Registered child run {Child} under parent {Parent}",
            childRunName, parentRunName);
    }

    private bool AllChildRunsComplete(string runName)
    {
        if (!_childRuns.TryGetValue(runName, out var children))
            return true;
        return !children.Any(childName => _activeRuns.ContainsKey(childName));
    }

    private async Task FinalizeRunAsync(OrchestratorRun run)
    {
        var failed = run.Tasks.Count(t => t.Status == "Failed");
        var cancelled = run.Tasks.Count(t => t.Status == "Cancelled");
        var completed = run.Tasks.Count(t => t.Status == "Completed");

        run.Status = (failed > 0 || cancelled > 0) ? "CompletedWithErrors" : "Completed";
        run.CompletedUtc = DateTime.UtcNow;
        var wallClock = run.CompletedUtc.Value - run.StartedUtc;

        // Set PostExecStatus before persisting, so crash between here and DispatchPostExecution is recoverable
        if (!string.IsNullOrEmpty(run.PostExecFunctionName))
            run.PostExecStatus = "Pending";

        // Flush-before-finalize: queue the run's final status, then await a full drain so every task's terminal
        // state + this run status are durable BEFORE we declare the run done and dispatch PostExecution.
        _writer.QueueRun(run);
        await _writer.FlushAsync();

        _activeRuns.TryRemove(run.Name, out _);
        _cancelledRuns.TryRemove(run.Name, out _);
        _runStatusTimers.TryRemove(run.Name, out var timer);
        timer?.Dispose();

        var wallDisplay = wallClock.TotalSeconds < 60
            ? $"{wallClock.TotalSeconds:F1}s"
            : $"{wallClock.TotalMinutes:F1}min";

        _logger.LogInformation(
            "[Scheduler] Run {Name} finalized: {Status} ({Completed}/{Failed}/{Cancelled}/{Total}) wall={Wall} {Memory}",
            run.Name, run.Status, completed, failed, cancelled, run.Tasks.Count, wallDisplay,
            BackgroundTaskLimiter.GetMemorySnapshot());

        // If this was a child run, re-check parent's completion — it may have been
        // waiting for this child to finish before it can finalize and run PostExecution
        if (!string.IsNullOrEmpty(run.ParentRunName) &&
            _activeRuns.TryGetValue(run.ParentRunName, out var parentRun))
        {
            lock (_lock) { CheckRunCompletion(parentRun); }
        }

        // Cleanup child run tracking for this run
        _childRuns.TryRemove(run.Name, out _);

        // Dispatch PostExecution if configured
        if (!string.IsNullOrEmpty(run.PostExecFunctionName))
        {
            DispatchPostExecution(run);
        }
        else
        {
            // No PostExec — cleanup results table (if any stray entries exist)
            _ = _store.CleanupRunAsync(run.Name);
        }
    }

    private void DispatchPostExecution(OrchestratorRun run)
    {
        var postExecFunc = _settings.Orchestrator.PostExecFunction;
        var postExecScript = !string.IsNullOrEmpty(postExecFunc) ? _psRunner.FindScript(postExecFunc) : null;
        if (postExecScript == null)
        {
            _logger.LogError("[Orchestrator] PostExec function '{Func}' not found, cannot run PostExecution for {Name}",
                postExecFunc, run.Name);
            return;
        }

        _logger.LogInformation(
            "[Orchestrator] Dispatching PostExecution Push-{Function} for run {Name} {Memory}",
            run.PostExecFunctionName, run.Name, BackgroundTaskLimiter.GetMemorySnapshot());

        _jobManager.Enqueue(
            name: $"{run.Name}-PostExec",
            priority: run.Priority,
            runName: run.Name,
            work: async (jobCt) =>
            {
                // Mark PostExec as Running
                run.PostExecStatus = "Running";
                await _store.UpsertRunAsync(run);

                // Stream results to a temp file instead of building a massive in-memory string
                // from the full entity list. For large runs (738+ tasks), the aggregated JSON can
                // be 50-150 MB. Streaming to file first means we only ever hold ONE copy of the
                // data (the file read), not two (entity list + serialized JSON).
                var tempFile = Path.Combine(Path.GetTempPath(), $"craft-postexec-{Guid.NewGuid():N}.json");
                try
                {
                    await _store.StreamResultsToFileAsync(run.Name, tempFile);
                    var fileSize = new FileInfo(tempFile).Length;
                    var fileSizeMB = fileSize / (1024.0 * 1024.0);
                    _logger.LogInformation(
                        "[Orchestrator] PostExec results for {Name}: {SizeMB:F1}MB streamed to temp file {Memory}",
                        run.Name, fileSizeMB, BackgroundTaskLimiter.GetMemorySnapshot());

                    // Read the file content and pass as ResultsJson — this keeps the PS interface
                    // unchanged so no consumer (Push-*) code needs modification.
                    var resultsJson = await File.ReadAllTextAsync(tempFile, jobCt);

                    // Delete the temp file immediately to free disk — we have the string now
                    try { File.Delete(tempFile); tempFile = null; }
                    catch { /* will retry in finally */ }

                    var parameters = new Dictionary<string, object>
                    {
                        { "FunctionName", run.PostExecFunctionName! },
                        { "ResultsJson", resultsJson }
                    };
                    if (!string.IsNullOrEmpty(run.PostExecParametersJson))
                        parameters["ParametersJson"] = run.PostExecParametersJson;

                    await _psRunner.ExecuteScript(postExecScript, parameters);

                    // PostExecution functions may call Start-CIPPOrchestrator (Phase 2)
                    await OrchestratorBridge.DrainPendingAsync();

                    // Mark PostExec as Completed
                    run.PostExecStatus = "Completed";
                    await _store.UpsertRunAsync(run);

                    _logger.LogInformation("[Orchestrator] PostExecution Push-{Function} completed for run {Name}",
                        run.PostExecFunctionName, run.Name);

                    // Cleanup after successful PostExec
                    await _store.CleanupRunAsync(run.Name);
                }
                catch (Exception ex)
                {
                    // Mark PostExec as Failed — will be retried on next startup via ResumeInterruptedRunsAsync
                    run.PostExecStatus = "Failed";
                    try { await _store.UpsertRunAsync(run); } catch { /* best effort */ }
                    _logger.LogError(ex, "[Orchestrator] PostExecution Push-{Function} failed for run {Name}",
                        run.PostExecFunctionName, run.Name);
                    throw;
                }
                finally
                {
                    // Clean up temp file if it still exists (early delete may have succeeded)
                    if (tempFile != null)
                    {
                        try { if (File.Exists(tempFile)) File.Delete(tempFile); }
                        catch (Exception ex) { _logger.LogDebug(ex, "[Orchestrator] Failed to delete temp file {Path}", tempFile); }
                    }
                }
            }
        );
    }

    private List<OrchestratorTaskItem> ParseTasksFromJson(string json, string runName)
    {
        var tasks = new List<OrchestratorTaskItem>();

        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            if (root.ValueKind != JsonValueKind.Array)
            {
                _logger.LogWarning("[Scheduler] Planner output not a JSON array: {Name}", runName);
                return tasks;
            }

            var usedIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var element in root.EnumerateArray())
            {
                // Skip null or non-object elements (planner returned $null for a tenant)
                if (element.ValueKind != JsonValueKind.Object)
                    continue;

                var parameters = new Dictionary<string, object>();
                string? collectionType = null;
                string? name = null;
                string? tenantFilter = null;
                string? functionName = null;
                string? suiteName = null;
                string? batchNumber = null;

                foreach (var prop in element.EnumerateObject())
                {
                    var value = prop.Value.ValueKind switch
                    {
                        JsonValueKind.String => (object)prop.Value.GetString()!,
                        JsonValueKind.Number => prop.Value.TryGetInt64(out var l) ? l : (object)prop.Value.GetDouble(),
                        JsonValueKind.True => true,
                        JsonValueKind.False => false,
                        _ => prop.Value.Clone()
                    };
                    parameters[prop.Name] = value;

                    if (prop.Name.Equals("CollectionType", StringComparison.OrdinalIgnoreCase))
                        collectionType = prop.Value.GetString();
                    if (prop.Name.Equals("Name", StringComparison.OrdinalIgnoreCase))
                        name = prop.Value.GetString();
                    if (prop.Name.Equals("TenantFilter", StringComparison.OrdinalIgnoreCase))
                        tenantFilter = prop.Value.GetString();
                    if (prop.Name.Equals("FunctionName", StringComparison.OrdinalIgnoreCase))
                        functionName = prop.Value.GetString();
                    if (prop.Name.Equals("SuiteName", StringComparison.OrdinalIgnoreCase))
                        suiteName = prop.Value.GetString();
                    if (prop.Name.Equals("BatchNumber", StringComparison.OrdinalIgnoreCase))
                        batchNumber = prop.Value.ToString();

                    // Extract tenant from nested Tenant object (e.g. audit log batch items)
                    if (tenantFilter == null && prop.Name.Equals("Tenant", StringComparison.OrdinalIgnoreCase)
                        && prop.Value.ValueKind == JsonValueKind.Object
                        && prop.Value.TryGetProperty("defaultDomainName", out var ddn)
                        && ddn.ValueKind == JsonValueKind.String)
                    {
                        tenantFilter = ddn.GetString();
                    }
                }

                // Build a unique task ID from available distinguishing properties
                var label = collectionType ?? suiteName ?? name ?? functionName ?? "unknown";
                var tenant = tenantFilter ?? "unknown";
                var taskId = batchNumber != null ? $"{label}_{tenant}_b{batchNumber}" : $"{label}_{tenant}";

                // Ensure uniqueness — append index if collision
                if (!usedIds.Add(taskId))
                {
                    var idx = 2;
                    while (!usedIds.Add($"{taskId}_{idx}")) idx++;
                    taskId = $"{taskId}_{idx}";
                }

                tasks.Add(new OrchestratorTaskItem
                {
                    Id = taskId,
                    Parameters = parameters,
                    Status = "Pending"
                });
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to parse planner output for run {Name}. Output: {Output}",
                runName, json?.Length > 500 ? json[..500] + "..." : json);
        }

        return tasks;
    }

    private string? FindTaskScript(string runName)
    {
        // Convention: strip "Start-" prefix → "Invoke-{rest}Task"
        var baseName = runName.StartsWith("Start-", StringComparison.OrdinalIgnoreCase)
            ? runName[6..]
            : runName;
        return _psRunner.FindScript($"Invoke-{baseName}Task");
    }

    /// <summary>
    /// Cancel a running orchestrator run. Pending tasks are marked Cancelled immediately.
    /// Already-running tasks are allowed to finish (no force-kill).
    /// Queued jobs in the JobManager will be skipped when they are dequeued.
    /// </summary>
    public async Task<(bool found, int cancelledCount)> CancelRunAsync(string name)
    {
        var run = await _store.GetRunAsync(name);
        if (run == null) return (false, 0);

        // Mark this run as cancelled so dispatched-but-not-yet-started tasks skip execution
        _cancelledRuns.TryAdd(name, true);

        int cancelled;
        var tasksToUpdate = new List<OrchestratorTaskItem>();
        lock (_lock)
        {
            var pendingTasks = run.Tasks.Where(t => t.Status == "Pending").ToList();
            cancelled = pendingTasks.Count;
            foreach (var task in pendingTasks)
            {
                task.Status = "Cancelled";
                task.LastError = "Cancelled by user";
                task.CompletedUtc = DateTime.UtcNow;
                tasksToUpdate.Add(task);
            }
        }

        // Persist cancelled task states
        foreach (var t in tasksToUpdate)
            await _store.UpsertTaskAsync(run.Name, t);

        // Check if the run is now fully done (Running tasks will finalize themselves)
        var remaining = run.Tasks.Count(t => t.Status is "Running");
        if (remaining == 0)
        {
            await FinalizeRunAsync(run);
        }
        else
        {
            await _store.UpsertRunAsync(run);
        }

        _logger.LogInformation("[Scheduler] Run {Name} cancelled: {Cancelled} pending tasks cancelled, {Running} still running",
            name, cancelled, run.Tasks.Count(t => t.Status == "Running"));

        return (true, cancelled);
    }

    /// <summary>
    /// Check whether a run has been cancelled (used by dispatch to skip queued tasks).
    /// </summary>
    public bool IsRunCancelled(string runName) => _cancelledRuns.ContainsKey(runName);

    /// <summary>
    /// Get the current state of a run (or null if it doesn't exist).
    /// Used by the API status endpoint.
    /// </summary>
    public async Task<OrchestratorRun?> GetRunStatusAsync(string name)
    {
        await _store.InitializeAsync();
        return await _store.GetRunAsync(name);
    }

    /// <summary>
    /// List all known run names from table storage.
    /// </summary>
    public async Task<List<string>> ListRunsAsync()
    {
        await _store.InitializeAsync();
        return await _store.ListRunsAsync();
    }
}

public class OrchestratorRun
{
    public string Name { get; set; } = string.Empty;
    public string? Reference { get; set; }
    public string Status { get; set; } = "Pending";
    public int Priority { get; set; } = 2;
    public DateTime StartedUtc { get; set; }
    public DateTime? CompletedUtc { get; set; }
    public List<OrchestratorTaskItem> Tasks { get; set; } = [];
    public string? TaskScriptName { get; set; }
    public string? PostExecFunctionName { get; set; }
    public string? PostExecParametersJson { get; set; }
    public string? PostExecStatus { get; set; }  // null | "Pending" | "Running" | "Completed" | "Failed"
    public string? ParentRunName { get; set; }
}

public class OrchestratorTaskItem
{
    public string Id { get; set; } = string.Empty;
    public string Status { get; set; } = "Pending";
    public Dictionary<string, object> Parameters { get; set; } = [];
    public int AttemptCount { get; set; }
    public string? LastError { get; set; }
    public DateTime? CompletedUtc { get; set; }
}

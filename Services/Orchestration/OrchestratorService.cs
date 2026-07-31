using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using System.Text.Json.Serialization;
using Craft.Configuration;
using Craft.Services;
using Craft.Storage;

namespace Craft.Orchestration;

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
public class OrchestratorService : IJobDescriptorStateWriter
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

    /// <summary>
    /// Child runs seen in storage at startup but not yet processed by <see cref="ResumeInterruptedRunsAsync"/>.
    /// They count as incomplete: without this, a parent processed BEFORE its child would find the child
    /// absent from <see cref="_activeRuns"/> (nothing has resumed it yet) and conclude it had finished.
    /// Each name is cleared as its run is processed, whichever way that goes.
    /// </summary>
    private readonly ConcurrentDictionary<string, bool> _recoveringChildren = new();
    private readonly ConcurrentDictionary<string, Timer> _runStatusTimers = new();
    private readonly ConcurrentDictionary<string, bool> _cancelledRuns = new();

    /// <summary>
    /// Resolved task-script path per run. One entry per RUN (not per task), so a 738-task fan-out costs
    /// one string reference instead of 738 captured ones. Populated at dispatch, dropped at finalize.
    /// </summary>
    private readonly ConcurrentDictionary<string, string> _taskScriptPaths = new();

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

        // The queue holds descriptors; this is how they become work again at dispatch time, and how
        // operator changes to a queued task are made durable.
        _jobManager.SetWorkResolver(ResolveTaskWorkAsync);
        _jobManager.SetDescriptorStateWriter(this);
    }

    // ─── IJobDescriptorStateWriter ───
    // Operator actions on a QUEUED task. Both mutate the live run graph (so the in-memory view and
    // CheckRunCompletion stay consistent) and queue a durable write through the same coalescing
    // status writer the task lifecycle already uses — which guarantees a flush before the run
    // finalizes and a final drain on shutdown.

    /// <summary>Persist a per-task priority override so recovery re-queues at the operator's priority.</summary>
    public void PriorityChanged(JobDescriptor descriptor, int newPriority)
    {
        if (!TryFindLive(descriptor, out _, out var task)) return;

        lock (_lock) task.Priority = newPriority;
        _writer.QueueTask(descriptor.RunName, task);
    }

    /// <summary>
    /// Persist a cancellation. Without this the task row stays Pending and
    /// <see cref="ResumeInterruptedRunsAsync"/> re-queues it after a restart — the job comes back.
    /// </summary>
    public void Cancelled(JobDescriptor descriptor)
    {
        if (!TryFindLive(descriptor, out var run, out var task)) return;

        lock (_lock)
        {
            if (task.Status is "Completed" or "Failed" or "Cancelled") return;
            task.Status = "Cancelled";
            task.LastError = "Cancelled by user";
            task.CompletedUtc = DateTime.UtcNow;
            task.Parameters = null!;
            // Cancelling the last outstanding task can complete the run.
            CheckRunCompletion(run);
        }
        _writer.QueueTask(descriptor.RunName, task);
    }

    /// <summary>
    /// Resolve a descriptor to its LIVE run and task instances. Operator actions only apply to QUEUED
    /// jobs, whose run is by definition still active, so a miss means the job is no longer
    /// cancellable/reprioritizable and there is nothing to persist.
    /// </summary>
    private bool TryFindLive(JobDescriptor descriptor,
        [NotNullWhen(true)] out OrchestratorRun? run,
        [NotNullWhen(true)] out OrchestratorTaskItem? task)
    {
        task = null;
        if (!_activeRuns.TryGetValue(descriptor.RunName, out run)) return false;
        lock (_lock) task = run.Tasks.FirstOrDefault(t => t.Id == descriptor.TaskId);
        return task != null;
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

        // Rebuild parent→child links BEFORE processing anything. _childRuns is in-memory, so without
        // this a resumed parent has no registered children, AllChildRunsComplete answers true, and the
        // parent can finalize (and fire PostExecution) while its children are still running — exactly
        // what the child-run guard exists to prevent. One partition scan, no task rows.
        var summaries = await _store.ListRunSummariesAsync();
        var reattached = 0;
        foreach (var child in summaries)
        {
            if (string.IsNullOrEmpty(child.ParentRunName)) continue;
            if (child.Status is not "Running") continue;   // terminal children cannot block a parent
            _childRuns.GetOrAdd(child.ParentRunName, _ => new ConcurrentBag<string>()).Add(child.Name);
            _recoveringChildren.TryAdd(child.Name, true);
            reattached++;
        }
        if (reattached > 0)
            _logger.LogInformation("[Scheduler] Reattached {Count} in-flight child runs to their parents", reattached);

        foreach (var runName in summaries.Select(s => s.Name))
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
            finally
            {
                // This run is no longer awaiting recovery, however it went. If it was resumed it is now
                // in _activeRuns and still blocks its parent; if it finalized or could not be resumed,
                // it must stop blocking. Clearing here covers every path including the failure one.
                _recoveringChildren.TryRemove(runName, out _);
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
        // Registered before anything is enqueued — the resolver reads it on the dispatch side.
        _taskScriptPaths[run.Name] = taskPath;

        // Start periodic status timer (every 60s) for this run
        if (!_runStatusTimers.ContainsKey(run.Name))
        {
            var timer = new Timer(_ => LogRunStatus(run), null, TimeSpan.FromSeconds(60), TimeSpan.FromSeconds(60));
            _runStatusTimers.TryAdd(run.Name, timer);
        }

        var pending = run.Tasks.Where(t => t.Status == "Pending").ToList();

        foreach (var task in pending)
        {
            DispatchSingleTask(run, task, priority);
        }

        _logger.LogInformation("[Scheduler] Dispatched {Count} tasks for {Name} at P{Priority}",
            pending.Count, run.Name, priority);
    }

    /// <summary>
    /// Enqueue one task BY IDENTITY. The JobManager holds only (runName, taskId, priority) — no run
    /// graph, no task, no script path, no service reference — and calls back into
    /// <see cref="ResolveTaskWorkAsync"/> at dispatch time to rebuild the work.
    /// </summary>
    private void DispatchSingleTask(OrchestratorRun run, OrchestratorTaskItem task, int priority)
    {
        // A per-task override (set by an operator reprioritizing this job) wins over the run's
        // priority; null — the normal case — inherits. Because the override is persisted, this holds
        // across a restart rather than reverting when the run is re-queued from storage.
        _jobManager.Enqueue(
            new JobDescriptor(run.Name, task.Id, task.Priority ?? priority),
            name: $"{run.Name}-{task.Id}");
    }

    /// <summary>
    /// Rebuild the work for a queued task. Registered on the JobManager at startup.
    ///
    /// Resolution order matters, for correctness before cost:
    ///   1. the live run in <see cref="_activeRuns"/>, if present — this is the steady-state path and
    ///      costs ZERO storage reads;
    ///   2. table storage, if the run is not in memory (crash recovery, or a run finalized and evicted
    ///      while its tasks were still queued).
    ///
    /// Step 1 is not an optimization, it is a requirement. <see cref="CheckRunCompletion"/> decides
    /// finalization by inspecting <c>run.Tasks</c>, and the task work mutates <c>task.Status</c> in
    /// place. Handing out a freshly-deserialized task object would mutate a copy the live run graph
    /// never sees, and no run would ever finalize. Object identity — not the field values — is the one
    /// piece of state here that is genuinely not rehydratable.
    /// </summary>
    private async Task<Func<CancellationToken, Task>?> ResolveTaskWorkAsync(JobDescriptor descriptor, CancellationToken ct)
    {
        if (!_taskScriptPaths.TryGetValue(descriptor.RunName, out var taskPath))
        {
            _logger.LogWarning("[Orchestrator] No task script path known for run {Run} — dropping {Task}",
                descriptor.RunName, descriptor.TaskId);
            return null;
        }

        OrchestratorRun? run;
        OrchestratorTaskItem? task;

        if (_activeRuns.TryGetValue(descriptor.RunName, out var liveRun))
        {
            run = liveRun;
            lock (_lock)
            {
                task = liveRun.Tasks.FirstOrDefault(t => t.Id == descriptor.TaskId);
            }
        }
        else
        {
            // Run is no longer in memory — rehydrate it. This is the same read the crash-recovery path
            // already performs (ResumeInterruptedRunsAsync), which resumed 421 runs in seconds.
            run = await _store.GetRunAsync(descriptor.RunName);
            task = run?.Tasks.FirstOrDefault(t => t.Id == descriptor.TaskId);
            if (run != null && task != null)
            {
                // Re-establish the live graph so sibling tasks share this instance and completion
                // tracking works, exactly as it does on the resume path.
                run = _activeRuns.GetOrAdd(run.Name, run);
                task = run.Tasks.FirstOrDefault(t => t.Id == descriptor.TaskId) ?? task;
            }
        }

        if (run == null || task == null)
        {
            _logger.LogDebug("[Orchestrator] Stale descriptor {Run}/{Task} — no longer in storage",
                descriptor.RunName, descriptor.TaskId);
            return null;
        }

        // Already terminal (e.g. cancelled, or completed by a previous attempt while queued).
        lock (_lock)
        {
            if (task.Status is "Completed" or "Failed" or "Cancelled")
                return null;
        }

        return BuildTaskWork(run, task, taskPath);
    }

    private Func<CancellationToken, Task> BuildTaskWork(OrchestratorRun run, OrchestratorTaskItem task, string taskPath)
    {
        return
            async (jobCt) =>
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
                try
                {
                    await _writer.MarkRunningAsync(run.Name, task, jobCt);
                }
                catch (MarkerNotPersistedException ex)
                {
                    // DEFERRAL, not failure. The marker never landed, so storage still has this task
                    // Pending — running it now would break the poison-task bound that the marker exists
                    // to provide. Put the in-memory copy back to Pending so it agrees with storage, give
                    // the slot up, and retry. Nothing is lost: even if this process dies first, recovery
                    // re-queues it from the Pending row.
                    lock (_lock) { task.Status = "Pending"; }
                    DeferTask(run, task, ex);
                    return;
                }
                _deferrals.TryRemove(DeferralKey(run.Name, task.Id), out _);

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
            };
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

    /// <summary>Deferrals per task while storage is unable to accept the durable marker. In-memory and
    /// intentionally so — it bounds retries within one process life, nothing more.</summary>
    private readonly ConcurrentDictionary<string, int> _deferrals = new();

    /// <summary>Cap on in-process retries before a task is left for the next recovery pass to pick up.</summary>
    private const int MaxDeferrals = 3;

    /// <summary>
    /// Re-queue a task whose durable marker could not be written, so it retries once storage recovers
    /// instead of waiting for a restart. Bounded: after <see cref="MaxDeferrals"/> the task is simply
    /// left Pending, which is already the durable state — recovery re-queues it on the next startup.
    /// </summary>
    private static string DeferralKey(string runName, string taskId) => $"{runName}{taskId}";

    private void DeferTask(OrchestratorRun run, OrchestratorTaskItem task, Exception cause)
    {
        var count = _deferrals.AddOrUpdate(DeferralKey(run.Name, task.Id), 1, (_, c) => c + 1);

        if (count > MaxDeferrals)
        {
            _logger.LogError(cause,
                "[Scheduler] Task {TaskId} in {Run} deferred {Count} times — leaving it Pending for the next recovery",
                task.Id, run.Name, count);
            return;
        }

        _logger.LogWarning(
            "[Scheduler] Task {TaskId} in {Run} could not be marked Running (attempt {Count}/{Max}) — re-queued, slot released",
            task.Id, run.Name, count, MaxDeferrals);

        _jobManager.Enqueue(new JobDescriptor(run.Name, task.Id, task.Priority ?? run.Priority),
            name: $"{run.Name}-{task.Id}");
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
        return !children.Any(childName =>
            _activeRuns.ContainsKey(childName) || _recoveringChildren.ContainsKey(childName));
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
        _taskScriptPaths.TryRemove(run.Name, out _);
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

                // Stream results to a temp file rather than building the aggregate in memory. For large
                // runs (738+ tasks) that aggregate is 50-150 MB, and StreamResultsToFileAsync now holds
                // one chunk at a time rather than the whole entity set (it used to buffer every result
                // row into a dictionary before writing a byte, so "streaming" still peaked at the full
                // payload in UTF-16 — roughly 2x the stored size — before this ran).
                //
                // The remaining copy is ResultsJson below: the PS contract takes the aggregate as a
                // string, so it is read back as one Large Object Heap allocation and PowerShell copies
                // it again on ConvertFrom-Json. Removing THAT needs a contract change with the
                // configured PostExecFunction (App:Orchestrator:PostExecFunction), whose downstream
                // Push-* consumers live outside this repo.
                var tempFile = Path.Combine(Path.GetTempPath(), $"craft-postexec-{Guid.NewGuid():N}.json");
                try
                {
                    await _store.StreamResultsToFileAsync(run.Name, tempFile, jobCt);
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
                string? queueName = null;
                string? customerId = null;

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
                    if (prop.Name.Equals("QueueName", StringComparison.OrdinalIgnoreCase)
                        && prop.Value.ValueKind == JsonValueKind.String)
                        queueName = prop.Value.GetString();
                    if (prop.Name.Equals("customerId", StringComparison.OrdinalIgnoreCase)
                        && prop.Value.ValueKind == JsonValueKind.String)
                        customerId = prop.Value.GetString();

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
                var tenant = tenantFilter ?? queueName ?? customerId ?? "unknown";
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

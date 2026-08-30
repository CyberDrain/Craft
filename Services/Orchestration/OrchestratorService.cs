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

    /// <summary>The durable job queue. Its table is created alongside the orchestrator's; nothing
    /// dispatches from it yet, so an existing deployment gains an empty table and nothing else.</summary>
    private readonly JobQueueStore _queue;
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

    /// <summary>
    /// Child runs queued through the bridge but not yet started, keyed by child name with a count
    /// of outstanding queue entries. They count as incomplete for the same reason recovering
    /// children do: between a task's script enqueuing a sub-orchestration and DrainPending getting
    /// it into <see cref="_activeRuns"/> there is otherwise nothing for the parent's completion
    /// check to see — and that window contains the parent's own last-task completion, because the
    /// drain runs in the background while the enqueuing task is marked terminal immediately.
    /// </summary>
    private readonly ConcurrentDictionary<string, int> _pendingChildRuns = new();
    private readonly ConcurrentDictionary<string, Timer> _runStatusTimers = new();
    private readonly ConcurrentDictionary<string, bool> _cancelledRuns = new();

    /// <summary>
    /// Resolved task-script path per run. One entry per RUN (not per task), so a 738-task fan-out costs
    /// one string reference instead of 738 captured ones. Populated at dispatch, dropped at finalize.
    /// </summary>
    private readonly ConcurrentDictionary<string, string> _taskScriptPaths = new();

    /// <summary>
    /// How many times a run's post-execution may be attempted before it is abandoned. Matches the
    /// per-task cap so recovery behaves the same at both levels: retry a few times across restarts,
    /// then stop and release the storage rather than retrying forever.
    /// </summary>
    private const int MaxPostExecAttempts = 3;

    /// <summary>
    /// Runs whose finalize has been claimed, so it happens once. Claimed in CheckRunCompletion,
    /// released on the deferral/failure paths there, and cleared in DispatchPendingTasksAsync when a
    /// run becomes live again. In-memory only: after a restart nothing has been finalized yet, so an
    /// empty set is the correct starting state.
    /// </summary>
    private readonly ConcurrentDictionary<string, bool> _finalizingRuns = new();

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
        JobQueueStore queue,
        OrchestratorStatusWriter writer,
        CraftSettings settings)
    {
        _logger = logger;
        _psRunner = psRunner;
        _limiter = limiter;
        _jobManager = jobManager;
        _store = store;
        _queue = queue;
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
        CancelLiveTask(run, task);
    }

    /// <summary>
    /// Mark a live task Cancelled in the run graph and queue the durable write. Returns false when the
    /// task is already terminal — nothing to cancel, nothing to persist. With
    /// <paramref name="requirePending"/>, a Running task is also refused: the callers that pass it
    /// found the task via a queue snapshot that may be seconds old, and cancelling a task that has
    /// since dispatched would race its own completion write.
    /// </summary>
    private bool CancelLiveTask(OrchestratorRun run, OrchestratorTaskItem task, bool requirePending = false)
    {
        lock (_lock)
        {
            if (task.Status is "Completed" or "Failed" or "Cancelled") return false;
            if (requirePending && task.Status != "Pending") return false;
            task.Status = "Cancelled";
            task.LastError = "Cancelled by user";
            task.CompletedUtc = DateTime.UtcNow;
            task.Parameters = null!;
            // Cancelling the last outstanding task can complete the run.
            CheckRunCompletion(run);
        }
        _writer.QueueTask(run.Name, task);
        return true;
    }

    /// <summary>
    /// Cancel a task that exists only as a durable queue row — queued in the table, not (yet) claimed
    /// into this instance's JobManager, which is where the worker-health page now sees most of a
    /// backlog.
    ///
    /// Order is load-bearing: the run graph is marked terminal and the write queued BEFORE the queue
    /// row is removed, because the orphan re-drive reads "Pending with no row" as work to re-queue —
    /// remove the row first and the cancel un-does itself within a minute. The row removal itself is
    /// best-effort: a row left behind is claimed, found terminal at rehydration, skipped and released.
    ///
    /// Returns false when the run is not live on this node or the task is already terminal; callers
    /// should treat that as "nothing cancellable here".
    /// </summary>
    public async Task<bool> TryCancelQueuedTaskAsync(string runName, string taskId)
    {
        if (!TryFindLive(new JobDescriptor(runName, taskId, 0), out var run, out var task)) return false;
        if (!CancelLiveTask(run, task, requirePending: true)) return false;

        try
        {
            await _queue.RemoveTaskAsync(runName, taskId);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "[Scheduler] Cancelled {Run}/{Task} but could not remove its queue row — it will be skipped at claim",
                runName, taskId);
        }

        return true;
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
            await _queue.InitializeAsync(ct);
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
                    await DispatchPendingTasksAsync(run, taskPath, run.Priority, ct);
                    return;
                }

                // All tasks finished — finalize, and STOP. Finalize dispatches post-execution
                // asynchronously, and its success path deletes the run's partitions by name
                // (CleanupRunAsync). Falling through to start a fresh same-named outing here raced
                // that delete and lost the new run's rows mid-flight; the next scheduler tick starts
                // the fresh outing cleanly instead.
                await FinalizeRunAsync(run);
                return;
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
            // Seed the durable outstanding-task count alongside the tasks themselves, so completion
            // is answerable from storage instead of by walking this run graph.
            await _store.InitRemainingAsync(name, tasks.Count, ct);
            _logger.LogInformation("[Scheduler] Run {Name} created with {Count} tasks at P{Priority}", name, tasks.Count, priority);
            await DispatchPendingTasksAsync(run, taskPath, priority, ct);
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
        await _queue.InitializeAsync(ct);

        // Rebuild parent→child links BEFORE processing anything. _childRuns is in-memory, so without
        // this a resumed parent has no registered children, AllChildRunsComplete answers true, and the
        // parent can finalize (and fire PostExecution) while its children are still running — exactly
        // what the child-run guard exists to prevent. One partition scan, no task rows.
        var summaries = await _store.ListRunSummariesAsync();
        var runningRuns = summaries.Where(s => s.Status == "Running").Select(s => s.Name)
            .ToHashSet(StringComparer.Ordinal);
        var reattached = 0;
        foreach (var child in summaries)
        {
            if (string.IsNullOrEmpty(child.ParentRunName)) continue;
            // Rows written before the self-parent guard can record a run as its own parent;
            // reattaching one would rebuild the self-wait this fix removes.
            if (child.ParentRunName == child.Name) continue;
            if (child.Status is not "Running") continue;   // terminal children cannot block a parent
            // The parent must itself be resuming. Lineage can point at a run that already
            // finalized — a run queued from PostExecution records its finished spawner as parent —
            // and reattaching those would build bags no completion check consults, or worse, gate
            // the NEXT outing of a recurring parent name on a leftover of the previous one.
            if (!runningRuns.Contains(child.ParentRunName)) continue;
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

                // Check for runs whose PostExec was pending/running when we crashed, or failed outright.
                //
                // "Failed" belongs here: post-execution IS the aggregation, so a failed one means the
                // run's whole point never happened (no cached permissions, no applied standards) and its
                // Results rows were never cleaned up, because CleanupRunAsync only runs on success. This
                // used to be excluded, which quietly contradicted the catch block's own comment that a
                // failure "will be retried on next startup".
                if (run.Status is "Completed" or "CompletedWithErrors"
                    && run.PostExecStatus is "Pending" or "Running" or "Failed")
                {
                    if (run.PostExecAttemptCount >= MaxPostExecAttempts)
                    {
                        // Out of retries. Say so once and release the storage, rather than re-reading
                        // this run's rows on every start for the life of the deployment.
                        _logger.LogError(
                            "[Scheduler] PostExecution for {Name} failed {Count} times — giving up and cleaning up results",
                            run.Name, run.PostExecAttemptCount);
                        run.PostExecStatus = "Abandoned";
                        await _store.UpsertRunAsync(run);
                        await _store.CleanupRunAsync(run.Name);
                        await _queue.RemoveRunAsync(run.Name, ct);
                        continue;
                    }

                    _logger.LogInformation(
                        "[Scheduler] Resuming interrupted PostExecution for run: {Name} (PostExecStatus={Status}, attempt {Attempt}/{Max})",
                        run.Name, run.PostExecStatus, run.PostExecAttemptCount + 1, MaxPostExecAttempts);
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
                    // Hand back the claims the dead process was holding. Reaching here means this run was
                    // interrupted, so every lease on its rows belongs to a process that no longer exists —
                    // but the lease itself is still live as far as storage is concerned, so nothing can
                    // claim those rows until it lapses. Re-dispatch below deliberately does not paper over
                    // that by writing duplicate rows, which is what used to hide it, so without this the
                    // run stalls for the remainder of the lease: measured as 12 tasks Pending with nothing
                    // running for the balance of a 30 minute lease after a kill.
                    try
                    {
                        var released = await _queue.ReleaseRunClaimsAsync(run.Name, ct);
                        if (released > 0)
                            _logger.LogInformation(
                                "[Scheduler] Released {Count} stale claim(s) held by the previous process for {Name}",
                                released, run.Name);
                    }
                    catch (Exception ex)
                    {
                        // Not fatal: the leases still lapse on their own, just slowly.
                        _logger.LogWarning(ex, "[Scheduler] Could not release stale claims for {Name}", run.Name);
                    }

                    _logger.LogInformation("[Scheduler] Resuming interrupted run {Name}: {Pending} pending", run.Name, pending);
                    await DispatchPendingTasksAsync(run, taskPath, run.Priority, ct);
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

        // First retention pass, now that every run that could be resumed is back in _activeRuns and so
        // exempt from the abandoned-run rule. The scheduler keeps it going on an interval from here.
        try
        {
            await RunRetentionSweepAsync(ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[Scheduler] Startup retention sweep failed");
        }
    }

    /// <summary>
    /// One retention pass over the orchestrator tables: finished runs past
    /// <c>Orchestrator:RetentionHours</c>, runs nobody is driving that have not been written to for that
    /// long, and Tasks/Results partitions whose Run row is already gone. Runs at the end of startup
    /// recovery and then every <c>Orchestrator:CleanupIntervalHours</c> via <see cref="RunRetentionLoopAsync"/>.
    /// </summary>
    public async Task<OrchestratorCleanupResult> RunRetentionSweepAsync(CancellationToken ct)
    {
        var retention = TimeSpan.FromHours(Math.Max(1, _settings.Orchestrator.RetentionHours));
        var active = _activeRuns.Keys.ToHashSet(StringComparer.Ordinal);
        var result = await _store.CleanupOldRunsAsync(retention, active, ct);

        // An abandoned run can still have rows in the durable queue. The pump would drop each as a
        // stale descriptor when it came to claim it — but only after paying for the claim.
        foreach (var name in result.AbandonedRuns)
        {
            try
            {
                await _queue.RemoveRunAsync(name, ct);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[Scheduler] Could not remove queue rows for abandoned run {Name}", name);
            }
        }

        return result;
    }

    /// <summary>
    /// Periodic retention sweeps for the life of the host, started by the scheduler once recovery has
    /// run. A sweep that fails is logged and tried again next interval; <c>CleanupIntervalHours</c> of 0
    /// leaves only the startup pass.
    /// </summary>
    public async Task RunRetentionLoopAsync(CancellationToken ct)
    {
        var hours = _settings.Orchestrator.CleanupIntervalHours;
        if (hours <= 0)
        {
            _logger.LogInformation(
                "[Scheduler] Periodic retention sweep disabled (CleanupIntervalHours={Hours}); only the startup pass runs", hours);
            return;
        }

        var interval = TimeSpan.FromHours(hours);
        using var timer = new PeriodicTimer(interval);
        try
        {
            while (await timer.WaitForNextTickAsync(ct))
            {
                try
                {
                    await RunRetentionSweepAsync(ct);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    return;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "[Scheduler] Retention sweep failed; next attempt in {Interval}", interval);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Host shutdown.
        }
    }

    /// <summary>
    /// Start an orchestrator run from a pre-built batch.
    /// Called by OrchestratorBridge.DrainPending() when PowerShell's Start-CIPPOrchestrator
    /// queues a run on CIPPNG (bypassing the planner script phase).
    ///
    /// The batch arrives one of two ways. <paramref name="batchFilePath"/>, when set, is a JSON Lines
    /// file with one task object per line and is the preferred form: the caller writes it a task at a
    /// time and this reads it a line at a time, so no single string ever holds the whole batch.
    /// <paramref name="batchJson"/> is the original whole-array-in-one-string form, kept for callers
    /// that still use it — it costs the full batch as a string here AND again as a JsonDocument.
    /// A file path wins when both are given; the file is deleted once parsed.
    /// </summary>
    public async Task StartFromBatchAsync(string name, string batchJson, int priority,
        string? postExecFunctionName, string? postExecParametersJson, CancellationToken ct,
        string? parentRunName = null, string? reference = null, string? batchFilePath = null)
    {
        // The batch file is this method's to dispose of, on EVERY path — including the two
        // "already running, skipping" returns below, which never look at it. Those are the common
        // case for a duplicate enqueue, so leaving cleanup at the parse site would quietly fill the
        // container's temp directory with the batches of runs that were skipped rather than started.
        try
        {
            await StartFromBatchCoreAsync(name, batchJson, priority, postExecFunctionName,
                postExecParametersJson, parentRunName, reference, batchFilePath, ct);
        }
        finally
        {
            if (!string.IsNullOrEmpty(batchFilePath))
            {
                try { if (File.Exists(batchFilePath)) File.Delete(batchFilePath); }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "[Orchestrator] Failed to delete batch file {Path}", batchFilePath);
                }
            }
        }
    }

    private async Task StartFromBatchCoreAsync(string name, string batchJson, int priority,
        string? postExecFunctionName, string? postExecParametersJson,
        string? parentRunName, string? reference, string? batchFilePath, CancellationToken ct)
    {
        // Run names become PartitionKeys verbatim, and batch names carry user-typed task names
        // ("Alert on Entra ID P1/P2 …"). An illegal key character 400s every write for the run —
        // run row, task rows, counter, queue rows — identically forever, so the run can neither
        // start nor be re-driven. Sanitize at the boundary, like task ids at mint.
        name = TableKeys.Sanitize(name);
        if (!string.IsNullOrEmpty(parentRunName))
            parentRunName = TableKeys.Sanitize(parentRunName);

        // A run cannot be its own parent. The ambient RunName rides along when a run is re-queued
        // from inside its own context; persisting it would feed the reattach loop a self-link on the
        // next start and show circular lineage in the status APIs.
        if (parentRunName == name)
            parentRunName = null;

        if (!_activePlanners.TryAdd(name, true))
        {
            _logger.LogInformation("[Orchestrator] Run {Name} already in progress, skipping", name);
            return;
        }

        try
        {
            await _store.InitializeAsync();
            await _queue.InitializeAsync(ct);

            var existing = await _store.GetRunAsync(name);
            if (existing != null && existing.Status == "Running" && _activeRuns.ContainsKey(name))
            {
                _logger.LogInformation("[Orchestrator] Run {Name} already active, skipping", name);
                return;
            }

            List<OrchestratorTaskItem> tasks;
            if (!string.IsNullOrEmpty(batchFilePath))
            {
                var batchBytes = File.Exists(batchFilePath) ? new FileInfo(batchFilePath).Length : 0;
                _logger.LogDebug(
                    "[Orchestrator] Batch for {Name} streamed from {Path} ({KB:F1} KB on disk, never held whole)",
                    name, batchFilePath, batchBytes / 1024.0);

                tasks = ParseTasksFromJsonLinesFile(batchFilePath, name);
            }
            else
            {
                // Sizing the legacy inbound batch: the whole run's task list as ONE string, built by
                // ConvertTo-Json in the calling PowerShell, held in the bridge queue, and parsed here
                // into a JsonDocument that holds it a second time.
                _logger.LogDebug(
                    "[Orchestrator] Batch for {Name} is {Chars} chars (~{ApproxKB:F1} KB in memory)",
                    name, batchJson.Length, batchJson.Length * 2 / 1024.0);

                tasks = ParseTasksFromJson(batchJson, name);
            }

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
            // Seed the durable outstanding-task count alongside the tasks themselves, so completion
            // is answerable from storage instead of by walking this run graph.
            await _store.InitRemainingAsync(name, tasks.Count, ct);
            // IsNullOrEmpty, not != null: PowerShell marshals a $null argument to an empty string, so a
            // run with no post-execution arrives here with "" and a null check logs the meaningless
            // "(PostExec: Push-)". Everything that acts on this field already uses IsNullOrEmpty —
            // only the log disagreed.
            var postExecSuffix = !string.IsNullOrEmpty(postExecFunctionName)
                ? $" (PostExec: Push-{postExecFunctionName})"
                : "";
            _logger.LogInformation(
                "[Orchestrator] Run {Name} created from batch: {Count} tasks P{Priority}{PostExec}",
                name, tasks.Count, priority, postExecSuffix);
            await DispatchPendingTasksAsync(run, taskPath, priority, ct);
        }
        finally
        {
            _activePlanners.TryRemove(name, out _);
        }
    }

    private async Task DispatchPendingTasksAsync(OrchestratorRun run, string taskPath, int priority, CancellationToken ct)
    {
        _activeRuns.TryAdd(run.Name, run);
        // Registered before anything is enqueued — the resolver reads it on the dispatch side.
        _taskScriptPaths[run.Name] = taskPath;

        // This run is live again, so it is allowed to finalize again. Matters for the stable run names
        // (CIPPDBCacheOrchestrator, ProcessDeltaQueries) that recur within one process lifetime — without
        // this, the finalize claim from their previous outing would strand the next one forever.
        _finalizingRuns.TryRemove(run.Name, out _);

        // Start periodic status timer (every 60s) for this run
        if (!_runStatusTimers.ContainsKey(run.Name))
        {
            // CheckRunCompletion is re-run here on purpose. It is normally driven by task transitions,
            // but a run whose finalize was deferred because storage still showed work outstanding has no
            // transitions left to retrigger it - without this periodic re-check that deferral would be
            // permanent, which is a worse failure than the premature finalize it exists to prevent.
            var timer = new Timer(_ =>
                {
                    // A System.Threading.Timer callback that throws crashes the process. This periodic
                    // maintenance tick must never take the host down on a transient error — a dependency
                    // disposed during shutdown, a race on run state — so it logs and waits for the next tick.
                    try
                    {
                        LogRunStatus(run);
                        RedrivePendingTasks(run);
                        lock (_lock) { CheckRunCompletion(run); }
                    }
                    catch (Exception ex)
                    {
                        _logger?.LogWarning(ex, "[Scheduler] Run status tick failed for {Name}", run?.Name);
                    }
                },
                null, TimeSpan.FromSeconds(60), TimeSpan.FromSeconds(60));
            if (!_runStatusTimers.TryAdd(run.Name, timer))
            {
                // Lost the ContainsKey→TryAdd race (concurrent dispatch of the same run — startup
                // resume vs a scheduler tick). An active periodic Timer is rooted by the runtime's
                // timer queue, so an undisposed loser would fire — and pin this run graph through its
                // closure — for the process lifetime.
                timer.Dispose();
            }
        }

        var pending = run.Tasks.Where(t => t.Status == "Pending").ToList();

        // Skip anything that already has a queue row.
        //
        // A queue RowKey is BuildRowKey(queuedUtc, run, task), so it is idempotent only for a GIVEN
        // timestamp — re-dispatching the same task later writes a SECOND row rather than updating the
        // first. That is exactly what crash recovery does: ResumeInterruptedRunsAsync flips interrupted
        // tasks back to Pending and calls this, while every pre-crash row for those tasks is still in the
        // queue. Measured on a killed 140-task fanout: 102 tasks re-dispatched on top of 102 survivors.
        //
        // Most duplicates are harmless — the second row resolves to a task that has since finished and is
        // dropped as a stale descriptor. But if both rows are claimed while the task is still RUNNING,
        // the resolver's terminal-status guard does not apply and the task runs twice. That happened:
        // Intune_dev.mspadvisors.com was claimed again 5 minutes into its own execution and ran a second
        // time. Not writing the duplicate is the fix; the resolver check below is the backstop.
        //
        // A read of the run's rows costs one table scan per dispatch, against a write per task avoided.
        HashSet<string> alreadyQueued;
        try
        {
            alreadyQueued = await _queue.GetQueuedTaskIdsAsync(run.Name, ct);
        }
        catch (Exception ex)
        {
            // Enqueuing a duplicate is recoverable; enqueuing nothing strands the run. Prefer the former.
            _logger.LogWarning(ex,
                "[Scheduler] Could not read existing queue rows for {Run} — dispatching without de-duplication",
                run.Name);
            alreadyQueued = [];
        }

        var toQueue = pending.Where(t => !alreadyQueued.Contains(t.Id)).ToList();

        // One batched write per priority bucket rather than one per task. The queue is the backlog now;
        // the JobManager only ever sees the batch JobQueuePump claims from it.
        await _queue.EnqueueBatchAsync(run.Name,
            toQueue.Select(t => (t.Id, t.Priority ?? priority)).ToList(),
            DateTime.UtcNow, ct);

        if (toQueue.Count == pending.Count)
        {
            _logger.LogInformation("[Scheduler] Dispatched {Count} tasks for {Name} at P{Priority}",
                toQueue.Count, run.Name, priority);
        }
        else
        {
            _logger.LogInformation(
                "[Scheduler] Dispatched {Count} tasks for {Name} at P{Priority} ({Existing} already queued)",
                toQueue.Count, run.Name, priority, pending.Count - toQueue.Count);
        }
    }

    /// <summary>
    /// Enqueue one task BY IDENTITY. The JobManager holds only (runName, taskId, priority) — no run
    /// graph, no task, no script path, no service reference — and calls back into
    /// <see cref="ResolveTaskWorkAsync"/> at dispatch time to rebuild the work.
    /// </summary>
    /// <summary>
    /// Put one task back on the durable queue. Fire-and-forget because every caller is on a lock or a
    /// timer callback, and a failure is recoverable: the task is still Pending in storage, so the next
    /// re-drive finds it again.
    /// </summary>
    private void RequeueToTable(OrchestratorRun run, OrchestratorTaskItem task)
    {
        var priority = task.Priority ?? run.Priority;
        _ = Task.Run(async () =>
        {
            var key = DeferralKey(run.Name, task.Id);
            try
            {
                await _queue.EnqueueAsync(run.Name, task.Id, priority, DateTime.UtcNow);
                _requeueFailures.TryRemove(key, out _);
            }
            catch (Exception ex)
            {
                // A row storage rejects is rejected identically forever (illegal key remnant,
                // oversized property), and the re-drive resets the deferral counter on every pass —
                // without this cap the retry loop is infinite and the run it belongs to can never
                // finalize. Consecutive failures only: a success above clears the count.
                var failures = _requeueFailures.AddOrUpdate(key, 1, (_, c) => c + 1);
                if (failures >= MaxRequeueFailures)
                {
                    _requeueFailures.TryRemove(key, out _);
                    FailTaskTerminally(run, task,
                        $"Could not re-queue after {failures} consecutive attempts: {ex.Message}");
                    return;
                }
                _logger.LogWarning(ex,
                    "[Scheduler] Could not re-queue {Task} in {Run} (attempt {Count}/{Max}) — the re-drive will retry",
                    task.Id, run.Name, failures, MaxRequeueFailures);
            }
        });
    }

    /// <summary>
    /// Move a task that can never run to Failed and let its run finish without it. The terminal write
    /// flows through the status writer like any other completion, so the remaining counter decrements
    /// and finalize proceeds — the alternative is a Pending task retried for the process lifetime,
    /// pinning the whole run graph with it.
    /// </summary>
    private void FailTaskTerminally(OrchestratorRun run, OrchestratorTaskItem task, string reason)
    {
        lock (_lock)
        {
            if (task.Status is "Completed" or "Failed" or "Cancelled") return;
            task.Status = "Failed";
            task.LastError = reason;
            task.CompletedUtc = DateTime.UtcNow;
            task.Parameters = null!;
            CheckRunCompletion(run);
        }
        PersistTaskAndRunAsync(run, task);
        _logger.LogError("[Scheduler] Task {TaskId} in {Run} permanently failed: {Reason}",
            task.Id, run.Name, reason);
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

            // A run absent from _activeRuns because it FINISHED must not be resurrected. Rehydrating it
            // put a finalized run back into the live graph, where the next completion re-finalized it
            // and re-dispatched its post-execution; and because terminal task writes are coalesced, the
            // rehydrated task could still read Pending and be run a second time. Storage is authoritative
            // here — FinalizeRunAsync flushes the run's terminal status before it removes the run from
            // _activeRuns, so a terminal Status seen here is not a race.
            if (run != null && run.Status is "Completed" or "CompletedWithErrors")
            {
                _logger.LogDebug(
                    "[Orchestrator] Descriptor {Run}/{Task} belongs to a run that already finished ({Status}) — dropping",
                    descriptor.RunName, descriptor.TaskId, run.Status);
                return null;
            }

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

        // Already terminal (e.g. cancelled, or completed by a previous attempt while queued), or already
        // executing.
        //
        // "Running" belongs here as well as the terminal states. A duplicate queue row claimed while its
        // task is mid-flight used to pass this check and start a SECOND copy — seen with a five-minute
        // Intune collection that was re-claimed four minutes in and ran twice. This does not block crash
        // recovery: ResumeInterruptedRunsAsync flips interrupted tasks from Running back to Pending
        // before re-dispatching them, so a task that genuinely needs re-running never reaches here as
        // Running. Within a live process, Running means a worker has it.
        lock (_lock)
        {
            if (task.Status is "Completed" or "Failed" or "Cancelled" or "Running")
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

                        // Sizing the result payload. This string is the whole task result held in one
                        // piece, and BgPoolSize of them can be live at once — each at roughly two bytes
                        // per char since it is UTF-16.
                        //
                        // Measured on a 16-tenant instance across 92 real task results (mailbox and
                        // calendar permission batches, the widest fan-out CIPP has): median 5.9K chars,
                        // p95 42.5K, max 43.3K — 0.08 MB in memory for the largest. Eight of those
                        // concurrently is under 1 MB against a 2398 MB heap cap, so this site is not
                        // where the memory goes; the aggregate built from these at post-execution is
                        // (see DispatchPostExecution). Reported in KB because MB rounds every real
                        // result to 0.0 and hides exactly that conclusion.
                        _logger.LogDebug(
                            "[Scheduler] Task {TaskId} in {Run} returned {Chars} chars (~{ApproxKB:F1} KB in memory)",
                            task.Id, run.Name, output?.Length ?? 0, (output?.Length ?? 0) * 2 / 1024.0);

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

    /// <summary>How many times a task has been deferred, and when it last was. The timestamp is what lets
    /// the re-drive tell an exhausted task that has been sitting for minutes from one that deferred a
    /// moment ago and is still legitimately retrying.</summary>
    private sealed record DeferralState(int Count, DateTime LastUtc);

    /// <summary>Deferrals per task while storage is unable to accept the durable marker. In-memory and
    /// intentionally so — it bounds retries within one process life, nothing more.</summary>
    private readonly ConcurrentDictionary<string, DeferralState> _deferrals = new();

    /// <summary>Cap on in-process retries before a task is left for the next recovery pass to pick up.</summary>
    private const int MaxDeferrals = 3;

    /// <summary>Consecutive finalize checks where storage still reported outstanding work for a run whose
    /// in-memory tasks are all terminal. At <see cref="ReconcileAfterDeferrals"/> the counter is recounted
    /// from the task rows — a lost decrement otherwise defers finalize forever.</summary>
    private readonly ConcurrentDictionary<string, int> _finalizeDeferrals = new();
    private const int ReconcileAfterDeferrals = 3;

    /// <summary>Consecutive re-queue failures per task. Storage rejecting the same entity is not
    /// transient — the write fails identically forever (see <see cref="TableKeys"/>) — so past
    /// <see cref="MaxRequeueFailures"/> the task is failed terminally instead of re-driven again.</summary>
    private readonly ConcurrentDictionary<string, int> _requeueFailures = new();
    private const int MaxRequeueFailures = 5;

    /// <summary>
    /// Re-queue a task whose durable marker could not be written, so it retries once storage recovers
    /// instead of waiting for a restart. Bounded: after <see cref="MaxDeferrals"/> the task is simply
    /// left Pending, which is already the durable state — recovery re-queues it on the next startup.
    /// </summary>
    private static string DeferralKey(string runName, string taskId) => $"{runName}{taskId}";

    private void DeferTask(OrchestratorRun run, OrchestratorTaskItem task, Exception cause)
    {
        var state = _deferrals.AddOrUpdate(DeferralKey(run.Name, task.Id),
            _ => new DeferralState(1, DateTime.UtcNow),
            (_, s) => new DeferralState(s.Count + 1, DateTime.UtcNow));
        var count = state.Count;

        if (count > MaxDeferrals)
        {
            // One exhausted deferral cycle counts as one attempt on the task, mirroring startup
            // recovery's 3-attempts rule. The re-drive resets the deferral counter when it re-queues,
            // so without this the marker-fail → re-queue → marker-fail cycle repeats for the process
            // lifetime and the run never finalizes. Exactly-once per cycle: only the call that
            // crosses the cap increments (a duplicate queue row can push count past it again).
            if (count == MaxDeferrals + 1 && ++task.AttemptCount >= 3)
            {
                FailTaskTerminally(run, task,
                    $"Durable Running marker rejected across {task.AttemptCount} deferral cycles: {cause.Message}");
                return;
            }

            // Left Pending on purpose — storage already says Pending, so nothing is lost. It is no longer
            // terminal though: RedrivePendingTasks picks it up once it has aged, so recovery is not
            // gated on a restart the way it used to be.
            _logger.LogError(cause,
                "[Scheduler] Task {TaskId} in {Run} deferred {Count} times — left Pending for the re-drive",
                task.Id, run.Name, count);
            return;
        }

        _logger.LogWarning(
            "[Scheduler] Task {TaskId} in {Run} could not be marked Running (attempt {Count}/{Max}) — re-queued, slot released",
            task.Id, run.Name, count, MaxDeferrals);

        // Back to the QUEUE, not to memory. The pump drops a claimed row once the JobManager is done with
        // the job, so an in-memory re-queue here would leave the retry with no durable row behind it — and
        // nothing to pick it up again if this instance went away.
        RequeueToTable(run, task);
    }

    /// <summary>
    /// How long a task must have sat Pending-and-unowned before the re-drive claims it. Long enough that a
    /// task mid-deferral (each attempt can take up to the barrier timeout) is not stolen out from under the
    /// attempt already in progress.
    /// </summary>
    private static readonly TimeSpan RedriveAge = TimeSpan.FromMinutes(5);

    /// <summary>
    /// Re-queue tasks that are Pending in memory but that nothing owns — no queued job, no running job.
    ///
    /// This is the safety net for the state a deferral leaves behind. A task whose durable "Running" marker
    /// could not be written is rolled back to Pending and retried, but only <see cref="MaxDeferrals"/>
    /// times; after that it used to sit Pending with nothing to pick it up, because the only other retry
    /// path was startup recovery. A healthy instance never restarts, so in production that meant 658 tasks
    /// pending and 0 running for three days, with "Run X already active, skipping" preventing a fresh run
    /// from doing the work instead.
    ///
    /// Ownership is decided by <see cref="JobManager.IsQueuedOrRunning"/> rather than by a timestamp on the
    /// task, so a task queued normally is never double-dispatched. The age gate only applies to tasks with
    /// a deferral history — anything Pending and unowned with no deferral record was lost some other way
    /// and there is nothing to wait for.
    /// </summary>
    private void RedrivePendingTasks(OrchestratorRun run) => _ = RedrivePendingTasksAsync(run);

    /// <summary>
    /// Re-queue tasks whose durable queue row went missing — a task Pending forever with nothing left to
    /// dispatch it. Runs off the 60s status timer.
    ///
    /// "Orphaned" has to mean "storage has no row for it". It used to mean "the JobManager does not have
    /// it queued or running", which was true in the world where dispatch enqueued every task into the
    /// JobManager immediately. Under the pump that is simply what a BACKLOG looks like: the pump holds a
    /// worker-pool-sized buffer and leaves the rest in storage, so a 124-task run against eight workers
    /// has most of its tasks Pending and absent from the JobManager for minutes.
    ///
    /// The consequence was severe and silent. Every 60 seconds this re-queued the entire un-started
    /// backlog — measured live at 92, then 60, 60, 52, 44, 36 tasks on consecutive ticks — and because
    /// RequeueToTable stamps UtcNow into the RowKey, each pass created an ADDITIONAL row for the same
    /// task instead of updating the existing one. Every copy was independently claimable, so tasks ran
    /// once per copy: one Intune collection executed six times, from six rows exactly 60s apart.
    /// </summary>
    private async Task RedrivePendingTasksAsync(OrchestratorRun run)
    {
        var now = DateTime.UtcNow;
        List<OrchestratorTaskItem> candidates;

        lock (_lock)
        {
            candidates = run.Tasks
                .Where(t => t.Status == "Pending")
                .Where(t => !_jobManager.IsQueuedOrRunning($"{run.Name}-{t.Id}"))
                .Where(t => !_deferrals.TryGetValue(DeferralKey(run.Name, t.Id), out var s)
                            || now - s.LastUtc >= RedriveAge)
                .ToList();
        }

        if (candidates.Count == 0) return;

        // Storage decides. A Pending task that still has a row is waiting its turn, not orphaned.
        HashSet<string> stillQueued;
        try
        {
            stillQueued = await _queue.GetQueuedTaskIdsAsync(run.Name);
        }
        catch (Exception ex)
        {
            // Without this answer every candidate looks orphaned, which is the failure being fixed.
            // Skip this tick; the timer comes back in 60s.
            _logger.LogWarning(ex, "[Scheduler] Could not read queued tasks for {Run} — skipping re-drive", run.Name);
            return;
        }

        var orphaned = candidates.Where(t => !stillQueued.Contains(t.Id)).ToList();
        if (orphaned.Count == 0) return;

        foreach (var task in orphaned)
        {
            // Clear the exhausted counter, or DeferTask would abandon it again on its first attempt.
            _deferrals.TryRemove(DeferralKey(run.Name, task.Id), out _);
            RequeueToTable(run, task);
        }

        _logger.LogWarning(
            "[Scheduler] Re-drove {Count} orphaned Pending task(s) in {Run} — no queue row and not queued or running",
            orphaned.Count, run.Name);
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
                try
                {
                    // The in-memory graph proposes, storage disposes. Finalizing is irreversible - it
                    // writes the aggregate and cleans the run up - so it must not run while storage still
                    // shows work outstanding, which is exactly the case when terminal writes have not yet
                    // flushed. A null count means the run predates the counter and cannot veto anything.
                    var remaining = await _store.GetRemainingAsync(run.Name);
                    if (remaining is > 0)
                    {
                        // A counter that keeps contradicting a fully-terminal graph is drifted, not
                        // busy — a decrement that exhausted its retries is never re-applied, and
                        // without a recount this deferral repeats on every 60s tick for the process
                        // lifetime, pinning the run graph with it. Give in-flight terminal writes a
                        // few checks to land, then recount the partition the counter summarizes.
                        var misses = _finalizeDeferrals.AddOrUpdate(run.Name, 1, (_, c) => c + 1);
                        if (misses >= ReconcileAfterDeferrals)
                        {
                            _finalizeDeferrals.TryRemove(run.Name, out _);
                            if (await _store.ReconcileRemainingAsync(run.Name) is 0)
                            {
                                await FinalizeRunAsync(run);
                                return;
                            }
                        }
                        _logger.LogInformation(
                            "[Scheduler] Run {Name} complete in memory but storage shows {Remaining} outstanding - deferring finalize",
                            run.Name, remaining);
                        return;
                    }

                    _finalizeDeferrals.TryRemove(run.Name, out _);
                    await FinalizeRunAsync(run);
                }
                catch (Exception ex) { _logger.LogError(ex, "[Scheduler] FinalizeRun failed for {Name}", run.Name); }
            });
        }
    }

    /// <summary>
    /// Register a child run under a parent at ENQUEUE time — while the parent task's script is
    /// still executing, so the parent cannot pass its completion check before the link exists. The
    /// parent will not finalize while any child is pending dispatch, active, or recovering. Only
    /// registers if the parent is still active; returns whether a pending gate was taken (the
    /// bridge releases exactly what was taken via <see cref="ReleasePendingChildRun"/>).
    /// </summary>
    internal bool TryRegisterPendingChildRun(string parentRunName, string childRunName)
    {
        // A run re-queued from inside its own context arrives with itself as parent — the
        // recurring-run pattern, or a duplicate enqueue of an already-active run. Linking it would
        // deadlock finalization: the run stays in _activeRuns until it finalizes, so
        // AllChildRunsComplete would wait on the run itself forever. Observed live as runs stuck
        // "Running" for days with every task terminal and Remaining=0.
        if (parentRunName == childRunName)
            return false;

        if (!_activeRuns.ContainsKey(parentRunName))
            return false; // Parent no longer active (e.g. queued from PostExec context)

        // Gate before link: the moment the link is visible to AllChildRunsComplete the pending
        // mark must already hold, or a completion check could slip between the two writes.
        _pendingChildRuns.AddOrUpdate(childRunName, 1, (_, n) => n + 1);
        _childRuns.GetOrAdd(parentRunName, _ => new ConcurrentBag<string>()).Add(childRunName);
        _logger.LogInformation("[Orchestrator] Registered child run {Child} under parent {Parent}",
            childRunName, parentRunName);
        return true;
    }

    /// <summary>
    /// Lift the enqueue-time gate for ONE queued entry of this child. Called by the bridge after
    /// the start attempt finishes, whatever the outcome: a started child is in _activeRuns by then
    /// (which takes over blocking the parent), and one that failed to start must stop blocking — a
    /// leaked gate would defer the parent's finalize for the process lifetime, re-checked every
    /// 60s. Counted rather than boolean so two queued entries under the same child name cannot
    /// release each other's gate.
    /// </summary>
    internal void ReleasePendingChildRun(string childRunName)
    {
        while (_pendingChildRuns.TryGetValue(childRunName, out var n))
        {
            if (n <= 1)
            {
                if (_pendingChildRuns.TryRemove(new KeyValuePair<string, int>(childRunName, n)))
                    return;
            }
            else if (_pendingChildRuns.TryUpdate(childRunName, n - 1, n))
            {
                return;
            }
        }
    }

    private bool AllChildRunsComplete(string runName)
    {
        if (!_childRuns.TryGetValue(runName, out var children))
            return true;
        // A run is never its own blocker. TryRegisterPendingChildRun refuses self-links, but ones
        // registered before that guard existed can still be sitting in the bag of a long-lived
        // process.
        return !children.Any(childName =>
            childName != runName &&
            (_pendingChildRuns.ContainsKey(childName) ||
             _activeRuns.ContainsKey(childName) ||
             _recoveringChildren.ContainsKey(childName)));
    }

    /// <summary>
    /// Declare a run finished: write its terminal status, drop it from the live graph, and hand off to
    /// post-execution. Runs at most once per run — see the claim below.
    /// </summary>
    private async Task FinalizeRunAsync(OrchestratorRun run)
    {
        // Finalize once. This is not an idempotent method: it re-arms PostExecStatus and dispatches the
        // post-execution again, so entering it twice runs the run's aggregation twice. Observed live
        // before this guard: one 13-task run finalized 7 times and dispatched Push-StoreMailboxRules 7
        // times. Idempotent consumers hid it; Push-ScheduledTaskPostExecution did not, because it
        // advances a recurring task by one interval per invocation.
        //
        // The claim lives here rather than at the call sites because there are four of them — normal
        // completion, two resume paths, and cancellation — and cancellation can race a completion.
        // DispatchPendingTasksAsync releases it when a run becomes live again, so a recurring run name
        // can finalize on its next outing.
        if (!_finalizingRuns.TryAdd(run.Name, true))
        {
            _logger.LogDebug("[Scheduler] Run {Name} has already been finalized — ignoring duplicate", run.Name);
            return;
        }

        try
        {
            await FinalizeRunCoreAsync(run);
        }
        catch
        {
            // Still needs finalizing, so it must stay claimable — the 60s status timer retriggers it.
            _finalizingRuns.TryRemove(run.Name, out _);
            throw;
        }
    }

    private async Task FinalizeRunCoreAsync(OrchestratorRun run)
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
        _finalizeDeferrals.TryRemove(run.Name, out _);
        // Deferral and re-queue tracking is keyed per task and nothing else removes entries for tasks
        // that ended without passing through their happy-path cleanup — without this sweep the residue
        // of every run that ever deferred outlives the run.
        foreach (var t in run.Tasks)
        {
            var key = DeferralKey(run.Name, t.Id);
            _deferrals.TryRemove(key, out _);
            _requeueFailures.TryRemove(key, out _);
        }

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

        // Anything of this run's still queued is now moot; leaving rows behind has the pump claim work
        // for a run that is already finished. That applies to EVERY finalized run — this used to sit in
        // the else below, so a run WITH post-execution kept its queue rows from finalize until
        // post-execution succeeded, and the pump spent that window re-claiming them. Observed live: one
        // 13-task run finalized 7 times, dispatched its Push-* aggregation 7 times, and re-ran
        // individual tasks up to 4 times each. The durable queue only ever carries TASKS — the
        // post-execution job is enqueued in-memory on the JobManager and, after a crash, is re-derived
        // from PostExecStatus — so dropping these rows here cannot cost the post-execution its retry.
        _ = _queue.RemoveRunAsync(run.Name);

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
            // Post-exec commonly starts follow-up runs (baseline → cache refresh); they should land
            // at this run's priority, not the enqueue default.
            inheritPriority: run.Priority,
            runName: run.Name,
            work: async (jobCt) =>
            {
                // Mark PostExec as Running, and count the attempt. Incremented BEFORE the work so a
                // crash mid-post-execution still burns an attempt — otherwise a post-execution that
                // kills the host would be retried forever, which is precisely what the bound is for.
                run.PostExecStatus = "Running";
                run.PostExecAttemptCount++;
                await _store.UpsertRunAsync(run);

                // Stream results to a temp file rather than building the aggregate in memory. For large
                // runs (738+ tasks) that aggregate is 50-150 MB, and StreamResultsToJsonLinesAsync holds
                // one chunk at a time rather than the whole entity set (it used to buffer every result
                // row into a dictionary before writing a byte, so "streaming" still peaked at the full
                // payload in UTF-16 — roughly 2x the stored size — before this ran).
                //
                // The path — not the content — is what goes to PowerShell. This used to read the file
                // back with File.ReadAllTextAsync and pass it as a ResultsJson string, which put the
                // whole aggregate on the Large Object Heap and had PowerShell copy it a second time on
                // ConvertFrom-Json. Handing over the path instead lets Invoke-CraftPostExecution walk
                // the file one line at a time, so neither copy is ever made. The file is JSON Lines for
                // exactly that reason; see StreamResultsToJsonLinesAsync.
                //
                // Consequence for the file's lifetime: it must now survive until PowerShell has read
                // it, so it is deleted in the finally below rather than immediately after streaming.
                var tempFile = Path.Combine(Path.GetTempPath(), $"craft-postexec-{Guid.NewGuid():N}.jsonl");
                try
                {
                    var resultCount = await _store.StreamResultsToJsonLinesAsync(run.Name, tempFile, jobCt);
                    var fileSize = new FileInfo(tempFile).Length;
                    var fileSizeMB = fileSize / (1024.0 * 1024.0);
                    _logger.LogInformation(
                        "[Orchestrator] PostExec results for {Name}: {Count} results, {SizeMB:F1}MB streamed to temp file {Memory}",
                        run.Name, resultCount, fileSizeMB, BackgroundTaskLimiter.GetMemorySnapshot());

                    var parameters = new Dictionary<string, object>
                    {
                        { "FunctionName", run.PostExecFunctionName! },
                        { "ResultsPath", tempFile }
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
                    await _queue.RemoveRunAsync(run.Name, jobCt);
                }
                catch (Exception ex)
                {
                    // Mark PostExec as Failed. ResumeInterruptedRunsAsync picks "Failed" back up on the
                    // next startup, up to MaxPostExecAttempts; the run's Results rows stay in storage
                    // until it either succeeds or is abandoned, because they are the retry's input.
                    run.PostExecStatus = "Failed";
                    try { await _store.UpsertRunAsync(run); } catch { /* best effort */ }
                    _logger.LogError(ex, "[Orchestrator] PostExecution Push-{Function} failed for run {Name}",
                        run.PostExecFunctionName, run.Name);
                    throw;
                }
                finally
                {
                    // The only delete. PowerShell reads the file during ExecuteScript above, so it
                    // cannot be freed any earlier — and it must still be freed when that throws.
                    try { if (File.Exists(tempFile)) File.Delete(tempFile); }
                    catch (Exception ex) { _logger.LogDebug(ex, "[Orchestrator] Failed to delete temp file {Path}", tempFile); }
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
                AddTaskFromElement(tasks, usedIds, element);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to parse planner output for run {Name}. Output: {Output}",
                runName, json?.Length > 500 ? json[..500] + "..." : json);
        }

        return tasks;
    }

    /// <summary>
    /// Parse a batch written as JSON Lines — one task object per line — holding only one line at a
    /// time. This is the counterpart to the caller writing the batch a task at a time: between them,
    /// a batch of any size costs one task's worth of string on each side instead of the whole array.
    ///
    /// Task IDs are de-duplicated across the whole file, exactly as the array parser does across the
    /// whole array, so the two forms produce identical task lists for identical input.
    ///
    /// A malformed line costs that task, not the run — the same failure isolation the results path
    /// gets. The array form cannot do this: one bad element fails the whole document.
    /// </summary>
    private List<OrchestratorTaskItem> ParseTasksFromJsonLinesFile(string path, string runName)
    {
        var tasks = new List<OrchestratorTaskItem>();
        var usedIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var lineNumber = 0;
        var failed = 0;

        try
        {
            // ReadLines is lazy — the file is never read into memory as a whole.
            foreach (var line in File.ReadLines(path))
            {
                lineNumber++;
                if (string.IsNullOrWhiteSpace(line)) continue;

                try
                {
                    using var doc = JsonDocument.Parse(line);
                    AddTaskFromElement(tasks, usedIds, doc.RootElement);
                }
                catch (Exception ex)
                {
                    failed++;
                    if (failed <= 3)
                        _logger.LogWarning(ex, "[Orchestrator] Batch line {Line} for run {Name} is not valid JSON",
                            lineNumber, runName);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[Orchestrator] Failed to read batch file {Path} for run {Name}", path, runName);
        }

        if (failed > 0)
            _logger.LogWarning("[Orchestrator] {Failed} of {Total} batch lines for run {Name} could not be parsed",
                failed, lineNumber, runName);

        return tasks;
    }

    /// <summary>
    /// Convert one batch element into a task and append it, deriving the task ID from whichever
    /// distinguishing properties the element carries. <paramref name="usedIds"/> is threaded through
    /// by the caller so IDs stay unique across the whole batch however it was delivered.
    /// </summary>
    // internal rather than private so the id it mints — which becomes a RowKey — is directly testable.
    internal static void AddTaskFromElement(List<OrchestratorTaskItem> tasks, HashSet<string> usedIds,
        JsonElement element)
    {
        // Skip null or non-object elements (planner returned $null for a tenant)
        if (element.ValueKind != JsonValueKind.Object)
            return;

        var parameters = new Dictionary<string, object>();
        string? collectionType = null;
        string? name = null;
        string? tenantFilter = null;
        string? functionName = null;
        string? suiteName = null;
        string? batchNumber = null;
        string? queueName = null;
        string? customerId = null;
        string? standardName = null;
        string? templateId = null;
        string? templateListValue = null;

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
            if (prop.Name.Equals("TenantFilter", StringComparison.OrdinalIgnoreCase)
                && prop.Value.ValueKind == JsonValueKind.String)
                tenantFilter = prop.Value.GetString();
            if (prop.Name.Equals("FunctionName", StringComparison.OrdinalIgnoreCase))
                functionName = prop.Value.GetString();
            if (prop.Name.Equals("SuiteName", StringComparison.OrdinalIgnoreCase))
                suiteName = prop.Value.GetString();
            if (prop.Name.Equals("Standard", StringComparison.OrdinalIgnoreCase)
                && prop.Value.ValueKind == JsonValueKind.String)
                standardName = prop.Value.GetString();
            if (prop.Name.Equals("TemplateId", StringComparison.OrdinalIgnoreCase)
                && prop.Value.ValueKind == JsonValueKind.String)
                templateId = prop.Value.GetString();

            // Template-backed standards (Intune / Conditional Access) expand one standards
            // template into several items that all carry the same TemplateId — only
            // Settings.TemplateList.value separates them.
            if (prop.Name.Equals("Settings", StringComparison.OrdinalIgnoreCase)
                && prop.Value.ValueKind == JsonValueKind.Object
                && prop.Value.TryGetProperty("TemplateList", out var tmplList)
                && tmplList.ValueKind == JsonValueKind.Object
                && tmplList.TryGetProperty("value", out var tmplValue)
                && tmplValue.ValueKind == JsonValueKind.String)
            {
                templateListValue = tmplValue.GetString();
            }
            if (prop.Name.Equals("BatchNumber", StringComparison.OrdinalIgnoreCase))
                batchNumber = prop.Value.ToString();
            if (prop.Name.Equals("QueueName", StringComparison.OrdinalIgnoreCase)
                && prop.Value.ValueKind == JsonValueKind.String)
                queueName = prop.Value.GetString();
            if (prop.Name.Equals("customerId", StringComparison.OrdinalIgnoreCase)
                && prop.Value.ValueKind == JsonValueKind.String)
                customerId = prop.Value.GetString();

            // Tenant is either a plain domain string (e.g. standards batch items) or a
            // nested tenant object (e.g. audit log batch items). TenantFilter still wins.
            if (tenantFilter == null && prop.Name.Equals("Tenant", StringComparison.OrdinalIgnoreCase))
            {
                if (prop.Value.ValueKind == JsonValueKind.String)
                {
                    tenantFilter = prop.Value.GetString();
                }
                else if (prop.Value.ValueKind == JsonValueKind.Object
                    && prop.Value.TryGetProperty("defaultDomainName", out var ddn)
                    && ddn.ValueKind == JsonValueKind.String)
                {
                    tenantFilter = ddn.GetString();
                }
            }
        }

        // Build a unique task ID from available distinguishing properties. Standards batch
        // items all share FunctionName = 'CIPPStandard', so fold in the Standard name.
        var label = collectionType ?? suiteName ?? name
            ?? (functionName != null && standardName != null
                ? $"{functionName}_{standardName}"
                : functionName)
            ?? standardName ?? "unknown";

        // Standards items sharing a Standard name are separated by their template identity.
        // Mirrors the API key CIPP itself uses for rerun detection (Push-CIPPStandard).
        if (standardName != null)
        {
            if (templateId != null) label = $"{label}_{templateId}";
            if (templateListValue != null) label = $"{label}_{templateListValue}";
        }

        var tenant = tenantFilter ?? queueName ?? customerId ?? "unknown";
        var taskId = batchNumber != null ? $"{label}_{tenant}_b{batchNumber}" : $"{label}_{tenant}";

        // The id becomes a RowKey in three tables — the job queue, the tasks table and the results
        // table — so it has to be a legal key before anything downstream tries to write it. Batch items
        // are labelled from caller-supplied names, and a name is free to contain a character the backend
        // refuses: a CIPP template-library task is named after its GitHub repo ("CIPP Template
        // Owner/Repo"), so its id carries a '/'. Enqueue then 400s identically on every attempt, the
        // task never leaves Pending, and the orphan re-drive retries it for the life of the process.
        //
        // Sanitize BEFORE the uniqueness check below, not after: folding is lossy, so two ids can
        // collapse onto one, and this is the check that already knows how to separate them.
        taskId = TableKeys.Sanitize(taskId);

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

        // Persist cancelled task states through the status-guarded cancel, not a plain upsert. Two
        // things ride on that. Cancelled is terminal, so the write must decrement the run counter —
        // skip it and CheckRunCompletion's finalize veto reads "{cancelled count} still outstanding"
        // forever once the running tasks drain. And the write must only land while storage still shows
        // Pending: dispatch can move a task to Running between our read above and this write, and
        // clobbering that would have the task's real completion decrement a second time. A task that
        // moved on is un-cancelled in our copy and left to finish.
        foreach (var t in tasksToUpdate)
        {
            var result = await _store.CancelPendingTaskAsync(run.Name, t);
            if (result.Cancelled) continue;

            cancelled--;
            lock (_lock)
            {
                t.Status = result.CurrentStatus ?? "Running";
                t.LastError = null;
                t.CompletedUtc = null;
            }
        }

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
        await _queue.InitializeAsync();
        return await _store.GetRunAsync(name);
    }

    /// <summary>
    /// List all known run names from table storage.
    /// </summary>
    public async Task<List<string>> ListRunsAsync()
    {
        await _store.InitializeAsync();
        await _queue.InitializeAsync();
        return await _store.ListRunsAsync();
    }
}

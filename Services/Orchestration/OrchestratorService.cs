using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Serialization;
using Craft.Configuration;
using Craft.PowerShellHost;
using Craft.Services;
using Craft.Storage;

namespace Craft.Orchestration;

/// <summary>
/// Fan-out/fan-in runs on top of <see cref="WorkStore"/>. A run is created durably, its tasks are claimed by
/// <see cref="WorkPump"/> and executed here, and each finish is one transaction that also advances the run's
/// counts; the finish that completes the tasks queues the run's PostExecution as one more task. Storage holds
/// all of it, so there is nothing to recover after a restart: unfinished claims lapse and are taken again.
///
/// This process keeps only what is in flight: the pump's claimed buffer and the jobs running from it.
/// </summary>
public class OrchestratorService : IJobDescriptorStateWriter
{
    internal readonly ILogger<OrchestratorService> _logger;
    private readonly PowerShellRunnerService _psRunner;
    private readonly BackgroundTaskLimiter _limiter;
    private readonly JobManager _jobManager;
    private readonly WorkStore _store;
    private readonly ResultStore _results;
    private readonly CraftSettings _settings;
    private readonly FinishBatcher _finisher;
    private readonly ConcurrentDictionary<string, bool> _activePlanners = new();
    private readonly ConcurrentDictionary<string, string?> _scripts = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, (int C, int R, int P, DateTime LoggedUtc)> _lastStatusLog = new();

    /// <summary>Identifies this process's claims, and is what a lease is checked against.</summary>
    public string Owner { get; }

    /// <summary>How long a claim is held before anyone may take it back. Longer than any task may run.</summary>
    public TimeSpan Lease { get; }

    private static readonly TimeSpan StatusHeartbeat = TimeSpan.FromMinutes(10);

    private static readonly JsonSerializerOptions s_jsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    /// <summary>Kept for callers that read it; there is no re-drive any more.</summary>
    public static long RedriveStorageReads => 0;

    /// <summary>Completes once startup has initialized storage; <see cref="WorkPump"/> claims nothing before it.</summary>
    public Task RecoveryDone => _recoveryDone.Task;
    private readonly TaskCompletionSource _recoveryDone = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public void MarkRecoveryDone() => _recoveryDone.TrySetResult();

    public OrchestratorService(
        ILogger<OrchestratorService> logger,
        PowerShellRunnerService psRunner,
        BackgroundTaskLimiter limiter,
        JobManager jobManager,
        WorkStore store,
        ResultStore results,
        IConfiguration configuration,
        CraftSettings settings)
    {
        _logger = logger;
        _psRunner = psRunner;
        _limiter = limiter;
        _jobManager = jobManager;
        _store = store;
        _results = results;
        _settings = settings;
        Owner = Environment.GetEnvironmentVariable("HOSTNAME") ?? $"instance-{Environment.ProcessId}";
        Lease = TimeSpan.FromSeconds(Math.Max(60, configuration.GetValue("JobQueueLeaseSeconds", 1800)));
        _finisher = new FinishBatcher(store, logger);
        _store.AfterFinish = AfterFinishAsync;

        _jobManager.SetWorkResolver(ResolveTaskWorkAsync);
        _jobManager.SetDescriptorStateWriter(this);
    }

    // ── starting runs ──

    /// <summary>
    /// Start a planner-based run: the planner script prints the task list. Skipped while a run of this name is
    /// still going, so a timer that fires again does not stack outings.
    /// </summary>
    public async Task StartOrResumeRun(string name, string plannerPath, string taskPath, int priority, CancellationToken ct)
    {
        if (!_activePlanners.TryAdd(name, true))
        {
            _logger.LogInformation("[Scheduler] Run {Name} already in progress, skipping", name);
            return;
        }

        try
        {
            if (await IsActiveAsync(name, ct))
            {
                _logger.LogInformation("[Scheduler] Run {Name} tasks already dispatched, skipping", name);
                return;
            }

            _logger.LogInformation("[Scheduler] Starting orchestrator: {Name}", name);
            string output;
            try
            {
                output = await _limiter.RunAsync(() => _psRunner.ExecuteScriptWithOutput(plannerPath), $"Planner-{name}", ct);
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

            await CreateAsync(name, tasks, priority, Path.GetFileNameWithoutExtension(taskPath), null, null, null, null, null,
                false, ct);
        }
        finally
        {
            _activePlanners.TryRemove(name, out _);
        }
    }

    /// <summary>Start a planner run by command name: planner <c>Start-X</c>, tasks <c>Invoke-XTask</c>.</summary>
    public async Task StartPlannerRunAsync(string command, int priority, CancellationToken ct)
    {
        var plannerFunc = _psRunner.FindScript(command);
        var baseName = command.StartsWith("Start-", StringComparison.OrdinalIgnoreCase) ? command[6..] : command;
        var taskFunc = _psRunner.FindScript($"Invoke-{baseName}Task");

        if (plannerFunc != null && taskFunc != null)
        {
            _logger.LogInformation("[Orchestrator] Planner run queued: {Command} P{Priority}", command, priority);
            await StartOrResumeRun(command, plannerFunc, taskFunc, priority, ct);
        }
        else
        {
            _logger.LogWarning("[Orchestrator] Scripts not found for planner run: {Command} planner={Planner} task={Task}",
                command, command, $"Invoke-{baseName}Task");
        }
    }

    /// <summary>
    /// Start a run from a pre-built batch (OrchestratorBridge). The batch is a JSON Lines file
    /// (<paramref name="batchFilePath"/>, deleted on every path) or a JSON array string. Returns whether a run
    /// was created: false when the batch is empty or a run of this name is still going.
    /// </summary>
    public async Task<bool> StartFromBatchAsync(string name, string batchJson, int priority,
        string? postExecFunctionName, string? postExecParametersJson, CancellationToken ct,
        string? parentRunName = null, string? reference = null, string? batchFilePath = null,
        bool sequential = false, string? parentRunKey = null, string? childKey = null)
    {
        try
        {
            name = TableKeys.Sanitize(name);
            if (!_activePlanners.TryAdd(name, true) || await IsActiveAsync(name, ct))
            {
                _activePlanners.TryRemove(name, out _);
                _logger.LogInformation("[Orchestrator] Run {Name} already active, skipping", name);
                return false;
            }

            try
            {
                var tasks = !string.IsNullOrEmpty(batchFilePath)
                    ? ParseTasksFromJsonLinesFile(batchFilePath, name)
                    : ParseTasksFromJson(batchJson, name);
                if (tasks.Count == 0)
                {
                    _logger.LogWarning("[Orchestrator] Batch for {Name} produced 0 tasks", name);
                    return false;
                }

                var genericTaskFunc = _settings.Orchestrator.GenericTaskFunction;
                if (string.IsNullOrEmpty(genericTaskFunc) || Script(genericTaskFunc) == null)
                {
                    _logger.LogError("[Orchestrator] Cannot start {Name}: task function {Func} not found", name, genericTaskFunc);
                    return false;
                }

                if (string.IsNullOrEmpty(postExecFunctionName)) postExecFunctionName = null;
                if (string.IsNullOrEmpty(postExecParametersJson)) postExecParametersJson = null;
                await CreateAsync(name, tasks, priority, genericTaskFunc, postExecFunctionName, postExecParametersJson,
                    reference, parentRunKey, childKey, sequential, ct);
                return true;
            }
            finally
            {
                _activePlanners.TryRemove(name, out _);
            }
        }
        finally
        {
            if (!string.IsNullOrEmpty(batchFilePath))
            {
                try { if (File.Exists(batchFilePath)) File.Delete(batchFilePath); }
                catch (Exception ex) { _logger.LogDebug(ex, "[Orchestrator] Failed to delete batch file {Path}", batchFilePath); }
            }
        }
    }

    private async Task<bool> IsActiveAsync(string name, CancellationToken ct) =>
        await _store.GetRunByNameAsync(name, ct) is { IsFinished: false };

    private async Task CreateAsync(string name, List<OrchestratorTaskItem> tasks, int priority, string taskScriptName,
        string? postExecFunctionName, string? postExecParametersJson, string? reference, string? parentRunKey,
        string? childKey, bool sequential, CancellationToken ct)
    {
        var started = DateTime.UtcNow;
        var header = new RunHeader
        {
            RunKey = WorkStore.RunKeyFor(name, started),
            Name = name,
            Priority = Math.Clamp(priority, 0, 99),
            StartedUtc = started,
            TaskScriptName = taskScriptName,
            PostExecFunctionName = postExecFunctionName,
            PostExecParametersJson = postExecParametersJson,
            Reference = reference,
            ParentRunKey = parentRunKey,
            ParentChildKey = childKey,
            Sequential = sequential,
        };
        await _store.CreateRunAsync(header, tasks.Select(t => new WorkStore.NewTask(t.Id, t.Parameters)).ToList(), ct);
        _logger.LogInformation("[Orchestrator] Run {Name} created with {Count} tasks at P{Priority}{PostExec}{Sequential}",
            name, tasks.Count, header.Priority,
            postExecFunctionName != null ? $" (PostExec: Push-{postExecFunctionName})" : "",
            sequential ? " (sequential)" : "");
    }

    // ── child runs ──

    /// <summary>
    /// Make <paramref name="parentRunName"/> wait for a child that is about to be queued. Called at enqueue time,
    /// inside the parent's task, so the parent cannot finish first. Returns the parent's run key and the
    /// placeholder key the child completes, or null when the parent is not running tasks (a run queued from an
    /// aggregation is not a child) or is the child itself.
    /// </summary>
    internal (string ParentRunKey, string ChildKey)? RegisterPendingChild(string parentRunName, string childRunName)
    {
        if (parentRunName == childRunName) return null;
        return Task.Run(async () =>
        {
            var parent = await _store.GetRunByNameAsync(parentRunName);
            if (parent is not { Phase: RunPhase.Tasks }) return ((string, string)?)null;
            var childKey = $"{childRunName}|{Guid.NewGuid():N}";
            if (!await _store.AddChildAsync(parent.RunKey, childKey)) return null;
            _logger.LogInformation("[Orchestrator] Registered child run {Child} under parent {Parent}", childRunName, parentRunName);
            return (parent.RunKey, childKey);
        }).GetAwaiter().GetResult();
    }

    /// <summary>A registered child that was never created: stop the parent waiting for it.</summary>
    internal async Task AbandonPendingChildAsync(string parentRunKey, string childKey)
    {
        await _store.FinishAsync(parentRunKey, [new WorkStore.Finish(0, "Completed", ChildKey: childKey)], null);
    }

    // ── what happens when tasks finish ──

    private async Task AfterFinishAsync(WorkStore.FinishOutcome outcome)
    {
        var h = outcome.Header;
        if (!outcome.Completed) return;

        _lastStatusLog.TryRemove(h.RunKey, out _);
        var wall = (h.CompletedUtc ?? DateTime.UtcNow) - h.StartedUtc;
        _logger.LogInformation("[Scheduler] Run {Name} finalized: {Status} ({Completed}/{Failed}/{Cancelled}/{Total}) wall={Wall} {Memory}",
            h.Name, h.Status, h.Done - h.Failed - h.Cancelled, h.Failed, h.Cancelled, h.Total,
            wall.TotalSeconds < 60 ? $"{wall.TotalSeconds:F1}s" : $"{wall.TotalMinutes:F1}min",
            BackgroundTaskLimiter.GetMemorySnapshot());

        try { await _results.DeleteRunAsync(h.RunKey); }
        catch (Exception ex) { _logger.LogDebug(ex, "[Orchestrator] Could not drop results of {Run}", h.Name); }

        if (h.ParentRunKey is { } parentKey && h.ParentChildKey is { } childKey)
        {
            await _store.FinishAsync(parentKey,
                [new WorkStore.Finish(0, h.Status == "Completed" ? "Completed" : "Failed", ChildKey: childKey)], null);
        }
    }

    // ── executing a claimed task ──

    private string? Script(string? name) =>
        string.IsNullOrEmpty(name) ? null : _scripts.GetOrAdd(name, n => FindScript(n));

    internal virtual string? FindScript(string name) => _psRunner.FindScript(name);

    /// <summary>Run a script on the pool, or on <paramref name="worker"/> when pinned; returns its output when captured.</summary>
    internal virtual async Task<string> RunScriptAsync(string path, Dictionary<string, object> parameters, bool captureOutput,
        PowerShellWorker? worker = null)
    {
        if (captureOutput) return await _psRunner.ExecuteScriptWithOutput(path, parameters, pinnedWorker: worker);
        await _psRunner.ExecuteScript(path, parameters, pinnedWorker: worker);
        return string.Empty;
    }

    /// <summary>Turn a claimed task into work. Registered on the JobManager; runs on the dispatched job.</summary>
    private async Task<Func<CancellationToken, Task>?> ResolveTaskWorkAsync(JobDescriptor descriptor, CancellationToken ct)
    {
        if (descriptor.RunKey is not { } runKey) return null;
        var header = await _store.GetRunAsync(runKey, ct);
        if (header == null || header.IsFinished) return null;

        if (descriptor.Seq == WorkStore.AggregateSeq) return jobCt => RunPostExecutionAsync(header, descriptor, jobCt);

        var taskPath = Script(header.TaskScriptName);
        if (taskPath == null)
        {
            await FinishAsync(header, descriptor.Seq, "Failed", $"Task script {header.TaskScriptName} not found");
            return null;
        }

        return header.Sequential
            ? BuildSequentialRunWork(header, descriptor, taskPath)
            : jobCt => RunTaskAsync(header, descriptor.Seq, descriptor.TaskId, taskPath, jobCt);
    }

    private Task<WorkStore.FinishOutcome?> FinishAsync(RunHeader h, int seq, string status, string? error = null) =>
        _finisher.FinishAsync(h.RunKey, new WorkStore.Finish(seq, status, error, Owner));

    private async Task RunTaskAsync(RunHeader header, int seq, string taskId, string taskPath, CancellationToken ct)
    {
        if (header.CancelRequested)
        {
            await FinishAsync(header, seq, "Cancelled", "Cancelled by user");
            return;
        }

        var parameters = await _store.GetPayloadAsync(header.RunKey, seq, ct);
        if (parameters == null)
        {
            await FinishAsync(header, seq, "Failed", "The task's payload row is missing");
            return;
        }

        try
        {
            var output = await RunScriptAsync(taskPath, TaskInvocation(parameters), header.HasPostExec);
            if (!string.IsNullOrEmpty(output)) await _results.StoreResultAsync(header.RunKey, taskId, output);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            await _store.ReleaseAsync(header.RunKey, seq, Owner, refundAttempt: true, CancellationToken.None);
            throw;
        }
        catch (Exception ex)
        {
            await FinishAsync(header, seq, "Failed", ex.Message);
            _logger.LogError(ex, "[Scheduler] Task failed: {TaskId}", taskId);
            throw;
        }

        await FinishAsync(header, seq, "Completed");
        _logger.LogDebug("[Scheduler] Task completed: {TaskId}", taskId);
    }

    private async Task RunPostExecutionAsync(RunHeader header, JobDescriptor descriptor, CancellationToken ct)
    {
        var postExecScript = Script(_settings.Orchestrator.PostExecFunction);
        if (postExecScript == null)
        {
            _logger.LogError("[Orchestrator] PostExec function '{Func}' not found, cannot run PostExecution for {Name}",
                _settings.Orchestrator.PostExecFunction, header.Name);
            await FinishAsync(header, WorkStore.AggregateSeq, "Failed", "PostExec function not found");
            return;
        }

        _logger.LogInformation("[Orchestrator] Dispatching PostExecution Push-{Function} for run {Name} {Memory}",
            header.PostExecFunctionName, header.Name, BackgroundTaskLimiter.GetMemorySnapshot());
        var tempFile = Path.Combine(Path.GetTempPath(), $"craft-postexec-{Guid.NewGuid():N}.jsonl");
        try
        {
            var count = await _results.StreamResultsToJsonLinesAsync(header.RunKey, tempFile, ct);
            _logger.LogInformation("[Orchestrator] PostExec results for {Name}: {Count} results, {SizeMB:F1}MB streamed to temp file",
                header.Name, count, new FileInfo(tempFile).Length / (1024.0 * 1024.0));

            var parameters = new Dictionary<string, object>
            {
                ["FunctionName"] = header.PostExecFunctionName!,
                ["ResultsPath"] = tempFile,
            };
            if (await _store.GetPostExecParametersAsync(header.RunKey, ct) is { Length: > 0 } postParameters)
                parameters["ParametersJson"] = postParameters;

            await RunScriptAsync(postExecScript, parameters, captureOutput: false);
            await OrchestratorBridge.DrainPendingAsync();
            _logger.LogInformation("[Orchestrator] PostExecution Push-{Function} completed for run {Name}",
                header.PostExecFunctionName, header.Name);
            await FinishAsync(header, WorkStore.AggregateSeq, "Completed");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            await _store.ReleaseAsync(header.RunKey, WorkStore.AggregateSeq, Owner, refundAttempt: true, CancellationToken.None);
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[Orchestrator] PostExecution Push-{Function} failed for run {Name} (attempt {Attempt})",
                header.PostExecFunctionName, header.Name, descriptor.Attempt);
            if (descriptor.Attempt < Math.Max(1, _settings.Orchestrator.MaxRetries))
                await _store.ReleaseAsync(header.RunKey, WorkStore.AggregateSeq, Owner, refundAttempt: false, CancellationToken.None);
            else
            {
                _logger.LogError("[Scheduler] PostExecution for {Name} failed {Count} times — giving up and cleaning up results",
                    header.Name, descriptor.Attempt);
                await FinishAsync(header, WorkStore.AggregateSeq, "Failed", ex.Message);
            }
            throw;
        }
        finally
        {
            try { if (File.Exists(tempFile)) File.Delete(tempFile); }
            catch (Exception ex) { _logger.LogDebug(ex, "[Orchestrator] Failed to delete temp file {Path}", tempFile); }
        }
    }

    // ── sequential runs ──

    /// <summary>
    /// A sequential run: one driver checks out one worker and runs every step on it in payload order, claiming
    /// each step itself under the run's driver lease so no other worker can take a step meanwhile. A failed
    /// step is recorded and the next one runs. Cancelling the run stops it at the next step.
    /// </summary>
    private Func<CancellationToken, Task> BuildSequentialRunWork(RunHeader header, JobDescriptor first, string taskPath) =>
        async jobCt =>
        {
            PowerShellWorker? worker = null;
            var faulted = false;
            var step = new WorkStore.ClaimedTask(header.RunKey, first.Seq, first.TaskId, first.Attempt);
            try
            {
                worker = CheckoutSequentialWorker(jobCt);
                while (true)
                {
                    var current = await _store.GetRunAsync(header.RunKey, jobCt);
                    if (current == null || current.IsFinished) break;
                    if (current.CancelRequested)
                    {
                        await FinishAsync(header, step.Seq, "Cancelled", "Cancelled by user");
                        await _store.CancelPendingAsync(header.RunKey, jobCt);
                        break;
                    }
                    if (step.Seq == WorkStore.AggregateSeq)
                    {
                        var aggregate = new JobDescriptor(header.Name, step.TaskId, header.Priority) { RunKey = header.RunKey, Seq = step.Seq, Attempt = step.Attempt };
                        await RunPostExecutionAsync(current, aggregate, jobCt);
                        break;
                    }

                    var parameters = await _store.GetPayloadAsync(header.RunKey, step.Seq, jobCt);
                    try
                    {
                        if (parameters == null) throw new InvalidOperationException("The task's payload row is missing");
                        var output = await RunScriptAsync(taskPath, TaskInvocation(parameters), current.HasPostExec, worker);
                        if (current.HasPostExec && !string.IsNullOrEmpty(output))
                            await _results.StoreResultAsync(header.RunKey, step.TaskId, output);
                        await FinishAsync(header, step.Seq, "Completed");
                    }
                    catch (OperationCanceledException) when (jobCt.IsCancellationRequested)
                    {
                        throw;
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "[Scheduler] Sequential task failed: {TaskId} — continuing with the next step", step.TaskId);
                        await FinishAsync(header, step.Seq, "Failed", ex.Message);
                    }

                    if (await _store.ClaimSequentialAsync(header.RunKey, Owner, Lease, continuing: true, jobCt) is not { } next) break;
                    step = next;
                }
            }
            catch (OperationCanceledException) when (jobCt.IsCancellationRequested)
            {
                faulted = true;
                await _store.ReleaseAsync(header.RunKey, step.Seq, Owner, refundAttempt: true, CancellationToken.None);
                throw;
            }
            catch (Exception ex)
            {
                faulted = true;
                _logger.LogError(ex, "[Scheduler] Sequential driver for {Run} failed", header.Name);
                throw;
            }
            finally
            {
                ReclaimSequentialWorker(worker, faulted);
                await _store.ReleaseDriverAsync(header.RunKey, Owner, CancellationToken.None);
            }
        };

    internal virtual PowerShellWorker? CheckoutSequentialWorker(CancellationToken ct) => _psRunner.CheckoutBackgroundWorker(ct);

    internal virtual void ReclaimSequentialWorker(PowerShellWorker? worker, bool faulted)
    {
        if (worker != null) _psRunner.ReclaimBackgroundWorker(worker, faulted: faulted);
    }

    private static Dictionary<string, object> TaskInvocation(Dictionary<string, object> parameters) =>
        new() { ["TaskJson"] = JsonSerializer.Serialize(parameters, s_jsonOptions) };

    // ── operator actions ──

    /// <summary>Cancel a run's pending tasks; running ones finish. Returns whether the run exists and how many were cancelled.</summary>
    public async Task<(bool found, int cancelledCount)> CancelRunAsync(string name)
    {
        var header = await _store.GetRunByNameAsync(TableKeys.Sanitize(name));
        if (header == null || header.IsFinished) return (false, 0);

        await _store.RequestCancelAsync(header.RunKey);
        var (cancelled, _) = await _store.CancelPendingAsync(header.RunKey);
        _logger.LogInformation("[Scheduler] Run {Name} cancelled: {Cancelled} pending tasks cancelled", header.Name, cancelled);
        return (true, cancelled);
    }

    /// <summary>Cancel one task that is still pending in storage. False when it is not pending (or not found).</summary>
    public async Task<bool> TryCancelQueuedTaskAsync(string runName, string taskId)
    {
        var header = await _store.GetRunByNameAsync(runName);
        if (header == null || header.IsFinished) return false;
        var task = (await _store.GetTasksAsync(header.RunKey, 'P')).FirstOrDefault(t => t.TaskId == taskId);
        if (task == null) return false;
        var outcome = await _store.FinishAsync(header.RunKey, [new WorkStore.Finish(task.Seq, "Cancelled", "Cancelled by user")], 'P');
        return outcome?.Applied > 0;
    }

    /// <summary>Move a run to another priority band. Applies to the whole run: its tasks share one queue position.</summary>
    public async Task<bool> ReprioritizeRunAsync(string runName, int priority)
    {
        var header = await _store.GetRunByNameAsync(runName);
        return header is { IsFinished: false } && await _store.SetPriorityAsync(header.RunKey, Math.Clamp(priority, 0, 99));
    }

    /// <summary>Cancel every pending task of every run — the whole backlog. Returns how many were cancelled.</summary>
    public async Task<int> ClearQueueAsync(CancellationToken ct = default)
    {
        var total = 0;
        var entries = new List<WorkStore.ReadyEntry>();
        await foreach (var e in _store.ReadReadyAsync(200, ct)) entries.Add(e);
        foreach (var e in entries)
        {
            await _store.RequestCancelAsync(e.RunKey, ct);
            total += (await _store.CancelPendingAsync(e.RunKey, ct)).Cancelled;
        }
        _logger.LogWarning("[JobQueue] Durable queue cleared — {Count} queued task(s) cancelled", total);
        return total;
    }

    /// <summary>A claimed job reprioritized in the local buffer: it is already claimed, so nothing is stored.</summary>
    public void PriorityChanged(JobDescriptor descriptor, int newPriority) { }

    /// <summary>A claimed job cancelled before it started: record it, so its claim does not lapse and run it.</summary>
    public void Cancelled(JobDescriptor descriptor)
    {
        if (descriptor.RunKey is not { } runKey) return;
        _ = _finisher.FinishAsync(runKey, new WorkStore.Finish(descriptor.Seq, "Cancelled", "Cancelled by user", Owner));
    }

    // ── lookups ──

    public string? GetRunReference(string runName) =>
        Task.Run(() => _store.GetRunByNameAsync(runName)).GetAwaiter().GetResult()?.Reference;

    public string? FindRunByReference(string reference) => Task.Run(async () =>
    {
        await foreach (var e in _store.ReadReadyAsync(200))
            if (string.Equals(e.Reference, reference, StringComparison.OrdinalIgnoreCase)) return e.Name;
        return null;
    }).GetAwaiter().GetResult();

    // ── startup, retention, status lines ──

    /// <summary>
    /// Startup. Storage is the state, so there is nothing to recover: create the tables, drop the previous
    /// design's tables once, and sweep expired runs.
    /// </summary>
    public async Task ResumeInterruptedRunsAsync(CancellationToken ct)
    {
        await _store.InitializeAsync(ct);
        await _store.DropLegacyTablesAsync(ct);
        try { await RunRetentionSweepAsync(ct); }
        catch (Exception ex) { _logger.LogWarning(ex, "[Scheduler] Startup retention sweep failed"); }
    }

    public async Task<int> RunRetentionSweepAsync(CancellationToken ct)
    {
        var removed = await _store.SweepFinishedAsync(TimeSpan.FromHours(Math.Max(1, _settings.Orchestrator.RetentionHours)), ct);
        if (removed > 0) _logger.LogInformation("[OrchestratorStore] Retention sweep removed {Count} finished run(s)", removed);
        return removed;
    }

    public async Task RunRetentionLoopAsync(CancellationToken ct)
    {
        var hours = _settings.Orchestrator.CleanupIntervalHours;
        if (hours <= 0) return;
        using var timer = new PeriodicTimer(TimeSpan.FromHours(hours));
        try
        {
            while (await timer.WaitForNextTickAsync(ct))
            {
                try { await RunRetentionSweepAsync(ct); }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logger.LogWarning(ex, "[Scheduler] Retention sweep failed; next attempt in {Hours}h", hours);
                }
            }
        }
        catch (OperationCanceledException) { }
    }

    /// <summary>
    /// One status line per active run on a change, or every ten minutes when unchanged:
    /// <c>T+{min}min: {done}/{total} done {running} running {pending} pending {failed} failed</c>. Health checks parse it to
    /// spot runs that sit with work pending and nothing running.
    /// </summary>
    public async Task RunStatusSweepLoopAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(Math.Max(1, _settings.Orchestrator.StatusTimerIntervalSeconds)));
        try
        {
            while (await timer.WaitForNextTickAsync(ct))
            {
                try { await LogRunStatusAsync(ct); }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logger.LogWarning(ex, "[Scheduler] Run status sweep failed");
                }
            }
        }
        catch (OperationCanceledException) { }
    }

    private async Task LogRunStatusAsync(CancellationToken ct)
    {
        if (!_logger.IsEnabled(LogLevel.Information)) return;
        var running = _jobManager.GetJobs(status: "Running").Where(j => j.RunName != null)
            .GroupBy(j => j.RunName!).ToDictionary(g => g.Key, g => g.Count());
        var now = DateTime.UtcNow;
        await foreach (var e in _store.ReadReadyAsync(200, ct))
        {
            var r = running.GetValueOrDefault(e.Name);
            var p = Math.Max(0, e.Total - e.Done - r);
            if (_lastStatusLog.TryGetValue(e.RunKey, out var prev) && prev.C == e.Done && prev.R == r && prev.P == p
                && now - prev.LoggedUtc < StatusHeartbeat) continue;
            _lastStatusLog[e.RunKey] = (e.Done, r, p, now);
            _logger.LogInformation(
                "[Scheduler] Run {Name} T+{Elapsed:F1}min: {Completed}/{Total} done {Running} running {Pending} pending {Failed} failed jobs={Active}a/{Queued}q {Memory}",
                e.Name, (now - e.StartedUtc).TotalMinutes, e.Done - e.Failed - e.Cancelled, e.Total, r, p, e.Failed,
                _jobManager.ActiveCount, _jobManager.QueuedCount, BackgroundTaskLimiter.GetMemorySnapshot());
        }
    }

    // ── parsing batches into tasks ──

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
}

using System.Text.Json;

namespace Craft.Services;

/// <summary>
/// Coalescing, batched, durable writer for orchestrator TASK and RUN status transitions. It removes the
/// per-task Azure Table write from the fan-out critical path (that write was the throughput ceiling — see
/// docs/orch-analysis.md) by coalescing many transitions and flushing them in ≤100-entity, byte-budgeted
/// transactions.
///
/// Durability is preserved:
///  - the pre-invoke "Running" marker is written under a synchronous barrier (batched across concurrently
///    starting tasks, but still durable-BEFORE-invoke, so AttemptCount/MaxRetries still bound poison tasks);
///  - <see cref="FlushAsync"/> guarantees all pending terminal states are persisted before a run finalizes;
///  - a final drain runs on shutdown.
///
/// RESULTS are deliberately NOT handled here — <c>OrchestratorTableStore.StoreResultAsync</c> keeps its
/// property-chunking / multi-row large-payload path completely untouched.
/// </summary>
public sealed class OrchestratorStatusWriter : IDisposable
{
    private readonly OrchestratorTableStore _store;
    private readonly ILogger<OrchestratorStatusWriter> _logger;
    private readonly bool _enabled;
    private readonly bool _durableBarrier;
    private readonly int _flushIntervalMs;

    // Match the read path (OrchestratorTableStore serializes/deserializes ParametersJson camelCase).
    private static readonly JsonSerializerOptions s_json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    private readonly object _lock = new();
    private Dictionary<string, TaskStatusWrite> _pendingTasks = new(); // key: runName  taskId (last-wins coalesce)
    private Dictionary<string, OrchestratorRun> _pendingRuns = new();  // key: runName
    private TaskCompletionSource _barrier = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly SemaphoreSlim _signal = new(0, int.MaxValue);
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _drainLoop;

    public bool Enabled => _enabled;

    public OrchestratorStatusWriter(OrchestratorTableStore store, ILogger<OrchestratorStatusWriter> logger, CraftSettings settings)
    {
        _store = store;
        _logger = logger;
        _enabled = settings.Orchestrator.BatchStatusWrites;
        _durableBarrier = settings.Orchestrator.DurableRunningBarrier;
        _flushIntervalMs = Math.Max(5, settings.Orchestrator.StatusFlushIntervalMs);
        _drainLoop = _enabled ? Task.Run(DrainLoopAsync) : Task.CompletedTask;
        _logger.LogInformation("[Orchestrator] StatusWriter: enabled={E} durableBarrier={B} flushMs={F}",
            _enabled, _durableBarrier, _flushIntervalMs);
    }

    private static string Key(string run, string task) => run + "" + task;
    private static TaskStatusWrite Snap(string run, OrchestratorTaskItem t) => new(
        run, t.Id, t.Status, JsonSerializer.Serialize(t.Parameters, s_json), t.AttemptCount, t.LastError, t.CompletedUtc);

    /// <summary>Persist the pre-invoke "Running" marker durably before the task runs. Under the barrier it is
    /// batched with other concurrently-starting tasks (N tasks → ~1 transaction) yet still lands before the
    /// invoke. Disabled → the original per-task awaited write.</summary>
    public async Task MarkRunningAsync(string runName, OrchestratorTaskItem task)
    {
        if (!_enabled) { await _store.UpsertTaskAsync(runName, task); return; }
        if (!_durableBarrier) { QueueTask(runName, task); return; } // eventual mode (weaker poison guarantee)

        Task barrier;
        lock (_lock)
        {
            _pendingTasks[Key(runName, task.Id)] = Snap(runName, task);
            barrier = _barrier.Task;
        }
        _signal.Release();
        await barrier; // completes only after the batch containing this marker is written
    }

    /// <summary>Queue a task's (usually terminal) status — non-blocking, coalesced, flushed by the drain loop.
    /// Disabled → the original fire-and-forget per-task write.</summary>
    public void QueueTask(string runName, OrchestratorTaskItem task)
    {
        if (!_enabled) { _ = _store.UpsertTaskAsync(runName, task); return; }
        lock (_lock) { _pendingTasks[Key(runName, task.Id)] = Snap(runName, task); }
        _signal.Release();
    }

    /// <summary>Queue a run's status — non-blocking, coalesced, flushed by the drain loop.</summary>
    public void QueueRun(OrchestratorRun run)
    {
        if (!_enabled) { _ = _store.UpsertRunAsync(run); return; }
        lock (_lock) { _pendingRuns[run.Name] = run; }
        _signal.Release();
    }

    /// <summary>Flush all currently-pending writes and await their persistence. Call before finalizing a run
    /// so terminal task states + run state are durable before post-execution reads results.</summary>
    public async Task FlushAsync()
    {
        if (!_enabled) return;
        Task barrier;
        lock (_lock) { barrier = _barrier.Task; }
        _signal.Release();
        await barrier;
    }

    private async Task DrainLoopAsync()
    {
        while (!_cts.IsCancellationRequested)
        {
            try { await _signal.WaitAsync(_flushIntervalMs, _cts.Token); }
            catch (OperationCanceledException) { break; }
            await FlushOnceAsync();
        }
        await FlushOnceAsync(); // final drain on shutdown
    }

    private async Task FlushOnceAsync()
    {
        Dictionary<string, TaskStatusWrite> tasks;
        Dictionary<string, OrchestratorRun> runs;
        TaskCompletionSource done;
        lock (_lock)
        {
            done = _barrier;
            _barrier = new(TaskCreationOptions.RunContinuationsAsynchronously);
            if (_pendingTasks.Count == 0 && _pendingRuns.Count == 0)
            {
                // Nothing to flush — still release barrier waiters (e.g. FlushAsync on an already-drained run).
                done.TrySetResult();
                return;
            }
            tasks = _pendingTasks; _pendingTasks = new();
            runs = _pendingRuns; _pendingRuns = new();
        }

        try
        {
            if (tasks.Count > 0)
                await _store.WriteTaskStatusBatchAsync(tasks.Values.ToList());
            foreach (var run in runs.Values)
            {
                try { await _store.UpsertRunAsync(run); }
                catch (Exception ex) { _logger.LogWarning(ex, "[Orchestrator] Run status write failed for {Run}", run.Name); }
            }
            done.TrySetResult();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[Orchestrator] Status flush failed ({Tasks} tasks, {Runs} runs)", tasks.Count, runs.Count);
            done.TrySetException(ex); // barrier waiters observe the failure (task gets marked Failed, as today)
        }
    }

    public void Dispose()
    {
        _cts.Cancel();
        try { _drainLoop.Wait(TimeSpan.FromSeconds(5)); } catch { /* best effort final drain */ }
        _cts.Dispose();
        _signal.Dispose();
    }
}

using System.Text.Json;
using Craft.Configuration;

namespace Craft.Orchestration;

/// <summary>
/// Coalescing, batched, durable writer for orchestrator TASK and RUN status transitions. It removes the
/// per-task Azure Table write from the fan-out critical path (that write was the throughput ceiling — see
/// perf-harness/orch-analysis.md) by coalescing many transitions and flushing them in ≤100-entity, byte-budgeted
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
    private readonly TimeSpan _barrierTimeout;
    private readonly TimeSpan _flushTimeout;
    private readonly int _flushConcurrency;

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
        _barrierTimeout = TimeSpan.FromSeconds(Math.Max(1, settings.Orchestrator.RunningBarrierTimeoutSeconds));
        _flushTimeout = TimeSpan.FromSeconds(Math.Max(1, settings.Orchestrator.StatusFlushTimeoutSeconds));
        _flushConcurrency = Math.Max(1, settings.Orchestrator.StatusFlushConcurrency);
        _drainLoop = _enabled ? Task.Run(DrainLoopAsync) : Task.CompletedTask;
        _logger.LogInformation(
            "[Orchestrator] StatusWriter: enabled={E} durableBarrier={B} flushMs={F} barrierTimeout={BT}s flushTimeout={FT}s concurrency={C}",
            _enabled, _durableBarrier, _flushIntervalMs, _barrierTimeout.TotalSeconds, _flushTimeout.TotalSeconds,
            _flushConcurrency);
    }

    private static string Key(string run, string task) => run + "" + task;
    private static TaskStatusWrite Snap(string run, OrchestratorTaskItem t) => new(
        run, t.Id, t.Status, JsonSerializer.Serialize(t.Parameters, s_json), t.AttemptCount, t.LastError,
        t.CompletedUtc, t.Priority);

    /// <summary>Persist the pre-invoke "Running" marker durably before the task runs. Under the barrier it is
    /// batched with other concurrently-starting tasks (N tasks → ~1 transaction) yet still lands before the
    /// invoke. Disabled → the original per-task awaited write.</summary>
    /// <exception cref="TimeoutException">
    /// The marker did not persist within <c>RunningBarrierTimeoutSeconds</c>. The caller must treat this
    /// as a DEFERRAL, not a failure: nothing was written, so storage still has the task Pending and it
    /// is safe — and necessary — to retry it.
    /// </exception>
    public async Task MarkRunningAsync(string runName, OrchestratorTaskItem task, CancellationToken ct = default)
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

        // Bounded. This wait sits between dispatch and worker checkout while holding a JobManager slot,
        // so waiting forever converts a slow flush into a whole-host outage — observed in production as
        // 8/8 slots held, every BG worker idle, 1,919 jobs queued and nothing moving until a restart.
        try
        {
            if (await CompletesWithinAsync(barrier, _barrierTimeout, ct)) return;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // The flush carrying this marker failed. It has been requeued, but THIS task must not start:
            // the marker is not in storage, so its retry bound would not hold.
            throw new MarkerNotPersistedException(
                $"Durable 'Running' marker for {runName}/{task.Id} did not persist: {ex.Message}", ex);
        }

        throw new MarkerNotPersistedException(
            $"Durable 'Running' marker for {runName}/{task.Id} did not persist within " +
            $"{_barrierTimeout.TotalSeconds:F0}s. The task was not started and remains Pending.");
    }

    /// <summary>
    /// Await <paramref name="work"/> with a ceiling. True if it finished (and any exception it carried
    /// is rethrown), false if the ceiling was hit first.
    /// </summary>
    private static async Task<bool> CompletesWithinAsync(Task work, TimeSpan limit, CancellationToken ct)
    {
        if (work.IsCompleted) { await work; return true; }

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var timer = Task.Delay(limit, cts.Token);
        var winner = await Task.WhenAny(work, timer);
        cts.Cancel(); // stop the timer whichever way this went, so it cannot outlive the call

        if (winner != work)
        {
            ct.ThrowIfCancellationRequested();
            return false;
        }

        await work; // observe a flush failure as an exception rather than a silent success
        return true;
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
    public async Task FlushAsync(CancellationToken ct = default)
    {
        if (!_enabled) return;
        Task barrier;
        lock (_lock) { barrier = _barrier.Task; }
        _signal.Release();

        // Bounded for the same reason as the Running marker: this is awaited by FinalizeRunAsync, and an
        // unbounded wait meant no run could finalize while a flush was stuck. Requeue-on-failure means a
        // timeout here does not lose the writes — they persist on a later flush or on the shutdown drain,
        // so finalization proceeds rather than blocking on transient storage trouble.
        try
        {
            if (await CompletesWithinAsync(barrier, _barrierTimeout, ct)) return;
            _logger.LogWarning("[Orchestrator] Flush barrier not met within {Sec}s — pending writes remain queued",
                _barrierTimeout.TotalSeconds);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "[Orchestrator] Flush barrier reported a failed write — requeued for retry");
        }
    }

    private async Task DrainLoopAsync()
    {
        while (!_cts.IsCancellationRequested)
        {
            try { await _signal.WaitAsync(_flushIntervalMs, _cts.Token); }
            catch (OperationCanceledException) { break; }

            // FlushOnceAsync used to be called outside any try. Anything escaping it — an OOM while
            // formatting the error log is enough — killed this loop silently, and a dead loop means no
            // barrier is ever completed again, so every task hangs at its durable marker forever.
            // This loop must not be able to die while the process lives.
            try { await FlushOnceAsync(_flushTimeout); }
            catch (Exception ex) { LogSafely(ex, "status flush"); }
        }

        // Final drain on shutdown. Deliberately NOT bounded by _cts (which is already cancelled) and
        // given a generous ceiling: this is the last chance for terminal task states to reach storage.
        try { await FlushOnceAsync(TimeSpan.FromSeconds(30), ignoreShutdown: true); }
        catch (Exception ex) { LogSafely(ex, "final status drain"); }
    }

    /// <summary>Log without letting the logger itself take the drain loop down (it allocates, and this
    /// path runs under exactly the memory pressure that makes allocation fail).</summary>
    private void LogSafely(Exception ex, string what)
    {
        try { _logger.LogError(ex, "[Orchestrator] Unhandled error during {What} — drain loop continues", what); }
        catch { /* nothing useful left to do; staying alive matters more than reporting */ }
    }

    private async Task FlushOnceAsync(TimeSpan timeout, bool ignoreShutdown = false)
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

        using var cts = ignoreShutdown
            ? new CancellationTokenSource(timeout)
            : CancellationTokenSource.CreateLinkedTokenSource(_cts.Token);
        if (!ignoreShutdown) cts.CancelAfter(timeout);

        var unwritten = new List<string>();
        Exception? failure = null;

        try
        {
            if (tasks.Count > 0)
                unwritten.AddRange(await _store.WriteTaskStatusBatchAsync(tasks.Values.ToList(), _flushConcurrency, cts.Token));

            // Batched, not one await per run. Run rows all share the "Run" partition key, so N of them
            // cost ceil(N/100) transactions; the previous per-run loop cost N round-trips inside a flush
            // bounded by _flushTimeout, which is how a busy instance (100+ live runs) blew the flush
            // budget and then the durable barrier. The store isolates a poison row by retrying a failed
            // chunk individually, so a single bad run no longer discards the other 99.
            if (runs.Count > 0)
                unwritten.AddRange(await _store.WriteRunStatusBatchAsync(runs.Values.ToList(), cts.Token));
        }
        catch (Exception ex)
        {
            // Timed out or the store threw wholesale — treat EVERYTHING in this snapshot as unwritten.
            failure = ex;
            unwritten.AddRange(tasks.Values.Select(t => t.RunName));
            unwritten.AddRange(runs.Keys);
            _logger.LogError(ex, "[Orchestrator] Status flush failed ({Tasks} tasks, {Runs} runs) — requeued for retry",
                tasks.Count, runs.Count);
        }

        // Durability: anything that did not reach storage goes back on the pending set. Dropping it
        // (the previous behaviour on any exception) silently lost terminal task states — a completed
        // task would look Pending forever and be re-run by the next recovery.
        if (unwritten.Count > 0) Requeue(tasks, runs, unwritten);

        // The barrier may ONLY report success when this batch actually persisted. Waiters cannot tell
        // which run in the batch was theirs, so any un-persisted write has to fail all of them — a
        // waiter that returns successfully goes on to run its task, and running a task whose "Running"
        // marker never landed is exactly the poison-retry hole the marker exists to close. Failing here
        // is cheap: the writes are requeued, and the waiters defer and retry.
        if (failure != null)
            done.TrySetException(failure);
        else if (unwritten.Count > 0)
            done.TrySetException(new InvalidOperationException(
                $"{unwritten.Count} status write(s) did not persist and were requeued"));
        else
            done.TrySetResult();
    }

    /// <summary>
    /// Put un-persisted writes back. <see cref="Dictionary{TKey,TValue}.TryAdd"/>, not assignment: a
    /// NEWER state for the same task may have arrived while the flush was in flight, and the retry of a
    /// stale snapshot must never overwrite it.
    /// </summary>
    private void Requeue(Dictionary<string, TaskStatusWrite> tasks, Dictionary<string, OrchestratorRun> runs,
        List<string> unwrittenRuns)
    {
        var retry = new HashSet<string>(unwrittenRuns, StringComparer.OrdinalIgnoreCase);
        var restored = 0;

        lock (_lock)
        {
            foreach (var (key, write) in tasks)
            {
                if (!retry.Contains(write.RunName)) continue;
                if (_pendingTasks.TryAdd(key, write)) restored++;
            }
            foreach (var (name, run) in runs)
            {
                if (!retry.Contains(name)) continue;
                if (_pendingRuns.TryAdd(name, run)) restored++;
            }
        }

        if (restored > 0)
        {
            _signal.Release(); // make sure the next flush actually runs
            _logger.LogWarning("[Orchestrator] Requeued {Count} un-persisted status writes across {Runs} runs",
                restored, retry.Count);
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

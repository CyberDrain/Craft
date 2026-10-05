using Craft.Storage;

namespace Craft.Orchestration;

/// <summary>
/// Coalesces task finishes per run, so tasks of one run finishing together share one transaction instead of
/// each paying for its own (and racing each other on the run header). Callers await their own outcome: a
/// finish is durable when the await returns, which is what lets a worker slot go.
/// </summary>
public sealed class FinishBatcher(WorkStore store, ILogger logger, TimeSpan? window = null)
{
    private readonly TimeSpan _window = window ?? TimeSpan.FromMilliseconds(15);
    private readonly object _lock = new();
    private readonly Dictionary<string, List<(WorkStore.Finish Finish, TaskCompletionSource<WorkStore.FinishOutcome?> Done)>> _pending = new(StringComparer.Ordinal);

    public Task<WorkStore.FinishOutcome?> FinishAsync(string runKey, WorkStore.Finish finish)
    {
        var done = new TaskCompletionSource<WorkStore.FinishOutcome?>(TaskCreationOptions.RunContinuationsAsynchronously);
        bool first;
        lock (_lock)
        {
            first = !_pending.TryGetValue(runKey, out var list);
            if (first) _pending[runKey] = list = [];
            list!.Add((finish, done));
        }
        if (first) _ = FlushAfterWindowAsync(runKey);
        return done.Task;
    }

    private async Task FlushAfterWindowAsync(string runKey)
    {
        await Task.Delay(_window);
        List<(WorkStore.Finish Finish, TaskCompletionSource<WorkStore.FinishOutcome?> Done)> batch;
        lock (_lock)
        {
            batch = _pending[runKey];
            _pending.Remove(runKey);
        }

        try
        {
            var outcome = await store.FinishAsync(runKey, batch.Select(b => b.Finish).ToList());
            foreach (var (_, done) in batch) done.TrySetResult(outcome);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "[Orchestrator] Could not record {Count} finished task(s) of {Run}; their leases lapse and they run again",
                batch.Count, runKey);
            foreach (var (_, done) in batch) done.TrySetException(ex);
        }
    }
}

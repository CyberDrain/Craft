using Craft.Storage;

namespace Craft.Orchestration;

/// <summary>
/// Coalesces task finishes per run, so tasks of one run finishing together share one transaction instead of
/// each paying for its own (and racing each other on the run header). Callers await their own outcome.
///
/// A finish that cannot be written (storage unavailable, or losing races for longer than the store retries)
/// is not dropped: it is retried in the background, backing off to a minute between attempts, for
/// <see cref="GiveUpAfter"/>. Each attempt is guarded by the claim's owner, so a claim taken over meanwhile is
/// never overwritten. Only if every attempt fails does the task fall back to its claim lapsing and running
/// again, and that is logged as an error naming the run.
/// </summary>
public sealed class FinishBatcher(WorkStore store, ILogger logger, TimeSpan? window = null)
{
    private readonly TimeSpan _window = window ?? TimeSpan.FromMilliseconds(15);
    private readonly object _lock = new();
    private readonly Dictionary<string, List<(WorkStore.Finish Finish, TaskCompletionSource<WorkStore.FinishOutcome?> Done)>> _pending = new(StringComparer.Ordinal);

    /// <summary>How long a finish that cannot be written keeps being retried.</summary>
    internal TimeSpan GiveUpAfter { get; set; } = TimeSpan.FromMinutes(30);

    /// <summary>The first background retry delay, doubling to a minute; tests shorten it.</summary>
    internal TimeSpan FirstRetry { get; set; } = TimeSpan.FromSeconds(1);

    /// <summary>Finishes waiting on a background retry, for status and tests.</summary>
    public int Retrying => Volatile.Read(ref _retrying);
    private int _retrying;

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

        var finishes = batch.Select(b => b.Finish).ToList();
        try
        {
            var outcome = await store.FinishAsync(runKey, finishes);
            foreach (var (_, done) in batch) done.TrySetResult(outcome);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "[Orchestrator] Could not record {Count} finished task(s) of {Run}; retrying in the background",
                batch.Count, runKey);
            foreach (var (_, done) in batch) done.TrySetResult(null);
            _ = RetryAsync(runKey, finishes);
        }
    }

    private async Task RetryAsync(string runKey, List<WorkStore.Finish> finishes)
    {
        Interlocked.Add(ref _retrying, finishes.Count);
        try
        {
            var delay = FirstRetry;
            var giveUpAt = DateTime.UtcNow + GiveUpAfter;
            for (var attempt = 1; ; attempt++)
            {
                await Task.Delay(delay);
                try
                {
                    await store.FinishAsync(runKey, finishes);
                    logger.LogInformation("[Orchestrator] Recorded {Count} finished task(s) of {Run} after {Attempts} retr{Ies}",
                        finishes.Count, runKey, attempt, attempt == 1 ? "y" : "ies");
                    return;
                }
                catch (Exception ex)
                {
                    if (DateTime.UtcNow >= giveUpAt)
                    {
                        logger.LogError(ex, "[Orchestrator] Gave up recording {Count} finished task(s) of {Run} after {Attempts} attempts; they run again when their claims lapse",
                            finishes.Count, runKey, attempt + 1);
                        return;
                    }
                    delay = delay * 2 > TimeSpan.FromMinutes(1) ? TimeSpan.FromMinutes(1) : delay * 2;
                }
            }
        }
        finally
        {
            Interlocked.Add(ref _retrying, -finishes.Count);
        }
    }
}

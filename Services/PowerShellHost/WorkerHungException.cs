namespace Craft.PowerShellHost;

/// <summary>
/// A cancelled invocation that did not stop within its worker's stop grace, or a call on a worker already
/// in that state. Derives from <see cref="OperationCanceledException"/> so every caller that reports a
/// cancelled invocation as a timeout keeps doing so; the worker is left <see cref="PowerShellWorker.IsHung"/>
/// and the pool replaces it when it is reclaimed.
/// </summary>
public sealed class WorkerHungException(int workerId, string message) : OperationCanceledException(message)
{
    public int WorkerId { get; } = workerId;
}

namespace Craft.Orchestration;

/// <summary>
/// The durable "Running" marker for a task did not reach storage — it timed out, or the flush carrying
/// it failed.
///
/// This is a DEFERRAL, not a task failure, and the distinction is the whole point of the type. Nothing
/// was written, so the task is still Pending in storage; running it anyway would defeat the
/// AttemptCount/MaxRetries bound the marker exists to provide, and failing it would lose work that never
/// ran. The correct response is to release the slot and retry.
/// </summary>
public sealed class MarkerNotPersistedException : Exception
{
    public MarkerNotPersistedException(string message) : base(message) { }
    public MarkerNotPersistedException(string message, Exception inner) : base(message, inner) { }
}

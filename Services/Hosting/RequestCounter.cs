namespace Craft.Hosting;

/// <summary>
/// Live count of in-flight requests, used for diagnostics logging.
/// <para>
/// A small class rather than a bare <see cref="int"/> field because the counter has to be shared with
/// an endpoint handler registered elsewhere, and <c>ref</c> locals cannot be captured by a lambda.
/// </para>
/// </summary>
public sealed class RequestCounter
{
    private int _active;

    /// <summary>Requests currently in flight.</summary>
    public int Active => Volatile.Read(ref _active);

    /// <summary>Records a request starting. Returns the new count.</summary>
    public int Increment() => Interlocked.Increment(ref _active);

    /// <summary>Records a request finishing. Returns the new count.</summary>
    public int Decrement() => Interlocked.Decrement(ref _active);
}

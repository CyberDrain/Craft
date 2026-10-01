namespace Craft.PowerShellHost;

/// <summary>
/// A pool of items handed to waiters in two FIFO tiers: a returned item always goes to the oldest
/// priority waiter, and to a low-priority waiter only when no priority waiter is queued. This is what
/// keeps interactive HTTP requests ahead of an API-client burst on the shared HTTP worker pool.
/// </summary>
public sealed class TieredHandoff<T> where T : class
{
    private readonly object _lock = new();
    private readonly Queue<T> _free = new();
    private readonly LinkedList<TaskCompletionSource<T>> _priority = new();
    private readonly LinkedList<TaskCompletionSource<T>> _low = new();

    public int Count { get { lock (_lock) return _free.Count; } }

    public void Add(T item)
    {
        TaskCompletionSource<T> waiter;
        lock (_lock)
        {
            var list = _priority.Count > 0 ? _priority : _low.Count > 0 ? _low : null;
            if (list == null)
            {
                _free.Enqueue(item);
                return;
            }
            waiter = list.First!.Value;
            list.RemoveFirst();
        }
        waiter.SetResult(item);
    }

    /// <summary>
    /// Takes an item, waiting up to <paramref name="timeout"/> behind earlier waiters of the same tier
    /// (and, for <paramref name="lowPriority"/>, behind every priority waiter). Null on timeout.
    /// </summary>
    public T? TryTake(bool lowPriority, TimeSpan timeout)
    {
        LinkedListNode<TaskCompletionSource<T>> node;
        lock (_lock)
        {
            // Add serves waiters before refilling _free, so a free item means nobody is queued.
            if (_free.TryDequeue(out var item)) return item;
            if (timeout <= TimeSpan.Zero) return null;
            node = (lowPriority ? _low : _priority).AddLast(
                new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously));
        }

        if (node.Value.Task.Wait(timeout)) return node.Value.Task.Result;

        lock (_lock)
        {
            if (node.List != null)
            {
                node.List.Remove(node);
                return null;
            }
        }
        // Add dequeued this waiter as the timeout fired; its item is already on the way.
        return node.Value.Task.GetAwaiter().GetResult();
    }
}

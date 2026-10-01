using Craft.PowerShellHost;

namespace Craft.Tests;

/// <summary>
/// The HTTP worker pool hands workers out through this queue, so its ordering is the guarantee that an
/// API-client burst cannot push interactive requests behind it.
/// </summary>
public class TieredHandoffTests
{
    private static readonly TimeSpan Long = TimeSpan.FromSeconds(10);

    private static Task<string?> Waiter(TieredHandoff<string> queue, bool lowPriority, TimeSpan timeout)
    {
        var task = Task.Factory.StartNew(() => queue.TryTake(lowPriority, timeout), TaskCreationOptions.LongRunning);
        Thread.Sleep(100); // let it enqueue before the next waiter
        return task;
    }

    [Fact]
    public void FreeItem_IsReturnedImmediately()
    {
        var queue = new TieredHandoff<string>();
        queue.Add("w1");
        Assert.Equal("w1", queue.TryTake(lowPriority: true, TimeSpan.Zero));
        Assert.Null(queue.TryTake(lowPriority: false, TimeSpan.Zero));
    }

    [Fact]
    public async Task PriorityWaiter_IsServedBeforeEarlierLowWaiters()
    {
        var queue = new TieredHandoff<string>();
        var low1 = Waiter(queue, lowPriority: true, Long);
        var low2 = Waiter(queue, lowPriority: true, Long);
        var priority = Waiter(queue, lowPriority: false, Long);

        queue.Add("a");
        Assert.Equal("a", await priority);
        Assert.False(low1.IsCompleted);

        queue.Add("b");
        queue.Add("c");
        Assert.Equal("b", await low1);
        Assert.Equal("c", await low2);
        Assert.Equal(0, queue.Count);
    }

    [Fact]
    public async Task PriorityWaiters_AreFifo()
    {
        var queue = new TieredHandoff<string>();
        var first = Waiter(queue, lowPriority: false, Long);
        var second = Waiter(queue, lowPriority: false, Long);

        queue.Add("a");
        Assert.Equal("a", await first);
        queue.Add("b");
        Assert.Equal("b", await second);
    }

    [Fact]
    public async Task TimedOutWaiter_IsDropped_AndTheNextItemGoesToTheFreeList()
    {
        var queue = new TieredHandoff<string>();
        Assert.Null(await Waiter(queue, lowPriority: false, TimeSpan.FromMilliseconds(50)));

        queue.Add("a");
        Assert.Equal(1, queue.Count);
    }

    [Fact]
    public async Task ConcurrentChurn_LosesNoItems()
    {
        var queue = new TieredHandoff<string>();
        for (var i = 0; i < 4; i++) queue.Add($"w{i}");

        var callers = Enumerable.Range(0, 32).Select(i => Task.Run(() =>
        {
            for (var n = 0; n < 200; n++)
            {
                var item = queue.TryTake(lowPriority: i % 2 == 0, TimeSpan.FromMilliseconds(5));
                if (item != null) queue.Add(item);
            }
        }));
        await Task.WhenAll(callers);

        Assert.Equal(4, queue.Count);
    }
}

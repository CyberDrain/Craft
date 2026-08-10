using System.Globalization;
using Craft.Configuration;
using Craft.Orchestration;
using Craft.PowerShellHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Craft.Tests;

/// <summary>
/// Reproduces a production deadlock: a 124-tenant instance sat at "3629/7336 done, 0 running, 3696
/// pending" for 28.5 hours. The process was alive and the scheduler kept logging a heartbeat every
/// minute, but not one job was started in that entire window.
///
/// The two counters disagreed, and that is the whole diagnosis:
///   - <c>JobManager.ActiveCount</c> was 0, so <c>RunJobAsync</c>'s finally HAD run.
///   - the limiter never scaled down. Its monitor drops to baseline within 10s of <c>_active</c>
///     reaching 0, and had done so reliably every 30 minutes for the preceding two days. After the
///     freeze: nothing, for 28.5 hours.
///
/// A gate that is never idle while nothing runs has lost slots. Once every slot is lost the dispatch
/// loop parks in <c>AcquireAsync</c> against a gate that will never open, and no amount of queued work
/// runs again.
///
/// The likeliest culprit — and the one these tests were written against — is a log call throwing under
/// memory pressure at a point where the accounting was already committed. <c>AcquireAsync</c> logged
/// between enqueueing a waiter and awaiting it: a throw there abandons the node in the queue, and the
/// next release grants a slot to a <c>TaskCompletionSource</c> nobody awaits. Once per slot and the
/// gate is shut for good. Every such site is now guarded, along with the sibling paths that could
/// orphan a slot the same way (release throwing, <c>RunJobAsync</c> throwing before its try, the
/// handoff failing, the loop dying on a non-cancellation exception).
///
/// All of them were silent, because the dispatch loop discards the task it hands to the thread pool.
/// </summary>
public class LimiterSlotLeakTests
{
    private static BackgroundTaskLimiter NewLimiter(int bgPoolSize = 4, int baseConcurrency = 4,
        int stalledTicks = 2)
    {
        var settings = new CraftSettings();
        settings.Worker.BgPoolSize = bgPoolSize;

        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["BackgroundBaseConcurrency"] = baseConcurrency.ToString(CultureInfo.InvariantCulture),
            ["BackgroundMaxConcurrency"] = bgPoolSize.ToString(CultureInfo.InvariantCulture),
            // Real default is 180 ticks (30 min). Shortened so the wedge is reachable without waiting.
            ["BackgroundStalledTicksBeforeReconcile"] = stalledTicks.ToString(CultureInfo.InvariantCulture),
        }).Build();

        var repo = new ScriptRepository(NullLogger<ScriptRepository>.Instance, settings);
        var pool = new PowerShellWorkerPool(repo, NullLogger<PowerShellWorkerPool>.Instance, config, settings);
        return new BackgroundTaskLimiter(NullLogger<BackgroundTaskLimiter>.Instance, config, settings, pool);
    }

    private static async Task<bool> WaitUntil(Func<bool> condition, int timeoutMs = 5000)
    {
        var deadline = Environment.TickCount64 + timeoutMs;
        while (Environment.TickCount64 < deadline)
        {
            if (condition()) return true;
            await Task.Delay(10);
        }
        return condition();
    }

    /// <summary>
    /// THE GUARANTEE. Slots leaked by any means must not deadlock the gate forever.
    ///
    /// Every slot is consumed and never released — the exact production end state — and a caller is
    /// queued behind them. Before the fix that caller waits forever. Now the monitor notices that
    /// nothing has been granted or released while callers are queued, concludes its own accounting is
    /// wrong, and reopens the gate.
    /// </summary>
    [Fact]
    public async Task LeakedSlotsDoNotDeadlockTheGateForever()
    {
        using var limiter = NewLimiter(bgPoolSize: 4, baseConcurrency: 4, stalledTicks: 2);

        // Consume every slot and never release: this IS the leak.
        for (var i = 0; i < 4; i++) await limiter.AcquireAsync($"leaked-{i}");
        Assert.Equal(4, limiter.Active);

        // A caller arrives behind the leaked slots and parks, exactly as the dispatch loop did.
        var blocked = limiter.AcquireAsync("dispatch-loop");
        Assert.True(await WaitUntil(() => limiter.Waiting == 1));
        Assert.False(blocked.IsCompleted, "must be parked while every slot is held");

        // Drive the monitor. Churn is frozen and a caller is queued, so this is the wedge.
        limiter.ReconcileSlots();
        Assert.False(blocked.IsCompleted, "one quiet tick is not yet proof of a wedge");

        limiter.ReconcileSlots();
        limiter.ReconcileSlots();

        Assert.True(await WaitUntil(() => blocked.IsCompleted),
            "the gate must reopen once it can prove no slot has moved while callers are queued");
        await blocked;
    }

    /// <summary>
    /// The wedge detector must not fire on a legitimately long job. Churn is frozen there too — that is
    /// what "one slow job holding its slot" looks like — so the only thing separating it from a real
    /// wedge is time, and reclaiming the slot early over-admits against a pool that is genuinely full.
    /// </summary>
    [Fact]
    public async Task LongRunningJobKeepsItsSlotWhileTheGateIsQuiet()
    {
        using var limiter = NewLimiter(bgPoolSize: 2, baseConcurrency: 2, stalledTicks: 50);

        await limiter.AcquireAsync("slow-job-1");
        await limiter.AcquireAsync("slow-job-2");

        var blocked = limiter.AcquireAsync("queued-behind");
        Assert.True(await WaitUntil(() => limiter.Waiting == 1));

        // Well short of the threshold: the slots are still presumed live.
        for (var i = 0; i < 20; i++) limiter.ReconcileSlots();

        Assert.False(blocked.IsCompleted, "a long job must keep its slot until the threshold is reached");
        Assert.Equal(2, limiter.Active);

        // And a normal release still serves the queue immediately.
        limiter.ReleaseSlot();
        Assert.True(await WaitUntil(() => blocked.IsCompleted));
        await blocked;
    }

    /// <summary>
    /// Churn resets the stall count. A gate that is saturated but working must never be reconciled,
    /// however long it stays saturated.
    /// </summary>
    [Fact]
    public async Task SaturatedButProgressingGateIsNeverReconciled()
    {
        using var limiter = NewLimiter(bgPoolSize: 2, baseConcurrency: 2, stalledTicks: 2);

        await limiter.AcquireAsync("job-1");
        await limiter.AcquireAsync("job-2");

        var blocked = limiter.AcquireAsync("queued-behind");
        Assert.True(await WaitUntil(() => limiter.Waiting == 1));

        // Ten rounds of "a slot moves, the queue is served, another caller queues behind" — five times
        // the stall threshold. Each tick sees churn since the last one, so the count keeps resetting.
        for (var i = 0; i < 10; i++)
        {
            limiter.ReleaseSlot();                       // churn: a slot moved, granting the parked waiter
            Assert.True(await WaitUntil(() => blocked.IsCompleted));
            await blocked;

            blocked = limiter.AcquireAsync($"queued-{i}");
            Assert.True(await WaitUntil(() => limiter.Waiting == 1));

            limiter.ReconcileSlots();
            Assert.False(blocked.IsCompleted,
                "a saturated but progressing gate must never be reconciled");
            Assert.Equal(2, limiter.Active);
        }

        limiter.ReleaseSlot();
        Assert.True(await WaitUntil(() => blocked.IsCompleted));
        await blocked;
    }

    /// <summary>
    /// A release must never drive the count negative. A double release used to leave <c>_active</c>
    /// below zero, which hands out phantom slots and over-admits against the worker pool — the same
    /// class of accounting bug as the leak, in the opposite direction.
    /// </summary>
    [Fact]
    public async Task DoubleReleaseCannotCreatePhantomSlots()
    {
        using var limiter = NewLimiter(bgPoolSize: 2, baseConcurrency: 2);

        await limiter.AcquireAsync("job");
        limiter.ReleaseSlot();
        limiter.ReleaseSlot(); // the buggy second release
        limiter.ReleaseSlot();

        Assert.Equal(0, limiter.Active);

        // The gate still admits exactly its maximum, not maximum-plus-the-phantoms.
        await limiter.AcquireAsync("a");
        await limiter.AcquireAsync("b");
        Assert.Equal(2, limiter.Active);

        var blocked = limiter.AcquireAsync("c");
        Assert.True(await WaitUntil(() => limiter.Waiting == 1));
        Assert.False(blocked.IsCompleted, "the gate must still be closed at its maximum");

        limiter.ReleaseSlot();
        Assert.True(await WaitUntil(() => blocked.IsCompleted));
        await blocked;
    }

    /// <summary>
    /// A logger that fails the way one does under memory pressure: at the point of use. Armed after
    /// construction so the limiter's own init logging still gets through.
    /// </summary>
    private sealed class ThrowingLogger : ILogger<BackgroundTaskLimiter>
    {
        public bool Armed { get; set; }

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;

        // Stands in for the OutOfMemoryException the runtime raises here under real memory pressure;
        // CA2201 reserves that type for the runtime, and the limiter must survive either way.
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (Armed) throw new InvalidOperationException("log failed");
        }
    }

    /// <summary>
    /// Acquiring must not throw once the slot has been handed over. Everything after the increment - and
    /// after the waiter completes on the queued path - runs with the slot already the caller's, and the
    /// caller is the only thing that can release it. A throw there strands a slot that is unreleasable by
    /// construction, because nobody ever learns it exists.
    ///
    /// This is the likeliest shape of the production wedge: the last line that instance ever logged of
    /// that kind was "Limiter slot acquired after 5227ms" at 02:37:23, and dispatch never ran again.
    /// </summary>
    [Fact]
    public async Task AcquireNeverThrowsOnceTheSlotIsGranted()
    {
        var settings = new CraftSettings();
        settings.Worker.BgPoolSize = 2;
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["BackgroundBaseConcurrency"] = "2",
            ["BackgroundMaxConcurrency"] = "2",
        }).Build();
        var repo = new ScriptRepository(NullLogger<ScriptRepository>.Instance, settings);
        var pool = new PowerShellWorkerPool(repo, NullLogger<PowerShellWorkerPool>.Instance, config, settings);

        var logger = new ThrowingLogger();
        using var limiter = new BackgroundTaskLimiter(logger, config, settings, pool);
        logger.Armed = true;

        // Immediate-grant path: throws from the log call that sits after the increment.
        await limiter.AcquireAsync("granted-immediately");
        Assert.Equal(1, limiter.Active);

        await limiter.AcquireAsync("also-immediate");
        Assert.Equal(2, limiter.Active);

        // Queued path: the log after the waiter completes is the one that went silent in production.
        var blocked = limiter.AcquireAsync("queued");
        Assert.True(await WaitUntil(() => limiter.Waiting == 1));

        limiter.ReleaseSlot();

        // Must complete, not fault — a faulted acquire means a slot nobody can give back.
        Assert.True(await WaitUntil(() => blocked.IsCompleted));
        Assert.False(blocked.IsFaulted, "acquire must not fault after the slot has been granted");
        await blocked;
        Assert.Equal(2, limiter.Active);
    }

    /// <summary>
    /// Releasing must not throw into the caller. It is called from a finally that also owns the job's
    /// bookkeeping, and in production it runs under memory pressure — an exception escaping here takes
    /// the surrounding finally with it.
    /// </summary>
    [Fact]
    public void ReleaseNeverThrowsIntoTheCaller()
    {
        using var limiter = NewLimiter();

        var ex = Record.Exception(() =>
        {
            limiter.ReleaseSlot();
            limiter.ReleaseSlot();
            limiter.ReconcileSlots();
        });

        Assert.Null(ex);
    }
}

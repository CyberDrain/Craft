using Craft.Configuration;
using Craft.Hosting;
using Craft.Orchestration;
using Craft.PowerShellHost;
using Microsoft.Extensions.Logging.Abstractions;

namespace Craft.Tests;

/// <summary>
/// Reproduces the production dispatch stalls: hour-scale windows with zero <c>[JobManager] Started:</c>
/// lines while the process was alive, memory was fine, and the limiter reported "1 active, 0 waiting".
///
/// Two candidates had to be separated:
///   (a) the scheduler is not enqueueing between cron ticks, so the queue is genuinely empty; or
///   (b) work IS queued but the dispatch loop is parked.
///
/// "0 waiting" was read as evidence for (a). It is not evidence of anything.
/// <c>BackgroundTaskLimiter.Waiting</c> counts callers blocked inside <c>AcquireAsync</c>, and the
/// JobManager dispatch loop is a SINGLE loop holding at most ONE outstanding acquire. Parked anywhere
/// other than <c>AcquireAsync</c>, it reports 0 waiting no matter how deep the queue is.
///
/// It can be parked, because <c>ExecuteAsync</c> starts the job inline — <c>_ = RunJobAsync(job, ct);</c>
/// with no <c>Task.Run</c>. An <c>async</c> method body runs synchronously on its caller's thread until
/// its first real await, so a synchronous block inside the job blocks the dispatch loop itself. The
/// production path that does exactly this is <c>PowerShellRunnerService.ExecuteScript</c>, whose first
/// statement is <c>_pool.CheckoutBackground(CancellationToken.None)</c> → an untimed
/// <c>BlockingCollection.Take()</c>, before any await — reached from <c>SchedulerService</c> as
/// <c>work: (ct) =&gt; _psRunner.ExecuteScript(capturedFunc, capturedParams)</c>.
/// </summary>
public class JobDispatchStallTests
{
    private static (JobManager Jobs, BackgroundTaskLimiter Limiter) NewPair(int bgPoolSize = 8)
    {
        var settings = new CraftSettings();
        settings.Worker.BgPoolSize = bgPoolSize;
        var repo = new ScriptRepository(NullLogger<ScriptRepository>.Instance, settings);
        var pool = new PowerShellWorkerPool(repo, NullLogger<PowerShellWorkerPool>.Instance, settings, new StartupProgressService(),
            new Lazy<WorkerMetricsService>(() => null!));
        var limiter = new BackgroundTaskLimiter(NullLogger<BackgroundTaskLimiter>.Instance, settings, pool);
        return (new JobManager(NullLogger<JobManager>.Instance, settings, limiter), limiter);
    }

    /// <summary>
    /// Start the dispatch loop OFF the calling thread.
    ///
    /// <c>BackgroundService.StartAsync</c> invokes <c>ExecuteAsync</c> inline and only gets a Task back
    /// once the loop reaches its first true yield. With items already queued, <c>_itemAvailable.WaitAsync</c>
    /// and <c>_limiter.AcquireAsync</c> both complete synchronously, so the loop runs all the way into the
    /// first job on the caller's thread — awaiting <c>StartAsync</c> from a test would deadlock the test
    /// itself. (In production the queue is empty at startup, so host startup yields before the first job.)
    /// </summary>
    private static Task StartPump(JobManager jobs) => Task.Run(() => jobs.StartAsync(CancellationToken.None));

    /// <summary>Stop without ever hanging the suite, whatever state the loop is in.</summary>
    private static Task StopPump(JobManager jobs) =>
        TestWait.StopWithin(jobs.StopAsync(CancellationToken.None));

    /// <summary>
    /// THE GUARANTEE. A job that blocks its thread must not stop other jobs from being dispatched while
    /// the limiter has capacity.
    ///
    /// Fails against inline <c>_ = RunJobAsync(...)</c> dispatch — 0/20 drain, reproducing the production
    /// signature exactly (1 active, 0 waiting, spare capacity, non-empty queue, zero dispatches). Passes
    /// once dispatch is decoupled from execution.
    /// </summary>
    [Fact]
    public async Task DispatchContinues_WhileOneJobBlocksItsThread()
    {
        var (jobs, limiter) = NewPair(bgPoolSize: 8);
        using var blocked = new ManualResetEventSlim(false);
        using var reached = new ManualResetEventSlim(false);
        var ran = 0;

        // A SchedulerService-style simple script: work blocks before it ever yields.
        jobs.Enqueue("Start-CIPPDBCache", priority: 0, work: _ =>
        {
            reached.Set();
            blocked.Wait(CancellationToken.None);   // == CheckoutBackground → BlockingCollection.Take()
            return Task.CompletedTask;
        });

        // Orchestrator fan-out tasks queued behind it.
        for (var i = 0; i < 20; i++)
            jobs.Enqueue($"CIPPDBCacheRun-Graph_tenant{i:D3}", priority: 5,
                work: _ => { Interlocked.Increment(ref ran); return Task.CompletedTask; },
                runName: "CIPPDBCacheRun");

        _ = StartPump(jobs);
        Assert.True(reached.Wait(30_000), "the blocking job never started");

        var drained = await TestWait.WaitUntil(() => Volatile.Read(ref ran) == 20);

        // Snapshot the stall signature while the blocker is still holding its thread.
        var stillBlocked = !blocked.IsSet;
        var waiting = limiter.Waiting;
        var queued = jobs.QueuedCount;
        var active = limiter.Active;
        var max = limiter.CurrentMax;

        blocked.Set();
        await StopPump(jobs);

        Assert.True(stillBlocked, "test invalid — the blocker was released early");
        Assert.True(drained,
            $"only {Volatile.Read(ref ran)}/20 jobs dispatched while ONE job held its thread. " +
            $"Stall signature: active={active} waiting={waiting} max={max} queued={queued}. " +
            "The dispatch loop is still coupled to job execution.");
    }

    /// <summary>
    /// Once dispatch is decoupled, <c>Waiting</c> becomes a truthful backlog signal: with the limiter
    /// saturated and work still queued, the loop parks in <c>AcquireAsync</c> and reports it. That is
    /// what makes "0 waiting" mean "no backlog" — which it did not before.
    /// </summary>
    [Fact]
    public async Task LimiterWaiting_ReportsBacklog_WhenTheLimiterIsSaturated()
    {
        var (jobs, limiter) = NewPair(bgPoolSize: 2);
        using var hold = new ManualResetEventSlim(false);
        var started = 0;

        // Enough long-running jobs to saturate every slot, plus a deep queue behind them.
        for (var i = 0; i < 40; i++)
            jobs.Enqueue($"hold{i}", 5, _ =>
            {
                Interlocked.Increment(ref started);
                hold.Wait(CancellationToken.None);
                return Task.CompletedTask;
            });

        _ = StartPump(jobs);

        // Saturated + backlog ⇒ the loop must be parked in AcquireAsync and say so.
        var reported = await TestWait.WaitUntil(() => limiter.Waiting > 0 && jobs.QueuedCount > 0);
        var waiting = limiter.Waiting;
        var queued = jobs.QueuedCount;
        var active = limiter.Active;

        hold.Set();
        await StopPump(jobs);

        Assert.True(reported,
            $"limiter reported waiting={waiting} with {queued} still queued and {active} active — " +
            "a saturated limiter with a backlog must report waiting>0, or the signal is unusable");
    }

    /// <summary>
    /// Concurrency must still be gated by the limiter after decoupling — dispatching off-thread must not
    /// turn into "run everything at once".
    /// </summary>
    [Fact]
    public async Task Dispatch_NeverExceedsTheLimiterCeiling()
    {
        var (jobs, limiter) = NewPair(bgPoolSize: 2);
        using var hold = new ManualResetEventSlim(false);
        var concurrent = 0;
        var peak = 0;

        for (var i = 0; i < 30; i++)
            jobs.Enqueue($"job{i}", 5, _ =>
            {
                var now = Interlocked.Increment(ref concurrent);
                InterlockedMax(ref peak, now);
                hold.Wait(CancellationToken.None);
                Interlocked.Decrement(ref concurrent);
                return Task.CompletedTask;
            });

        _ = StartPump(jobs);
        await TestWait.WaitUntil(() => Volatile.Read(ref concurrent) >= limiter.CurrentMax);
        await Task.Delay(300);

        var observedPeak = Volatile.Read(ref peak);
        var ceiling = limiter.CeilingConcurrency + limiter.OverSubscribe;

        hold.Set();
        await StopPump(jobs);

        Assert.True(observedPeak <= ceiling,
            $"peak concurrency {observedPeak} exceeded the limiter ceiling {ceiling}");
    }

    private static void InterlockedMax(ref int target, int value)
    {
        int current;
        while (value > (current = Volatile.Read(ref target)))
            if (Interlocked.CompareExchange(ref target, value, current) == current) return;
    }
}

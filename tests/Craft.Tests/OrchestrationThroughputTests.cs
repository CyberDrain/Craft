using System.Diagnostics;

namespace Craft.Tests;

/// <summary>
/// The pump's real loop, not single refills: it must refill as soon as the JobManager's buffer drains and as soon
/// as a run appears, not on its poll. On the poll alone, short tasks ran at about one batch a second (pool-size
/// tasks per second) and an idle instance took up to the 10 s idle poll to notice a new run.
/// </summary>
public class OrchestrationThroughputTests
{
    private static async Task<OrchestrationHarness> StartedAsync()
    {
        var h = await OrchestrationHarness.CreateAsync(poolSize: 4);
        h.Svc.MarkRecoveryDone();
        await h.Pump.StartAsync(CancellationToken.None);
        var deadline = Environment.TickCount64 + 10_000;
        while (!h.Pump.HoldsLock && Environment.TickCount64 < deadline) await Task.Delay(20);
        Assert.True(h.Pump.HoldsLock);
        return h;
    }

    private static async Task<bool> WaitUntil(Func<Task<bool>> done, int timeoutMs)
    {
        var deadline = Environment.TickCount64 + timeoutMs;
        while (Environment.TickCount64 < deadline)
        {
            if (await done()) return true;
            await Task.Delay(10);
        }
        return await done();
    }

    [Fact]
    public async Task ShortTasks_AreNotHeldToOneBatchASecond()
    {
        await using var h = await StartedAsync();
        try
        {
            var sw = Stopwatch.StartNew();
            Assert.True(await h.Start("Quick", OrchestrationHarness.Batch(200, "q")));

            Assert.True(await WaitUntil(async () => await h.Store.GetRunByNameAsync("Quick") is { IsFinished: true }, 30_000));
            sw.Stop();
            // On the poll alone this took about 50 s (four tasks a second).
            Assert.True(sw.Elapsed < TimeSpan.FromSeconds(10), $"200 short tasks took {sw.Elapsed.TotalSeconds:F1}s");
        }
        finally
        {
            await h.Pump.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task AnIdleInstance_ClaimsANewRunAtOnce()
    {
        await using var h = await StartedAsync();
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(4));                       // let the idle poll back off
            var sw = Stopwatch.StartNew();
            Assert.True(await h.Start("Fresh", OrchestrationHarness.Batch(1, "f")));

            Assert.True(await WaitUntil(() => Task.FromResult(h.Svc.Started.Contains("f0")), 15_000));
            sw.Stop();
            Assert.True(sw.Elapsed < TimeSpan.FromSeconds(1), $"the first task of a new run started after {sw.Elapsed.TotalMilliseconds:F0}ms");
        }
        finally
        {
            await h.Pump.StopAsync(CancellationToken.None);
        }
    }
}

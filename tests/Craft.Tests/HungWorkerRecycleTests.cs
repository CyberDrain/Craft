using System.Collections;
using System.Diagnostics;
using System.Management.Automation;
using System.Management.Automation.Runspaces;
using Craft.Configuration;
using Craft.PowerShellHost;
using Craft.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace Craft.Tests;

/// <summary>
/// A pipeline blocked inside a .NET call (a lock, a sync wait, a read with no timeout) never sees PowerShell's stop
/// request. The timeout used to call <see cref="PowerShell.Stop"/> from the cancellation callback, which blocks until
/// the pipeline unwinds, so the timeout was never reported and the worker never went back to the pool. Once every
/// worker had hit such a call the instance served nothing: HTTP shed every request with 503 and background work
/// sat at "N running" for hours, with the process otherwise idle.
/// <para>
/// A worker that does not stop within its grace is now abandoned (<see cref="PowerShellWorker.IsHung"/>), the caller
/// gets a timeout, and the pool replaces the worker.
/// </para>
/// </summary>
public sealed class HungWorkerRecycleTests : IDisposable
{
    private readonly List<ManualResetEventSlim> _gates = [];

    public void Dispose()
    {
        // Release every blocked pipeline so the abandoned runspaces can unwind and close.
        foreach (var gate in _gates) gate.Set();
    }

    private ManualResetEventSlim NewGate()
    {
        var gate = new ManualResetEventSlim(false);
        _gates.Add(gate);
        return gate;
    }

    private static PowerShellWorker NewWorker(TimeSpan grace)
    {
        var iss = InitialSessionState.CreateDefault2();
        iss.Commands.Add(new SessionStateFunctionEntry("Block", "param($Gate) $null = $Gate.Wait(); 'unblocked'"));
        var worker = new PowerShellWorker(7, iss, NullLogger.Instance) { StopGrace = grace };
        worker.Runspace.ThreadOptions = PSThreadOptions.ReuseThread;
        if (worker.Runspace.RunspaceStateInfo.State == RunspaceState.BeforeOpen) worker.Runspace.Open();
        return worker;
    }

    /// <summary>Hang a worker the way production did: cancel an invocation blocked in a .NET wait.</summary>
    private static async Task<PowerShellWorker> HungWorkerAsync(ManualResetEventSlim gate)
    {
        var worker = NewWorker(TimeSpan.FromMilliseconds(300));
        using var cts = new CancellationTokenSource(100);
        await Assert.ThrowsAsync<WorkerHungException>(() => worker.InvokeAsync("Block", new() { ["Gate"] = gate }, cts.Token));
        return worker;
    }

    // ── The worker ──────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task ACancelledInvocationBlockedInDotNet_ReturnsAfterTheGrace()
    {
        var worker = NewWorker(TimeSpan.FromSeconds(1));
        using var cts = new CancellationTokenSource(500);
        var sw = Stopwatch.StartNew();

        var invoke = Task.Run(() => worker.InvokeAsync("Block", new() { ["Gate"] = NewGate() }, cts.Token));
        var returned = await Task.WhenAny(invoke, Task.Delay(TimeSpan.FromSeconds(15))) == invoke;

        Assert.True(returned, "InvokeAsync never returned after its token was cancelled");
        await Assert.ThrowsAsync<WorkerHungException>(() => invoke);
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(8), $"took {sw.Elapsed.TotalSeconds:0.0}s");
        Assert.True(worker.IsHung);
    }

    [Fact]
    public async Task TheTimeoutIsStillAnOperationCanceledException()
    {
        // Every runner catch site maps OperationCanceledException to its timeout (504, TimeoutException).
        var worker = NewWorker(TimeSpan.FromMilliseconds(300));
        using var cts = new CancellationTokenSource(100);

        var ex = await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => worker.InvokeAsync("Block", new() { ["Gate"] = NewGate() }, cts.Token));

        Assert.IsType<WorkerHungException>(ex);
    }

    [Fact]
    public async Task AHungWorkerRefusesFurtherCalls()
    {
        var worker = await HungWorkerAsync(NewGate());

        await Assert.ThrowsAsync<WorkerHungException>(() => worker.InvokeAsync("Block", new()));
        await Assert.ThrowsAsync<WorkerHungException>(() => worker.InvokeScriptAsync(ScriptBlock.Create("'x'")));
    }

    [Fact]
    public async Task AScriptBlockedInDotNet_IsAbandonedToo()
    {
        var worker = NewWorker(TimeSpan.FromMilliseconds(300));
        using var cts = new CancellationTokenSource(100);

        var key = NewSharedGate();

        await Assert.ThrowsAsync<WorkerHungException>(() => worker.InvokeScriptAsync(ScriptBlock.Create(
            $"$null = [Craft.Services.PowerShellRunnerService]::GetSharedCache('CraftPoolFixture')['{key}'].Wait()"), ct: cts.Token));

        Assert.True(worker.IsHung);
    }

    [Fact]
    public async Task AStoppableInvocation_IsCancelledWithoutAbandoningTheWorker()
    {
        // Start-Sleep honours the stop request, so this must stay an ordinary cancellation and the worker usable.
        using var worker = NewWorker(TimeSpan.FromSeconds(5));
        using var cts = new CancellationTokenSource(200);

        var ex = await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => worker.InvokeScriptAsync(ScriptBlock.Create("Start-Sleep -Seconds 30"), ct: cts.Token));

        Assert.IsNotType<WorkerHungException>(ex);
        Assert.False(worker.IsHung);
        Assert.Equal("after", Assert.Single(await worker.InvokeScriptAsync(ScriptBlock.Create("'after'"))).ToString());
    }

    [Fact]
    public async Task DisposingAHungWorkerDoesNotBlock_AndClosesItOnceTheCallReturns()
    {
        var gate = NewGate();
        var worker = await HungWorkerAsync(gate);
        var runspace = worker.Runspace;

        var dispose = Task.Run(worker.Dispose);
        Assert.True(await Task.WhenAny(dispose, Task.Delay(TimeSpan.FromSeconds(5))) == dispose, "Dispose blocked on the hung pipeline");
        Assert.NotEqual(RunspaceState.Closed, runspace.RunspaceStateInfo.State);

        gate.Set();
        var deadline = Environment.TickCount64 + 10_000;
        while (runspace.RunspaceStateInfo.State != RunspaceState.Closed && Environment.TickCount64 < deadline)
            await Task.Delay(50);
        Assert.Equal(RunspaceState.Closed, runspace.RunspaceStateInfo.State);
    }

    // ── A sequential run's pinned worker ────────────────────────────────────────────────────────

    [Fact]
    public async Task ASequentialRunSwapsOutAWorkerThatHung_AndRunsTheRemainingStepsOnAFreshOne()
    {
        // A sequential run pins one worker for every step. Kept after a step hung, it would fail every step after.
        await using var h = await OrchestrationHarness.CreateAsync();
        var hung = NewWorker(TimeSpan.FromMilliseconds(300));
        using var fresh = NewWorker(TimeSpan.FromSeconds(5));
        var workers = new Queue<PowerShellWorker>([hung, fresh]);
        h.Svc.NextWorker = () => workers.Dequeue();
        var gate = NewGate();
        h.Svc.BeforeRun = async t =>
        {
            if (FakeOrchestrator.IdOf(t) != "s0") return;
            using var cts = new CancellationTokenSource(100);
            await Assert.ThrowsAsync<WorkerHungException>(() => hung.InvokeAsync("Block", new() { ["Gate"] = gate }, cts.Token));
            throw new TimeoutException("step timed out");
        };
        Assert.True(await h.Start("SeqHung", OrchestrationHarness.Batch(3, "s"), sequential: true));

        Assert.True(await h.DriveUntilFinished("SeqHung"));
        Assert.Equal(new PowerShellWorker?[] { hung, fresh, fresh }, h.Svc.RanOn.ToArray());
        Assert.Equal(new (PowerShellWorker?, bool)[] { (hung, true), (fresh, false) }, h.Svc.Reclaimed.ToArray());
        var run = (await h.Store.GetRunByNameAsync("SeqHung"))!;
        Assert.Equal((1, 3), (run.Failed, run.Done));
    }

    // ── The pool, through the runner ────────────────────────────────────────────────────────────

    private static (PowerShellWorkerPool Pool, PowerShellRunnerService Runner) StartPool()
    {
        var settings = new CraftSettings();
        settings.Worker.HttpPoolSize = 1;
        settings.Worker.BgPoolSize = 1;
        settings.Worker.HttpTimeoutSeconds = 1;
        settings.Worker.BgTimeoutSeconds = 1;
        settings.Worker.StopGraceSeconds = 1;
        settings.Worker.HttpQueueTimeoutSeconds = 30;

        var repo = new ScriptRepository(NullLogger<ScriptRepository>.Instance, settings);
        var pool = new PowerShellWorkerPool(repo, NullLogger<PowerShellWorkerPool>.Instance,
            new ConfigurationBuilder().Build(), settings);
        pool.Initialize();
        Assert.True(pool.WaitForBgReady(TimeSpan.FromMinutes(2)), "pool never became ready");
        return (pool, new PowerShellRunnerService(NullLogger<PowerShellRunnerService>.Instance, pool, repo, settings));
    }

    private string NewSharedGate()
    {
        var key = Guid.NewGuid().ToString("N");
        PowerShellRunnerService.GetSharedCache("CraftPoolFixture")[key] = NewGate();
        return key;
    }

    private static async Task WaitFor(Func<bool> condition, string what)
    {
        var deadline = Environment.TickCount64 + 60_000;
        while (!condition() && Environment.TickCount64 < deadline) await Task.Delay(50);
        Assert.True(condition(), what);
    }

    [Fact]
    public async Task AHungBackgroundJobTimesOut_AndThePoolKeepsServing()
    {
        var (pool, runner) = StartPool();
        using var _ = pool;

        // A pool of one: without the replacement, the second call would wait for that worker forever.
        for (var i = 0; i < 2; i++)
        {
            var call = runner.ExecuteScript("Wait-CraftPoolFixtureGate", new() { ["GateKey"] = NewSharedGate() });
            Assert.True(await Task.WhenAny(call, Task.Delay(TimeSpan.FromSeconds(30))) == call, $"hung job {i} never timed out");
            await Assert.ThrowsAsync<TimeoutException>(() => call);
        }

        var output = await runner.ExecuteScriptWithOutput("Get-CraftPoolFixtureThing", new() { ["TenantFilter"] = "after" })
            .WaitAsync(TimeSpan.FromSeconds(60));
        Assert.Equal("thing:after", output);
        Assert.Equal(2, pool.HungWorkersTotal);
        Assert.Equal(2, pool.HungWorkers);

        foreach (var gate in _gates) gate.Set();
        await WaitFor(() => pool.HungWorkers == 0, "the abandoned workers were never counted as unwound");
    }

    [Fact]
    public async Task AHungHttpRequestReturns504_AndTheNextRequestGetsAWorker()
    {
        var (pool, runner) = StartPool();
        using var _ = pool;

        for (var i = 0; i < 2; i++)
        {
            var call = runner.ExecuteHttpEndpoint("Wait-CraftPoolFixtureGate", new Hashtable { ["GateKey"] = NewSharedGate() });
            Assert.True(await Task.WhenAny(call, Task.Delay(TimeSpan.FromSeconds(30))) == call, $"hung request {i} never returned");
            Assert.Equal(504, (await call).StatusCode);
        }

        await WaitFor(() => pool.HttpAvailable == pool.HttpPoolSize, "the HTTP pool never got its worker back");
        Assert.Equal(2, pool.HungWorkersTotal);
    }
}

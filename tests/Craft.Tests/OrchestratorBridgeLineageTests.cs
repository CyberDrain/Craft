using System.Collections.Concurrent;
using System.Management.Automation;
using System.Management.Automation.Runspaces;
using System.Reflection;
using Craft.Hosting;
using Craft.Orchestration;
using Craft.PowerShellHost;
using Craft.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace Craft.Tests;

/// <summary>
/// Lineage of PS-queued child runs: how the parent run's name reaches
/// <see cref="OrchestratorBridge.QueueOrchestration"/> so the parent's finalize can be gated on the
/// child (see OrchestratorChildRunGuardTests for the gate itself).
///
/// The bridge's ambient read of <see cref="OperationContext"/> cannot work for PowerShell callers:
/// the pipeline runs on the runspace's reused thread (ReuseThread, the production default), whose
/// ExecutionContext was frozen when the thread was created — at pool warmup, before any operation
/// context existed. The first test pins down exactly that blindness — it is the bug that made every
/// PS-queued child run (e.g. DomainAnalyser_&lt;tenant&gt; from Push-DomainAnalyserTenant) lose its
/// parent, so parents finalized and dispatched PostExecution while their children were still
/// running. The rest assert the fix: the wrapper reads the run name from the stamped
/// $global:CraftOperationContext and passes it back explicitly, with the ambient read kept as the
/// fallback for .NET callers and older wrapper scripts.
///
/// One class on purpose: the bridge queue is static, and xUnit runs methods of a single class
/// sequentially. Every test removes what it enqueued.
/// </summary>
public class OrchestratorBridgeLineageTests
{
    private static readonly FieldInfo s_pendingField = typeof(OrchestratorBridge)
        .GetField("s_pending", BindingFlags.NonPublic | BindingFlags.Static)!;

    private static readonly FieldInfo s_serviceField = typeof(OrchestratorBridge)
        .GetField("s_service", BindingFlags.NonPublic | BindingFlags.Static)!;

    /// <summary>
    /// Remove and return this test's entry from the static bridge queue, re-enqueueing anything
    /// else untouched — tests in other classes may have queued their own items.
    /// </summary>
    private static OrchestratorBridge.PendingOrchestration? TakePending(string name)
    {
        var queue = (ConcurrentQueue<OrchestratorBridge.PendingOrchestration>)s_pendingField.GetValue(null)!;
        OrchestratorBridge.PendingOrchestration? match = null;
        var keep = new List<OrchestratorBridge.PendingOrchestration>();
        while (queue.TryDequeue(out var item))
        {
            if (match == null && item.Name == name) match = item;
            else keep.Add(item);
        }
        foreach (var item in keep) queue.Enqueue(item);
        return match;
    }

    /// <summary>
    /// A worker configured exactly like production: ReuseThread, pipeline thread created (and its
    /// ExecutionContext frozen) by a first invocation made with NO operation context — the pool
    /// warmup. Anything a later invocation observes must have come through the stamped variable.
    /// </summary>
    private static async Task<PowerShellWorker> NewPinnedWorkerAsync()
    {
        var worker = new PowerShellWorker(98, InitialSessionState.CreateDefault2(), NullLogger.Instance);
        // Set exactly like PowerShellWorker.Initialize does. PowerShell.Create(iss) has already
        // opened the runspace, and the local-runspace setter accepts the change while no pipeline
        // runs — the reused thread is created at the next pipeline execution.
        worker.Runspace.ThreadOptions = PSThreadOptions.ReuseThread;
        if (worker.Runspace.RunspaceStateInfo.State == RunspaceState.BeforeOpen)
            worker.Runspace.Open();
        await worker.InvokeScriptAsync(ScriptBlock.Create("$null"));
        return worker;
    }

    [Fact]
    public async Task AmbientCapture_FromReusedPipelineThread_SeesNoParentRun()
    {
        // The bug, pinned down: the caller holds an operation context with a run name, yet the
        // bridge — invoked from inside the pipeline — captures nothing. This is why PS callers
        // MUST pass the parent explicitly; if this test ever starts failing, the runtime has
        // started flowing ExecutionContext into reused pipeline threads and the explicit
        // parameter is no longer load-bearing.
        var worker = await NewPinnedWorkerAsync();
        try
        {
            using (OperationContext.Set(new OperationContext.Invocation("Push-Task") { RunName = "LineageAmbientParent" }))
            {
                await worker.InvokeScriptAsync(ScriptBlock.Create(
                    "[Craft.Services.OrchestratorBridge]::QueueOrchestration('LineageChild-ambient', '[]', 4)"));
            }

            var pending = TakePending("LineageChild-ambient");
            Assert.NotNull(pending);
            Assert.Null(pending!.ParentRunName);
            Assert.False(pending.PendingChildRegistered);
        }
        finally
        {
            worker.Dispose();
        }
    }

    [Fact]
    public async Task ExplicitParent_FromStampedContext_CarriesLineage()
    {
        // The fix, end to end: worker stamps the caller's context into the runspace, the script
        // reads the run name back the way Start-CraftOrchestrator does, and passes it explicitly.
        var worker = await NewPinnedWorkerAsync();
        try
        {
            using (OperationContext.Set(new OperationContext.Invocation("Push-Task") { RunName = "LineageStampedParent" }))
            {
                await worker.InvokeScriptAsync(ScriptBlock.Create(@"
                    $OpContext = Get-Variable -Name 'CraftOperationContext' -Scope Global -ValueOnly -ErrorAction SilentlyContinue
                    [Craft.Services.OrchestratorBridge]::QueueOrchestration('LineageChild-explicit', '[]', 4, $null, $null, $null, $OpContext.RunName)"));
            }

            var pending = TakePending("LineageChild-explicit");
            Assert.NotNull(pending);
            Assert.Equal("LineageStampedParent", pending!.ParentRunName);
        }
        finally
        {
            worker.Dispose();
        }
    }

    [Fact]
    public void AmbientFallback_StillWorks_ForNetCallers()
    {
        // Old callers (and .NET call sites) pass no parent; the ambient read must keep working
        // where the AsyncLocal actually flows.
        using (OperationContext.Set(new OperationContext.Invocation("net-caller") { RunName = "LineageNetParent" }))
        {
            OrchestratorBridge.QueueOrchestration("LineageChild-net", "[]", 4);
        }

        var pending = TakePending("LineageChild-net");
        Assert.NotNull(pending);
        Assert.Equal("LineageNetParent", pending!.ParentRunName);
    }

    [Fact]
    public void EmptyParent_MeansNotPassed_AndFallsBackToAmbient()
    {
        // PowerShell marshals $null to "" for string parameters — an older wrapper passing an
        // absent value must not erase lineage a .NET caller's ambient context still provides.
        using (OperationContext.Set(new OperationContext.Invocation("net-caller") { RunName = "LineageEmptyParent" }))
        {
            OrchestratorBridge.QueueOrchestration("LineageChild-empty", "[]", 4,
                null, null, null, parentRunName: "");
        }

        var pending = TakePending("LineageChild-empty");
        Assert.NotNull(pending);
        Assert.Equal("LineageEmptyParent", pending!.ParentRunName);
    }

    [Fact]
    public void SelfParent_IsDroppedAtEnqueue()
    {
        // The recurring-run pattern: a run re-queues itself from inside its own context. It must
        // not arrive as its own child — that gated finalization on the run itself.
        using (OperationContext.Set(new OperationContext.Invocation("requeue") { RunName = "LineageSelf" }))
        {
            OrchestratorBridge.QueueOrchestration("LineageSelf", "[]", 4);
        }

        var pending = TakePending("LineageSelf");
        Assert.NotNull(pending);
        Assert.Null(pending!.ParentRunName);
    }

    [Fact]
    public async Task Drain_ReleasesTheParent_WhenTheChildIsNeverCreated()
    {
        // The deadlock-avoidance guarantee: a child registered at enqueue that then fails to start (here an
        // empty batch) must stop holding its parent, or the parent never reaches its barrier.
        var (svc, store) = NewStoreBackedService();
        var parent = await CreateRunAsync(store, "LineageDrainParent");

        var previousService = s_serviceField.GetValue(null);
        try
        {
            OrchestratorBridge.Initialize(svc);

            OrchestratorBridge.QueueOrchestration("LineageDrainChild", "[]", 4,
                null, null, null, parentRunName: "LineageDrainParent");
            Assert.Equal(2, (await store.GetRunAsync(parent.RunKey))!.Total);

            OrchestratorBridge.DrainPending();

            var after = (await store.GetRunAsync(parent.RunKey))!;
            Assert.Equal(1, after.Done);
            Assert.Equal(2, after.Total);
            Assert.Null(await store.GetRunByNameAsync("LineageDrainChild"));
        }
        finally
        {
            // The bridge service is static process state — put back whatever was there so this
            // test cannot redirect other tests' drains into this one.
            s_serviceField.SetValue(null, previousService);
        }
    }

    [Fact]
    public async Task IsRunActive_SeesUnfinishedRunsAndRunsQueuedToStart_SoACallerCanSkipAndSaySo()
    {
        var (svc, store) = NewStoreBackedService();
        await CreateRunAsync(store, "LineageActiveRun");

        var previousService = s_serviceField.GetValue(null);
        try
        {
            OrchestratorBridge.Initialize(svc);
            Assert.True(OrchestratorBridge.IsRunActive("LineageActiveRun"));
            Assert.False(OrchestratorBridge.IsRunActive("LineageNoSuchRun"));

            OrchestratorBridge.QueueOrchestration("LineageQueuedRun", "[]", 4);
            Assert.True(OrchestratorBridge.IsRunActive("LineageQueuedRun"));
            Assert.NotNull(TakePending("LineageQueuedRun"));
        }
        finally
        {
            s_serviceField.SetValue(null, previousService);
        }
    }

    private static (OrchestratorService Service, Craft.Storage.WorkStore Store) NewStoreBackedService()
    {
        var settings = new Craft.Configuration.CraftSettings();
        var config = new ConfigurationBuilder().AddInMemoryCollection([]).Build();
        var repo = new ScriptRepository(NullLogger<ScriptRepository>.Instance, settings);
        var pool = new PowerShellWorkerPool(repo, NullLogger<PowerShellWorkerPool>.Instance, config, settings);
        var limiter = new BackgroundTaskLimiter(NullLogger<BackgroundTaskLimiter>.Instance, config, settings, pool);
        var jobs = new Craft.Orchestration.JobManager(NullLogger<Craft.Orchestration.JobManager>.Instance, settings, limiter);
        var mem = new MemoryTableStore();
        var store = new Craft.Storage.WorkStore(NullLogger<Craft.Storage.WorkStore>.Instance, settings, mem);
        var svc = new OrchestratorService(NullLogger<OrchestratorService>.Instance, null!, limiter, jobs, store,
            new Craft.Storage.ResultStore(NullLogger<Craft.Storage.ResultStore>.Instance, settings, mem), config, settings);
        return (svc, store);
    }

    private static Task<Craft.Storage.RunHeader> CreateRunAsync(Craft.Storage.WorkStore store, string name)
    {
        var started = DateTime.UtcNow;
        return store.CreateRunAsync(new Craft.Storage.RunHeader
        {
            RunKey = Craft.Storage.WorkStore.RunKeyFor(name, started),
            Name = name,
            StartedUtc = started,
            TaskScriptName = "Invoke-CraftTask",
        }, [new Craft.Storage.WorkStore.NewTask("t0", new())]);
    }
}

using System.Collections.Concurrent;
using System.Management.Automation;
using System.Management.Automation.Runspaces;
using System.Reflection;
using System.Runtime.CompilerServices;
using Craft.Hosting;
using Craft.Orchestration;
using Craft.PowerShellHost;
using Craft.Services;
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
    public void Drain_ReleasesTheGate_WhenStartFails()
    {
        // The deadlock-avoidance guarantee: a child whose start attempt throws must stop gating
        // its parent. The service here is deliberately missing its storage fields, so
        // StartFromBatchAsync fails immediately — the finally in DrainPending must still release.
        var svc = (OrchestratorService)RuntimeHelpers.GetUninitializedObject(typeof(OrchestratorService));
        void Set(string field, object value) =>
            typeof(OrchestratorService).GetField(field, BindingFlags.NonPublic | BindingFlags.Instance)!
                .SetValue(svc, value);
        Set("_logger", NullLogger<OrchestratorService>.Instance);
        var activeRuns = new ConcurrentDictionary<string, OrchestratorRun>();
        Set("_activeRuns", activeRuns);
        Set("_childRuns", new ConcurrentDictionary<string, ConcurrentBag<string>>());
        Set("_recoveringChildren", new ConcurrentDictionary<string, bool>());
        var pendingChildRuns = new ConcurrentDictionary<string, int>();
        Set("_pendingChildRuns", pendingChildRuns);
        activeRuns.TryAdd("LineageDrainParent", new OrchestratorRun { Name = "LineageDrainParent", Status = "Running" });

        var previousService = s_serviceField.GetValue(null);
        try
        {
            OrchestratorBridge.Initialize(svc);

            OrchestratorBridge.QueueOrchestration("LineageDrainChild", "[]", 4,
                null, null, null, parentRunName: "LineageDrainParent");
            Assert.True(pendingChildRuns.ContainsKey("LineageDrainChild"));

            OrchestratorBridge.DrainPending();

            Assert.False(pendingChildRuns.ContainsKey("LineageDrainChild"));
            Assert.False(activeRuns.ContainsKey("LineageDrainChild"));
        }
        finally
        {
            // The bridge service is static process state — put back whatever was there so this
            // test cannot redirect other tests' drains into the crippled service.
            s_serviceField.SetValue(null, previousService);
        }
    }
}

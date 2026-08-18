using System.Collections.Concurrent;
using System.Reflection;
using System.Runtime.CompilerServices;
using Craft.Orchestration;
using Microsoft.Extensions.Logging.Abstractions;

namespace Craft.Tests;

/// <summary>
/// A run must never wait on itself to finalize — and must genuinely wait on its children.
///
/// The bridge registers the queued run under the ambient-or-explicit parent, so a run re-queued
/// from inside its own context (the recurring-run pattern) used to arrive as its own child. The
/// child-run guard then blocked finalization until the "child" left _activeRuns — which only happens
/// at finalization. Observed live as seven runs stuck "Running" for days, every task terminal,
/// Remaining=0, and their PostExecutions (audit log processing) never dispatched.
///
/// The guard has to stay specific: a REAL child (different name) must block its parent from the
/// moment it is REGISTERED — which happens at enqueue time, while the child exists nowhere but the
/// bridge queue. Registration takes a pending gate that only <see
/// cref="OrchestratorService.ReleasePendingChildRun"/> lifts, once the start attempt has either put
/// the child into _activeRuns (which takes over the blocking) or failed (so the parent must not
/// wait forever). All directions are asserted here.
/// </summary>
public class OrchestratorChildRunGuardTests
{
    /// <summary>An OrchestratorService with only the fields the child-run guard touches.</summary>
    private static OrchestratorService NewService()
    {
        var svc = (OrchestratorService)RuntimeHelpers.GetUninitializedObject(typeof(OrchestratorService));
        Set(svc, "_logger", NullLogger<OrchestratorService>.Instance);
        Set(svc, "_activeRuns", new ConcurrentDictionary<string, OrchestratorRun>());
        Set(svc, "_childRuns", new ConcurrentDictionary<string, ConcurrentBag<string>>());
        Set(svc, "_recoveringChildren", new ConcurrentDictionary<string, bool>());
        Set(svc, "_pendingChildRuns", new ConcurrentDictionary<string, int>());
        return svc;
    }

    private static void Set(object target, string field, object value) =>
        typeof(OrchestratorService).GetField(field, BindingFlags.NonPublic | BindingFlags.Instance)!
            .SetValue(target, value);

    private static T Get<T>(object target, string field) =>
        (T)typeof(OrchestratorService).GetField(field, BindingFlags.NonPublic | BindingFlags.Instance)!
            .GetValue(target)!;

    private static void Activate(OrchestratorService svc, string name) =>
        Get<ConcurrentDictionary<string, OrchestratorRun>>(svc, "_activeRuns")
            .TryAdd(name, new OrchestratorRun { Name = name, Status = "Running" });

    private static bool AllChildRunsComplete(OrchestratorService svc, string runName) =>
        (bool)typeof(OrchestratorService).GetMethod("AllChildRunsComplete",
            BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(svc, [runName])!;

    [Fact]
    public void SelfRegistration_IsRefused()
    {
        var svc = NewService();
        Activate(svc, "StandardsApply");

        Assert.False(svc.TryRegisterPendingChildRun("StandardsApply", "StandardsApply"));

        Assert.False(Get<ConcurrentDictionary<string, ConcurrentBag<string>>>(svc, "_childRuns")
            .ContainsKey("StandardsApply"));
        Assert.True(AllChildRunsComplete(svc, "StandardsApply"));
    }

    [Fact]
    public void InactiveParent_IsRefused()
    {
        // Runs queued from PostExecution land here: the spawning run has already finalized, so the
        // new run keeps its lineage on the row but must not gate anything.
        var svc = NewService();

        Assert.False(svc.TryRegisterPendingChildRun("FinalizedParent", "FollowUpRun"));
        Assert.True(AllChildRunsComplete(svc, "FinalizedParent"));
    }

    [Fact]
    public void PendingChild_BlocksParent_ThroughItsWholeLifecycle()
    {
        var svc = NewService();
        Activate(svc, "Parent");

        // Registered at enqueue time — the child exists nowhere but the bridge queue, and the
        // parent must already be blocked, because its own last task is what queued the child.
        Assert.True(svc.TryRegisterPendingChildRun("Parent", "Child"));
        Assert.False(AllChildRunsComplete(svc, "Parent"));

        // The drain starts the child (it enters the live graph) and then lifts the gate: still
        // blocked, the live graph has taken over.
        Activate(svc, "Child");
        svc.ReleasePendingChildRun("Child");
        Assert.False(AllChildRunsComplete(svc, "Parent"));

        // Child finalizes and is evicted from the live graph — the parent unblocks.
        Get<ConcurrentDictionary<string, OrchestratorRun>>(svc, "_activeRuns").TryRemove("Child", out _);
        Assert.True(AllChildRunsComplete(svc, "Parent"));
    }

    [Fact]
    public void FailedStart_ReleasesTheGate()
    {
        // A child that never starts (0 tasks, missing task function, storage failure) must stop
        // blocking once the start attempt is over — a leaked gate would defer the parent's
        // finalize for the process lifetime.
        var svc = NewService();
        Activate(svc, "Parent");

        Assert.True(svc.TryRegisterPendingChildRun("Parent", "StillbornChild"));
        Assert.False(AllChildRunsComplete(svc, "Parent"));

        svc.ReleasePendingChildRun("StillbornChild");
        Assert.True(AllChildRunsComplete(svc, "Parent"));
    }

    [Fact]
    public void DoubleQueuedChild_NeedsBothReleases()
    {
        // Two queue entries under the same child name (e.g. two parent tasks each queueing the
        // same fan-out) hold independent gates: the first release must not lift the second's.
        var svc = NewService();
        Activate(svc, "Parent");

        Assert.True(svc.TryRegisterPendingChildRun("Parent", "SharedChild"));
        Assert.True(svc.TryRegisterPendingChildRun("Parent", "SharedChild"));

        svc.ReleasePendingChildRun("SharedChild");
        Assert.False(AllChildRunsComplete(svc, "Parent"));

        svc.ReleasePendingChildRun("SharedChild");
        Assert.True(AllChildRunsComplete(svc, "Parent"));
    }

    [Fact]
    public void StaleSelfLink_DoesNotBlockFinalize()
    {
        // A self-link registered before the guard existed (or rebuilt from a pre-fix storage row)
        // can still be sitting in the bag. It must not count as an outstanding child.
        var svc = NewService();
        Activate(svc, "AuditLogSearchCreationV2");
        Get<ConcurrentDictionary<string, ConcurrentBag<string>>>(svc, "_childRuns")
            .TryAdd("AuditLogSearchCreationV2", ["AuditLogSearchCreationV2"]);

        Assert.True(AllChildRunsComplete(svc, "AuditLogSearchCreationV2"));
    }
}

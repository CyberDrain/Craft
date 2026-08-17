using System.Collections.Concurrent;
using System.Reflection;
using System.Runtime.CompilerServices;
using Craft.Orchestration;
using Microsoft.Extensions.Logging.Abstractions;

namespace Craft.Tests;

/// <summary>
/// A run must never wait on itself to finalize.
///
/// The bridge captures the ambient RunName as the parent of every queued orchestration, so a run
/// re-queued from inside its own context (the recurring-run pattern) arrived as its own child. The
/// child-run guard then blocked finalization until the "child" left _activeRuns — which only happens
/// at finalization. Observed live as seven runs stuck "Running" for days, every task terminal,
/// Remaining=0, and their PostExecutions (audit log processing) never dispatched.
///
/// The guard has to stay specific: a REAL child (different name) must still block its parent, which
/// is what sub-orchestration fan-out depends on. Both directions are asserted here.
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

        svc.TryRegisterChildRun("StandardsApply", "StandardsApply");

        Assert.False(Get<ConcurrentDictionary<string, ConcurrentBag<string>>>(svc, "_childRuns")
            .ContainsKey("StandardsApply"));
        Assert.True(AllChildRunsComplete(svc, "StandardsApply"));
    }

    [Fact]
    public void RealChild_StillBlocksParent_UntilItLeavesTheLiveGraph()
    {
        var svc = NewService();
        Activate(svc, "Parent");
        Activate(svc, "Child");

        svc.TryRegisterChildRun("Parent", "Child");

        Assert.False(AllChildRunsComplete(svc, "Parent"));

        // Child finalizes and is evicted from the live graph — the parent unblocks.
        Get<ConcurrentDictionary<string, OrchestratorRun>>(svc, "_activeRuns").TryRemove("Child", out _);
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

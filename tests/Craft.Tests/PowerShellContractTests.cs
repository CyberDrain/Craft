using System.Reflection;

namespace Craft.Tests;

/// <summary>
/// Guards the type names that downstream PowerShell reaches by fully-qualified name.
/// <para>
/// Hosted apps call these directly — <c>[Craft.Services.RealtimeBridge]::Publish(...)</c>,
/// <c>[Craft.Services.OrchestratorBridge]::QueueOrchestration(...)</c>. A namespace rename, a class
/// rename or a visibility change compiles perfectly and then fails at runtime inside the hosted app
/// with "Unable to find type", which is the worst possible place to discover it. Type forwarding
/// cannot soften the blow: <c>[TypeForwardedTo]</c> only works across assemblies, and all of these
/// live in Craft.dll.
/// </para>
/// <para>
/// If a test here fails, the fix is almost always to revert the rename — not to update the list.
/// Only edit the expected names as part of a deliberate, released breaking change coordinated with
/// the downstream apps.
/// </para>
/// </summary>
public class PowerShellContractTests
{
    private static readonly Assembly s_craft = typeof(Craft.Services.RealtimeBridge).Assembly;
    private static readonly string[] ContractSurface =
        {
            // Bridges
            "AppLifecycleBridge", "AuthBridge", "CacheBridge", "LogBridge", "OrchestratorBridge",
            "QueueBridge", "QueueStatusBridge", "RealtimeBridge", "SchedulerBridge",
            "StartupInfoBridge", "StatsHistoryBridge", "WorkerMetricsBridge",
            "PowerShellRunnerService",
            // DTOs a bridge can hand to PowerShell, directly or nested
            "CacheStats", "LogFileInfo", "StartupStats", "StatsDataPoint", "ScriptResult",
            "JobRecord", "JobSummary", "JobRunSummary", "JobDetail",
            "WorkerStats", "WorkerMetricsSnapshot", "PoolMetrics", "WorkerDetail", "LimiterMetrics",
            "JobMetrics", "MemoryMetrics", "WorkerSummary", "MemoryBreakdown", "GenerationDetail",
            // Nested public records on the bridges — the queued-item types the background side drains.
            "PendingOrchestration", "PendingPlannerRun", "PendingQueueCommand",
        };

    /// <summary>
    /// Every static bridge PowerShell invokes by name. Sourced from the <c>[Craft.Services.X]</c>
    /// references in Runtime/**/*.ps1, docs/configuration.md and downstream app code.
    /// </summary>
    public static TheoryData<string> ContractTypeNames() => new()
    {
        "Craft.Services.AppLifecycleBridge",
        "Craft.Services.AuthBridge",
        "Craft.Services.CacheBridge",
        "Craft.Services.LogBridge",
        "Craft.Services.OrchestratorBridge",
        "Craft.Services.QueueBridge",
        "Craft.Services.QueueStatusBridge",
        "Craft.Services.RealtimeBridge",
        "Craft.Services.SchedulerBridge",
        "Craft.Services.StartupInfoBridge",
        "Craft.Services.StatsHistoryBridge",
        "Craft.Services.WorkerMetricsBridge",
        "Craft.Services.PowerShellRunnerService",
    };

    [Theory]
    [MemberData(nameof(ContractTypeNames))]
    public void ContractType_ExistsAndIsPublic(string fullName)
    {
        var type = s_craft.GetType(fullName, throwOnError: false);

        Assert.True(type is not null,
            $"[{fullName}] is referenced from PowerShell but no longer exists under that name. " +
            "Renaming it (or its namespace) breaks downstream apps at runtime, not at build time. " +
            "Revert the rename, or treat this as a coordinated breaking change.");

        Assert.True(type!.IsPublic,
            $"[{fullName}] is no longer public, so PowerShell cannot resolve it.");
    }

    /// <summary>
    /// The mirror of the Azure Functions worker's response type. Hosted-app routers (CIPP's
    /// New-CippCoreRequest) select a function's response out of the pipeline by matching
    /// <c>PSObject.TypeNames</c> against this exact string, so the namespace is load-bearing.
    /// </summary>
    [Fact]
    public void HttpResponseContext_KeepsFunctionsWorkerTypeName()
    {
        const string expected = "Microsoft.Azure.Functions.PowerShellWorker.HttpResponseContext";
        var type = s_craft.GetType(expected, throwOnError: false);

        Assert.True(type is not null,
            $"{expected} must keep this exact name — hosted-app routers match on PSObject.TypeNames.");
        Assert.True(type!.IsPublic);
    }

    /// <summary>
    /// Catches the reverse mistake: a type left behind in Craft.Services that was meant to move to a
    /// real namespace during the Phase 2 restructure. Anything still in Craft.Services is, by
    /// definition, claiming to be part of the frozen PowerShell surface — so it should be a bridge, a
    /// DTO a bridge hands back, or the PowerShell runner.
    /// </summary>
    [Fact]
    public void CraftServicesNamespace_ContainsOnlyTheContractSurface()
    {
        var allowed = new HashSet<string>(ContractSurface);

        var actual = s_craft.GetExportedTypes()
            .Where(t => t.Namespace == "Craft.Services")
            .Select(t => t.Name)
            .ToList();

        var unexpected = actual.Where(n => !allowed.Contains(n)).OrderBy(n => n).ToList();
        Assert.True(unexpected.Count == 0,
            "These types sit in the frozen Craft.Services namespace but are not part of the documented " +
            "PowerShell contract. Either move them to a real namespace (Craft.Hosting, Craft.Storage, ...) " +
            "or add them to the allow-list here and to the README: " + string.Join(", ", unexpected));

        var missing = allowed.Where(n => !actual.Contains(n)).OrderBy(n => n).ToList();
        Assert.True(missing.Count == 0,
            "These types are on the contract allow-list but no longer exist in Craft.Services: "
            + string.Join(", ", missing));
    }
}

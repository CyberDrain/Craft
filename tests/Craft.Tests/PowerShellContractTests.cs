namespace Craft.Tests;

/// <summary>
/// Guards the type names that downstream PowerShell reaches by fully-qualified name.
/// <para>
/// Hosted apps call these directly — <c>[Craft.Services.RealtimeBridge]::Publish(...)</c>,
/// <c>[Craft.Services.OrchestratorBridge]::QueueOrchestration(...)</c>. A namespace rename, a class
/// rename or a visibility change compiles perfectly and then fails at runtime inside the hosted app
/// with "Unable to find type", which is the worst possible place to discover it.
/// </para>
/// <para>
/// After the Contracts extraction, bridge facades and <c>PowerShellRunnerService</c> live in the
/// host <c>Craft</c> assembly while DTOs live in <c>Craft.Contracts</c> (same <c>Craft.Services</c>
/// namespace). PowerShell resolves by FQN across loaded assemblies; these tests do the same.
/// </para>
/// <para>
/// If a test here fails, the fix is almost always to revert the rename — not to update the list.
/// Only edit the expected names as part of a deliberate, released breaking change coordinated with
/// the downstream apps.
/// </para>
/// </summary>
public class PowerShellContractTests
{
    private static readonly string[] ContractSurface =
        {
            // Bridges + runner (host assembly)
            "AppLifecycleBridge", "AuthBridge", "CacheBridge", "LogBridge", "OrchestratorBridge",
            "QueueBridge", "QueueStatusBridge", "RealtimeBridge", "SchedulerBridge",
            "StartupInfoBridge", "StatsHistoryBridge", "WorkerMetricsBridge",
            "PowerShellRunnerService",
            // DTOs (Craft.Contracts assembly, same namespace)
            "CacheStats", "LogFileInfo", "StartupStats", "StatsDataPoint", "ScriptResult",
            "JobSummary", "JobRunSummary", "JobDetail",
            "WorkerMetricsSnapshot", "PoolMetrics", "WorkerDetail", "LimiterMetrics",
            "JobMetrics", "MemoryMetrics", "WorkerSummary", "MemoryBreakdown", "GenerationDetail",
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

    private static Type? FindContractType(string fullName)
    {
        // Touch both assemblies so they are loaded before we scan.
        _ = typeof(Craft.Services.RealtimeBridge);
        _ = typeof(Craft.Services.ScriptResult);

        foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
        {
            var name = asm.GetName().Name;
            if (name is not ("Craft" or "Craft.Contracts"))
                continue;
            var type = asm.GetType(fullName, throwOnError: false);
            if (type is not null)
                return type;
        }

        return null;
    }

    private static IEnumerable<Type> EnumerateCraftServicesTypes()
    {
        _ = typeof(Craft.Services.RealtimeBridge);
        _ = typeof(Craft.Services.ScriptResult);

        foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
        {
            var name = asm.GetName().Name;
            if (name is not ("Craft" or "Craft.Contracts"))
                continue;

            foreach (var type in asm.GetExportedTypes())
            {
                if (type.Namespace == "Craft.Services")
                    yield return type;
            }
        }
    }

    [Theory]
    [MemberData(nameof(ContractTypeNames))]
    public void ContractType_ExistsAndIsPublic(string fullName)
    {
        var type = FindContractType(fullName);

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
        var type = typeof(Microsoft.Azure.Functions.PowerShellWorker.HttpResponseContext);

        Assert.Equal(expected, type.FullName);
        Assert.True(type.IsPublic);
        Assert.Equal("Craft.Contracts", type.Assembly.GetName().Name);
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

        var actual = EnumerateCraftServicesTypes()
            .Select(t => t.Name)
            .Distinct()
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

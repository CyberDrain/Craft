using System.Management.Automation;
using System.Management.Automation.Runspaces;
using Craft.Configuration;
using Craft.PowerShellHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace Craft.Tests;

/// <summary>
/// Workers instantiate the app's script modules from one parse per process (<see cref="SharedScriptModules"/>).
/// Measured on CIPP's built modules: ~2 MB and ~60 ms CPU per extra worker, against ~19 MB / ~250 ms for a
/// manifest import per worker and ~12 MB for the SSFE clone this replaces.
/// </summary>
public class WorkerModuleSharingTests : IDisposable
{
    private const string ModuleName = "CraftShareTestModule";
    private const string Exported = "Get-CraftShareThing";

    private readonly string _root = Path.Combine(Path.GetTempPath(), "craft-share-test-" + Guid.NewGuid().ToString("N")[..8]);
    private readonly List<PowerShellWorker> _workers = [];

    public void Dispose()
    {
        foreach (var worker in _workers) worker.Dispose();
        try { Directory.Delete(_root, recursive: true); } catch { /* temp dir */ }
        GC.SuppressFinalize(this);
    }

    [Fact]
    public void WorkersShareOneParseButEachHasItsOwnModuleScope()
    {
        var shared = Load(WriteModule());
        var first = Open(shared);
        var second = Open(shared);

        Assert.Same(FunctionAst(first), FunctionAst(second));
        Assert.Equal("1", Eval(first, "Add-CraftShareCount"));
        Assert.Equal("2", Eval(first, "Add-CraftShareCount"));
        Assert.Equal("1", Eval(second, "Add-CraftShareCount"));
    }

    [Fact]
    public void EveryWorkerReportsTheModuleItsFunctionsCameFrom()
    {
        // CIPP's scheduler refuses any command whose $Command.Module is not on its allowlist.
        var shared = Load(WriteModule());

        foreach (var worker in new[] { Open(shared), Open(shared) })
        {
            Assert.Equal(ModuleName, Eval(worker, $"(Get-Command {Exported}).Module.ToString()"));
            Assert.Equal("thing:helper", Eval(worker, Exported));
        }
    }

    [Theory]
    [InlineData("TypesToProcess = @('types.ps1xml')", "")]
    [InlineData("", ". $PSScriptRoot/extra.ps1")]
    public void AModuleItsFileCannotCarryIsImportedPerWorker(string manifestLine, string psm1Line)
    {
        var shared = Load(WriteModule(manifestLine, psm1Line));

        Assert.Empty(shared.Modules);
        Assert.Single(shared.NativeImports);
    }

    [Fact]
    public void DotSourcingInsideAFunctionStillShares()
    {
        var shared = Load(WriteModule(psm1Line: "function Invoke-CraftShareScript($Script) { . $Script }"));

        Assert.Single(shared.Modules);
        Assert.Empty(shared.NativeImports);
    }

    [Fact]
    public void RequiredAssembliesWrittenWithWindowsSeparatorsResolve()
    {
        var shared = Load(WriteModule(@"RequiredAssemblies = @('..\lib\Thing.dll')"));

        Assert.Equal(Path.GetFullPath(Path.Combine(_root, "lib", "Thing.dll")), Assert.Single(shared.Assemblies));
    }

    [Fact]
    public void TheRealPoolBuildsWorkersThatKnowTheirModulesAndShareTheirParse()
    {
        // Warm a real pool against the fixture module laid out where the pool looks, and check every worker.
        var settings = new CraftSettings();
        settings.Worker.HttpPoolSize = 0;
        settings.Worker.BgPoolSize = 2;

        var repo = new ScriptRepository(NullLogger<ScriptRepository>.Instance, settings);
        using var pool = new PowerShellWorkerPool(repo, NullLogger<PowerShellWorkerPool>.Instance,
            new ConfigurationBuilder().Build(), settings);

        pool.Initialize(enableHttp: false, enableBg: true);
        Assert.True(pool.WaitForBgReady(TimeSpan.FromMinutes(2)), "background pool never became ready");

        var workers = new List<PowerShellWorker>();
        try
        {
            for (int i = 0; i < settings.Worker.BgPoolSize; i++)
                workers.Add(pool.CheckoutBackground(CancellationToken.None));

            foreach (var worker in workers)
                Assert.Equal("CraftPoolFixture", Eval(worker, "(Get-Command Get-CraftPoolFixtureThing).Module.ToString()"));
            Assert.Same(FunctionAst(workers[0], "CraftPoolFixture", "Get-CraftPoolFixtureThing"),
                FunctionAst(workers[1], "CraftPoolFixture", "Get-CraftPoolFixtureThing"));
        }
        finally
        {
            foreach (var worker in workers)
                pool.Reclaim(worker, isHttp: false);
        }
    }

    // ── Fixture ─────────────────────────────────────────────────────────────────────────────────

    private static SharedScriptModules Load(string manifest) =>
        SharedScriptModules.Load([manifest], new(StringComparer.OrdinalIgnoreCase), NullLogger.Instance);

    private PowerShellWorker Open(SharedScriptModules shared)
    {
        var iss = InitialSessionState.CreateDefault();
        if (OperatingSystem.IsWindows())
            iss.ExecutionPolicy = Microsoft.PowerShell.ExecutionPolicy.Bypass;
        var worker = new PowerShellWorker(_workers.Count + 1, iss, NullLogger.Instance);
        _workers.Add(worker);
        var settings = new CraftSettings();
        worker.Initialize(new ScriptRepository(NullLogger<ScriptRepository>.Instance, settings), _root, settings, shared.Modules);
        return worker;
    }

    private static System.Management.Automation.Language.Ast FunctionAst(PowerShellWorker worker,
        string module = ModuleName, string function = Exported) =>
        worker.LoadedModules().Single(m => m.Name == module).ExportedFunctions[function].ScriptBlock.Ast;

    private static string Eval(PowerShellWorker worker, string script)
    {
        var results = worker.InvokeScriptAsync(ScriptBlock.Create(script)).GetAwaiter().GetResult();
        return results.FirstOrDefault()?.ToString() ?? string.Empty;
    }

    private string WriteModule(string manifestLine = "", string psm1Line = "")
    {
        var dir = Path.Combine(_root, ModuleName);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, $"{ModuleName}.psm1"), $$"""
            {{psm1Line}}
            $script:Count = 0
            function Get-CraftShareHelper { 'helper' }
            function {{Exported}} { "thing:$(Get-CraftShareHelper)" }
            function Add-CraftShareCount { $script:Count++; $script:Count }
            """);
        var manifest = Path.Combine(dir, $"{ModuleName}.psd1");
        File.WriteAllText(manifest, $$"""
            @{
                ModuleVersion     = '1.0.0'
                GUID              = '{{Guid.NewGuid()}}'
                RootModule        = '.\{{ModuleName}}.psm1'
                FunctionsToExport = @('{{Exported}}', 'Add-CraftShareCount')
                {{manifestLine}}
            }
            """);
        return manifest;
    }
}

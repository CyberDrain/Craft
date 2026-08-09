using System.Management.Automation;
using System.Management.Automation.Runspaces;
using Craft.Configuration;
using Craft.PowerShellHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace Craft.Tests;

/// <summary>
/// Cloned workers inject their functions as <see cref="SessionStateFunctionEntry"/> instead of
/// re-importing the modules — that is the whole reason a 14-worker pool warms in ~150 ms instead of
/// ~1.2 s. SSFE cannot carry module association, so without a deliberate repair every function on a
/// cloned worker reports an empty <c>ModuleName</c>.
/// <para>
/// That is not cosmetic. Downstream apps gate on module identity: CIPP's scheduler refuses any command
/// whose <c>$Command.Module</c> is not on its allowlist, and an empty module fails that test, so every
/// scheduled task is blocked with "unauthorized module: \Some-Command". The damage is total and silent
/// — the host is healthy, the runspaces are warm, and nothing runs.
/// </para>
/// <para>
/// These tests pin the repair. They are the only place the invariant is checked: it lives in metadata
/// nothing else reads, so a refactor of the clone path can drop it without failing anything else.
/// </para>
/// </summary>
public class ClonedWorkerModuleIdentityTests : IDisposable
{
    private const string ModuleName = "CraftTestModule";
    private const string ExportedFunction = "Get-CraftTestThing";

    private readonly string _moduleRoot = CreateModuleFixture();

    public void Dispose()
    {
        try { Directory.Delete(_moduleRoot, recursive: true); } catch { /* temp dir */ }
        GC.SuppressFinalize(this);
    }

    // ── The regression ──────────────────────────────────────────────────────────────────────────

    [Fact]
    public void AClonedWorkerReportsTheModuleAFunctionCameFrom()
    {
        var state = ExportFromNativeWorker();

        using var cloned = NewWorker(Pool().BuildClonedISS(state));
        cloned.RestoreExportedModuleNames(state);

        Assert.Equal(ModuleName, ModuleNameOf(cloned, ExportedFunction));
    }

    [Fact]
    public void WithoutTheRepairTheModuleIsLost()
    {
        // Not a redundant assertion of the negative: this is what makes the test above meaningful.
        // If SSFE ever starts preserving module association on its own, this fails and the repair
        // (and its two reflection dependencies) can be deleted rather than carried forever.
        var state = ExportFromNativeWorker();

        using var cloned = NewWorker(Pool().BuildClonedISS(state));

        Assert.Equal(string.Empty, ModuleNameOf(cloned, ExportedFunction));
    }

    // ── The shape downstream actually queries ───────────────────────────────────────────────────

    [Fact]
    public void GetCommandFiltersByModuleTheWayAnAllowlistDoes()
    {
        // CIPP asks `$Command.Module -notin @(...)`, comparing a PSModuleInfo against strings. That
        // only works because PSModuleInfo.ToString() returns the name, so assert on the object the
        // caller actually gets rather than on ModuleName alone.
        var state = ExportFromNativeWorker();

        using var cloned = NewWorker(Pool().BuildClonedISS(state));
        cloned.RestoreExportedModuleNames(state);

        Assert.Equal(ModuleName, Eval(cloned, $"(Get-Command {ExportedFunction}).Module.ToString()"));
        Assert.Equal("1", Eval(cloned, $"(Get-Command -Module {ModuleName} -CommandType Function | Measure-Object).Count"));
    }

    [Fact]
    public void FunctionsThatAlreadyCarryAModuleAreLeftAlone()
    {
        // The repair must not overwrite genuine module identity — binary modules and the
        // NativeImportModulePaths escape hatch import normally and already have a real PSModuleInfo.
        var state = ExportFromNativeWorker();

        using var native = NewWorker(NativeIss());
        var before = ModuleNameOf(native, "Get-Command");
        native.RestoreExportedModuleNames(state);

        Assert.Equal(before, ModuleNameOf(native, "Get-Command"));
        Assert.Equal("Microsoft.PowerShell.Core", before);
    }

    [Fact]
    public void TheRepairIsIdempotent()
    {
        // Workers are recycled and re-initialized; running the repair twice must not corrupt state.
        var state = ExportFromNativeWorker();

        using var cloned = NewWorker(Pool().BuildClonedISS(state));
        cloned.RestoreExportedModuleNames(state);
        cloned.RestoreExportedModuleNames(state);

        Assert.Equal(ModuleName, ModuleNameOf(cloned, ExportedFunction));
    }

    // ── The wiring, not just the mechanism ──────────────────────────────────────────────────────

    [Fact]
    public void TheRealPoolWarmUpProducesWorkersThatKnowTheirModules()
    {
        // Everything above proves RestoreExportedModuleNames works. None of it proves the pool still
        // calls it — and a dropped call site is exactly how this regresses (a large refactor that
        // rebuilds the clone path silently loses it). So warm a real pool against a fixture module
        // laid out where the pool looks, and ask a checked-out worker what it knows.
        var settings = new CraftSettings();
        settings.Worker.HttpPoolSize = 0;   // BG-only node keeps this to two runspaces
        // Two, not one: worker 1 is built by native import, and only workers 2..N are cloned
        // (`if (_bgPoolSize > 1)`). A pool of one would never exercise the path under test.
        settings.Worker.BgPoolSize = 2;

        var repo = new ScriptRepository(NullLogger<ScriptRepository>.Instance, settings);
        using var pool = new PowerShellWorkerPool(repo, NullLogger<PowerShellWorkerPool>.Instance,
            new ConfigurationBuilder().Build(), settings);

        pool.Initialize(enableHttp: false, enableBg: true);
        Assert.True(pool.WaitForBgReady(TimeSpan.FromMinutes(2)), "background pool never became ready");

        // Check out the whole pool rather than one worker: checkout order is not defined, and the
        // assertion has to land on the cloned worker, not just the natively-imported first one.
        var workers = new List<PowerShellWorker>();
        try
        {
            for (int i = 0; i < settings.Worker.BgPoolSize; i++)
                workers.Add(pool.CheckoutBackground(CancellationToken.None));

            foreach (var worker in workers)
                Assert.Equal("CraftPoolFixture",
                    Eval(worker, "(Get-Command Get-CraftPoolFixtureThing).ModuleName"));
        }
        finally
        {
            foreach (var worker in workers)
                pool.Reclaim(worker, isHttp: false);
        }
    }

    // ── Fixture ─────────────────────────────────────────────────────────────────────────────────

    private static PowerShellWorkerPool Pool()
    {
        var settings = new CraftSettings();
        var repo = new ScriptRepository(NullLogger<ScriptRepository>.Instance, settings);
        return new PowerShellWorkerPool(repo, NullLogger<PowerShellWorkerPool>.Instance,
            new ConfigurationBuilder().Build(), settings);
    }

    private static PowerShellWorker NewWorker(InitialSessionState iss) =>
        new(1, iss, NullLogger.Instance);

    private InitialSessionState NativeIss()
    {
        var iss = InitialSessionState.CreateDefault();
        if (OperatingSystem.IsWindows())
            iss.ExecutionPolicy = Microsoft.PowerShell.ExecutionPolicy.Bypass;
        iss.ImportPSModule(new[] { Path.Combine(_moduleRoot, ModuleName, $"{ModuleName}.psd1") });
        return iss;
    }

    /// <summary>A real worker with the module imported normally — the source the clone copies from.</summary>
    private ExportedModuleState ExportFromNativeWorker()
    {
        using var worker = NewWorker(NativeIss());
        return worker.ExportModuleState();
    }

    private static string ModuleNameOf(PowerShellWorker worker, string function) =>
        Eval(worker, $"(Get-Command {function} -ErrorAction SilentlyContinue).ModuleName");

    private static string Eval(PowerShellWorker worker, string script)
    {
        // The worker owns its runspace and does not expose it, so drive it through the same public
        // entry point the host dispatches through rather than adding a test-only seam.
        var results = worker.InvokeScriptAsync(ScriptBlock.Create(script)).GetAwaiter().GetResult();
        return results.FirstOrDefault()?.ToString() ?? string.Empty;
    }

    private static string CreateModuleFixture()
    {
        var root = Path.Combine(Path.GetTempPath(), "craft-clone-test-" + Guid.NewGuid().ToString("N")[..8]);
        var dir = Path.Combine(root, ModuleName);
        Directory.CreateDirectory(dir);

        File.WriteAllText(Path.Combine(dir, $"{ModuleName}.psm1"), $@"
function {ExportedFunction} {{
    [CmdletBinding()]
    param([string]$TenantFilter)
    ""thing:$TenantFilter""
}}
");
        File.WriteAllText(Path.Combine(dir, $"{ModuleName}.psd1"), $@"@{{
    ModuleVersion     = '1.0.0'
    GUID              = '{Guid.NewGuid()}'
    Author            = 'Craft.Tests'
    RootModule        = '{ModuleName}.psm1'
    FunctionsToExport = @('{ExportedFunction}')
    CmdletsToExport   = @()
    VariablesToExport = @()
    AliasesToExport   = @()
}}");
        return root;
    }
}

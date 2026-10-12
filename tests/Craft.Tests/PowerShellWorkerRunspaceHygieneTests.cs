using System.Management.Automation;
using System.Management.Automation.Runspaces;
using Craft.Configuration;
using Craft.PowerShellHost;
using Microsoft.Extensions.Logging.Abstractions;

namespace Craft.Tests;

/// <summary>State a worker carries between invocations: $Error is emptied, global JSON preloads are one shared object.</summary>
public class PowerShellWorkerRunspaceHygieneTests
{
    [Fact]
    public async Task Cleanup_EmptiesDollarError_AcrossWorkerInvocations()
    {
        var worker = new PowerShellWorker(98, InitialSessionState.CreateDefault2(), NullLogger.Instance);

        await worker.InvokeScriptAsync(ScriptBlock.Create("try { throw 'first' } catch { }; Get-Item -LiteralPath '/nope' -ErrorAction SilentlyContinue"));
        var r = await worker.InvokeScriptAsync(ScriptBlock.Create("$Error.Count"));

        Assert.Equal(0, (int)r[0].BaseObject);
        worker.Dispose();
    }

    [Fact]
    public async Task Initialize_SharesGlobalJsonPreloads_BetweenWorkers()
    {
        var dir = Directory.CreateTempSubdirectory("craft-preload-").FullName;
        File.WriteAllText(Path.Combine(dir, "perms.json"), """{ "Invoke-ListThing": { "Role": "Tenant.Read" } }""");
        File.WriteAllText(Path.Combine(dir, "roles.json"), """{ "readonly": { "include": ["*.Read"] } }""");
        var settings = new CraftSettings();
        settings.Worker.JsonPreloads =
        [
            new GlobalJsonPreload { File = "perms.json", Variable = "Perms", Scope = "global", AsHashtable = true },
            new GlobalJsonPreload { File = "roles.json", Variable = "Roles", Scope = "global" },
        ];
        var repo = new ScriptRepository(NullLogger<ScriptRepository>.Instance, settings);

        var a = new PowerShellWorker(96, InitialSessionState.CreateDefault2(), NullLogger.Instance);
        var b = new PowerShellWorker(97, InitialSessionState.CreateDefault2(), NullLogger.Instance);
        a.Initialize(repo, dir, settings);
        b.Initialize(repo, dir, settings);

        var fromA = await a.InvokeScriptAsync(ScriptBlock.Create("$global:Perms; $global:Roles"));
        var fromB = await b.InvokeScriptAsync(ScriptBlock.Create("$global:Perms; $global:Roles; $global:Perms['invoke-listthing'].Role"));

        Assert.Same(fromA[0].BaseObject, fromB[0].BaseObject);
        Assert.Same(fromA[1], fromB[1]);
        Assert.Equal("Tenant.Read", fromB[2].BaseObject); // the shared hashtable keeps its case-insensitive lookup

        a.Dispose(); b.Dispose();
        Directory.Delete(dir, recursive: true);
    }
}

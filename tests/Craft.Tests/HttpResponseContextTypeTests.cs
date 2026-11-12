using System.Management.Automation;
using System.Management.Automation.Runspaces;
using Craft.Configuration;
using Craft.PowerShellHost;
using Microsoft.Extensions.Logging.Abstractions;

namespace Craft.Tests;

/// <summary>
/// [HttpResponseContext] must not be compiled per runspace: every PowerShell class compile leaves per-type data in the
/// engine's static caches, so a worker recycle that compiled it again leaked a little for good.
/// </summary>
[Collection(LargeAllocationSerialTests.Name)]
public class HttpResponseContextTypeTests
{
    private static PowerShellWorker NewWorker(int id, string dir, CraftSettings settings)
    {
        var worker = new PowerShellWorker(id, InitialSessionState.CreateDefault2(), NullLogger.Instance);
        worker.Initialize(new ScriptRepository(NullLogger<ScriptRepository>.Instance, settings), dir, settings);
        return worker;
    }

    // Production runs far more distinct scripts than the engine's script block cache holds, so a recycled worker
    // finds its Initialize scripts evicted and compiles them again.
    private static void ClearScriptBlockCache() =>
        typeof(ScriptBlock).GetMethod("ClearScriptBlockCache", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!
            .Invoke(null, null);

    private static long HeapAfterGc()
    {
        GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
        return GC.GetTotalMemory(forceFullCollection: true);
    }

    [Fact]
    public async Task RebuildingWorkers_ReusesOneHttpResponseContextType()
    {
        const int Workers = 200;
        var dir = Directory.CreateTempSubdirectory("craft-hrc-").FullName;
        var settings = new CraftSettings();
        NewWorker(900, dir, settings).Dispose();
        var heapBefore = HeapAfterGc();
        for (var i = 0; i < Workers; i++)
        {
            ClearScriptBlockCache();
            NewWorker(1000 + i, dir, settings).Dispose();
        }
        var perWorker = (HeapAfterGc() - heapBefore) / Workers;

        var types = new HashSet<Type>();
        for (var i = 0; i < 5; i++)
        {
            ClearScriptBlockCache();
            using var worker = NewWorker(2000 + i, dir, settings);
            types.Add((Type)(await worker.InvokeScriptAsync(ScriptBlock.Create("[HttpResponseContext]")))[0].BaseObject);
        }
        Directory.Delete(dir, recursive: true);

        Assert.True(types.Count == 1, $"5 worker builds compiled {types.Count} HttpResponseContext types ({perWorker:+#;-#;0} bytes retained per build)");
    }

    [Fact]
    public async Task HttpResponseContext_KeepsItsShapeForScripts()
    {
        var dir = Directory.CreateTempSubdirectory("craft-hrc-").FullName;
        using var worker = NewWorker(950, dir, new CraftSettings());

        var plain = await worker.InvokeScriptAsync(ScriptBlock.Create(
            "$r = [HttpResponseContext]@{ StatusCode = [System.Net.HttpStatusCode]::Created; Body = @{ Results = 'ok' } }; $r.PSObject.TypeNames; $r.StatusCode; $r.Body.Results; $r.ContentType"));
        var aliased = await worker.InvokeScriptAsync(ScriptBlock.Create(
            "using namespace Microsoft.Azure.Functions.PowerShellWorker\n([HttpResponseContext]@{ StatusCode = 200 }).PSObject.TypeNames"));
        Directory.Delete(dir, recursive: true);

        Assert.Equal(["HttpResponseContext", "Microsoft.Azure.Functions.PowerShellWorker.HttpResponseContext", "System.Object",
            "Created", "ok", "application/json"], plain.Select(o => o.ToString()));
        Assert.Contains("Microsoft.Azure.Functions.PowerShellWorker.HttpResponseContext", aliased.Select(o => o.ToString()));
    }
}

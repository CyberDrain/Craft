using System.Management.Automation;
using System.Management.Automation.Runspaces;
using Craft.PowerShellHost;
using Microsoft.Extensions.Logging.Abstractions;

namespace Craft.Tests;

/// <summary>
/// A worker is reused across invocations, and an invocation that throws must not cost the next one its output.
/// It did: after a failed asynchronous invocation, the PowerShell object's own output buffer came back null from
/// the next EndInvoke, so that call returned nothing. A sequential run lost the result of the step after every
/// failed step; any reused worker lost one call's output after any failure.
/// </summary>
public class PowerShellWorkerOutputTests
{
    private static async Task<PowerShellWorker> NewWorkerAsync()
    {
        var iss = InitialSessionState.CreateDefault2();
        iss.Commands.Add(new SessionStateFunctionEntry("Step", "param($N) if ($N -eq 2) { throw \"boom $N\" }; \"out$N\""));
        var worker = new PowerShellWorker(96, iss, NullLogger.Instance);
        worker.Runspace.ThreadOptions = PSThreadOptions.ReuseThread;
        if (worker.Runspace.RunspaceStateInfo.State == RunspaceState.BeforeOpen) worker.Runspace.Open();
        await worker.InvokeScriptAsync(ScriptBlock.Create("$null"));
        return worker;
    }

    [Fact]
    public async Task TheCallAfterAFailedCall_StillReturnsItsOutput()
    {
        using var worker = await NewWorkerAsync();
        var outputs = new List<string>();
        for (var i = 0; i < 5; i++)
        {
            try { outputs.Add(string.Join(",", (await worker.InvokeAsync("Step", new() { ["N"] = i })).Select(o => o.ToString()))); }
            catch (RuntimeException) { outputs.Add("threw"); }
        }

        Assert.Equal(["out0", "out1", "threw", "out3", "out4"], outputs);
    }

    [Fact]
    public async Task TheScriptAfterAFailedScript_StillReturnsItsOutput()
    {
        using var worker = await NewWorkerAsync();
        await Assert.ThrowsAsync<RuntimeException>(() => worker.InvokeScriptAsync(ScriptBlock.Create("throw 'boom'")));

        Assert.Equal("after", Assert.Single(await worker.InvokeScriptAsync(ScriptBlock.Create("'after'"))).ToString());
    }
}

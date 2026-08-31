using System.Management.Automation;
using System.Management.Automation.Runspaces;
using Craft.PowerShellHost;
using Microsoft.Extensions.Logging.Abstractions;

namespace Craft.Tests;

/// <summary>A process-static AsyncLocal, so the leak survives the worker's global-variable sweep and we
/// are testing the ExecutionContext reset specifically (not variable cleanup). Its .Value lives in the
/// pipeline thread's ExecutionContext.</summary>
public static class LeakProbe
{
    public static readonly System.Threading.AsyncLocal<object?> Slot = new();
}

/// <summary>
/// End-to-end: the worker's per-invocation <c>Cleanup</c> resets the reused pipeline thread's
/// ExecutionContext, so an AsyncLocal set by one invocation does not leak into the next on the same
/// worker. Exercises the real <see cref="PowerShellWorker.InvokeScriptAsync"/> path.
/// </summary>
public class PowerShellWorkerContextResetTests
{
    [Fact]
    public async Task Cleanup_ResetsAsyncLocal_AcrossWorkerInvocations()
    {
        var worker = new PowerShellWorker(99, InitialSessionState.CreateDefault2(), NullLogger.Instance);
        worker.Runspace.ThreadOptions = PSThreadOptions.ReuseThread; // as Initialize sets it; must precede open

        // Production captures this in Initialize; do the same clean-thread capture here (Slot is unset now).
        await worker.InvokeScriptAsync(ScriptBlock.Create(
            "[Craft.PowerShellHost.PipelineExecutionContext]::CaptureBaselineIfNeeded()"));
        Assert.True(PipelineExecutionContext.Captured);

        // Invocation A: leak an AsyncLocal into the pipeline thread's ExecutionContext.
        await worker.InvokeScriptAsync(ScriptBlock.Create("[Craft.Tests.LeakProbe]::Slot.Value = 'LEAK'"));

        // Invocation B: read it back. Cleanup after A restored the clean baseline, so it is gone.
        var r = await worker.InvokeScriptAsync(ScriptBlock.Create(
            "[pscustomobject]@{ V = [Craft.Tests.LeakProbe]::Slot.Value }"));

        Assert.Null(r[0].Properties["V"]?.Value); // reset cleared the AsyncLocal on the real worker path

        worker.Dispose();
    }
}

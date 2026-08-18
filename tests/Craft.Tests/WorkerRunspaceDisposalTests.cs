using System.Management.Automation.Runspaces;
using Craft.PowerShellHost;
using Microsoft.Extensions.Logging.Abstractions;

namespace Craft.Tests;

/// <summary>
/// Disposing a worker must dispose its runspace. <c>PowerShell.Create(iss)</c> ASSIGNS the runspace
/// (caller-owned) rather than creating it lazily, so <c>PowerShell.Dispose()</c> deliberately leaves it
/// open — and an open runspace with <c>ReuseThread</c> keeps a dedicated pipeline thread alive, which
/// roots the entire session state (every SSFE-injected function of every module) through any GC.
/// Measured live: ~20 MB retained per recycled worker, ~2 GB after 95 recycles, indistinguishable from
/// a managed-heap leak because that is exactly what it is.
/// <para>
/// This is the only place the invariant is checked. Nothing functional breaks when the runspace
/// outlives the worker — the replacement worker works fine — so a refactor of Dispose can silently
/// reintroduce the leak without failing anything else.
/// </para>
/// </summary>
public class WorkerRunspaceDisposalTests
{
    [Fact]
    public void DisposeClosesTheRunspace()
    {
        var worker = new PowerShellWorker(1, InitialSessionState.CreateDefault2(), NullLogger.Instance);
        var runspace = worker.Runspace;
        if (runspace.RunspaceStateInfo.State == RunspaceState.BeforeOpen)
            runspace.Open();

        worker.Dispose();

        Assert.Equal(RunspaceState.Closed, runspace.RunspaceStateInfo.State);
    }

    [Fact]
    public void DisposeOfANeverOpenedWorkerStillTearsTheRunspaceDown()
    {
        // The base worker used for ISS cloning is created and disposed without ever running a
        // pipeline; its runspace must not survive either.
        var worker = new PowerShellWorker(2, InitialSessionState.CreateDefault2(), NullLogger.Instance);
        var runspace = worker.Runspace;

        worker.Dispose();

        Assert.NotEqual(RunspaceState.Opened, runspace.RunspaceStateInfo.State);
    }
}

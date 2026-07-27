// NAMESPACE PINNED — do not change.
// Downstream PowerShell reaches these types by fully-qualified name, e.g.
//   [Craft.Services.RealtimeBridge]::Publish($userId, $jobId, 'start', $data)
// Renaming the namespace compiles fine and then fails at runtime in the hosted app
// ("Unable to find type"). Type forwarding cannot help — it only works across assemblies.
// The folder is free to move; the namespace is a published contract.
namespace Craft.Services;

// ── Data models ──

// Fields, not properties, by necessity: IsBusy is volatile and the _total* fields are passed to
// Interlocked by ref. Neither is possible on a property. This is internal mutable state, projected
// into WorkerDetail before anything outside the bridge sees it.
[System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1051:Do not declare visible instance fields",
    Justification = "IsBusy is volatile and _total* fields are Interlocked targets; neither works on a property.")]
public class WorkerStats
{
    public int WorkerId;
    public bool IsHttp;
    public volatile bool IsBusy;
    public string? CurrentFunction;
    public DateTime? LastCheckoutUtc;
    public DateTime? LastReclaimUtc;
    public long LastDurationMs;
    public long MinDurationMs = long.MaxValue;
    public long MaxDurationMs;
    public long CheckoutAllocBytes;
    public long LastAllocBytes;

    // Interlocked fields
    internal long _totalInvocations;
    internal long _totalBusyMs;
    internal long _totalFaults;
    internal long _totalAllocBytes;
}

namespace Craft.Hosting;

// Fields, not properties, by necessity: IsBusy is volatile and the _total* fields are passed to
// Interlocked by ref. Neither is possible on a property. This is internal mutable state, projected
// into WorkerDetail (Craft.Contracts) before anything outside the host sees it.
[System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1051:Do not declare visible instance fields",
    Justification = "IsBusy is volatile and _total* fields are Interlocked targets; neither works on a property.")]
internal sealed class WorkerStats
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
    public long _totalInvocations;
    public long _totalBusyMs;
    public long _totalFaults;
    public long _totalAllocBytes;
}

namespace Craft.Hosting;

/// <summary>Per-request segment ticks filled in by the runner when profiling is enabled.</summary>
internal sealed class DispatchTiming
{
    public long CheckoutTicks;
    public long InvokeTicks;
    public long ExtractTicks;
}

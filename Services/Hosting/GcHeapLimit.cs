using Craft.Configuration;

namespace Craft.Hosting;

/// <summary>
/// Host-tier GC heap sizing — the companion to <see cref="SkuProfileSelector"/>.
/// <para>
/// The image bakes a conservative <c>DOTNET_GCHeapHardLimit</c> sized for the smallest supported
/// tier, and the CLR consumes that env var before any managed code runs — a tier with more memory
/// to give can only raise the limit after startup. .NET 8 provides exactly that hatch:
/// <see cref="AppContext.SetData"/> under the <c>GCHeapHardLimit</c> key, then
/// <see cref="GC.RefreshMemoryLimit"/>, which re-reads the limit configuration (AppContext data
/// takes precedence over the startup env var) and applies it to the running GC.
/// </para>
/// <para>
/// Best-effort under the same contract as pool sizing: any failure — including
/// <see cref="GC.RefreshMemoryLimit"/> refusing a limit below what the heap has already
/// committed — logs and keeps the baseline rather than taking the host down over a sizing hint.
/// </para>
/// </summary>
public static class GcHeapLimit
{
    /// <summary>
    /// Applies <see cref="SkuProfile.GCHeapHardLimitMB"/> from the matched profile, if it carries one.
    /// </summary>
    /// <param name="profile">The profile <see cref="SkuProfileSelector"/> matched, or null.</param>
    /// <param name="log">Sink for the operator-facing before/after line.</param>
    /// <param name="refresh">The actual limit change, injectable for tests; null = the real
    /// AppContext + <see cref="GC.RefreshMemoryLimit"/> path.</param>
    /// <returns><see langword="true"/> when a limit was applied.</returns>
    public static bool Apply(SkuProfile? profile, Action<string> log, Action<ulong>? refresh = null)
    {
        ArgumentNullException.ThrowIfNull(log);

        var mb = profile?.GCHeapHardLimitMB ?? 0;
        if (mb <= 0) return false;

        refresh ??= SetAndRefresh;
        var beforeMB = GC.GetGCMemoryInfo().TotalAvailableMemoryBytes / (1024 * 1024);
        try
        {
            refresh((ulong)mb * 1024 * 1024);
            var afterMB = GC.GetGCMemoryInfo().TotalAvailableMemoryBytes / (1024 * 1024);
            log($"[System] GC heap hard limit set to {mb} MB by SkuProfile " +
                $"(TotalAvailableMemory {beforeMB} MB -> {afterMB} MB)");
            return true;
        }
        catch (Exception ex)
        {
            // Same contract as pool sizing: a sizing hint must never prevent startup.
            log($"[System] GC heap hard limit refresh to {mb} MB failed " +
                $"({ex.GetType().Name}: {ex.Message}); keeping {beforeMB} MB");
            return false;
        }
    }

    /// <summary>Convenience overload writing to the console, like <c>SkuProfileSelector.Apply(settings)</c>.</summary>
    public static bool Apply(SkuProfile? profile) => Apply(profile, Console.WriteLine);

    private static void SetAndRefresh(ulong bytes)
    {
        AppContext.SetData("GCHeapHardLimit", bytes);
        GC.RefreshMemoryLimit();
    }
}

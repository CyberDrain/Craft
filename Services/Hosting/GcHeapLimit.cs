using System.Globalization;
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
/// takes precedence over the startup env var — the runtime deliberately won't re-read that var) and
/// applies it to the running GC.
/// </para>
/// <para>
/// Because that AppContext write overrides <c>DOTNET_GCHeapHardLimit</c>, a fleet-wide profile value
/// would otherwise silently countermand a limit an operator hand-set on a single instance. The
/// <c>CRAFT_GC_HEAP_LIMIT_MB</c> env var is the per-instance escape hatch that restores that control,
/// following the same pattern as <c>CRAFT_API_CONCURRENCY_LIMIT</c> and <c>CRAFT_HTTP_QUEUE_TIMEOUT</c>:
/// when set it wins over the matched profile. A value of <c>0</c> disables the cap entirely (refresh to
/// the container's own memory allowance, discarding the baked limit).
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
    /// Applies a GC heap hard limit: the <c>CRAFT_GC_HEAP_LIMIT_MB</c> env override if set, otherwise
    /// <see cref="SkuProfile.GCHeapHardLimitMB"/> from the matched profile.
    /// </summary>
    /// <param name="profile">The profile <see cref="SkuProfileSelector"/> matched, or null.</param>
    /// <param name="log">Sink for the operator-facing before/after line.</param>
    /// <param name="refresh">The actual limit change, injectable for tests; null = the real
    /// AppContext + <see cref="GC.RefreshMemoryLimit"/> path.</param>
    /// <param name="readEnv">Reader for <c>CRAFT_GC_HEAP_LIMIT_MB</c>, injectable for tests; null =
    /// the real process environment.</param>
    /// <returns><see langword="true"/> when a limit change was applied.</returns>
    public static bool Apply(SkuProfile? profile, Action<string> log, Action<ulong>? refresh = null,
                             Func<string?>? readEnv = null)
    {
        ArgumentNullException.ThrowIfNull(log);

        // Per-instance escape hatch. CRAFT_GC_HEAP_LIMIT_MB, when it parses to a non-negative integer,
        // wins over the fleet-wide profile — same shape as ResolvedApiConcurrencyLimit / ResolveHttpQueueTimeout.
        //   > 0 : set that many MB, overriding the profile.
        //     0 : disable the cap entirely — refresh to the container's own memory allowance, discarding
        //         the image-baked DOTNET_GCHeapHardLimit. Removing the cap only ever raises the limit, so
        //         it cannot trip RefreshMemoryLimit's "below committed heap" guard.
        //   unset / negative / unparseable : defer to the profile.
        readEnv ??= () => Environment.GetEnvironmentVariable("CRAFT_GC_HEAP_LIMIT_MB");
        var envMB = int.TryParse(readEnv(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var e) && e >= 0
            ? e
            : (int?)null;

        int mb;
        string source;
        if (envMB is int fromEnv)
        {
            mb = fromEnv;
            source = "CRAFT_GC_HEAP_LIMIT_MB";
        }
        else
        {
            source = "SkuProfile";
            // A profile carries the same three-way meaning as the env override above:
            //   null / omitted / negative : no opinion — keep the process baseline (like unused SkuProfiles).
            //     0 : disable the cap entirely, identical to CRAFT_GC_HEAP_LIMIT_MB=0 (a large tier that has
            //         more memory to give than the baked limit lets it use container/physical memory).
            //   > 0 : set that many MB.
            var profileMB = profile?.GCHeapHardLimitMB;
            if (profileMB is null or < 0) return false;
            mb = profileMB.Value;
        }

        refresh ??= SetAndRefresh;
        var beforeMB = GC.GetGCMemoryInfo().TotalAvailableMemoryBytes / (1024 * 1024);
        try
        {
            refresh((ulong)mb * 1024 * 1024);
            var afterMB = GC.GetGCMemoryInfo().TotalAvailableMemoryBytes / (1024 * 1024);
            var what = mb == 0
                ? $"GC heap hard limit disabled by {source} — deferring to container/physical memory"
                : $"GC heap hard limit set to {mb} MB by {source}";
            log($"[System] {what} (TotalAvailableMemory {beforeMB} MB -> {afterMB} MB)");
            return true;
        }
        catch (Exception ex)
        {
            // Same contract as pool sizing: a sizing hint must never prevent startup.
            var target = mb == 0 ? "disable" : $"{mb} MB";
            log($"[System] GC heap hard limit change ({target}) by {source} failed " +
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

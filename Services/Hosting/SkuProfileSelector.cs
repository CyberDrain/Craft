using Craft.Configuration;

namespace Craft.Hosting;

/// <summary>
/// Host-tier pool sizing. Picks the first <see cref="SkuProfile"/> matching the runtime the process
/// actually landed on and overwrites <c>Worker.HttpPoolSize</c> / <c>Worker.BgPoolSize</c> with its
/// values.
/// <para>
/// This runs as a <c>PostConfigure</c> on <see cref="CraftSettings"/>, so it must apply before any
/// consumer resolves the options — a worker pool that reads its size before the profile lands would
/// silently size itself for the wrong tier.
/// </para>
/// <para>
/// Detection is best-effort by design: any failure logs and leaves the baseline sizes untouched
/// rather than taking the host down over a pool-sizing hint.
/// </para>
/// </summary>
public static class SkuProfileSelector
{
    /// <summary>
    /// Applies the matching profile to <paramref name="settings"/>, mutating it in place.
    /// </summary>
    /// <param name="settings">Settings to adjust.</param>
    /// <param name="processorCount">CPU count to match profiles against.</param>
    /// <param name="env">Environment lookup for each profile's <see cref="SkuProfile.SkuEnv"/>.</param>
    /// <param name="log">Sink for the operator-facing explanation of what matched and why.</param>
    /// <returns>The profile that was applied, or <see langword="null"/> if the baseline was kept.</returns>
    public static SkuProfile? Apply(CraftSettings settings, int processorCount,
                                    Func<string, string?> env, Action<string> log)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(env);
        ArgumentNullException.ThrowIfNull(log);

        var worker = settings.Worker;

        if (worker.IgnoreSkuProfiles)
        {
            log($"[System] SkuProfile evaluation skipped: IgnoreSkuProfiles=true; " +
                $"using baseline HttpPoolSize={worker.HttpPoolSize} BgPoolSize={worker.BgPoolSize}");
            return null;
        }

        // Feature not configured — stay silent rather than logging on every start.
        if (worker.SkuProfiles.Count == 0) return null;

        try
        {
            foreach (var profile in worker.SkuProfiles)
            {
                string? skuValue = null;
                bool skuMatch;
                if (string.IsNullOrWhiteSpace(profile.SkuEnv))
                {
                    skuMatch = true;
                }
                else
                {
                    skuValue = env(profile.SkuEnv) ?? "";
                    skuMatch = string.Equals(skuValue, profile.Sku ?? "", StringComparison.OrdinalIgnoreCase);
                }

                // Cpu unset or 0 means "any CPU count".
                var cpuMatch = profile.Cpu is null or 0 || profile.Cpu == processorCount;

                if (!skuMatch || !cpuMatch) continue;

                log($"[System] SkuProfile matched (SkuEnv='{profile.SkuEnv}' Sku='{profile.Sku}' " +
                    $"Cpu={profile.Cpu}) for runtime ({profile.SkuEnv}='{skuValue}' " +
                    $"ProcessorCount={processorCount}); " +
                    $"applying HttpPoolSize={profile.HttpPoolSize} BgPoolSize={profile.BgPoolSize}");

                worker.HttpPoolSize = profile.HttpPoolSize;
                worker.BgPoolSize = profile.BgPoolSize;
                return profile;
            }

            log($"[System] No SkuProfile matched runtime (ProcessorCount={processorCount}, " +
                $"checked {worker.SkuProfiles.Count} profile(s)); " +
                $"using baseline HttpPoolSize={worker.HttpPoolSize} BgPoolSize={worker.BgPoolSize}");
            return null;
        }
        catch (Exception ex)
        {
            // Deliberately broad: pool sizing is a hint, and no failure mode here justifies refusing
            // to start. The baseline sizes are already valid.
            log($"[System] SkuProfile detection failed ({ex.GetType().Name}: {ex.Message}); " +
                $"using baseline HttpPoolSize={worker.HttpPoolSize} BgPoolSize={worker.BgPoolSize}");
            return null;
        }
    }

    /// <summary>Convenience overload using the real processor count, environment and console.</summary>
    public static SkuProfile? Apply(CraftSettings settings) =>
        Apply(settings, Environment.ProcessorCount, Environment.GetEnvironmentVariable, Console.WriteLine);
}

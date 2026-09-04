using System.Globalization;
using Craft.Configuration;

namespace Craft.Hosting;

/// <summary>
/// Host-tier pool sizing. Picks the first <see cref="SkuProfile"/> matching the runtime the process
/// actually landed on and overwrites <c>Worker.HttpPoolSize</c> / <c>Worker.BgPoolSize</c> with its
/// values.
/// <para>
/// A second matrix is supported: when the env var named by <see cref="WorkerSettings.SkuProfilesAltEnv"/>
/// is present (non-empty), <see cref="WorkerSettings.SkuProfilesAlt"/> is matched instead of
/// <see cref="WorkerSettings.SkuProfiles"/> (when non-empty), so a deployment can ship one config with
/// two sizings and pick between them with a single env var — e.g. a smaller per-instance matrix for
/// instances packed onto a shared App Service Plan.
/// </para>
/// <para>
/// A per-instance escape hatch beats both matrices: <c>CRAFT_HTTP_POOL_SIZE</c> / <c>CRAFT_BG_POOL_SIZE</c>,
/// when set to a non-negative integer, win over the matched profile (and baseline), following the same
/// shape as <c>CRAFT_GC_HEAP_LIMIT_MB</c>.
/// </para>
/// <para>
/// This runs as a <c>PostConfigure</c> on <see cref="CraftSettings"/>, so it must apply before any
/// consumer resolves the options — a worker pool that reads its size before the profile lands would
/// silently size itself for the wrong tier.
/// </para>
/// <para>
/// Detection is best-effort by design: any failure logs and leaves the baseline sizes untouched
/// rather than taking the host down over a pool-sizing hint. The returned profile may also carry a
/// <see cref="SkuProfile.GCHeapHardLimitMB"/>; applying that is a process-wide side effect and lives in
/// <see cref="GcHeapLimit"/>, not here.
/// </para>
/// </summary>
public static class SkuProfileSelector
{
    /// <summary>
    /// Applies the matching profile (and any pool-size env overrides) to <paramref name="settings"/>,
    /// mutating it in place.
    /// </summary>
    /// <param name="settings">Settings to adjust.</param>
    /// <param name="processorCount">CPU count to match profiles against.</param>
    /// <param name="env">Environment lookup for SkuEnv, the second-matrix flag, and the pool-size overrides.</param>
    /// <param name="log">Sink for the operator-facing explanation of what matched and why.</param>
    /// <returns>The profile that was applied, or <see langword="null"/> if no profile matched.</returns>
    public static SkuProfile? Apply(CraftSettings settings, int processorCount,
                                    Func<string, string?> env, Action<string> log)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(env);
        ArgumentNullException.ThrowIfNull(log);

        var worker = settings.Worker;
        SkuProfile? matched = null;

        if (worker.IgnoreSkuProfiles)
        {
            log($"[System] SkuProfile evaluation skipped: IgnoreSkuProfiles=true; " +
                $"using baseline HttpPoolSize={worker.HttpPoolSize} BgPoolSize={worker.BgPoolSize}");
        }
        else
        {
            try
            {
                matched = MatchAndApply(worker, processorCount, env, log);
            }
            catch (Exception ex)
            {
                // Deliberately broad: pool sizing is a hint, and no failure mode here justifies refusing
                // to start. The baseline sizes are already valid.
                log($"[System] SkuProfile detection failed ({ex.GetType().Name}: {ex.Message}); " +
                    $"using baseline HttpPoolSize={worker.HttpPoolSize} BgPoolSize={worker.BgPoolSize}");
            }
        }

        // Per-instance escape hatch, applied last and unconditionally so it wins over profile/baseline:
        // CRAFT_HTTP_POOL_SIZE / CRAFT_BG_POOL_SIZE, same shape as CRAFT_GC_HEAP_LIMIT_MB.
        ApplyPoolSizeEnvOverrides(worker, env, log);

        return matched;
    }

    /// <summary>Selects the default vs hosted matrix, matches it, and applies the winner. Returns the
    /// applied profile, or null when the baseline was kept.</summary>
    private static SkuProfile? MatchAndApply(WorkerSettings worker, int processorCount,
                                             Func<string, string?> env, Action<string> log)
    {
        var altSelected = !string.IsNullOrWhiteSpace(worker.SkuProfilesAltEnv)
            && !string.IsNullOrWhiteSpace(env(worker.SkuProfilesAltEnv));

        List<SkuProfile> profiles;
        string matrix;
        if (altSelected && worker.SkuProfilesAlt.Count > 0)
        {
            profiles = worker.SkuProfilesAlt;
            matrix = "alt";
        }
        else
        {
            profiles = worker.SkuProfiles;
            matrix = "default";
            if (altSelected && worker.SkuProfilesAlt.Count == 0)
                log($"[System] Second SkuProfiles matrix selected ({worker.SkuProfilesAltEnv} is set) but " +
                    $"SkuProfilesAlt is empty; using the default SkuProfiles matrix.");
        }

        // Feature not configured for the selected matrix — stay silent rather than logging on every start.
        if (profiles.Count == 0) return null;

        foreach (var profile in profiles)
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

            log($"[System] SkuProfile matched [{matrix}] (SkuEnv='{profile.SkuEnv}' Sku='{profile.Sku}' " +
                $"Cpu={profile.Cpu}) for runtime ({profile.SkuEnv}='{skuValue}' ProcessorCount={processorCount}); " +
                $"applying HttpPoolSize={profile.HttpPoolSize} BgPoolSize={profile.BgPoolSize}");

            worker.HttpPoolSize = profile.HttpPoolSize;
            worker.BgPoolSize = profile.BgPoolSize;
            return profile;
        }

        log($"[System] No SkuProfile matched runtime in the {matrix} matrix (ProcessorCount={processorCount}, " +
            $"checked {profiles.Count} profile(s)); using baseline HttpPoolSize={worker.HttpPoolSize} BgPoolSize={worker.BgPoolSize}");
        return null;
    }

    private static void ApplyPoolSizeEnvOverrides(WorkerSettings worker, Func<string, string?> env, Action<string> log)
    {
        if (TryReadEnvInt(env, "CRAFT_HTTP_POOL_SIZE", out var http))
        {
            log($"[System] HttpPoolSize overridden to {http} by CRAFT_HTTP_POOL_SIZE (was {worker.HttpPoolSize})");
            worker.HttpPoolSize = http;
        }
        if (TryReadEnvInt(env, "CRAFT_BG_POOL_SIZE", out var bg))
        {
            log($"[System] BgPoolSize overridden to {bg} by CRAFT_BG_POOL_SIZE (was {worker.BgPoolSize})");
            worker.BgPoolSize = bg;
        }
    }

    // Non-negative int env override, robust to a throwing env lookup (pool sizing must never break startup).
    private static bool TryReadEnvInt(Func<string, string?> env, string name, out int value)
    {
        value = 0;
        try
        {
            return int.TryParse(env(name), NumberStyles.Integer, CultureInfo.InvariantCulture, out value) && value >= 0;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Convenience overload using the real processor count, environment and console.</summary>
    public static SkuProfile? Apply(CraftSettings settings) =>
        Apply(settings, Environment.ProcessorCount, Environment.GetEnvironmentVariable, Console.WriteLine);
}

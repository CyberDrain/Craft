using Craft.Configuration;
using Microsoft.Extensions.Options;

namespace Craft.Hosting;

/// <summary>
/// Options-pattern registration for <see cref="CraftSettings"/>: bind <c>App</c>, apply shared
/// post-bind fixes, and fail fast on a few high-value invariants.
/// </summary>
internal static class CraftSettingsConfiguration
{
    /// <summary>
    /// Registers <see cref="CraftSettings"/> via <c>AddOptions</c> + <c>BindConfiguration("App")</c>,
    /// shared post-configure (SKU profiles, AzureWebJobsStorage fallback), and
    /// <c>ValidateOnStart</c>. Pool-size rules are role-aware.
    /// </summary>
    public static OptionsBuilder<CraftSettings> AddCraftSettings(
        this IServiceCollection services,
        IConfiguration configuration,
        CraftRoles roles)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(roles);

        return services
            .AddOptions<CraftSettings>()
            .BindConfiguration("App")
            .PostConfigure(s => ApplyPostBind(s, configuration))
            .Validate(IsValidReadinessMode,
                "App:ReadinessMode must be Immediate, HttpReady, or AllReady.")
            .Validate(IsValidWarmupMode,
                "App:Worker:WarmupMode must be BeforeReady, AfterReady, or Background.")
            .Validate(
                s => IsValidPoolSizes(s, roles),
                "App:Worker pool sizes must be at least 1 for each pool this node's roles use " +
                "(HttpPoolSize when Http is enabled, BgPoolSize when Background is enabled).")
            .ValidateOnStart();
    }

    /// <summary>
    /// SKU pool overrides, root <c>AzureWebJobsStorage</c> → <c>Storage.ConnectionString</c> when unset,
    /// and legacy root-level background-limiter keys → <see cref="CraftSettings.BackgroundLimiter"/>.
    /// </summary>
    public static void ApplyPostBind(CraftSettings settings, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(configuration);

        SkuProfileSelector.Apply(settings);

        if (string.IsNullOrWhiteSpace(settings.Storage.ConnectionString))
            settings.Storage.ConnectionString = configuration["AzureWebJobsStorage"] ?? "";

        ApplyLegacyBackgroundLimiterKeys(settings.BackgroundLimiter, configuration);
    }

    /// <summary>
    /// Overlay deprecated root-level <c>Background*</c> keys onto <see cref="BackgroundLimiterSettings"/>.
    /// Prefer <c>App:BackgroundLimiter:*</c> / <c>App__BackgroundLimiter__*</c>; root keys remain for
    /// existing harness compose files.
    /// </summary>
    private static void ApplyLegacyBackgroundLimiterKeys(BackgroundLimiterSettings bl, IConfiguration configuration)
    {
        if (int.TryParse(configuration["BackgroundBaseConcurrency"], out var baseConcurrency))
            bl.BaseConcurrency = baseConcurrency;
        if (int.TryParse(configuration["BackgroundMaxConcurrency"], out var maxConcurrency))
            bl.MaxConcurrency = maxConcurrency;
        if (int.TryParse(configuration["BackgroundScaleUpAfterSeconds"], out var scaleUp))
            bl.ScaleUpAfterSeconds = scaleUp;
        if (int.TryParse(configuration["BackgroundHttpPressureThreshold"], out var httpThreshold))
            bl.HttpPressureThreshold = httpThreshold;
        if (int.TryParse(configuration["BackgroundHttpPressureAfterSeconds"], out var httpAfter))
            bl.HttpPressureAfterSeconds = httpAfter;
        if (int.TryParse(configuration["BackgroundOverSubscribe"], out var overSubscribe))
            bl.OverSubscribe = overSubscribe;

        var burst = configuration["BackgroundBurstToCeiling"];
        if (!string.IsNullOrWhiteSpace(burst))
        {
            bl.BurstToCeiling = burst.Equals("true", StringComparison.OrdinalIgnoreCase)
                || burst == "1";
        }
    }

    private static bool IsValidPoolSizes(CraftSettings settings, CraftRoles roles)
    {
        if (!roles.RunsPowerShell) return true;
        if (roles.Http && settings.Worker.HttpPoolSize < 1) return false;
        if (roles.Background && settings.Worker.BgPoolSize < 1) return false;
        return true;
    }

    private static bool IsValidReadinessMode(CraftSettings settings)
    {
        var mode = settings.ReadinessMode?.Trim() ?? "";
        return mode.Equals("Immediate", StringComparison.OrdinalIgnoreCase)
            || mode.Equals("HttpReady", StringComparison.OrdinalIgnoreCase)
            || mode.Equals("AllReady", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsValidWarmupMode(CraftSettings settings)
    {
        var mode = settings.Worker.WarmupMode?.Trim() ?? "";
        return mode.Equals("BeforeReady", StringComparison.OrdinalIgnoreCase)
            || mode.Equals("AfterReady", StringComparison.OrdinalIgnoreCase)
            || mode.Equals("Background", StringComparison.OrdinalIgnoreCase);
    }
}

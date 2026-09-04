using Craft.Configuration;
using Craft.Hosting;

namespace Craft.Tests;

/// <summary>
/// SKU profiles size the PowerShell worker pools to the host tier the container landed on. Getting
/// this wrong sizes the pools for the wrong machine — the failure is a memory or throughput problem in
/// production, not an error anywhere.
/// </summary>
public class SkuProfileSelectorTests
{
    private static Func<string, string?> Env(params (string Key, string Value)[] pairs)
    {
        var map = pairs.ToDictionary(p => p.Key, p => p.Value, StringComparer.OrdinalIgnoreCase);
        return name => map.TryGetValue(name, out var v) ? v : null;
    }

    private static CraftSettings WithProfiles(params SkuProfile[] profiles)
    {
        var settings = new CraftSettings();
        settings.Worker.HttpPoolSize = 2;
        settings.Worker.BgPoolSize = 4;
        settings.Worker.SkuProfiles.AddRange(profiles);
        return settings;
    }

    [Fact]
    public void NoProfilesConfigured_KeepsBaselineAndStaysSilent()
    {
        var settings = WithProfiles();
        var logs = new List<string>();

        var applied = SkuProfileSelector.Apply(settings, 2, Env(), logs.Add);

        Assert.Null(applied);
        Assert.Empty(logs);   // the feature is opt-in; don't log on every start when unused
        Assert.Equal(2, settings.Worker.HttpPoolSize);
        Assert.Equal(4, settings.Worker.BgPoolSize);
    }

    [Fact]
    public void MatchingProfile_OverwritesPoolSizes()
    {
        var settings = WithProfiles(new SkuProfile
        {
            SkuEnv = "WEBSITE_SKU",
            Sku = "PremiumV3",
            Cpu = 4,
            HttpPoolSize = 8,
            BgPoolSize = 16,
        });

        var applied = SkuProfileSelector.Apply(
            settings, processorCount: 4, Env(("WEBSITE_SKU", "PremiumV3")), _ => { });

        Assert.NotNull(applied);
        Assert.Equal(8, settings.Worker.HttpPoolSize);
        Assert.Equal(16, settings.Worker.BgPoolSize);
    }

    [Fact]
    public void SkuComparison_IsCaseInsensitive()
    {
        var settings = WithProfiles(new SkuProfile
        {
            SkuEnv = "WEBSITE_SKU",
            Sku = "premiumv3",
            HttpPoolSize = 8,
            BgPoolSize = 16,
        });

        var applied = SkuProfileSelector.Apply(
            settings, 2, Env(("WEBSITE_SKU", "PREMIUMV3")), _ => { });

        Assert.NotNull(applied);
    }

    [Fact]
    public void CpuMismatch_SkipsTheProfile()
    {
        var settings = WithProfiles(new SkuProfile
        {
            SkuEnv = "WEBSITE_SKU",
            Sku = "B2",
            Cpu = 8,
            HttpPoolSize = 99,
            BgPoolSize = 99,
        });

        var applied = SkuProfileSelector.Apply(
            settings, processorCount: 2, Env(("WEBSITE_SKU", "B2")), _ => { });

        Assert.Null(applied);
        Assert.Equal(2, settings.Worker.HttpPoolSize);   // baseline preserved
    }

    [Theory]
    [InlineData(null)]
    [InlineData(0)]
    public void CpuUnsetOrZero_MatchesAnyProcessorCount(int? cpu)
    {
        var settings = WithProfiles(new SkuProfile
        {
            SkuEnv = "WEBSITE_SKU",
            Sku = "B2",
            Cpu = cpu,
            HttpPoolSize = 3,
            BgPoolSize = 5,
        });

        var applied = SkuProfileSelector.Apply(
            settings, processorCount: 64, Env(("WEBSITE_SKU", "B2")), _ => { });

        Assert.NotNull(applied);
        Assert.Equal(3, settings.Worker.HttpPoolSize);
    }

    [Fact]
    public void BlankSkuEnv_MatchesUnconditionally_AndActsAsACatchAll()
    {
        var settings = WithProfiles(
            new SkuProfile { SkuEnv = "WEBSITE_SKU", Sku = "P3", HttpPoolSize = 9, BgPoolSize = 9 },
            new SkuProfile { SkuEnv = "", HttpPoolSize = 1, BgPoolSize = 1 });

        // First profile's SKU does not match, so evaluation falls through to the catch-all.
        var applied = SkuProfileSelector.Apply(
            settings, 2, Env(("WEBSITE_SKU", "B2")), _ => { });

        Assert.NotNull(applied);
        Assert.Equal(1, settings.Worker.HttpPoolSize);
    }

    [Fact]
    public void FirstMatchWins()
    {
        var settings = WithProfiles(
            new SkuProfile { SkuEnv = "", HttpPoolSize = 11, BgPoolSize = 22 },
            new SkuProfile { SkuEnv = "", HttpPoolSize = 33, BgPoolSize = 44 });

        SkuProfileSelector.Apply(settings, 2, Env(), _ => { });

        Assert.Equal(11, settings.Worker.HttpPoolSize);
        Assert.Equal(22, settings.Worker.BgPoolSize);
    }

    [Fact]
    public void IgnoreSkuProfiles_SkipsEvaluationEntirelyAndSaysSo()
    {
        var settings = WithProfiles(new SkuProfile { SkuEnv = "", HttpPoolSize = 99, BgPoolSize = 99 });
        settings.Worker.IgnoreSkuProfiles = true;
        var logs = new List<string>();

        var applied = SkuProfileSelector.Apply(settings, 2, Env(), logs.Add);

        Assert.Null(applied);
        Assert.Equal(2, settings.Worker.HttpPoolSize);
        Assert.Contains(logs, l => l.Contains("IgnoreSkuProfiles=true", StringComparison.Ordinal));
    }

    [Fact]
    public void NoProfileMatches_LogsAndKeepsBaseline()
    {
        var settings = WithProfiles(new SkuProfile
        {
            SkuEnv = "WEBSITE_SKU",
            Sku = "P3",
            HttpPoolSize = 99,
            BgPoolSize = 99,
        });
        var logs = new List<string>();

        var applied = SkuProfileSelector.Apply(settings, 2, Env(("WEBSITE_SKU", "B2")), logs.Add);

        Assert.Null(applied);
        Assert.Equal(2, settings.Worker.HttpPoolSize);
        Assert.Contains(logs, l => l.Contains("No SkuProfile matched", StringComparison.Ordinal));
    }

    [Fact]
    public void EnvironmentLookupThrowing_DoesNotTakeTheHostDown()
    {
        // Pool sizing is a hint. A failure here must never prevent startup — the baseline is valid.
        var settings = WithProfiles(new SkuProfile { SkuEnv = "BOOM", Sku = "x", HttpPoolSize = 99, BgPoolSize = 99 });
        var logs = new List<string>();

        var applied = SkuProfileSelector.Apply(
            settings, 2, _ => throw new InvalidOperationException("env blew up"), logs.Add);

        Assert.Null(applied);
        Assert.Equal(2, settings.Worker.HttpPoolSize);
        Assert.Contains(logs, l => l.Contains("SkuProfile detection failed", StringComparison.Ordinal));
    }

    // ---- Second SkuProfiles matrix — selected by env-var presence ----

    [Fact]
    public void AltEnvPresent_SelectsSecondMatrixOverDefault()
    {
        var settings = WithProfiles(new SkuProfile { SkuEnv = "", HttpPoolSize = 20, BgPoolSize = 30 }); // default
        settings.Worker.SkuProfilesAltEnv = "CIPP_HOSTED";
        settings.Worker.SkuProfilesAlt.Add(new SkuProfile { SkuEnv = "", HttpPoolSize = 2, BgPoolSize = 3 }); // second

        var applied = SkuProfileSelector.Apply(settings, 8, Env(("CIPP_HOSTED", "true")), _ => { });

        Assert.NotNull(applied);
        Assert.Equal(2, settings.Worker.HttpPoolSize); // second matrix won
        Assert.Equal(3, settings.Worker.BgPoolSize);
    }

    [Fact]
    public void AltEnvName_IsConfigurable_AndAnyNonEmptyValueCounts()
    {
        var settings = WithProfiles(new SkuProfile { SkuEnv = "", HttpPoolSize = 20, BgPoolSize = 30 });
        settings.Worker.SkuProfilesAltEnv = "MY_FLAG";
        settings.Worker.SkuProfilesAlt.Add(new SkuProfile { SkuEnv = "", HttpPoolSize = 2, BgPoolSize = 2 });

        var applied = SkuProfileSelector.Apply(settings, 8, Env(("MY_FLAG", "anything")), _ => { });

        Assert.NotNull(applied);
        Assert.Equal(2, settings.Worker.HttpPoolSize);
    }

    [Fact]
    public void AltEnvAbsent_UsesDefaultMatrix()
    {
        var settings = WithProfiles(new SkuProfile { SkuEnv = "", HttpPoolSize = 20, BgPoolSize = 30 });
        settings.Worker.SkuProfilesAltEnv = "CIPP_HOSTED";
        settings.Worker.SkuProfilesAlt.Add(new SkuProfile { SkuEnv = "", HttpPoolSize = 2, BgPoolSize = 3 });

        var applied = SkuProfileSelector.Apply(settings, 8, Env(), _ => { }); // flag not set

        Assert.NotNull(applied);
        Assert.Equal(20, settings.Worker.HttpPoolSize);
    }

    [Fact]
    public void AltEnvPresent_ButSecondMatrixEmpty_FallsBackToDefaultAndLogs()
    {
        var settings = WithProfiles(new SkuProfile { SkuEnv = "", HttpPoolSize = 20, BgPoolSize = 30 });
        settings.Worker.SkuProfilesAltEnv = "CIPP_HOSTED"; // SkuProfilesAlt left empty
        var logs = new List<string>();

        var applied = SkuProfileSelector.Apply(settings, 8, Env(("CIPP_HOSTED", "true")), logs.Add);

        Assert.NotNull(applied);
        Assert.Equal(20, settings.Worker.HttpPoolSize); // default matrix used
        Assert.Contains(logs, l => l.Contains("SkuProfilesAlt is empty", StringComparison.Ordinal));
    }

    // ---- Per-instance env overrides (win over the matrix) ----

    [Fact]
    public void EnvOverride_HttpAndBg_WinOverMatchedProfile()
    {
        var settings = WithProfiles(new SkuProfile { SkuEnv = "", HttpPoolSize = 20, BgPoolSize = 30 });

        SkuProfileSelector.Apply(settings, 8, Env(("CRAFT_HTTP_POOL_SIZE", "3"), ("CRAFT_BG_POOL_SIZE", "5")), _ => { });

        Assert.Equal(3, settings.Worker.HttpPoolSize); // env beat the profile's 20
        Assert.Equal(5, settings.Worker.BgPoolSize);   // env beat the profile's 30
    }

    [Fact]
    public void EnvOverride_AppliesEvenWithNoProfilesConfigured()
    {
        var settings = WithProfiles(); // baseline 2 / 4, no profiles

        SkuProfileSelector.Apply(settings, 8, Env(("CRAFT_HTTP_POOL_SIZE", "7")), _ => { });

        Assert.Equal(7, settings.Worker.HttpPoolSize); // override applied over the baseline
        Assert.Equal(4, settings.Worker.BgPoolSize);   // untouched
    }

    [Fact]
    public void EnvOverride_ZeroIsHonored_NegativeAndGarbageAreIgnored()
    {
        var settings = WithProfiles();
        settings.Worker.HttpPoolSize = 6;
        settings.Worker.BgPoolSize = 8;

        SkuProfileSelector.Apply(settings, 8, Env(("CRAFT_HTTP_POOL_SIZE", "0"), ("CRAFT_BG_POOL_SIZE", "-1")), _ => { });

        Assert.Equal(0, settings.Worker.HttpPoolSize); // 0 is a valid HTTP pool size (native-only HTTP)
        Assert.Equal(8, settings.Worker.BgPoolSize);   // negative ignored -> baseline kept
    }
}

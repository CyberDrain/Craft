using Craft.Configuration;
using Craft.Hosting;

namespace Craft.Tests;

/// <summary>
/// Role resolution decides what an instance actually does, and every deployment topology depends on
/// getting it right. Before extraction this lived in <c>Program.cs</c> top-level statements and was
/// only exercised end-to-end in the combined role, so the split topologies had no coverage at all.
/// </summary>
public class CraftRolesTests
{
    /// <summary>An environment where nothing is set.</summary>
    private static Func<string, string?> NoEnv => _ => null;

    private static Func<string, string?> Env(params (string Key, string Value)[] pairs)
    {
        var map = pairs.ToDictionary(p => p.Key, p => p.Value, StringComparer.OrdinalIgnoreCase);
        return name => map.TryGetValue(name, out var v) ? v : null;
    }

    [Fact]
    public void NothingSetAnywhere_RunsTheCombinedMonolith()
    {
        var roles = CraftRoles.Resolve(new CraftSettings(), NoEnv);

        Assert.True(roles.Frontend);
        Assert.True(roles.Http);
        Assert.True(roles.Background);
        Assert.False(roles.None);
        Assert.Equal("Frontend+Http+Background", roles.ToString());
    }

    [Theory]
    [InlineData("CRAFT_SERVE_FRONTEND", true, false, false)]
    [InlineData("CRAFT_SERVE_API", false, true, false)]
    [InlineData("CRAFT_RUN_BACKGROUND", false, false, true)]
    public void SettingOneRole_TurnsTheOthersOff(string variable, bool frontend, bool http, bool background)
    {
        // The rule that trips people up: roles are declared by enabling what you want. Enabling one
        // does NOT leave the rest at their defaults — it turns them off.
        var roles = CraftRoles.Resolve(new CraftSettings(), Env((variable, "true")));

        Assert.Equal(frontend, roles.Frontend);
        Assert.Equal(http, roles.Http);
        Assert.Equal(background, roles.Background);
    }

    [Fact]
    public void EnvironmentWinsOverAppRolesConfiguration()
    {
        var settings = new CraftSettings();
        settings.Roles.Http = true;
        settings.Roles.Background = true;

        var roles = CraftRoles.Resolve(settings, Env(("CRAFT_SERVE_API", "false")));

        Assert.False(roles.Http);
        Assert.True(roles.Background);   // not overridden, so the config value stands
    }

    [Fact]
    public void AllRolesExplicitlyOff_IsDetectedAsMisconfiguration()
    {
        var roles = CraftRoles.Resolve(new CraftSettings(), Env(
            ("CRAFT_SERVE_FRONTEND", "false"),
            ("CRAFT_SERVE_API", "false"),
            ("CRAFT_RUN_BACKGROUND", "false")));

        Assert.True(roles.None);
        Assert.Equal("none", roles.ToString());
    }

    [Theory]
    [InlineData(true, true, true)]     // combined — browser UI and its API on one node
    [InlineData(true, false, false)]   // static-only
    [InlineData(false, true, false)]   // api-only
    public void ResponseCache_DefaultsOnOnlyWhenOneNodeServesBothUiAndApi(
        bool frontend, bool http, bool expected)
    {
        var roles = CraftRoles.Resolve(new CraftSettings(), Env(
            ("CRAFT_SERVE_FRONTEND", frontend ? "true" : "false"),
            ("CRAFT_SERVE_API", http ? "true" : "false"),
            ("CRAFT_RUN_BACKGROUND", "true")));

        Assert.Equal(expected, roles.ResponseCacheEnabled);
    }

    [Fact]
    public void ResponseCache_ExplicitEnvOverridesTheDerivedDefault()
    {
        var roles = CraftRoles.Resolve(new CraftSettings(), Env(("CRAFT_RESPONSE_CACHE", "false")));
        Assert.False(roles.ResponseCacheEnabled);
    }

    [Fact]
    public void RunsPowerShell_IsTrueForAnyRoleThatExecutesScripts()
    {
        var frontendOnly = CraftRoles.Resolve(new CraftSettings(), Env(("CRAFT_SERVE_FRONTEND", "true")));
        Assert.False(frontendOnly.RunsPowerShell);

        var backgroundOnly = CraftRoles.Resolve(new CraftSettings(), Env(("CRAFT_RUN_BACKGROUND", "true")));
        Assert.True(backgroundOnly.RunsPowerShell);
    }

    [Theory]
    [InlineData("healthz", "/healthz")]        // leading slash added
    [InlineData("/probe", "/probe")]
    [InlineData("  /spaced  ", "/spaced")]     // trimmed before the slash check
    public void HealthPath_IsNormalisedToStartWithASlash(string configured, string expected)
    {
        var roles = CraftRoles.Resolve(new CraftSettings(), Env(("CRAFT_HEALTH_PATH", configured)));
        Assert.Equal(expected, roles.HealthPath);
    }

    [Fact]
    public void HealthPath_FallsBackToConfigurationWhenEnvIsBlank()
    {
        var settings = new CraftSettings();
        settings.Health.Path = "/from-config";

        var roles = CraftRoles.Resolve(settings, Env(("CRAFT_HEALTH_PATH", "   ")));

        Assert.Equal("/from-config", roles.HealthPath);
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData("", null)]
    [InlineData("   ", null)]
    [InlineData("true", true)]
    [InlineData("TRUE", true)]
    [InlineData("True", true)]
    [InlineData("1", true)]
    [InlineData("false", false)]
    [InlineData("0", false)]
    [InlineData("yes", false)]     // only "true"/"1" mean true
    public void EnvFlag_IsTriState(string? raw, bool? expected)
    {
        // null vs false is load-bearing: null falls back to App: configuration, false overrides it.
        Assert.Equal(expected, EnvFlag.Parse(raw));
    }

    // ── compression toggles: static and /api are independent ──────────────────────────────────────────

    [Fact]
    public void Compression_BothOnByDefault()
    {
        var roles = CraftRoles.Resolve(new CraftSettings(), NoEnv);

        Assert.True(roles.CompressionEnabled);      // static (Frontend)
        Assert.True(roles.ApiCompressionEnabled);   // dynamic /api
    }

    [Fact]
    public void StaticCompressionOff_DoesNotDisableApiCompression()
    {
        // The whole point of the split: a CDN compressing the static bundle (CRAFT_COMPRESSION=false)
        // must not stop the origin compressing its API JSON.
        var roles = CraftRoles.Resolve(new CraftSettings(), Env(("CRAFT_COMPRESSION", "false")));

        Assert.False(roles.CompressionEnabled);
        Assert.True(roles.ApiCompressionEnabled);
    }

    [Fact]
    public void ApiCompression_CanBeDisabledIndependently()
    {
        var roles = CraftRoles.Resolve(new CraftSettings(), Env(("CRAFT_API_COMPRESSION", "false")));

        Assert.True(roles.CompressionEnabled);       // static untouched
        Assert.False(roles.ApiCompressionEnabled);   // /api off
    }

    [Fact]
    public void ApiCompression_EnvOverridesAppSetting()
    {
        // App:Api:Compression=false, but the env var wins (true).
        var settings = new CraftSettings();
        settings.Api.Compression = false;

        var roles = CraftRoles.Resolve(settings, Env(("CRAFT_API_COMPRESSION", "true")));

        Assert.True(roles.ApiCompressionEnabled);
    }

    // ── compression LEVEL resolution ──────────────────────────────────────────────────────────────────

    [Fact]
    public void CompressionLevel_DefaultsToOptimal()
    {
        // Optimal is a near-free win over Fastest (measured ~8.4x vs ~4.9x at the same CPU); see
        // ApiSettings.CompressionLevel. SmallestSize is deliberately not the default (Brotli q11 is
        // catastrophic on a small container).
        var level = CraftHostBuilderExtensions.ResolveCompressionLevel(new CraftSettings(), NoEnv);
        Assert.Equal(System.IO.Compression.CompressionLevel.Optimal, level);
    }

    [Theory]
    [InlineData("Optimal")]
    [InlineData("optimal")]     // case-insensitive
    [InlineData("SmallestSize")]
    [InlineData("NoCompression")]
    public void CompressionLevel_ParsesConfiguredName(string name)
    {
        var settings = new CraftSettings();
        settings.Api.CompressionLevel = name;
        var level = CraftHostBuilderExtensions.ResolveCompressionLevel(settings, NoEnv);
        Assert.Equal(Enum.Parse<System.IO.Compression.CompressionLevel>(name, ignoreCase: true), level);
    }

    [Fact]
    public void CompressionLevel_EnvWinsOverAppSetting()
    {
        var settings = new CraftSettings();
        settings.Api.CompressionLevel = "Fastest";
        var level = CraftHostBuilderExtensions.ResolveCompressionLevel(
            settings, Env(("CRAFT_API_COMPRESSION_LEVEL", "SmallestSize")));
        Assert.Equal(System.IO.Compression.CompressionLevel.SmallestSize, level);
    }

    [Fact]
    public void CompressionLevel_UnrecognisedFallsBackToFastest()
    {
        var settings = new CraftSettings();
        settings.Api.CompressionLevel = "banana";
        var level = CraftHostBuilderExtensions.ResolveCompressionLevel(settings, NoEnv);
        Assert.Equal(System.IO.Compression.CompressionLevel.Fastest, level);
    }
}

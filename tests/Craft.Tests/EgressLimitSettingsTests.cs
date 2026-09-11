using Craft.Configuration;
using Craft.Hosting;
using Microsoft.Extensions.DependencyInjection;

namespace Craft.Tests;

/// <summary>
/// Enablement is pure logic over an injected environment lookup, so it is tested without touching the
/// process environment: accounting is on when the hosted flag (default CIPP_HOSTED) is present, the
/// explicit CRAFT_API_EGRESS_LIMIT_ENABLED flag wins either way (force-on off-host / kill switch
/// on-host), and a blank HostedEnv opts out of auto-enable entirely.
/// </summary>
public class EgressLimitSettingsResolveEnabledTests
{
    private static Func<string, string?> Env(params (string Name, string? Value)[] pairs)
    {
        var map = pairs.ToDictionary(p => p.Name, p => p.Value);
        return name => map.TryGetValue(name, out var v) ? v : null;
    }

    [Fact]
    public void OffWhenHostedEnvAbsent()
    {
        Assert.False(new EgressLimitSettings().ResolveEnabled(Env()));
    }

    [Fact]
    public void OnWhenHostedEnvPresent()
    {
        // Piggybacks the existing hosted marker used by the SKU profiles.
        Assert.True(new EgressLimitSettings().ResolveEnabled(Env(("CIPP_HOSTED", "1"))));
    }

    [Fact]
    public void ForcedTrue_EnablesEvenOffHost()
    {
        Assert.True(new EgressLimitSettings().ResolveEnabled(Env(("CRAFT_API_EGRESS_LIMIT_ENABLED", "true"))));
    }

    [Fact]
    public void ForcedFalse_IsKillSwitchEvenWhenHosted()
    {
        Assert.False(new EgressLimitSettings().ResolveEnabled(
            Env(("CIPP_HOSTED", "1"), ("CRAFT_API_EGRESS_LIMIT_ENABLED", "false"))));
    }

    [Fact]
    public void BlankHostedEnv_NeverAutoEnables_ButForceStillWorks()
    {
        var s = new EgressLimitSettings { HostedEnv = "" };
        Assert.False(s.ResolveEnabled(Env(("CIPP_HOSTED", "1"))));
        Assert.True(s.ResolveEnabled(Env(("CRAFT_API_EGRESS_LIMIT_ENABLED", "1"))));
    }

    [Fact]
    public void CustomHostedEnvName_IsHonoured()
    {
        var s = new EgressLimitSettings { HostedEnv = "MY_HOST_FLAG" };
        Assert.True(s.ResolveEnabled(Env(("MY_HOST_FLAG", "yes"))));
        Assert.False(s.ResolveEnabled(Env(("CIPP_HOSTED", "1")))); // the default name no longer applies
    }

    [Theory]
    [InlineData("true", true)]
    [InlineData("TRUE", true)]
    [InlineData("1", true)]
    [InlineData("false", false)]
    [InlineData("0", false)]
    [InlineData("nonsense", false)]
    public void ForcedFlag_ParsesTriState(string value, bool expected)
    {
        var s = new EgressLimitSettings { HostedEnv = "" }; // isolate the forced flag from hosted detection
        Assert.Equal(expected, s.ResolveEnabled(Env(("CRAFT_API_EGRESS_LIMIT_ENABLED", value))));
    }
}

/// <summary>
/// The env-override resolution and the DI gating read the real process environment, so these run in one
/// class (serial within it) and save/restore every variable they touch. They pin the tuning knobs
/// (bytes/day, flush interval) and the escape hatches, and that registration only happens when hosted.
/// </summary>
public class EgressLimitSettingsEnvTests : IDisposable
{
    private static readonly string[] Vars =
    {
        "CRAFT_API_EGRESS_LIMIT_ENABLED",
        "CRAFT_API_EGRESS_LIMIT_BYTES",
        "CRAFT_API_EGRESS_FLUSH_SECONDS",
        "CIPP_HOSTED",
    };

    private readonly Dictionary<string, string?> _saved = new();

    public EgressLimitSettingsEnvTests()
    {
        foreach (var v in Vars)
        {
            _saved[v] = Environment.GetEnvironmentVariable(v);
            Environment.SetEnvironmentVariable(v, null);
        }
    }

    public void Dispose()
    {
        foreach (var v in Vars) Environment.SetEnvironmentVariable(v, _saved[v]);
        GC.SuppressFinalize(this);
    }

    // ── BytesPerDay (the daily budget / tuning + enforcement toggle) ────────────────────────────────

    [Fact]
    public void Bytes_DefaultsToZero_AccountingOnly() =>
        Assert.Equal(0L, new EgressLimitSettings().ResolvedBytesPerDay);

    [Fact]
    public void Bytes_ConfiguredValueUsed() =>
        Assert.Equal(1_073_741_824L, new EgressLimitSettings { BytesPerDay = 1_073_741_824 }.ResolvedBytesPerDay);

    [Fact]
    public void Bytes_EnvOverrideWins()
    {
        Environment.SetEnvironmentVariable("CRAFT_API_EGRESS_LIMIT_BYTES", "500");
        Assert.Equal(500L, new EgressLimitSettings { BytesPerDay = 999 }.ResolvedBytesPerDay);
    }

    [Fact]
    public void Bytes_EnvZeroDisablesEnforcementEvenWhenConfigured()
    {
        Environment.SetEnvironmentVariable("CRAFT_API_EGRESS_LIMIT_BYTES", "0");
        Assert.Equal(0L, new EgressLimitSettings { BytesPerDay = 999 }.ResolvedBytesPerDay);
    }

    [Theory]
    [InlineData("notanumber")]
    [InlineData("-5")]
    public void Bytes_InvalidOrNegativeEnvIgnored(string value)
    {
        Environment.SetEnvironmentVariable("CRAFT_API_EGRESS_LIMIT_BYTES", value);
        Assert.Equal(777L, new EgressLimitSettings { BytesPerDay = 777 }.ResolvedBytesPerDay);
    }

    // ── FlushSeconds (tuning) ───────────────────────────────────────────────────────────────────────

    [Fact]
    public void Flush_DefaultsTo60() =>
        Assert.Equal(60, new EgressLimitSettings().ResolvedFlushSeconds);

    [Fact]
    public void Flush_EnvOverrideWins()
    {
        Environment.SetEnvironmentVariable("CRAFT_API_EGRESS_FLUSH_SECONDS", "15");
        Assert.Equal(15, new EgressLimitSettings().ResolvedFlushSeconds);
    }

    [Fact]
    public void Flush_FlooredAtOne() =>
        Assert.Equal(1, new EgressLimitSettings { FlushSeconds = 0 }.ResolvedFlushSeconds);

    [Theory]
    [InlineData("0")]
    [InlineData("-3")]
    [InlineData("x")]
    public void Flush_InvalidEnvIgnored(string value)
    {
        Environment.SetEnvironmentVariable("CRAFT_API_EGRESS_FLUSH_SECONDS", value);
        Assert.Equal(42, new EgressLimitSettings { FlushSeconds = 42 }.ResolvedFlushSeconds);
    }

    // ── ResolvedEnabled convenience + DI gating (the piggyback on CIPP_HOSTED) ──────────────────────

    [Fact]
    public void ResolvedEnabled_TracksCippHosted()
    {
        Assert.False(new EgressLimitSettings().ResolvedEnabled);
        Environment.SetEnvironmentVariable("CIPP_HOSTED", "1");
        Assert.True(new EgressLimitSettings().ResolvedEnabled);
    }

    [Fact]
    public void Registration_SkippedWhenOff()
    {
        var services = new ServiceCollection();
        services.AddCraftEgressLimiter(new CraftSettings());
        Assert.DoesNotContain(services, d => d.ServiceType == typeof(EgressLedger));
    }

    [Fact]
    public void Registration_HappensWhenHosted()
    {
        Environment.SetEnvironmentVariable("CIPP_HOSTED", "1");
        var services = new ServiceCollection();
        services.AddCraftEgressLimiter(new CraftSettings());
        Assert.Contains(services, d => d.ServiceType == typeof(EgressLedger));
    }
}

using System.Globalization;
using Craft.Configuration;
using Craft.Orchestration;
using Craft.PowerShellHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace Craft.Tests;

/// <summary>
/// HTTP-pressure defaults. Half the HTTP pool (3 of 6) tripped on routine API-client traffic and the
/// fixed throttle target of 2 then held an 8-slot BG pool at 2 for hours, so a scheduled cache run of
/// ~5.5k tasks could not finish inside a day.
/// </summary>
public class LimiterHttpPressureTests
{
    private static BackgroundTaskLimiter NewLimiter(int httpPoolSize, int bgPoolSize, Dictionary<string, string?>? extra = null)
    {
        var settings = new CraftSettings();
        settings.Worker.HttpPoolSize = httpPoolSize;
        settings.Worker.BgPoolSize = bgPoolSize;
        var values = new Dictionary<string, string?>
        {
            ["BackgroundMaxConcurrency"] = bgPoolSize.ToString(CultureInfo.InvariantCulture),
        };
        foreach (var kv in extra ?? []) values[kv.Key] = kv.Value;
        var config = new ConfigurationBuilder().AddInMemoryCollection(values).Build();
        var repo = new ScriptRepository(NullLogger<ScriptRepository>.Instance, settings);
        var pool = new PowerShellWorkerPool(repo, NullLogger<PowerShellWorkerPool>.Instance, config, settings);
        return new BackgroundTaskLimiter(NullLogger<BackgroundTaskLimiter>.Instance, config, settings, pool);
    }

    [Theory]
    [InlineData(6, 8, 4, 4)]    // hosted default profile
    [InlineData(2, 2, 1, 2)]    // Basic 1-cpu profile: target never exceeds the ceiling
    [InlineData(16, 24, 10, 12)]
    public void DefaultsScaleWithPools(int http, int bg, int threshold, int target)
    {
        using var limiter = NewLimiter(http, bg);
        Assert.Equal(threshold, limiter.HttpPressureThreshold);
        Assert.Equal(target, limiter.HttpPressureConcurrency);
    }

    [Fact]
    public void ConfigOverridesAreHonouredAndClampedToTheCeiling()
    {
        using var limiter = NewLimiter(6, 8, new()
        {
            ["BackgroundHttpPressureThreshold"] = "5",
            ["BackgroundHttpPressureConcurrency"] = "50",
        });
        Assert.Equal(5, limiter.HttpPressureThreshold);
        Assert.Equal(8, limiter.HttpPressureConcurrency);
    }
}

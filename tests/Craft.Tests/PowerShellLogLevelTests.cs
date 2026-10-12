using Craft.Configuration;
using Microsoft.Extensions.Logging;

namespace Craft.Tests;

public class PowerShellLogLevelTests
{
    [Theory]
    [InlineData("Trace", null, LogLevel.Trace)]
    [InlineData("Trace", "", LogLevel.Trace)]
    [InlineData("Trace", "Information", LogLevel.Information)]
    [InlineData("Trace", "Debug", LogLevel.Debug)]
    [InlineData("Information", "Trace", LogLevel.Information)]
    [InlineData("Debug", "nonsense", LogLevel.Debug)]
    public void PowerShellCaptureFollowsTheLessVerboseLevel(string logLevel, string? powerShellLogLevel, LogLevel expected)
    {
        var settings = new FileLoggingSettings { LogLevel = logLevel, PowerShellLogLevel = powerShellLogLevel };
        Assert.Equal(expected, settings.ParsedPowerShellLogLevel);
    }
}

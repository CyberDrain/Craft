using Craft.Configuration;
using Craft.Hosting;

namespace Craft.Tests;

/// <summary>
/// The HTTP queue timeout bounds how long a request waits for a free runspace before it is shed with
/// 503. It resolves from the <c>CRAFT_HTTP_QUEUE_TIMEOUT</c> env var, then <c>Worker:HttpQueueTimeoutSeconds</c>,
/// then the built-in default — mirroring how the thread-pool minimum resolves.
/// </summary>
public class HttpQueueTimeoutTests : IDisposable
{
    private readonly string? _original = Environment.GetEnvironmentVariable("CRAFT_HTTP_QUEUE_TIMEOUT");

    public HttpQueueTimeoutTests() => Environment.SetEnvironmentVariable("CRAFT_HTTP_QUEUE_TIMEOUT", null);

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("CRAFT_HTTP_QUEUE_TIMEOUT", _original);
        GC.SuppressFinalize(this);
    }

    private static WorkerSettings Worker(int queueTimeout = 0) =>
        new() { HttpQueueTimeoutSeconds = queueTimeout };

    [Fact]
    public void UnsetFallsBackToTheBuiltInDefault()
    {
        Assert.Equal(
            TimeSpan.FromSeconds(CraftHostBuilderExtensions.DefaultHttpQueueTimeoutSeconds),
            CraftHostBuilderExtensions.ResolveHttpQueueTimeout(Worker()));
    }

    [Fact]
    public void ExplicitSettingWins()
    {
        Assert.Equal(
            TimeSpan.FromSeconds(60),
            CraftHostBuilderExtensions.ResolveHttpQueueTimeout(Worker(queueTimeout: 60)));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    public void ZeroOrNegativeSettingFallsBackToTheDefault(int configured)
    {
        Assert.Equal(
            TimeSpan.FromSeconds(CraftHostBuilderExtensions.DefaultHttpQueueTimeoutSeconds),
            CraftHostBuilderExtensions.ResolveHttpQueueTimeout(Worker(configured)));
    }

    [Fact]
    public void EnvOverrideWinsOverTheSetting()
    {
        Environment.SetEnvironmentVariable("CRAFT_HTTP_QUEUE_TIMEOUT", "90");

        Assert.Equal(
            TimeSpan.FromSeconds(90),
            CraftHostBuilderExtensions.ResolveHttpQueueTimeout(Worker(queueTimeout: 60)));
    }

    [Theory]
    [InlineData("")]
    [InlineData("notanumber")]
    [InlineData("0")]
    [InlineData("-1")]
    public void InvalidOrNonPositiveEnvIsIgnored(string value)
    {
        Environment.SetEnvironmentVariable("CRAFT_HTTP_QUEUE_TIMEOUT", value);

        Assert.Equal(
            TimeSpan.FromSeconds(60),
            CraftHostBuilderExtensions.ResolveHttpQueueTimeout(Worker(queueTimeout: 60)));
    }
}

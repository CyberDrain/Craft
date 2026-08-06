using Craft.Configuration;
using Craft.PowerShellHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace Craft.Tests;

/// <summary>
/// <c>Worker:HttpPoolSize = 0</c> means this node hosts no PowerShell over HTTP.
/// <para>
/// An app whose routes are all native endpoints has nothing to dispatch to a runspace. Building one
/// anyway is not free: the first worker loads every module in the API tree and holds that memory for
/// the process lifetime, and startup waits for it before serving traffic. The pool size therefore has
/// to survive as 0 rather than being clamped up to 1 — which is what it used to do.
/// </para>
/// </summary>
public class ZeroHttpPoolTests
{
    private static PowerShellWorkerPool Pool(int httpPoolSize)
    {
        var settings = new CraftSettings();
        settings.Worker.HttpPoolSize = httpPoolSize;
        settings.Worker.BgPoolSize = 4;

        var config = new ConfigurationBuilder().Build();
        var repo = new ScriptRepository(NullLogger<ScriptRepository>.Instance, settings);
        return new PowerShellWorkerPool(repo, NullLogger<PowerShellWorkerPool>.Instance, config, settings);
    }

    [Fact]
    public void ZeroIsPreservedRatherThanClampedToOne()
    {
        Assert.Equal(0, Pool(0).HttpPoolSize);
    }

    [Fact]
    public void ConstructingWithZeroDoesNotThrow()
    {
        // BlockingCollection rejects a bounded capacity of 0, so the backing collection has to be
        // sized independently of the configured pool size.
        var exception = Record.Exception(() => Pool(0));
        Assert.Null(exception);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(8)]
    [InlineData(64)]
    public void PositiveSizesAreUnaffected(int size)
    {
        Assert.Equal(size, Pool(size).HttpPoolSize);
    }

    [Fact]
    public void NegativeSizesFallBackToZero()
    {
        // A negative pool size is a typo, not an intent. Treating it as "no pool" is the safe reading:
        // the alternative is an exception at construction, which takes the whole host down over a
        // stray minus sign in a config file.
        Assert.Equal(0, Pool(-1).HttpPoolSize);
    }

    [Fact]
    public void DisablingBothPoolsSignalsReadyImmediately()
    {
        // The readiness gate in Program.cs holds every /API/* request until the pool reports ready. A
        // node that builds no pools must still report ready, or its native endpoints are unreachable
        // forever — this was the failure that made a fully-native app hang on startup.
        var pool = Pool(0);
        pool.Initialize(enableHttp: false, enableBg: false);

        Assert.True(pool.IsReady);
        Assert.True(pool.BackgroundReady);
    }
}

using Craft.Configuration;
using Craft.PowerShellHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace Craft.Tests;

/// <summary>
/// Interactive requests that find every HTTP worker busy grow the pool past HttpPoolSize, up to HttpBurstMax, and
/// the extra workers are trimmed once idle. API clients stay capped at HttpPoolSize workers, so a burst of
/// automation cannot take the capacity built for users.
/// </summary>
public sealed class HttpBurstTests : IDisposable
{
    private static readonly TimeSpan Long = TimeSpan.FromSeconds(60);
    private readonly PowerShellWorkerPool _pool;

    public HttpBurstTests()
    {
        var settings = new CraftSettings();
        settings.Worker.HttpPoolSize = 1;
        settings.Worker.BgPoolSize = 1;
        settings.Worker.HttpBurstMax = 2;
        settings.Worker.HttpBurstIdleSeconds = 0;
        var repo = new ScriptRepository(NullLogger<ScriptRepository>.Instance, settings);
        _pool = new PowerShellWorkerPool(repo, NullLogger<PowerShellWorkerPool>.Instance, new ConfigurationBuilder().Build(), settings);
        _pool.Initialize(enableHttp: true, enableBg: false);
    }

    public void Dispose() => _pool.Dispose();

    [Fact]
    public void AUserGetsABurstWorkerWhileAnApiClientHoldsThePool()
    {
        var api = _pool.CheckoutHttp(Long, lowPriority: true);
        Assert.NotNull(api);

        var user = _pool.CheckoutHttp(Long);

        Assert.NotNull(user);
        Assert.Equal(2, _pool.HttpWorkers);
        _pool.Reclaim(user, isHttp: true);
        _pool.Reclaim(api, isHttp: true);
    }

    [Fact]
    public void ApiClientsNeverHoldMoreThanThePoolEvenWithAFreeBurstWorker()
    {
        var api = _pool.CheckoutHttp(Long, lowPriority: true)!;
        var user = _pool.CheckoutHttp(Long)!;
        _pool.Reclaim(user, isHttp: true);
        Assert.Equal(1, _pool.HttpAvailable);

        Assert.Null(_pool.CheckoutHttp(TimeSpan.FromMilliseconds(300), lowPriority: true));

        _pool.Reclaim(api, isHttp: true);
        var second = _pool.CheckoutHttp(TimeSpan.FromMilliseconds(300), lowPriority: true);
        Assert.NotNull(second);
        _pool.Reclaim(second, isHttp: true);
    }

    [Fact]
    public void BurstStopsAtHttpBurstMax()
    {
        var held = Enumerable.Range(0, 3).Select(_ => _pool.CheckoutHttp(Long)!).ToList();

        Assert.Null(_pool.CheckoutHttp(TimeSpan.FromMilliseconds(500)));
        Assert.Equal(3, _pool.HttpWorkers);
        foreach (var worker in held) _pool.Reclaim(worker, isHttp: true);
    }

    [Fact]
    public void IdleBurstWorkersAreTrimmedBackToThePoolOneAtATime()
    {
        var held = Enumerable.Range(0, 3).Select(_ => _pool.CheckoutHttp(Long)!).ToList();
        foreach (var worker in held) _pool.Reclaim(worker, isHttp: true);

        _pool.TrimHttpBurst();
        Assert.Equal(2, _pool.HttpWorkers);
        _pool.TrimHttpBurst();
        _pool.TrimHttpBurst();

        Assert.Equal(1, _pool.HttpWorkers);
        Assert.Equal(1, _pool.HttpAvailable);
    }
}

using System.Threading.RateLimiting;
using Craft.Configuration;
using Craft.Hosting;
using Microsoft.AspNetCore.Http;

namespace Craft.Tests;

public class KestrelTimeoutTests
{
    [Fact]
    public void ExplicitKestrelTimeout_Wins()
    {
        var settings = new CraftSettings { KestrelTimeoutSeconds = 42 };
        settings.Worker.HttpTimeoutSeconds = 900;

        Assert.Equal(42, CraftHostBuilderExtensions.ResolveKestrelTimeoutSeconds(settings));
    }

    [Fact]
    public void UnsetKestrelTimeout_DerivesFromTheWorkerTimeout()
    {
        // Kestrel must not give up before the worker does, or the caller sees an aborted connection
        // while the script keeps running and holding a runspace.
        var settings = new CraftSettings { KestrelTimeoutSeconds = 0 };
        settings.Worker.HttpTimeoutSeconds = 900;

        Assert.Equal(900, CraftHostBuilderExtensions.ResolveKestrelTimeoutSeconds(settings));
    }

    [Fact]
    public void NeitherConfigured_FallsBackToTenMinutes()
    {
        var settings = new CraftSettings { KestrelTimeoutSeconds = 0 };
        settings.Worker.HttpTimeoutSeconds = 0;

        Assert.Equal(600, CraftHostBuilderExtensions.ResolveKestrelTimeoutSeconds(settings));
    }
}

/// <summary>
/// Partition selection decides who shares a rate-limit budget with whom, so both a too-coarse and a
/// too-fine key are availability bugs.
/// </summary>
public class RateLimitPartitionKeyTests
{
    private static DefaultHttpContext Request(
        string? principal = null, string? forwardedFor = null, string? remoteIp = null)
    {
        var context = new DefaultHttpContext();
        if (principal is not null)
            context.Request.Headers["x-ms-client-principal-name"] = principal;
        if (forwardedFor is not null)
            context.Request.Headers["X-Forwarded-For"] = forwardedFor;
        if (remoteIp is not null)
            context.Connection.RemoteIpAddress = System.Net.IPAddress.Parse(remoteIp);
        return context;
    }

    [Fact]
    public void AuthenticatedPrincipal_IsPreferred()
    {
        var key = RateLimitPartitionKey.Resolve(
            Request(principal: "user@contoso.com", forwardedFor: "1.2.3.4", remoteIp: "10.0.0.1"));

        Assert.Equal("user@contoso.com", key);
    }

    [Fact]
    public void AnonymousBehindALoadBalancer_PartitionsOnTheFirstForwardedHop()
    {
        // The whole point: behind App Service every anonymous caller shares one RemoteIpAddress (the
        // platform load balancer). Without X-Forwarded-For they would all land in one bucket and any
        // single client could deny service to the rest.
        var key = RateLimitPartitionKey.Resolve(
            Request(forwardedFor: "203.0.113.7, 70.41.3.18, 150.172.238.178", remoteIp: "10.0.0.1"));

        Assert.Equal("203.0.113.7", key);
    }

    [Fact]
    public void ForwardedForIsTrimmed()
    {
        var key = RateLimitPartitionKey.Resolve(Request(forwardedFor: "  203.0.113.7  , 10.0.0.9"));
        Assert.Equal("203.0.113.7", key);
    }

    [Fact]
    public void NoForwardedFor_FallsBackToTheSocketPeer()
    {
        Assert.Equal("10.0.0.1", RateLimitPartitionKey.Resolve(Request(remoteIp: "10.0.0.1")));
    }

    [Fact]
    public void BlankForwardedForHop_DoesNotProduceAnEmptyPartitionKey()
    {
        // An empty key would silently merge every such caller into one shared bucket.
        var key = RateLimitPartitionKey.Resolve(Request(forwardedFor: "  , 10.0.0.9", remoteIp: "10.0.0.1"));
        Assert.Equal("10.0.0.1", key);
    }

    [Fact]
    public void NothingIdentifying_StillProducesAStableKey()
    {
        Assert.Equal("anonymous", RateLimitPartitionKey.Resolve(new DefaultHttpContext()));
    }
}

/// <summary>
/// Retry-After is the only backoff signal a throttled caller gets, so a wrong value is worse than
/// none: too low and well-behaved clients hammer the limiter, too high and they stall needlessly.
/// </summary>
public class RetryAfterTests
{
    private sealed class Lease(TimeSpan? retryAfter) : RateLimitLease
    {
        public override bool IsAcquired => false;

        public override IEnumerable<string> MetadataNames =>
            retryAfter is null ? [] : [MetadataName.RetryAfter.Name];

        public override bool TryGetMetadata(string metadataName, out object? metadata)
        {
            if (retryAfter is not null && metadataName == MetadataName.RetryAfter.Name)
            {
                metadata = retryAfter.Value;
                return true;
            }

            metadata = null;
            return false;
        }
    }

    [Fact]
    public void UsesTheLimitersOwnEstimateWhenItOffersOne()
    {
        var seconds = CraftHostBuilderExtensions.ResolveRetryAfterSeconds(
            new Lease(TimeSpan.FromSeconds(4)), TimeSpan.FromSeconds(10));

        Assert.Equal(4, seconds);
    }

    [Fact]
    public void NoMetadata_FallsBackToTheFullWindow()
    {
        // The window is the worst case for a fixed-window limiter, so it is always safe to advertise.
        var seconds = CraftHostBuilderExtensions.ResolveRetryAfterSeconds(
            new Lease(null), TimeSpan.FromSeconds(10));

        Assert.Equal(10, seconds);
    }

    [Fact]
    public void SubSecondWait_RoundsUpRatherThanTellingTheClientToRetryImmediately()
    {
        // Truncating to 0 would turn a throttle into a hot retry loop.
        var seconds = CraftHostBuilderExtensions.ResolveRetryAfterSeconds(
            new Lease(TimeSpan.FromMilliseconds(400)), TimeSpan.FromSeconds(10));

        Assert.Equal(1, seconds);
    }

    [Fact]
    public void PartialSecond_RoundsUpSoTheClientNeverRetriesEarly()
    {
        var seconds = CraftHostBuilderExtensions.ResolveRetryAfterSeconds(
            new Lease(TimeSpan.FromMilliseconds(2100)), TimeSpan.FromSeconds(10));

        Assert.Equal(3, seconds);
    }
}

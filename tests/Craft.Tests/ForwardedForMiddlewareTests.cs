using Craft.Hosting;
using Microsoft.AspNetCore.Http;

namespace Craft.Tests;

/// <summary>
/// X-Forwarded-For feeds IP allow-lists, so a caller-supplied entry must never be taken for the client.
/// </summary>
public class ForwardedForMiddlewareTests
{
    private static string? Resolve(int hops, string? forwardedFor, string? remoteIp = "10.0.0.1")
    {
        var context = new DefaultHttpContext();
        if (forwardedFor is not null) context.Request.Headers["X-Forwarded-For"] = forwardedFor;
        if (remoteIp is not null) context.Connection.RemoteIpAddress = System.Net.IPAddress.Parse(remoteIp);
        ForwardedForMiddleware.Apply(context, hops);
        return context.Request.Headers.TryGetValue("X-Forwarded-For", out var value) ? value.ToString() : null;
    }

    [Fact]
    public void BehindAppService_ASpoofedFirstEntryIsIgnored() =>
        Assert.Equal("198.51.100.9", Resolve(1, "1.2.3.4, 198.51.100.9:51000"));

    [Fact]
    public void BehindAppService_IPv6EntryLosesPortAndBrackets() =>
        Assert.Equal("2001:db8::77", Resolve(1, "[2001:db8::77]:51000"));

    [Fact]
    public void TwoTrustedProxies_TakeTheSecondFromTheRight() =>
        Assert.Equal("198.51.100.9", Resolve(2, "1.2.3.4, 198.51.100.9, 172.16.0.5:443"));

    [Fact]
    public void DirectExposure_TheHeaderIsReplacedByTheSocketPeer() =>
        Assert.Equal("10.0.0.1", Resolve(0, "1.2.3.4"));

    [Fact]
    public void ShorterThanTheTrustedChain_FallsBackToTheSocketPeer() =>
        Assert.Equal("10.0.0.1", Resolve(2, "1.2.3.4"));

    [Fact]
    public void UnparseableTrustedEntry_FallsBackToTheSocketPeer() =>
        Assert.Equal("10.0.0.1", Resolve(1, "1.2.3.4, not-an-ip"));

    [Fact]
    public void NoAddressAnywhere_RemovesTheHeader() =>
        Assert.Null(Resolve(1, null, remoteIp: null));
}

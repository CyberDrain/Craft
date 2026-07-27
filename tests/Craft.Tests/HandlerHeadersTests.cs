using System.Collections;
using Craft.Hosting;
using Microsoft.AspNetCore.Http;

namespace Craft.Tests;

/// <summary>
/// Headers set by a PowerShell handler used to be silently discarded between the response object and
/// the wire, which turned every handler-authored redirect into a bodiless 302 the browser rendered
/// instead of followed. These tests pin the whole path: what survives normalisation, what does not,
/// and that a redirect actually reaches the response.
/// </summary>
public class HandlerHeadersTests
{
    private static readonly string[] VaryValues = ["Accept-Encoding", "Origin"];

    [Fact]
    public void Redirect_SurvivesToTheResponse()
    {
        var headers = HandlerHeaders.FromPowerShell(
            new Hashtable { ["Location"] = "https://contoso-admin.sharepoint.com" });

        Assert.NotNull(headers);

        var response = new DefaultHttpContext().Response;
        HandlerHeaders.Apply(response, headers);

        Assert.Equal("https://contoso-admin.sharepoint.com", response.Headers.Location);
    }

    [Fact]
    public void NoHeaders_AllocatesNothing()
    {
        Assert.Null(HandlerHeaders.FromPowerShell(null));
        Assert.Null(HandlerHeaders.FromPowerShell(new Hashtable()));
    }

    /// <summary>
    /// The bug that motivated dropping empties: a handler computing <c>Location</c> from a failed
    /// lookup emits <c>@{ Location = $null }</c>, and an empty Location is indistinguishable on the
    /// wire from no Location at all. Better to have no header than a header that lies.
    /// </summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void EmptyValues_AreDropped(object? value)
    {
        var headers = HandlerHeaders.FromPowerShell(new Hashtable { ["Location"] = value });

        Assert.Null(headers);
    }

    [Theory]
    [InlineData("Content-Length")]
    [InlineData("content-length")]
    [InlineData("Transfer-Encoding")]
    [InlineData("Connection")]
    [InlineData("Host")]
    public void ReservedHeaders_AreRefused(string name)
    {
        var headers = HandlerHeaders.FromPowerShell(new Hashtable { [name] = "12345" });

        Assert.Null(headers);
    }

    /// <summary>
    /// Handler header values are attacker-influenced in practice — CIPP's SharePoint redirect builds
    /// its Location from a Graph response — so a CRLF in a value must not become two headers.
    /// </summary>
    [Fact]
    public void CrlfInValue_IsStripped()
    {
        var headers = HandlerHeaders.FromPowerShell(
            new Hashtable { ["Location"] = "https://evil\r\nSet-Cookie: admin=1" });

        Assert.Equal("https://evilSet-Cookie: admin=1", headers!["Location"]);
    }

    [Fact]
    public void ArrayValues_AreJoined()
    {
        var headers = HandlerHeaders.FromPowerShell(
            new Hashtable { ["Vary"] = VaryValues });

        Assert.Equal("Accept-Encoding, Origin", headers!["Vary"]);
    }

    /// <summary>
    /// Content-Type is a property on HttpResponse, not an ordinary header — assigning it through the
    /// header collection fights the property instead of replacing it.
    /// </summary>
    [Fact]
    public void ContentTypeHeader_SetsTheProperty()
    {
        var headers = HandlerHeaders.FromPowerShell(new Hashtable { ["Content-Type"] = "text/html" });

        var response = new DefaultHttpContext().Response;
        HandlerHeaders.Apply(response, headers);

        Assert.Equal("text/html", response.ContentType);
    }

    [Theory]
    [InlineData(null, "application/json")]
    [InlineData("", "application/json")]
    [InlineData("   ", "application/json")]
    [InlineData("text/csv", "text/csv")]
    public void ContentType_FallsBackToJson(string? declared, string expected) =>
        Assert.Equal(expected, HandlerHeaders.ResolveContentType(declared));

    [Theory]
    [InlineData(200, true)]
    [InlineData(302, true)]   // a redirect may carry a body; browsers just ignore it
    [InlineData(404, true)]
    [InlineData(500, true)]
    [InlineData(204, false)]
    [InlineData(304, false)]
    public void AllowsBody_MatchesRfc9110(int statusCode, bool expected) =>
        Assert.Equal(expected, HandlerHeaders.AllowsBody(statusCode));
}

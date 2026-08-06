using System.Text;
using System.Text.Json;
using Craft.Endpoints;
using Microsoft.AspNetCore.Http;

namespace Craft.Tests;

/// <summary>
/// Native endpoints read and write JSON on web conventions, the same as a minimal API.
/// <para>
/// Found by running a real request against a real endpoint: with System.Text.Json's bare defaults,
/// <c>{"hostname":"x"}</c> silently bound to null and the endpoint answered "hostname is required"
/// for a request that supplied one — no exception, no log line. No unit test of the endpoint itself
/// would have caught it, which is why the defaults are pinned here.
/// </para>
/// </summary>
public class CraftJsonDefaultsTests
{
    private sealed record AddDomainRequest(string? Hostname);

    private static CraftRequest RequestWithBody(string json)
    {
        var context = new DefaultHttpContext();
        var bytes = Encoding.UTF8.GetBytes(json);
        context.Request.Body = new MemoryStream(bytes);
        context.Request.ContentLength = bytes.Length;
        context.Request.ContentType = "application/json";

        return new CraftRequest(context, "AddDomain", new CraftEndpointAttribute("AddDomain"));
    }

    [Fact]
    public async Task ReadJson_BindsCamelCaseBodies()
    {
        var body = await RequestWithBody("""{"hostname":"expired.badssl.com"}""")
            .ReadJsonAsync<AddDomainRequest>();

        Assert.Equal("expired.badssl.com", body?.Hostname);
    }

    [Fact]
    public async Task ReadJson_StillBindsPascalCase()
    {
        // Case-insensitivity is a superset of the old behaviour, so nothing that worked stops working.
        var body = await RequestWithBody("""{"Hostname":"example.com"}""")
            .ReadJsonAsync<AddDomainRequest>();

        Assert.Equal("example.com", body?.Hostname);
    }

    [Fact]
    public async Task ReadJson_HonoursExplicitOptions()
    {
        var strict = new JsonSerializerOptions { PropertyNameCaseInsensitive = false };

        var body = await RequestWithBody("""{"hostname":"example.com"}""")
            .ReadJsonAsync<AddDomainRequest>(strict);

        Assert.Null(body?.Hostname);
    }

    [Fact]
    public void ResultJson_WritesCamelCase()
    {
        var result = CraftResult.Json(new AddDomainRequest("example.com"));

        // What a browser client actually receives — PascalCase here would mean every SPA in the
        // ecosystem writing `data.Hostname` against one service and `data.hostname` against the rest.
        Assert.Contains("\"hostname\":\"example.com\"", Body(result), StringComparison.Ordinal);
    }

    [Fact]
    public void ResultJson_HonoursExplicitOptions()
    {
        var result = CraftResult.Json(new AddDomainRequest("example.com"),
            options: new JsonSerializerOptions());

        Assert.Contains("\"Hostname\":", Body(result), StringComparison.Ordinal);
    }

    [Fact]
    public void ResultRawJson_IsUntouched()
    {
        // The verbatim path must stay verbatim — it exists for bodies somebody else already shaped.
        Assert.Equal("""{"Already":"Shaped"}""", Body(CraftResult.RawJson("""{"Already":"Shaped"}""")));
    }

    private static string Body(CraftResult result)
    {
        var context = new DefaultHttpContext();
        var stream = new MemoryStream();
        context.Response.Body = stream;

        result.WriteAsync(context, CancellationToken.None).GetAwaiter().GetResult();

        return Encoding.UTF8.GetString(stream.ToArray());
    }
}

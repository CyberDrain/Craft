using System.Net;
using System.Text;
using Craft.Caching;
using Craft.Configuration;
using Craft.Endpoints;
using Craft.Hosting;
using Craft.Hosting.Endpoints;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Craft.Tests;

/// <summary>
/// Native GETs share the PowerShell path's response cache: per caller, stale-while-revalidate, cleared by writes,
/// and never in front of the central handler.
/// </summary>
[Collection(CacheBodyBytesTests.Name)]
public class NativeEndpointCacheTests
{
    internal abstract class Counting : ICraftEndpoint
    {
        public int Calls;
        public ValueTask<CraftResult> HandleAsync(CraftRequest r, CancellationToken ct) =>
            new(CraftResult.Json(new { Call = Interlocked.Increment(ref Calls) }));
    }

    [CraftEndpoint("ListCacheThings", Methods = ["GET"])]
    internal sealed class ListThings : Counting;

    [CraftEndpoint("GetCacheThing", Methods = ["GET"], Caching = EndpointCaching.Cached)]
    internal sealed class GetThing : Counting;

    [CraftEndpoint("GetCacheLive", Methods = ["GET"])]
    internal sealed class GetLive : Counting;

    [CraftEndpoint("ListCacheLive", Methods = ["GET"], Caching = EndpointCaching.Uncached)]
    internal sealed class ListLive : Counting;

    [CraftEndpoint("ExecCacheWrite", Methods = ["POST"])]
    internal sealed class ExecWrite : Counting;

    /// <summary>Refuses any request carrying x-deny, as an application's authorization would.</summary>
    internal sealed class DenyHandler : ICraftEndpointHandler
    {
        public ValueTask<CraftResult> HandleAsync(CraftRequest request, CraftEndpointNext invokeEndpoint, CancellationToken ct) =>
            request.Http.Request.Headers.ContainsKey("x-deny") ? new(CraftResult.Problem(403, "denied")) : invokeEndpoint();
    }

    private static readonly Type[] s_types = [typeof(ListThings), typeof(GetThing), typeof(GetLive), typeof(ListLive), typeof(ExecWrite)];

    private static async Task<(WebApplication App, HttpClient Client)> StartAsync()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();
        foreach (var type in s_types) builder.Services.AddSingleton(type);
        builder.Services.AddSingleton<ICraftEndpointHandler, DenyHandler>();
        builder.Services.AddSingleton(new CacheService(NullLogger<CacheService>.Instance, new CraftSettings(), enabled: true));
        var app = builder.Build();
        var descriptors = s_types.Select(t => new NativeEndpointDescriptor(
            ((CraftEndpointAttribute)Attribute.GetCustomAttribute(t, typeof(CraftEndpointAttribute))!).Route, t,
            (CraftEndpointAttribute)Attribute.GetCustomAttribute(t, typeof(CraftEndpointAttribute))!)).ToList();
        app.MapCraftNativeEndpoints(descriptors, new RequestCounter(), NullLogger.Instance);
        await app.StartAsync();
        var client = app.GetTestClient();
        // A distinct caller per test, so tests sharing the on-disk cache never share an entry
        client.DefaultRequestHeaders.Add("x-ms-client-principal",
            Convert.ToBase64String(Encoding.UTF8.GetBytes($$"""{"userId":"{{Guid.NewGuid()}}"}""")));
        return (app, client);
    }

    private static async Task<(string Cache, string Body)> GetAsync(HttpClient client, string path, bool deny = false)
    {
        using var message = new HttpRequestMessage(HttpMethod.Get, path);
        if (deny) message.Headers.Add("x-deny", "1");
        using var response = await client.SendAsync(message);
        var cache = response.Headers.TryGetValues("X-Cache", out var v) ? v.Single() : "";
        return (response.StatusCode == HttpStatusCode.OK ? cache : ((int)response.StatusCode).ToString(System.Globalization.CultureInfo.InvariantCulture), await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task AListGetIsServedFromTheCacheThenRefreshedInTheBackground()
    {
        var (app, client) = await StartAsync();
        await using var _ = app;

        Assert.Equal(("MISS", """{"call":1}"""), await GetAsync(client, "/API/ListCacheThings"));
        Assert.Equal(("HIT", """{"call":1}"""), await GetAsync(client, "/API/ListCacheThings"));

        // The hit started a refresh that replaces the entry with a later call's result
        var body = "";
        for (var i = 0; i < 100 && (body = (await GetAsync(client, "/API/ListCacheThings")).Body) == """{"call":1}"""; i++)
            await Task.Delay(20);
        Assert.NotEqual("""{"call":1}""", body);
    }

    [Fact]
    public async Task TheCentralHandlerStillDecidesOnAHit()
    {
        var (app, client) = await StartAsync();
        await using var _ = app;

        Assert.Equal("MISS", (await GetAsync(client, "/API/ListCacheThings")).Cache);
        Assert.Equal("403", (await GetAsync(client, "/API/ListCacheThings", deny: true)).Cache);
    }

    [Fact]
    public async Task AWriteClearsTheCache()
    {
        var (app, client) = await StartAsync();
        await using var _ = app;

        await GetAsync(client, "/API/ListCacheThings");
        (await client.PostAsync("/API/ExecCacheWrite", null)).EnsureSuccessStatusCode();
        Assert.Equal("MISS", (await GetAsync(client, "/API/ListCacheThings")).Cache);
    }

    [Fact]
    public async Task CachingFollowsTheEndpointsDeclaration()
    {
        var (app, client) = await StartAsync();
        await using var _ = app;

        foreach (var (path, cached) in new[] { ("/API/GetCacheThing", true), ("/API/GetCacheLive", false), ("/API/ListCacheLive", false) })
        {
            await GetAsync(client, path);
            var second = await GetAsync(client, path);
            Assert.Equal(cached ? ("HIT", """{"call":1}""") : ("", """{"call":2}"""), second);
        }
    }
}

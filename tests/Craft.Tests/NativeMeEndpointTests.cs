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

[CraftEndpoint("WhoAmI", Methods = ["GET"])]
internal sealed class WhoAmIEndpoint : ICraftEndpoint
{
    public ValueTask<CraftResult> HandleAsync(CraftRequest r, CancellationToken ct) =>
        new(CraftResult.RawJson("""{"clientPrincipal":{"userDetails":"native"},"permissions":["*"]}"""));
}

/// <summary>
/// An application with no PowerShell workers can still answer the SPA's /api/me: a native endpoint on
/// the configured me route serves it, and the PowerShell route is not mapped at all.
/// </summary>
public class NativeMeEndpointTests
{
    [Fact]
    public async Task ANativeEndpointOnTheMeRouteAnswersApiMe()
    {
        var settings = new CraftSettings { Auth = { MeEndpointFunction = "WhoAmI" } };
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();
        builder.Services.AddSingleton<WhoAmIEndpoint>();
        await using var app = builder.Build();
        var descriptor = new NativeEndpointDescriptor("WhoAmI", typeof(WhoAmIEndpoint),
            (CraftEndpointAttribute)Attribute.GetCustomAttribute(typeof(WhoAmIEndpoint), typeof(CraftEndpointAttribute))!);

        // No PowerShellRunnerService is registered: mapping must not need one when /api/me is native
        app.MapCraftAuthEndpoints(settings, NullLogger.Instance, meIsNative: true);
        app.MapCraftNativeEndpoints([descriptor], new RequestCounter(), NullLogger.Instance, meRoute: AuthEndpoints.MeRoute(settings));
        await app.StartAsync();
        using var client = app.GetTestClient();

        var me = await client.GetStringAsync("/api/me");
        Assert.Contains("\"userDetails\":\"native\"", me);
        Assert.Equal(me, await client.GetStringAsync("/API/WhoAmI"));
        Assert.Contains("clientPrincipal", await client.GetStringAsync("/.auth/me"));
    }

    [CraftEndpoint("me", Methods = ["GET"])]
    internal sealed class MeEndpoint : ICraftEndpoint
    {
        public ValueTask<CraftResult> HandleAsync(CraftRequest r, CancellationToken ct) =>
            new(CraftResult.RawJson("""{"clientPrincipal":{"userDetails":"me"},"permissions":[]}"""));
    }

    [Fact]
    public async Task ANativeEndpointNamedMeIsMappedOnce()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();
        builder.Services.AddSingleton<MeEndpoint>();
        await using var app = builder.Build();
        var descriptor = new NativeEndpointDescriptor("me", typeof(MeEndpoint),
            (CraftEndpointAttribute)Attribute.GetCustomAttribute(typeof(MeEndpoint), typeof(CraftEndpointAttribute))!);

        app.MapCraftAuthEndpoints(new CraftSettings(), NullLogger.Instance, meIsNative: true);
        app.MapCraftNativeEndpoints([descriptor], new RequestCounter(), NullLogger.Instance, meRoute: "me");
        await app.StartAsync();
        using var client = app.GetTestClient();

        // Mapped twice, this would throw an ambiguous match
        Assert.Contains("\"userDetails\":\"me\"", await client.GetStringAsync("/api/me"));
    }

    [Fact]
    public void TheMeRouteDefaultsToMe()
    {
        Assert.Equal("me", AuthEndpoints.MeRoute(new CraftSettings()));
        Assert.Equal("WhoAmI", AuthEndpoints.MeRoute(new CraftSettings { Auth = { MeEndpointFunction = "WhoAmI" } }));
    }
}

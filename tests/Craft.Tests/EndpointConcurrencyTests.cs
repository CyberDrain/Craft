using Craft.Configuration;

namespace Craft.Tests;

/// <summary>
/// In-flight limits for native endpoints, and where they come from.
/// <para>
/// With <c>Worker:HttpPoolSize=0</c> the runspace pool no longer caps concurrent work — it was the
/// implicit limit, because a request could not run without a free worker. Native endpoints hold no
/// thread while awaiting an upstream, so the process will accept far more concurrent requests than it
/// can finish unless something says otherwise.
/// </para>
/// </summary>
public class EndpointConcurrencyTests
{
    private static EndpointSettings Settings(int global = 0, params (string Route, int Limit)[] perRoute)
    {
        var settings = new EndpointSettings { MaxConcurrency = global };
        foreach (var (route, limit) in perRoute) settings.Concurrency[route] = limit;
        return settings;
    }

    [Fact]
    public void UnconfiguredEndpointsAreUnbounded()
    {
        Assert.Equal(0, Settings().ResolveConcurrency("GetIPInfo", declared: 0));
    }

    [Fact]
    public void DeclaredLimitAppliesWhenNothingIsConfigured()
    {
        Assert.Equal(4, Settings().ResolveConcurrency("GeoDBDownload", declared: 4));
    }

    [Fact]
    public void GlobalDefaultAppliesOnlyToEndpointsThatDeclaredNothing()
    {
        var settings = Settings(global: 16);

        Assert.Equal(16, settings.ResolveConcurrency("GetIPInfo", declared: 0));

        // The endpoint declared 4. A blanket default of 16 must not widen it: the author asserted
        // something about this endpoint that an operator setting a global cap cannot know — for
        // GeoDBDownload, that each concurrent caller streams a whole table.
        Assert.Equal(4, settings.ResolveConcurrency("GeoDBDownload", declared: 4));
    }

    [Fact]
    public void PerRouteConfigOverridesTheDeclaredLimit()
    {
        var settings = Settings(global: 16, ("GeoDBDownload", 2));
        Assert.Equal(2, settings.ResolveConcurrency("GeoDBDownload", declared: 4));
    }

    [Fact]
    public void PerRouteZeroExplicitlyRemovesADeclaredLimit()
    {
        // Distinct from "unset". Naming the route with 0 is how an operator lifts a limit deliberately.
        var settings = Settings(global: 16, ("GeoDBDownload", 0));
        Assert.Equal(0, settings.ResolveConcurrency("GeoDBDownload", declared: 4));
    }

    [Fact]
    public void RouteLookupIsCaseInsensitive()
    {
        // Routes are matched case-insensitively by ASP.NET, so a limit keyed differently in a config
        // file than in the attribute must still bind — otherwise the limit silently does nothing.
        var settings = Settings(perRoute: ("geodbdownload", 2));
        Assert.Equal(2, settings.ResolveConcurrency("GeoDBDownload", declared: 4));
    }

    [Fact]
    public void NegativeValuesAreTreatedAsUnbounded()
    {
        Assert.Equal(0, Settings(perRoute: ("GetIPInfo", -1)).ResolveConcurrency("GetIPInfo", declared: 8));
        Assert.Equal(0, Settings(global: -5).ResolveConcurrency("GetIPInfo", declared: 0));
    }

    [Fact]
    public void QueueTimeoutFallsBackToTheEndpointsOwn()
    {
        Assert.Equal(30, new EndpointSettings().ResolveQueueTimeout(declared: 30));
    }

    [Fact]
    public void QueueTimeoutOverrideWins()
    {
        Assert.Equal(5, new EndpointSettings { QueueTimeoutSeconds = 5 }.ResolveQueueTimeout(declared: 30));
    }
}

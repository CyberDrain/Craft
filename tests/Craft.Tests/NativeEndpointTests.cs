using Craft.Configuration;
using Craft.Endpoints;
using Microsoft.Extensions.Logging.Abstractions;

namespace Craft.Tests;

[CraftEndpoint("TestAlpha")]
internal sealed class AlphaEndpoint : ICraftEndpoint
{
    public ValueTask<CraftResult> HandleAsync(CraftRequest r, CancellationToken ct) =>
        new(CraftResult.RawJson("""{"ok":true}"""));
}

[CraftEndpoint("TestBeta", Methods = ["POST"], MaxConcurrency = 4, Role = "admin")]
internal sealed class BetaEndpoint : ICraftEndpoint
{
    public ValueTask<CraftResult> HandleAsync(CraftRequest r, CancellationToken ct) =>
        new(CraftResult.RawJson("""{"ok":true}"""));
}

/// <summary>
/// Discovery and collision handling. These are the parts that fail silently rather than loudly: a
/// route that never got mapped, or a PowerShell endpoint quietly shadowed by a native one, both look
/// like the application misbehaving rather than like configuration.
/// </summary>
public class NativeEndpointDiscoveryTests
{
    private static EndpointSettings Off => new();

    [Fact]
    public void DisabledByDefault()
    {
        // A deployment that configures nothing must pay nothing — no assembly loading, no reflection.
        var catalog = NativeEndpointRegistry.Discover(Off, AppContext.BaseDirectory, NullLogger.Instance);

        Assert.True(catalog.IsEmpty);
        Assert.Empty(catalog.Endpoints);
        Assert.Null(catalog.HandlerType);
        Assert.Empty(catalog.ScheduledTasks);
    }

    [Fact]
    public void EnabledWithNoAssembliesFindsNothing()
    {
        var settings = new EndpointSettings { Enabled = true };

        Assert.True(NativeEndpointRegistry.Discover(
            settings, AppContext.BaseDirectory, NullLogger.Instance).IsEmpty);
    }

    [Fact]
    public void MissingAssemblyIsReportedNotThrown()
    {
        // A typo in configuration must not take the host down: the other endpoints still work, and
        // the log names the path. Throwing here would turn one bad setting into a failed deploy.
        var settings = new EndpointSettings { Enabled = true, Assemblies = ["does-not-exist.dll"] };

        Assert.True(NativeEndpointRegistry.Discover(
            settings, AppContext.BaseDirectory, NullLogger.Instance).IsEmpty);
    }

    [Fact]
    public void AttributeCarriesRouteAndMetadata()
    {
        var alpha = typeof(AlphaEndpoint).GetCustomAttributes(typeof(CraftEndpointAttribute), false);
        var beta = (CraftEndpointAttribute)typeof(BetaEndpoint)
            .GetCustomAttributes(typeof(CraftEndpointAttribute), false)[0];

        Assert.Single(alpha);
        Assert.Equal("TestBeta", beta.Route);
        Assert.Equal(["POST"], beta.Methods);
        Assert.Equal(4, beta.MaxConcurrency);
        Assert.Equal("admin", beta.Role);
    }

    [Fact]
    public void LeadingAndTrailingSlashesAreTrimmedFromRoutes()
    {
        // The route is concatenated onto "/API/", so a leading slash would produce "/API//X" — a
        // different URL that silently never matches what the caller asks for.
        Assert.Equal("Thing", new CraftEndpointAttribute("/Thing/").Route);
    }

    [Fact]
    public void EmptyRouteIsRejected()
    {
        Assert.ThrowsAny<ArgumentException>(() => new CraftEndpointAttribute("  "));
    }
}

public class NativeEndpointCollisionTests
{
    private static readonly NativeEndpointDescriptor s_alpha =
        new("TestAlpha", typeof(AlphaEndpoint), new CraftEndpointAttribute("TestAlpha"));
    private static readonly NativeEndpointDescriptor s_beta =
        new("TestBeta", typeof(BetaEndpoint), new CraftEndpointAttribute("TestBeta"));

    private static IReadOnlyList<NativeEndpointDescriptor> Resolve(
        string policy, params string[] psRoutes) =>
        NativeEndpointRegistry.ResolveCollisions(
            [s_alpha, s_beta], psRoutes, policy, NullLogger.Instance);

    [Fact]
    public void NoCollisionPassesEverythingThrough()
    {
        Assert.Equal(2, Resolve("PreferNative", "SomethingElse").Count);
    }

    [Fact]
    public void PreferNativeKeepsTheNativeEndpoint()
    {
        // The migration path: the PowerShell function stays loaded as the rollback while the native
        // one serves.
        var mapped = Resolve("PreferNative", "TestAlpha");

        Assert.Equal(2, mapped.Count);
        Assert.Contains(mapped, e => e.Route == "TestAlpha");
    }

    [Fact]
    public void PreferPowerShellDropsTheNativeEndpoint()
    {
        // Rollback without rebuilding the assembly — one setting, and the PowerShell function is
        // serving again.
        var mapped = Resolve("PreferPowerShell", "TestAlpha");

        Assert.Single(mapped);
        Assert.Equal("TestBeta", mapped[0].Route);
    }

    [Fact]
    public void FailRefusesToStartAndNamesTheRoute()
    {
        var ex = Assert.Throws<InvalidOperationException>(() => Resolve("Fail", "TestAlpha"));

        Assert.Contains("TestAlpha", ex.Message, StringComparison.Ordinal);
        Assert.Contains("OnCollision", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void RouteMatchingIsCaseInsensitive()
    {
        // CRAFT's PowerShell route table is case-insensitive, so a collision that differs only by case
        // is still a collision — otherwise both would map and which one answered would depend on
        // ASP.NET's own matching rather than on the configured policy.
        Assert.Single(Resolve("PreferPowerShell", "testalpha"));
    }

    [Fact]
    public void TwoNativeEndpointsOnOneRouteAlwaysThrows()
    {
        // Unlike a PowerShell collision this has no rollback story: which one won would come down to
        // assembly scan order, so it is a bug in every policy.
        var duplicate = new NativeEndpointDescriptor(
            "TestAlpha", typeof(BetaEndpoint), new CraftEndpointAttribute("TestAlpha"));

        var ex = Assert.Throws<InvalidOperationException>(() =>
            NativeEndpointRegistry.ResolveCollisions(
                [s_alpha, duplicate], [], "PreferNative", NullLogger.Instance));

        Assert.Contains("Duplicate", ex.Message, StringComparison.Ordinal);
    }
}

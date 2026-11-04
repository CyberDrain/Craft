using System.Text.Json;
using Craft.Hosting.Endpoints;
using Craft.Setup;

namespace Craft.Tests;

/// <summary>
/// Restricted multi-tenant mode writes WEBSITE_AUTH_AAD_ALLOWED_TENANTS. The setup tenant must always be on
/// that list or the admin who ran setup locks themselves out, and the setting must stay unset for the other
/// two modes, where it would either do nothing (single) or wrongly restrict (open multi-tenant).
/// </summary>
public class RestrictedTenantModeTests
{
    private const string Home = "11111111-1111-1111-1111-111111111111";
    private const string Partner = "22222222-2222-2222-2222-222222222222";

    [Fact]
    public void SingleTenant_NeverSetsTheList() =>
        Assert.Empty(SetupService.RestrictedTenants(Home, multiTenant: false, [Partner]));

    [Fact]
    public void OpenMultiTenant_LeavesTheListUnset() =>
        Assert.Empty(SetupService.RestrictedTenants(Home, multiTenant: true, ["", " "]));

    [Fact]
    public void RestrictedMultiTenant_PutsTheSetupTenantFirst_Normalised()
    {
        var tenants = SetupService.RestrictedTenants(Home, multiTenant: true, [Partner.ToUpperInvariant(), Home, Partner]);

        Assert.Equal([Home, Partner], tenants);
    }

    private static JsonElement Body(string json) => JsonDocument.Parse(json).RootElement.Clone();

    [Fact]
    public void MissingAllowedTenants_IsAnEmptyList() =>
        Assert.Empty(SetupEndpoints.ReadAllowedTenants(Body("""{"multiTenant":true}"""))!);

    [Fact]
    public void AllowedTenants_AreNormalisedGuids() =>
        Assert.Equal([Partner], SetupEndpoints.ReadAllowedTenants(Body($$"""{"allowedTenants":["{{Partner.ToUpperInvariant()}}"]}"""))!);

    [Theory]
    [InlineData("""{"allowedTenants":["contoso.com"]}""")]
    [InlineData("""{"allowedTenants":[42]}""")]
    public void AnythingButATenantId_IsRejected(string json) =>
        Assert.Null(SetupEndpoints.ReadAllowedTenants(Body(json)));
}

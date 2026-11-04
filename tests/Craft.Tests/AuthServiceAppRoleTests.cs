using Craft.Auth;
using Craft.Configuration;
using Craft.Storage;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace Craft.Tests;

/// <summary>
/// Any tenant that consents a multi-tenant app can assign itself any of the app's roles, so a token's
/// roles claim must only count for roles the app-role table lets that token's own tenant grant.
/// </summary>
public class AuthServiceAppRoleTests
{
    private const string Home = "11111111-1111-1111-1111-111111111111";
    private const string Partner = "22222222-2222-2222-2222-222222222222";
    private static readonly string?[] Ids = ["user@contoso.com", "user-oid"];

    private static async Task<AuthService> Service(bool allowAll = true, string appRoleTable = "allowedAppRoles")
    {
        var store = new MemoryTableStore();
        await store.UpsertAsync("allowedAppRoles", new StoreRow(Home, "admin"));
        await store.UpsertAsync("allowedAppRoles", new StoreRow(Partner, "editor"));
        var settings = new CraftSettings();
        settings.Auth.AllowAllTenantUsers = allowAll;
        settings.Auth.AppRoleTableName = appRoleTable;
        var config = new ConfigurationBuilder().AddInMemoryCollection([]).Build();
        return new AuthService(NullLogger<AuthService>.Instance, config, settings, store);
    }

    [Fact]
    public async Task TrustedAppRole_IsGranted_WithoutAnAllowedUsersRow()
    {
        var auth = await Service(allowAll: false);

        var roles = await auth.GetUserRoles(Ids, Partner, ["EDITOR"]);

        Assert.Equal(["editor", "anonymous", "authenticated"], roles!);
    }

    [Fact]
    public async Task RoleTheTenantIsNotTrustedWith_IsDropped()
    {
        // The partner tenant assigned itself admin; only its own grant counts.
        var auth = await Service();

        var roles = await auth.GetUserRoles(Ids, Partner, ["admin", "editor"]);

        Assert.Equal(["editor", "anonymous", "authenticated"], roles!);
    }

    [Fact]
    public async Task UnknownTenant_GetsNoAppRoles()
    {
        var auth = await Service(allowAll: false);

        Assert.Null(await auth.GetUserRoles(Ids, "33333333-3333-3333-3333-333333333333", ["admin"]));
    }

    [Fact]
    public async Task AppRoles_AreIgnored_WhenNoTableIsConfigured()
    {
        var auth = await Service(appRoleTable: "");

        Assert.Equal(["anonymous", "authenticated"], (await auth.GetUserRoles(Ids, Home, ["admin"]))!);
    }
}

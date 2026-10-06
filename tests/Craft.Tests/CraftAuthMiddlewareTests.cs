using System.Text;
using System.Text.Json;
using Craft.Auth;
using Craft.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;

namespace Craft.Tests;

/// <summary>
/// Pins how an EasyAuth principal is rewritten for the hosted app. An app-only token must become an API
/// client (idp "aad", name = app id, no allowedUsers lookup); a signed-in user must be authorised against
/// allowedUsers and keep its own identity even when the token names the app it signed in through. Both
/// keep the token's claims. Getting the split wrong either locks out every API client or runs a user as
/// an app.
/// </summary>
public class CraftAuthMiddlewareTests
{
    private const string OidClaim = "http://schemas.microsoft.com/identity/claims/objectidentifier";
    private static readonly string[] AdminRoles = ["admin"];
    private static readonly string?[] UserIdentifiers = ["a@b.com", "user-oid"];

    private static string EasyAuthHeader(params (string Typ, string Val)[] claims)
    {
        var items = string.Join(",", claims.Select(c => $$"""{"typ":"{{c.Typ}}","val":"{{c.Val}}"}"""));
        return Convert.ToBase64String(Encoding.UTF8.GetBytes($$"""{"auth_typ":"aad","claims":[{{items}}]}"""));
    }

    private static DefaultHttpContext Request(string principalHeader)
    {
        var context = new DefaultHttpContext();
        context.Request.Headers["x-ms-client-principal"] = principalHeader;
        context.Request.Headers["x-ms-client-principal-idp"] = "aad";
        context.Response.Body = new MemoryStream();
        return context;
    }

    private sealed class RoleLookup(string[]? roles)
    {
        public List<string?[]> Calls { get; } = [];

        public Task<string[]?> Resolve(IEnumerable<string?> ids)
        {
            Calls.Add([.. ids]);
            return Task.FromResult(roles);
        }
    }

    private static async Task<bool> Normalise(HttpContext context, RoleLookup lookup) =>
        await CraftAuthMiddleware.TryNormalisePrincipalAsync(
            context, lookup.Resolve, NullLogger.Instance, context.Request.Headers["x-ms-client-principal"].ToString());

    private static JsonElement Principal(HttpContext context)
    {
        using var document = EasyAuthPrincipal.Decode(context.Request.Headers["x-ms-client-principal"].ToString());
        return document.RootElement.Clone();
    }

    private static string? Claim(JsonElement principal, string typ) =>
        principal.GetProperty("claims").EnumerateArray()
            .Where(c => c.GetProperty("typ").GetString() == typ)
            .Select(c => c.GetProperty("val").GetString())
            .FirstOrDefault();

    [Fact]
    public async Task AppOnlyToken_BecomesAnApiClient()
    {
        var lookup = new RoleLookup(AdminRoles);
        var context = Request(EasyAuthHeader(("appid", "app-1"), (OidClaim, "sp-oid"), ("idtyp", "app"), ("azpacr", "1")));

        Assert.True(await Normalise(context, lookup));

        Assert.Equal("aad", context.Request.Headers["x-ms-client-principal-idp"].ToString());
        Assert.Equal("app-1", context.Request.Headers["x-ms-client-principal-name"].ToString());
        var principal = Principal(context);
        Assert.Equal("app-1", principal.GetProperty("userDetails").GetString());
        Assert.Equal("sp-oid", principal.GetProperty("userId").GetString());
        Assert.Equal(0, principal.GetProperty("userRoles").GetArrayLength());
        Assert.Equal("1", Claim(principal, "azpacr"));
        Assert.Empty(lookup.Calls);   // an API client is never looked up in allowedUsers
    }

    [Fact]
    public async Task SignedInUser_IsAuthorisedAndKeepsItsOwnIdentity()
    {
        var lookup = new RoleLookup(AdminRoles);
        var context = Request(EasyAuthHeader(
            ("upn", "a@b.com"), (OidClaim, "user-oid"), ("azp", "client-abc"), ("scp", "user_impersonation")));

        Assert.True(await Normalise(context, lookup));

        // The token names the app the user signed in through, but the caller is the user, not that app.
        Assert.Equal("azureStaticWebApps", context.Request.Headers["x-ms-client-principal-idp"].ToString());
        Assert.Equal("a@b.com", context.Request.Headers["x-ms-client-principal-name"].ToString());
        var principal = Principal(context);
        Assert.Equal("a@b.com", principal.GetProperty("userDetails").GetString());
        Assert.Equal("user-oid", principal.GetProperty("userId").GetString());
        Assert.Equal("admin", principal.GetProperty("userRoles")[0].GetString());
        Assert.Equal("client-abc", Claim(principal, "azp"));
        Assert.Equal("user_impersonation", Claim(principal, "scp"));
        Assert.Equal(UserIdentifiers, Assert.Single(lookup.Calls));
    }

    [Fact]
    public async Task UserNotInAllowedUsers_IsRejectedAndThePrincipalStripped()
    {
        var context = Request(EasyAuthHeader(("upn", "a@b.com"), ("azp", "client-abc")));

        Assert.False(await Normalise(context, new RoleLookup(null)));

        Assert.Equal(StatusCodes.Status401Unauthorized, context.Response.StatusCode);
        Assert.False(context.Request.Headers.ContainsKey("x-ms-client-principal"));
    }

    [Fact]
    public async Task NormalisedPrincipal_PassesThroughUntouched()
    {
        var normalised = EasyAuthPrincipal.EncodeNormalised(
            JsonDocument.Parse("""{"claims":[{"typ":"upn","val":"a@b.com"}]}""").RootElement,
            "aad", "user-oid", "a@b.com", AdminRoles);
        var lookup = new RoleLookup(null);
        var context = Request(normalised);

        Assert.True(await Normalise(context, lookup));

        Assert.Equal(normalised, context.Request.Headers["x-ms-client-principal"].ToString());
        Assert.Empty(lookup.Calls);
    }

    [Fact]
    public async Task PrincipalWithNoIdentity_PassesThroughUntouched()
    {
        var header = EasyAuthHeader(("scp", "user_impersonation"));
        var lookup = new RoleLookup(AdminRoles);
        var context = Request(header);

        Assert.True(await Normalise(context, lookup));

        Assert.Equal(header, context.Request.Headers["x-ms-client-principal"].ToString());
        Assert.Empty(lookup.Calls);
    }

    [Fact]
    public async Task UnparseablePrincipal_PassesThroughUntouched()
    {
        var context = Request("not-base64!!");

        Assert.True(await Normalise(context, new RoleLookup(AdminRoles)));

        Assert.Equal("not-base64!!", context.Request.Headers["x-ms-client-principal"].ToString());
    }
}

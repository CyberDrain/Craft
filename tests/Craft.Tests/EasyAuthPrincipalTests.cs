using System.Text;
using System.Text.Json;
using Craft.Auth;

namespace Craft.Tests;

/// <summary>
/// Claim extraction decides whether a caller is treated as a signed-in user (checked against the
/// allowedUsers table) or a service principal (not checked). A mistake here either locks out every API
/// client or lets an unlisted user through, so it is worth pinning down precisely.
/// </summary>
public class EasyAuthPrincipalTests
{
    private static JsonElement Principal(string json) => JsonDocument.Parse(json).RootElement.Clone();

    private static JsonElement WithClaims(params (string Typ, string Val)[] claims)
    {
        var items = string.Join(",", claims.Select(c =>
            $$"""{"typ":"{{c.Typ}}","val":"{{c.Val}}"}"""));
        return Principal($$"""{"claims":[{{items}}]}""");
    }

    [Theory]
    [InlineData("/_next/static/chunk.js")]
    [InlineData("/assets/logo.svg")]
    [InlineData("/.auth/me")]
    [InlineData("/favicon.ico")]
    public void StaticAndAuthPaths_SkipInjection(string path)
    {
        // Keeps an allowedUsers table lookup off the static asset path.
        Assert.True(EasyAuthPrincipal.ShouldSkipInjection(path));
    }

    [Theory]
    [InlineData("/api/ListUsers")]
    [InlineData("/dashboard")]
    [InlineData("/")]
    public void ApplicationPaths_GetInjection(string path) =>
        Assert.False(EasyAuthPrincipal.ShouldSkipInjection(path));

    [Fact]
    public void EasyAuthFormat_NeedsTransform()
    {
        Assert.True(EasyAuthPrincipal.NeedsTransform(Principal("""{"claims":[]}""")));
    }

    [Fact]
    public void SwaFormat_PassesThroughUntouched()
    {
        // A principal carrying userRoles came from the trusted front end — EasyAuth strips inbound
        // principal headers upstream, so it cannot have been supplied by the caller.
        Assert.False(EasyAuthPrincipal.NeedsTransform(
            Principal("""{"userRoles":["admin"],"userDetails":"a@b.com"}""")));
        Assert.False(EasyAuthPrincipal.NeedsTransform(
            Principal("""{"claims":[],"userRoles":["admin"]}""")));
    }

    [Fact]
    public void UpnClaim_IsExtracted()
    {
        var claims = EasyAuthPrincipal.ExtractClaims(WithClaims(
            ("http://schemas.xmlsoap.org/ws/2005/05/identity/claims/upn", "user@contoso.com"),
            ("http://schemas.microsoft.com/identity/claims/objectidentifier", "oid-123")));

        Assert.Equal("user@contoso.com", claims.Upn);
        Assert.Equal("oid-123", claims.ObjectId);
        Assert.False(claims.IsAppOnly);
    }

    [Fact]
    public void ShortFormUpnClaim_IsAlsoAccepted()
    {
        var claims = EasyAuthPrincipal.ExtractClaims(WithClaims(("upn", "user@contoso.com")));
        Assert.Equal("user@contoso.com", claims.Upn);
    }

    [Fact]
    public void AuthoritativeUpn_BeatsPreferredUsername()
    {
        // preferred_username is user-changeable and not guaranteed unique, so it must never override a
        // real upn claim — authorization is keyed on the result.
        var claims = EasyAuthPrincipal.ExtractClaims(WithClaims(
            ("preferred_username", "alias@contoso.com"),
            ("upn", "real@contoso.com")));

        Assert.Equal("real@contoso.com", claims.Upn);
    }

    [Fact]
    public void PreferredUsername_IsUsedOnlyWhenNoUpnPresent()
    {
        var claims = EasyAuthPrincipal.ExtractClaims(WithClaims(("preferred_username", "alias@contoso.com")));
        Assert.Equal("alias@contoso.com", claims.Upn);
    }

    [Theory]
    [InlineData("appid")]
    [InlineData("azp")]
    public void AppOnlyToken_IsDetectedFromTheClientIdClaim(string claimType)
    {
        var claims = EasyAuthPrincipal.ExtractClaims(WithClaims((claimType, "client-abc")));

        Assert.Null(claims.Upn);
        Assert.Equal("client-abc", claims.AppId);
        Assert.True(claims.IsAppOnly);
    }

    [Fact]
    public void AppOnlyToken_IsDetectedFromIdtyp()
    {
        var claims = EasyAuthPrincipal.ExtractClaims(WithClaims(("idtyp", "APP")));
        Assert.True(claims.IsAppOnly);   // comparison is case-insensitive
    }

    [Fact]
    public void TokenWithBothUpnAndAppId_IsTreatedAsAUser()
    {
        // An on-behalf-of token carries both. Treating it as app-only would skip the allowedUsers
        // check for a real human — the presence of a UPN must win.
        var claims = EasyAuthPrincipal.ExtractClaims(WithClaims(
            ("upn", "user@contoso.com"),
            ("appid", "client-abc")));

        Assert.False(claims.IsAppOnly);
        Assert.Equal("user@contoso.com", claims.Upn);
    }

    [Fact]
    public void EmptyClaimValues_AreNormalisedToNull()
    {
        var claims = EasyAuthPrincipal.ExtractClaims(WithClaims(("upn", ""), ("appid", "")));

        Assert.Null(claims.Upn);
        Assert.Null(claims.AppId);
        Assert.False(claims.IsAppOnly);   // no UPN, but no positive app evidence either
    }

    [Fact]
    public void MissingOrMalformedClaimsArray_DoesNotThrow()
    {
        Assert.Null(EasyAuthPrincipal.ExtractClaims(Principal("""{}""")).Upn);
        Assert.Null(EasyAuthPrincipal.ExtractClaims(Principal("""{"claims":"nope"}""")).Upn);
        Assert.Null(EasyAuthPrincipal.ExtractClaims(Principal("""{"claims":[{"val":"orphan"}]}""")).Upn);
    }

    [Fact]
    public void IdentityProviderHeader_WinsOverAuthTyp()
    {
        var root = Principal("""{"auth_typ":"from-principal"}""");
        Assert.Equal("from-header", EasyAuthPrincipal.ResolveIdentityProvider("from-header", root));
        Assert.Equal("from-principal", EasyAuthPrincipal.ResolveIdentityProvider("", root));
        Assert.Equal("", EasyAuthPrincipal.ResolveIdentityProvider(null, Principal("{}")));
    }

    private static readonly string[] AdminRole = ["admin"];

    [Fact]
    public void EncodeAndDecode_RoundTrip()
    {
        var encoded = EasyAuthPrincipal.Encode(new { userDetails = "user@contoso.com", userRoles = AdminRole });

        // Must be the base64-of-JSON shape the platform uses, since the hosted app decodes it the same way.
        var decodedJson = Encoding.UTF8.GetString(Convert.FromBase64String(encoded));
        Assert.Contains("user@contoso.com", decodedJson, StringComparison.Ordinal);

        using var document = EasyAuthPrincipal.Decode(encoded);
        Assert.Equal("user@contoso.com", document.RootElement.GetProperty("userDetails").GetString());
    }

    [Fact]
    public void Decode_RejectsGarbage()
    {
        // The middleware catches this and passes the request through as anonymous rather than 500ing.
        Assert.ThrowsAny<FormatException>(() => EasyAuthPrincipal.Decode("not-base64!!"));
    }
}

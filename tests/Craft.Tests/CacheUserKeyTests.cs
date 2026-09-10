using Craft.Auth;
using Craft.Caching;
using Craft.Configuration;
using Craft.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;

namespace Craft.Tests;

/// <summary>
/// The response cache key is keyed to the caller's identity, not their role set. This is a security
/// boundary: a cache hit skips the PowerShell handler entirely, so the key is the only thing standing
/// between one caller and another caller's data. If two different users ever produce the same key for
/// the same request, the second is served the first one's response — the exact leak that made the cache
/// key user-scoped in the first place. Every test here is a direction that leak could come back from.
/// </summary>
public class CacheUserKeyTests
{
    // A disabled cache still builds keys the same way (BuildCacheKey does not consult _enabled); disabled
    // just keeps these pure tests off the disk and eviction timer.
    private static CacheService KeyBuilder() =>
        new(NullLogger<CacheService>.Instance, new CraftSettings(), enabled: false);

    /// <summary>Builds a context carrying a normalised SWA principal, exactly as the auth middleware writes it.</summary>
    private static DefaultHttpContext Principal(string? userId, string? userDetails = null, string[]? roles = null)
    {
        var context = new DefaultHttpContext();
        context.Request.Headers["x-ms-client-principal"] = EasyAuthPrincipal.Encode(new
        {
            userId,
            userDetails,
            userRoles = roles ?? Array.Empty<string>(),
        });
        return context;
    }

    private static HttpContext WithQuery(HttpContext context, string queryString)
    {
        context.Request.QueryString = new QueryString(queryString);
        return context;
    }

    private static string KeyFor(CacheService cache, string endpoint, HttpContext context) =>
        cache.BuildCacheKey(endpoint, context.Request.Query, CacheService.GetUserKey(context));

    [Fact]
    public void SameUser_SameRequest_ProducesTheSameKey()
    {
        var cache = KeyBuilder();
        var a = WithQuery(Principal("oid-alice"), "?tenantFilter=contoso.com");
        var b = WithQuery(Principal("oid-alice"), "?tenantFilter=contoso.com");

        Assert.Equal(KeyFor(cache, "ListUsers", a), KeyFor(cache, "ListUsers", b));
    }

    [Fact]
    public void DifferentUsers_SameRolesAndQuery_ProduceDifferentKeys()
    {
        // The bug this whole change exists to close: two callers with identical roles hitting the same
        // endpoint with the same query must NOT collide, or a hit serves one of them the other's data.
        var cache = KeyBuilder();
        var alice = WithQuery(Principal("oid-alice", roles: ["editor"]), "?tenantFilter=contoso.com");
        var bob = WithQuery(Principal("oid-bob", roles: ["editor"]), "?tenantFilter=contoso.com");

        Assert.NotEqual(KeyFor(cache, "ListUsers", alice), KeyFor(cache, "ListUsers", bob));
    }

    [Fact]
    public void SameUser_DifferentRoles_ProducesTheSameKey()
    {
        // Keyed to identity, not roles — documents the deliberate change from the old role-hash scheme.
        // A user's own entries are theirs regardless of the role list the principal happens to carry.
        var cache = KeyBuilder();
        var admin = WithQuery(Principal("oid-alice", roles: ["admin", "editor"]), "?tenantFilter=contoso.com");
        var reader = WithQuery(Principal("oid-alice", roles: ["readonly"]), "?tenantFilter=contoso.com");

        Assert.Equal(KeyFor(cache, "ListUsers", admin), KeyFor(cache, "ListUsers", reader));
    }

    [Fact]
    public void AuthenticatedRequest_CarriesAUserComponent()
    {
        var cache = KeyBuilder();
        var key = KeyFor(cache, "ListUsers", Principal("oid-alice"));

        Assert.Contains("|_user=", key);
    }

    [Fact]
    public void AnonymousRequest_HasNoUserComponent_AndAllAnonymousShareOneBucket()
    {
        // No principal → no identity to key on. All anonymous callers collapsing into one bucket is
        // correct: they share one (empty) authorization, so there is nothing to leak between them.
        var cache = KeyBuilder();

        Assert.Null(CacheService.GetUserKey(new DefaultHttpContext()));

        var one = cache.BuildCacheKey("ListUsers", new DefaultHttpContext().Request.Query, null);
        var two = cache.BuildCacheKey("ListUsers", new DefaultHttpContext().Request.Query, null);
        Assert.DoesNotContain("|_user=", one);
        Assert.Equal(one, two);
    }

    [Fact]
    public void AppOnlyClients_WithDifferentIds_ProduceDifferentKeys()
    {
        // Service principals arrive with an empty userRoles array. Under the old role-hash scheme every
        // such client hashed identically and shared one bucket; keying on userId keeps them apart.
        var cache = KeyBuilder();
        var clientA = Principal("app-1111", userDetails: "1111", roles: []);
        var clientB = Principal("app-2222", userDetails: "2222", roles: []);

        Assert.NotEqual(KeyFor(cache, "ListUsers", clientA), KeyFor(cache, "ListUsers", clientB));
    }

    [Fact]
    public void UserId_IsPreferred_AndUserDetails_IsTheFallback()
    {
        // userId is the stable identifier; userDetails only stands in when a principal lacks one. A
        // principal whose userId equals another's userDetails must therefore key the same.
        Assert.Equal(
            CacheService.GetUserKey(Principal(userId: null, userDetails: "alice@contoso.com")),
            CacheService.GetUserKey(Principal(userId: "alice@contoso.com", userDetails: "ignored")));
    }

    [Fact]
    public void UnparseableHeader_IsTreatedAsAnonymous()
    {
        var context = new DefaultHttpContext();
        context.Request.Headers["x-ms-client-principal"] = "}}not base64 json{{";

        Assert.Null(CacheService.GetUserKey(context));
    }

    [Fact]
    public void QueryScope_StillDifferentiates_ForTheSameUser()
    {
        // User keying layers on top of query scoping; it must not flatten two tenants into one entry.
        var cache = KeyBuilder();
        var contoso = WithQuery(Principal("oid-alice"), "?tenantFilter=contoso.com");
        var fabrikam = WithQuery(Principal("oid-alice"), "?tenantFilter=fabrikam.com");

        Assert.NotEqual(KeyFor(cache, "ListUsers", contoso), KeyFor(cache, "ListUsers", fabrikam));
    }
}

/// <summary>
/// The same guarantee, driven through the real Set → Get path rather than string comparison: a response
/// cached for one user is never handed back to another, while the original caller still gets their hit.
/// This is the end-to-end proof that the key isolation actually gates what <see cref="CacheService"/>
/// serves, not just what strings <c>BuildCacheKey</c> emits.
/// </summary>
public class CacheUserIsolationTests
{
    private static readonly string[] EditorRole = ["editor"];

    private static DefaultHttpContext Principal(string userId)
    {
        var context = new DefaultHttpContext();
        context.Request.QueryString = new QueryString("?tenantFilter=contoso.com");
        context.Request.Headers["x-ms-client-principal"] = EasyAuthPrincipal.Encode(new
        {
            userId,
            userDetails = userId,
            userRoles = EditorRole,
        });
        return context;
    }

    private static string KeyFor(CacheService cache, HttpContext context) =>
        cache.BuildCacheKey("ListUsers", context.Request.Query, CacheService.GetUserKey(context));

    [Fact]
    public async Task CachedResponse_IsNotServedToADifferentUser()
    {
        using var cache = new CacheService(NullLogger<CacheService>.Instance, new CraftSettings(), enabled: true);

        var aliceKey = KeyFor(cache, Principal("oid-alice"));
        var bobKey = KeyFor(cache, Principal("oid-bob"));

        await cache.Set(aliceKey, new ScriptResult { StatusCode = 200, Body = "{\"secret\":\"alice-only\"}" });

        // Bob, same roles and same query, gets a clean miss — the handler will run for him.
        Assert.Null(await cache.Get(bobKey, "ListUsers"));

        // Alice still hits her own entry.
        var aliceHit = await cache.Get(aliceKey, "ListUsers");
        Assert.NotNull(aliceHit);
        Assert.Equal("{\"secret\":\"alice-only\"}", aliceHit!.Result.Body);
    }

    [Fact]
    public async Task SameUser_HitsTheirOwnCachedResponse()
    {
        using var cache = new CacheService(NullLogger<CacheService>.Instance, new CraftSettings(), enabled: true);

        var key = KeyFor(cache, Principal("oid-alice"));
        await cache.Set(key, new ScriptResult { StatusCode = 200, Body = "{\"ok\":true}" });

        // A second request from the same principal keys identically and hits.
        var hit = await cache.Get(KeyFor(cache, Principal("oid-alice")), "ListUsers");
        Assert.NotNull(hit);
        Assert.Equal("{\"ok\":true}", hit!.Result.Body);
    }
}

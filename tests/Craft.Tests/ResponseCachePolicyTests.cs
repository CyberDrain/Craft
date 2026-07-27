using Craft.Caching;
using Craft.Configuration;
using Microsoft.AspNetCore.Http;

namespace Craft.Tests;

/// <summary>
/// The admission policy decides which requests are allowed to share a cache entry, so both of its
/// failure directions are real bugs: too permissive and one caller sees another caller's scope, too
/// strict and the cache silently stops paying for itself.
/// </summary>
public class ResponseCachePolicyTests
{
    private static HttpRequest Request(string? queryString = null, (string Name, string Value)? header = null)
    {
        var context = new DefaultHttpContext();
        if (queryString is not null)
            context.Request.QueryString = new QueryString(queryString);
        if (header is { } h)
            context.Request.Headers[h.Name] = h.Value;
        return context.Request;
    }

    private static ResponseCachePolicy CippPolicy() =>
        new("tenantFilter", ["AllTenants"], "x-craft-no-cache", ["ListLogs", "ListScheduled*"]);

    [Fact]
    public void UnconfiguredPolicy_AllowsEverything()
    {
        // The upgrade path: a deployment that sets none of the new keys must keep caching what it
        // cached before, so an empty RequiredParam cannot mean "nothing qualifies".
        var policy = ResponseCachePolicy.FromSettings(new CacheSettings());

        Assert.True(policy.Allows("ListUsers", Request(), out var reason));
        Assert.Null(reason);
    }

    [Fact]
    public void RequiredParamPresent_IsCacheable()
    {
        Assert.True(CippPolicy().Allows("ListUsers", Request("?tenantFilter=contoso.com"), out var reason));
        Assert.Null(reason);
    }

    [Fact]
    public void RequiredParamMissing_IsNotCacheable()
    {
        // The ListTenants case: no tenantFilter, answered per user, fast on its own. Caching it is
        // how one user's tenant list ends up served to another.
        Assert.False(CippPolicy().Allows("ListUsers", Request("?Endpoint=tenants"), out var reason));
        Assert.Equal("missing-required-param", reason);
    }

    [Fact]
    public void RequiredParamWithNoValue_IsNotCacheable()
    {
        // "?tenantFilter=" parses as present-but-empty, which scopes nothing.
        Assert.False(CippPolicy().Allows("ListUsers", Request("?tenantFilter="), out var reason));
        Assert.Equal("empty-required-param", reason);
    }

    [Theory]
    [InlineData("AllTenants")]
    [InlineData("alltenants")]   // matching is case-insensitive
    public void ExcludedValue_IsNotCacheable(string value)
    {
        Assert.False(CippPolicy().Allows("ListUsers", Request($"?tenantFilter={value}"), out var reason));
        Assert.Equal("excluded-param-value", reason);
    }

    [Fact]
    public void RepeatedParam_IsExcludedIfAnyValueIs()
    {
        // One request covering several scopes produces one response covering all of them, so a single
        // excluded scope taints the whole thing.
        Assert.False(CippPolicy().Allows("ListUsers", Request("?tenantFilter=contoso.com&tenantFilter=AllTenants"), out var reason));
        Assert.Equal("excluded-param-value", reason);
    }

    [Fact]
    public void ExcludedValuesWithoutARequiredParam_GateNothing()
    {
        // Documents the misconfiguration CacheService warns about at startup: excluded values are
        // values *of the required param*, so without one they can never match anything.
        var policy = new ResponseCachePolicy("", ["AllTenants"], "");

        Assert.True(policy.Allows("ListUsers", Request("?tenantFilter=AllTenants"), out _));
    }

    [Fact]
    public void NoCacheHeader_BypassesEvenAnOtherwiseCacheableRequest()
    {
        var request = Request("?tenantFilter=contoso.com", ("x-craft-no-cache", "true"));

        Assert.False(CippPolicy().Allows("ListUsers", request, out var reason));
        Assert.Equal("no-cache-header", reason);
    }

    [Theory]
    [InlineData("false")]
    [InlineData("0")]
    [InlineData("no")]
    [InlineData("")]
    public void NoCacheHeaderWithAFalsyValue_DoesNotBypass(string value)
    {
        // A client that always sends the header and flips its value must not lose caching entirely.
        var request = Request("?tenantFilter=contoso.com", ("x-craft-no-cache", value));

        Assert.True(CippPolicy().Allows("ListUsers", request, out _));
    }

    [Fact]
    public void NoCacheHeaderIsHonouredWithAnyDeliberateValue()
    {
        // Presence is the signal — requiring one exact spelling makes the header a footgun.
        var request = Request("?tenantFilter=contoso.com", ("x-craft-no-cache", "please"));

        Assert.False(CippPolicy().Allows("ListUsers", request, out _));
    }

    [Fact]
    public void EmptyHeaderName_DisablesTheHeaderCheck()
    {
        var policy = new ResponseCachePolicy("", null, "");
        var request = Request(header: ("x-craft-no-cache", "true"));

        Assert.True(policy.Allows("ListUsers", request, out _));
    }

    [Fact]
    public void DefaultSettings_ShipTheBypassHeaderButNoRequirement()
    {
        var settings = new CacheSettings();

        Assert.Equal("", settings.RequiredParam);
        Assert.Empty(settings.ExcludedParamValues);
        Assert.Empty(settings.ExcludedEndpoints);
        Assert.Equal("x-craft-no-cache", settings.NoCacheHeader);
    }
}

/// <summary>
/// Outright endpoint exclusion is the escape hatch for reads the query-string rules cannot classify —
/// an endpoint that takes the required parameter and is still a bad cache candidate.
/// </summary>
public class ResponseCachePolicyEndpointExclusionTests
{
    private static HttpRequest Request(string queryString = "?tenantFilter=contoso.com")
    {
        var context = new DefaultHttpContext();
        context.Request.QueryString = new QueryString(queryString);
        return context.Request;
    }

    private static ResponseCachePolicy Policy(params string[] excludedEndpoints) =>
        new("tenantFilter", ["AllTenants"], "x-craft-no-cache", excludedEndpoints);

    [Fact]
    public void ExcludedEndpoint_IsNotCacheableEvenWithTheRequiredParam()
    {
        // The point of the whole feature: a well-formed, fully-scoped request that still must not be
        // cached because of what the endpoint is.
        Assert.False(Policy("ListLogs").Allows("ListLogs", Request(), out var reason));
        Assert.Equal("excluded-endpoint", reason);
    }

    [Fact]
    public void ExclusionIsCaseInsensitive()
    {
        // Routes are matched case-insensitively elsewhere in dispatch, so the exclusion list must be
        // too — otherwise /API/listlogs quietly slips past a "ListLogs" entry.
        Assert.True(Policy("ListLogs").IsEndpointExcluded("listlogs"));
    }

    [Fact]
    public void UnlistedEndpoint_IsUnaffected()
    {
        Assert.True(Policy("ListLogs").Allows("ListUsers", Request(), out var reason));
        Assert.Null(reason);
    }

    [Theory]
    [InlineData("ListLog*", "ListLogs", true)]
    [InlineData("ListLog*", "ListLogEntries", true)]
    [InlineData("ListLog*", "ListLog", true)]            // '*' matches nothing at all
    [InlineData("ListLog*", "ListUsers", false)]
    [InlineData("*Logs", "ListLogs", true)]
    [InlineData("*Logs", "ListLogsDetail", false)]
    [InlineData("List*Log*", "ListAuditLogs", true)]
    [InlineData("*", "Anything", true)]
    [InlineData("List*s", "ListUsers", true)]
    [InlineData("List*s", "ListUser", false)]
    public void PatternsMatchTheWayGlobsDo(string pattern, string endpoint, bool excluded) =>
        Assert.Equal(excluded, Policy(pattern).IsEndpointExcluded(endpoint));

    [Fact]
    public void BacktrackingPattern_TerminatesAndMatches()
    {
        // Guards the matcher's backtrack path: the naive greedy walk fails this one, and the naive
        // recursive fix is what makes pathological patterns expensive.
        Assert.True(Policy("*a*b*c*").IsEndpointExcluded("xxaxxbxxcxx"));
        Assert.False(Policy("*a*b*c*").IsEndpointExcluded("xxaxxcxxbxx"));
    }

    [Fact]
    public void BlankAndWhitespaceEntries_AreIgnored()
    {
        // A trailing "" left in a JSON array must not turn into "exclude everything".
        var policy = Policy("", "   ", "ListLogs");

        Assert.Equal(1, policy.ExcludedEndpointCount);
        Assert.True(policy.Allows("ListUsers", Request(), out _));
    }

    [Fact]
    public void EntriesAreTrimmed()
    {
        Assert.True(Policy(" ListLogs ").IsEndpointExcluded("ListLogs"));
    }

    [Fact]
    public void NoExclusions_ExcludesNothing()
    {
        Assert.False(ResponseCachePolicy.AllowAll.IsEndpointExcluded("ListLogs"));
        Assert.False(ResponseCachePolicy.FromSettings(new CacheSettings()).IsEndpointExcluded("ListLogs"));
    }

    [Fact]
    public void EndpointExclusionOutranksTheOtherReasons()
    {
        // Ordering is a support-experience decision: "this endpoint is never cached" is the useful
        // answer, not "you also happened to send AllTenants".
        var excluded = Policy("ListLogs").Allows("ListLogs", Request("?tenantFilter=AllTenants"), out var reason);

        Assert.False(excluded);
        Assert.Equal("excluded-endpoint", reason);
    }
}

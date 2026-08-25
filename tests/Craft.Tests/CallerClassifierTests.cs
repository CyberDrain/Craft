using Craft.Configuration;
using Craft.Hosting;
using Microsoft.AspNetCore.Http;

namespace Craft.Tests;

/// <summary>
/// Classifying a caller as API vs UI gates the API concurrency cap, so a misclassification is a
/// correctness bug in both directions: a mislabelled UI request would be capped (throttling a person),
/// and a mislabelled API request would escape the cap it exists to enforce.
/// </summary>
public class CallerClassifierTests
{
    private static DefaultHttpContext Request(string? idp = null, string? name = null)
    {
        var context = new DefaultHttpContext();
        if (idp is not null) context.Request.Headers["x-ms-client-principal-idp"] = idp;
        if (name is not null) context.Request.Headers["x-ms-client-principal-name"] = name;
        return context;
    }

    [Fact]
    public void AppOnlyClient_WithAadIdpAndGuidName_IsApi()
    {
        // Exactly what CraftAuthMiddleware writes for a client-credentials caller.
        Assert.True(CallerClassifier.IsApiClient(
            Request(idp: "aad", name: "11111111-2222-3333-4444-555555555555")));
    }

    [Fact]
    public void InteractiveEntraUser_IsNotApi()
    {
        // A signed-in user is normalised to azureStaticWebApps with a UPN, never aad + GUID.
        Assert.False(CallerClassifier.IsApiClient(
            Request(idp: "azureStaticWebApps", name: "user@contoso.com")));
    }

    [Fact]
    public void AadIdpButNonGuidName_IsNotApi()
    {
        // Belt-and-suspenders: a stray aad idp on a non-GUID principal must not be read as an API client.
        Assert.False(CallerClassifier.IsApiClient(Request(idp: "aad", name: "user@contoso.com")));
    }

    [Fact]
    public void GuidNameButNonAadIdp_IsNotApi()
    {
        Assert.False(CallerClassifier.IsApiClient(
            Request(idp: "azureStaticWebApps", name: "11111111-2222-3333-4444-555555555555")));
    }

    [Fact]
    public void AnonymousRequest_IsNotApi()
    {
        Assert.False(CallerClassifier.IsApiClient(new DefaultHttpContext()));
    }

    [Fact]
    public void IdpMatchIsCaseInsensitive()
    {
        Assert.True(CallerClassifier.IsApiClient(
            Request(idp: "AAD", name: "11111111-2222-3333-4444-555555555555")));
    }
}

/// <summary>
/// The API concurrency cap must default off (unlimited) and honour its env override, and its presence
/// must be what turns the limiter middleware on independently of the per-client rate limiter — so a
/// deployment can run the concurrency cap with the rate limiter disabled, and vice versa.
/// </summary>
public class ApiConcurrencyLimitSettingsTests : IDisposable
{
    private readonly string? _original = Environment.GetEnvironmentVariable("CRAFT_API_CONCURRENCY_LIMIT");

    public ApiConcurrencyLimitSettingsTests() =>
        Environment.SetEnvironmentVariable("CRAFT_API_CONCURRENCY_LIMIT", null);

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("CRAFT_API_CONCURRENCY_LIMIT", _original);
        GC.SuppressFinalize(this);
    }

    [Fact]
    public void DefaultsToOff()
    {
        var rl = new RateLimitSettings();
        Assert.Equal(0, rl.ResolvedApiConcurrencyLimit);
    }

    [Fact]
    public void ConfiguredValueIsUsed()
    {
        var rl = new RateLimitSettings { ApiConcurrencyLimit = 10 };
        Assert.Equal(10, rl.ResolvedApiConcurrencyLimit);
    }

    [Fact]
    public void EnvOverrideWins()
    {
        Environment.SetEnvironmentVariable("CRAFT_API_CONCURRENCY_LIMIT", "4");
        var rl = new RateLimitSettings { ApiConcurrencyLimit = 10 };
        Assert.Equal(4, rl.ResolvedApiConcurrencyLimit);
    }

    [Fact]
    public void EnvOverrideOfZeroDisablesEvenWhenConfigured()
    {
        // A deployment-level kill switch: CRAFT_API_CONCURRENCY_LIMIT=0 turns the cap off regardless
        // of the setting baked into appsettings.
        Environment.SetEnvironmentVariable("CRAFT_API_CONCURRENCY_LIMIT", "0");
        var rl = new RateLimitSettings { ApiConcurrencyLimit = 10 };
        Assert.Equal(0, rl.ResolvedApiConcurrencyLimit);
    }

    [Theory]
    [InlineData("notanumber")]
    [InlineData("-3")]
    public void InvalidOrNegativeEnvIsIgnored(string value)
    {
        Environment.SetEnvironmentVariable("CRAFT_API_CONCURRENCY_LIMIT", value);
        var rl = new RateLimitSettings { ApiConcurrencyLimit = 7 };
        Assert.Equal(7, rl.ResolvedApiConcurrencyLimit);
    }

    [Fact]
    public void RateLimiterOff_ButConcurrencyOn_StillNeedsTheMiddleware()
    {
        var rl = new RateLimitSettings { Enabled = false, ApiConcurrencyLimit = 5 };
        Assert.True(rl.RequiresLimiterMiddleware);
    }

    [Fact]
    public void BothOff_SkipsTheMiddleware()
    {
        var rl = new RateLimitSettings { Enabled = false, ApiConcurrencyLimit = 0 };
        Assert.False(rl.RequiresLimiterMiddleware);
    }
}

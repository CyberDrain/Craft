using Craft.Hosting;

namespace Craft.Tests;

/// <summary>
/// Cache-control policy for static assets. The stakes are asymmetric: over-caching a control file can
/// strand every browser on a stale build with no way to recover, while under-caching a hashed bundle
/// only costs bandwidth.
/// </summary>
public class StaticCachePolicyTests
{
    [Theory]
    [InlineData("/sw.js")]
    [InlineData("/version.json")]
    [InlineData("/manifest.json")]
    [InlineData("/nested/sw.js")]
    public void ControlFiles_AreNeverCached(string path)
    {
        // The service worker decides what the browser fetches next and version.json is how the app
        // notices a new build. A stale copy of either pins clients to an old release.
        var directive = StaticCachePolicy.For(path);

        Assert.Equal("no-cache, must-revalidate", directive.CacheControl);
        Assert.True(directive.IncludeETag);
    }

    [Fact]
    public void ControlFiles_BeatTheHashedBundleRule()
    {
        // A service worker emitted under /_next/static/ must still never be cached, so ordering here
        // is load-bearing rather than incidental.
        var directive = StaticCachePolicy.For("/_next/static/sw.js");
        Assert.Equal("no-cache, must-revalidate", directive.CacheControl);
    }

    [Fact]
    public void ContentHashedBundles_AreImmutableAndNeedNoETag()
    {
        var directive = StaticCachePolicy.For("/_next/static/chunks/main-a1b2c3.js");

        Assert.Equal("public, max-age=86400, immutable", directive.CacheControl);
        // The hash in the URL already identifies the bytes; an ETag would only add a wasted round trip.
        Assert.False(directive.IncludeETag);
    }

    [Theory]
    [InlineData("/logo.png")]
    [InlineData("/icon.ICO")]
    [InlineData("/font.woff2")]
    [InlineData("/photo.jpeg")]
    public void StableNamedBinaries_AreCachedButRevalidated(string path)
    {
        // The filename does not change when the content does, so these can be stored but must be
        // rechecked once the max-age lapses.
        var directive = StaticCachePolicy.For(path);

        Assert.Equal("public, max-age=86400, must-revalidate", directive.CacheControl);
        Assert.True(directive.IncludeETag);
    }

    [Fact]
    public void DataJson_IsStoredButAlwaysRevalidated()
    {
        var directive = StaticCachePolicy.For("/permissionsList.json");
        Assert.Equal("no-cache, must-revalidate", directive.CacheControl);
        Assert.True(directive.IncludeETag);
    }

    [Theory]
    [InlineData("/index.html")]
    [InlineData("/dashboard")]
    [InlineData("")]
    public void HtmlAndEverythingElse_IsAlwaysRevalidated(string path) =>
        Assert.Equal("no-cache, must-revalidate", StaticCachePolicy.For(path).CacheControl);

    [Theory]
    [InlineData("/_next/static/chunk.js", "public, max-age=86400, immutable")]
    [InlineData("/app.js", "no-cache, must-revalidate")]
    public void PrecompressedVariants_FollowTheSameHashedBundleRule(string path, string expected) =>
        Assert.Equal(expected, StaticCachePolicy.ForPrecompressed(path));
}

/// <summary>
/// The gate that runs while the PowerShell worker pool is still warming up.
/// </summary>
public class StartupGateTests
{
    private const string HealthPath = "/healthz";

    [Theory]
    [InlineData("/healthz")]
    [InlineData("/HEALTHZ")]
    [InlineData("/api/setup/health")]
    [InlineData("/.craft/events")]
    public void ProbesAreNeverGated(string path)
    {
        // Blocking a platform probe during a slow start is how a slow start becomes a restart loop.
        Assert.Equal(StartupGateAction.PassThrough,
            StartupGate.Decide(path, setupEnabled: false, healthEnabled: true, HealthPath));
    }

    [Fact]
    public void HealthPath_IsNotSpecialWhenTheProbeIsDisabled()
    {
        Assert.NotEqual(StartupGateAction.PassThrough,
            StartupGate.Decide(HealthPath, setupEnabled: false, healthEnabled: false, HealthPath));
    }

    [Theory]
    [InlineData("/setup")]
    [InlineData("/setup/step1")]
    [InlineData("/api/setup/status")]
    public void SetupRoutes_AreReachableDuringStartup_WhenSetupIsEnabled(string path)
    {
        // The wizard is exactly where an operator is trying to get when the host cannot finish starting.
        Assert.Equal(StartupGateAction.PassThrough,
            StartupGate.Decide(path, setupEnabled: true, healthEnabled: true, HealthPath));
    }

    [Fact]
    public void SetupRoutes_AreGatedWhenSetupIsDisabled()
    {
        Assert.Equal(StartupGateAction.LoadingPage,
            StartupGate.Decide("/setup", setupEnabled: false, healthEnabled: true, HealthPath));
    }

    [Theory]
    [InlineData("/api/ListUsers")]
    [InlineData("/API/ListUsers")]
    public void ApiCallers_Get503NotAnHtmlPage(string path)
    {
        // An API client handed a loading page would parse HTML as JSON and fail confusingly.
        Assert.Equal(StartupGateAction.ApiUnavailable,
            StartupGate.Decide(path, setupEnabled: false, healthEnabled: true, HealthPath));
    }

    [Theory]
    [InlineData("/")]
    [InlineData("/dashboard")]
    public void BrowsersGetTheLoadingPage(string path) =>
        Assert.Equal(StartupGateAction.LoadingPage,
            StartupGate.Decide(path, setupEnabled: false, healthEnabled: true, HealthPath));
}

public class DevFrontendProxyTests
{
    private static Func<string, string?> NoEnv => _ => null;

    [Fact]
    public void DevProxy_OnlyRunsForAFrontendNodeInDevelopment()
    {
        Assert.Null(DevFrontendProxy.ResolveDevServerUrl(frontendRole: false, isDevelopment: true, NoEnv));
        Assert.Null(DevFrontendProxy.ResolveDevServerUrl(frontendRole: true, isDevelopment: false, NoEnv));
        Assert.Equal("http://localhost:3000",
            DevFrontendProxy.ResolveDevServerUrl(frontendRole: true, isDevelopment: true, NoEnv));
    }

    [Fact]
    public void DevServerUrl_IsOverridable()
    {
        var url = DevFrontendProxy.ResolveDevServerUrl(true, true,
            name => name == "CRAFT_DEV_FRONTEND_URL" ? "http://localhost:4321" : null);

        Assert.Equal("http://localhost:4321", url);
    }

    [Theory]
    [InlineData("/_next/static/chunk.js")]
    [InlineData("/__nextjs_original-stack-frame")]
    [InlineData("/version.json")]   // generated by the dev server, not present on disk
    [InlineData("/favicon.ico")]
    public void FrontendAssets_GoToTheDevServer(string path) =>
        Assert.True(DevFrontendProxy.ShouldProxy(path));

    [Theory]
    [InlineData("/api/ListUsers")]
    [InlineData("/API/report.json")]
    [InlineData("/.auth/me")]
    public void ApiAndAuthPaths_AreServedByThisHost(string path)
    {
        // These are CRAFT's own routes. Proxying them to Next would 404 every API call in development.
        Assert.False(DevFrontendProxy.ShouldProxy(path));
    }

    [Theory]
    [InlineData("/dashboard")]
    [InlineData("/")]
    public void ExtensionlessRoutes_FallThroughToTheSpaFallback(string path) =>
        Assert.False(DevFrontendProxy.ShouldProxy(path));
}

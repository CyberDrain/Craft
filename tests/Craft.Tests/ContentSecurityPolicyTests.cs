using Craft.Configuration;

namespace Craft.Tests;

/// <summary>
/// The default Content-Security-Policy is emitted on every response, including CRAFT's own setup
/// wizard. A CSP that blocks the app's own same-origin calls produces no server-side error and no
/// build failure — just a page whose JavaScript silently cannot talk to its own API.
/// </summary>
public class ContentSecurityPolicyTests
{
    private static string DefaultPolicy => new FrontendSettings().ContentSecurityPolicy!;

    private static string DefaultSrc =>
        DefaultPolicy.Split(';').First(d => d.TrimStart().StartsWith("default-src", StringComparison.Ordinal));

    [Fact]
    public void DefaultSrc_Allows_TheAppsOwnOrigin()
    {
        // `connect-src` is not listed, so it falls back to `default-src`. Without 'self' the policy is
        // gated on the https: scheme alone, and every same-origin fetch is refused as soon as the app
        // is reached over http — behind a TLS-terminating proxy, self-hosted, or in local docker.
        // This exact gap took down the setup wizard: its /api/setup/status call was blocked, and the
        // page has no way to configure authentication without it.
        Assert.Contains("'self'", DefaultSrc, StringComparison.Ordinal);
    }

    [Fact]
    public void ThePolicyHasNoExplicitConnectSrc_SoDefaultSrcIsTheOneThatMatters()
    {
        // Guards the reasoning above: if someone adds a connect-src, the 'self' assertion has to move
        // there too, and this test is where they will find out.
        Assert.DoesNotContain("connect-src", DefaultPolicy, StringComparison.Ordinal);
    }

    [Fact]
    public void ADeploymentIsStillCoveredByDefault()
    {
        // Secure-by-default: a host that configures nothing still gets a policy.
        Assert.False(string.IsNullOrWhiteSpace(DefaultPolicy));
        Assert.Contains("object-src", DefaultPolicy, StringComparison.Ordinal);
    }

    [Fact]
    public void TheDocumentedDefault_MatchesTheCode()
    {
        // The Frontend block in appsettings.example.jsonc is entirely commented out, so the config
        // provider never sees it and ConfigurationReferenceTests cannot compare it. Read as text
        // instead — a stale value here is what someone copies into their own settings.
        var example = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "appsettings.example.jsonc"));

        Assert.Contains($"\"ContentSecurityPolicy\": \"{DefaultPolicy}\"", example, StringComparison.Ordinal);
    }
}

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

    private static string Directive(string name) =>
        DefaultPolicy.Split(';').First(d => d.TrimStart().StartsWith(name, StringComparison.Ordinal));

    private static string DefaultSrc => Directive("default-src");

    private static string ConnectSrc => Directive("connect-src");

    [Fact]
    public void DefaultSrc_Allows_TheAppsOwnOrigin()
    {
        // Every fetch directive that is not spelled out falls back to `default-src`. Without 'self' the
        // policy is gated on the https: scheme alone, and same-origin requests are refused as soon as
        // the app is reached over http — behind a TLS-terminating proxy, self-hosted, or in local
        // docker. This exact gap took down the setup wizard: its /api/setup/status call was blocked,
        // and the page has no way to configure authentication without it.
        Assert.Contains("'self'", DefaultSrc, StringComparison.Ordinal);
    }

    [Fact]
    public void ConnectSrc_Allows_TheAppsOwnOrigin()
    {
        // `connect-src` is spelled out, so it no longer inherits 'self' from `default-src` — it has to
        // carry it. This is the trap: dropping 'self' from here alone is enough to block every XHR the
        // app makes to its own API over http, while `default-src` still looks correct.
        Assert.Contains("'self'", ConnectSrc, StringComparison.Ordinal);
    }

    [Fact]
    public void ConnectSrc_Allows_WasmInlinedAsADataUrl()
    {
        // Emscripten SINGLE_FILE builds inline their wasm as data:application/octet-stream;base64,…
        // and fetch it, which `connect-src` gates; wasm-backed layout and parsing libraries are
        // commonly shipped this way. Blocking it is survivable — the loader decodes the base64 itself —
        // but it costs a failed request and a console error every time such a library loads.
        Assert.Contains("data:", ConnectSrc, StringComparison.Ordinal);
    }

    [Fact]
    public void DataUrls_AreNotAllowedToLoadScripts()
    {
        // The reason `connect-src` is spelled out at all: `data:` belongs to it, not to `default-src`,
        // which `script-src` falls back to. `script-src data:` is an XSS vector. If this fails, someone
        // widened `default-src` instead of the directive that actually needed it.
        Assert.DoesNotContain("data:", DefaultSrc, StringComparison.Ordinal);
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

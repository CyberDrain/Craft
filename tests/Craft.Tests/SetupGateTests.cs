using Craft.Setup;

namespace Craft.Tests;

/// <summary>
/// The setup gate serves three audiences at once — a browser that should be steered to or away from
/// the wizard, a poller that needs a parseable answer while the container restarts, and API callers
/// that must never receive an HTML redirect where they expected JSON.
/// </summary>
public class SetupGateTests
{
    private const bool Configured = true;
    private const bool NotConfigured = false;
    private const bool Requested = true;
    private const bool NotRequested = false;

    [Theory]
    [InlineData("/api/ListUsers")]
    [InlineData("/dashboard")]
    [InlineData("/setup")]
    public void SetupModeNotRequested_EverythingFlowsNormally(string path)
    {
        // Setup mode is opt-in. Until the hosted app asks for it, the gate must be invisible.
        Assert.Equal(SetupGateAction.PassThrough,
            SetupGate.Decide(path, NotConfigured, NotRequested));
    }

    // ── Setup required ──────────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("/setup")]
    [InlineData("/setup/step2")]
    [InlineData("/api/setup/status")]
    [InlineData("/api/setup/device-code")]
    public void SetupRequired_TheWizardAndItsApiAreReachable(string path) =>
        Assert.Equal(SetupGateAction.PassThrough, SetupGate.Decide(path, NotConfigured, Requested));

    [Theory]
    [InlineData("/_next/static/chunk.js")]
    [InlineData("/__nextjs_original-stack-frame")]
    [InlineData("/logo.png")]
    [InlineData("/styles.css")]
    public void SetupRequired_AssetsTheWizardNeedsAreLetThrough(string path) =>
        Assert.Equal(SetupGateAction.PassThrough, SetupGate.Decide(path, NotConfigured, Requested));

    [Theory]
    [InlineData("/api/ListUsers")]
    [InlineData("/API/ListUsers")]
    public void SetupRequired_ApiCallsGet503NotARedirect(string path)
    {
        // An API client parsing a 302 to an HTML page produces a confusing failure a long way from
        // the cause. 503 says "not yet" in a way a caller can act on.
        Assert.Equal(SetupGateAction.ApiUnavailable, SetupGate.Decide(path, NotConfigured, Requested));
    }

    [Fact]
    public void SetupRequired_ApiPathWithAnExtension_IsStillTreatedAsApi()
    {
        // The static-asset rule must not become an escape hatch for /api/*.
        Assert.Equal(SetupGateAction.ApiUnavailable,
            SetupGate.Decide("/api/report.json", NotConfigured, Requested));
    }

    [Theory]
    [InlineData("/")]
    [InlineData("/dashboard")]
    [InlineData("/tenants/contoso")]
    public void SetupRequired_BrowsersAreSentToTheWizard(string path) =>
        Assert.Equal(SetupGateAction.RedirectToSetup, SetupGate.Decide(path, NotConfigured, Requested));

    // ── Setup already complete ──────────────────────────────────────────────────────────────────

    [Fact]
    public void Configured_HealthPollingKeepsWorking()
    {
        // Readiness polling has to survive setup completing, or the loading screen hangs forever.
        Assert.Equal(SetupGateAction.PassThrough,
            SetupGate.Decide("/api/setup/health", Configured, NotRequested));
    }

    [Fact]
    public void Configured_StatusPollerGetsAnActionablePayload()
    {
        // The restart screen polls this to learn the new container is live. A 404 would leave it stuck.
        Assert.Equal(SetupGateAction.SetupCompleteStatus,
            SetupGate.Decide("/api/setup/status", Configured, NotRequested));
    }

    [Theory]
    [InlineData("/api/setup/configure")]
    [InlineData("/api/setup/seed-user")]
    [InlineData("/api/setup/device-code")]
    public void Configured_RemainingSetupEndpointsAreGone(string path)
    {
        // These reconfigure authentication. Once setup is done they must not be callable again.
        Assert.Equal(SetupGateAction.SetupEndpointsDisabled,
            SetupGate.Decide(path, Configured, NotRequested));
    }

    [Theory]
    [InlineData("/setup")]
    [InlineData("/setup/step2")]
    public void Configured_BrowsersOnAWizardPageGoBackToTheApp(string path) =>
        Assert.Equal(SetupGateAction.RedirectToRoot, SetupGate.Decide(path, Configured, NotRequested));

    [Theory]
    [InlineData("/dashboard")]
    [InlineData("/api/ListUsers")]
    public void Configured_NormalTrafficIsUnaffected(string path) =>
        Assert.Equal(SetupGateAction.PassThrough, SetupGate.Decide(path, Configured, NotRequested));

    [Fact]
    public void Configured_WinsEvenIfSetupModeWasRequested()
    {
        // A stale RequestSetupMode() must never re-open the wizard on a configured host.
        Assert.Equal(SetupGateAction.RedirectToRoot, SetupGate.Decide("/setup", Configured, Requested));
        Assert.Equal(SetupGateAction.SetupEndpointsDisabled,
            SetupGate.Decide("/api/setup/configure", Configured, Requested));
    }

    [Theory]
    [InlineData("/SETUP")]
    [InlineData("/API/SETUP/STATUS")]
    public void PathMatchingIsCaseInsensitive(string path)
    {
        // A browser or proxy can change casing; the gate must not be bypassable by it.
        Assert.NotEqual(SetupGateAction.PassThrough, SetupGate.Decide(path, Configured, NotRequested));
    }
}

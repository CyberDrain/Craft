namespace Craft.Setup;

/// <summary>What the setup gate decided to do with a request.</summary>
public enum SetupGateAction
{
    /// <summary>Continue down the pipeline.</summary>
    PassThrough,

    /// <summary>Setup is finished; these endpoints no longer exist. 404.</summary>
    SetupEndpointsDisabled,

    /// <summary>Answer the restart-screen poller so it knows the new container is live.</summary>
    SetupCompleteStatus,

    /// <summary>Setup is finished; send a browser sitting on a setup page back to the app.</summary>
    RedirectToRoot,

    /// <summary>Setup is required; send the browser to the wizard.</summary>
    RedirectToSetup,

    /// <summary>Setup is required; refuse the API call.</summary>
    ApiUnavailable,
}

/// <summary>
/// Routing decisions for the first-run setup wizard.
/// <para>
/// The gate has to satisfy three audiences at once: a browser that should be steered to (or away
/// from) <c>/setup</c>, a poller that needs a parseable answer while the container restarts, and API
/// callers that must not receive an HTML redirect where they expected JSON. Extracted as a pure
/// function because the branch table is the whole behaviour.
/// </para>
/// </summary>
public static class SetupGate
{
    /// <summary>
    /// Decides how to handle <paramref name="path"/>.
    /// </summary>
    /// <param name="path">Request path.</param>
    /// <param name="easyAuthConfigured">Whether the platform already has authentication configured.</param>
    /// <param name="setupModeRequested">
    /// Whether the hosted app has called <c>RequestSetupMode()</c>. Setup mode is opt-in: until the
    /// child app decides it cannot self-configure, requests flow normally.
    /// </param>
    public static SetupGateAction Decide(string path, bool easyAuthConfigured, bool setupModeRequested)
    {
        path ??= "";

        if (easyAuthConfigured) return DecideWhenConfigured(path);

        // Not configured, and the app has not asked for the wizard — the startup loading middleware
        // handles the "pool not ready" case, so nothing to do here.
        if (!setupModeRequested) return SetupGateAction.PassThrough;

        return DecideWhenSetupRequired(path);
    }

    private static SetupGateAction DecideWhenConfigured(string path)
    {
        if (path.StartsWith("/api/setup", StringComparison.OrdinalIgnoreCase))
        {
            // Readiness polling must keep working even after setup completes.
            if (path.Equals("/api/setup/health", StringComparison.OrdinalIgnoreCase))
                return SetupGateAction.PassThrough;

            // The restart screen polls this to learn when the new container is online with EasyAuth
            // active. It needs a 200 payload it can act on, not a 404.
            if (path.Equals("/api/setup/status", StringComparison.OrdinalIgnoreCase))
                return SetupGateAction.SetupCompleteStatus;

            return SetupGateAction.SetupEndpointsDisabled;
        }

        if (IsSetupPage(path)) return SetupGateAction.RedirectToRoot;

        return SetupGateAction.PassThrough;
    }

    private static SetupGateAction DecideWhenSetupRequired(string path)
    {
        // The wizard itself and its API.
        if (path.StartsWith("/api/setup", StringComparison.OrdinalIgnoreCase) || IsSetupPage(path))
            return SetupGateAction.PassThrough;

        // Dev-server assets (HMR and friends) — without these the wizard cannot render in development.
        if (path.StartsWith("/_next/", StringComparison.OrdinalIgnoreCase) ||
            path.StartsWith("/__nextjs", StringComparison.OrdinalIgnoreCase))
            return SetupGateAction.PassThrough;

        var isApi = path.StartsWith("/api/", StringComparison.OrdinalIgnoreCase);

        // Static assets the wizard page needs. Checked before the API rule so that a genuine asset
        // request is not mistaken for an API call.
        if (Path.HasExtension(path) && !isApi) return SetupGateAction.PassThrough;

        // An API caller must get a status code it can handle, never an HTML redirect.
        if (isApi) return SetupGateAction.ApiUnavailable;

        return SetupGateAction.RedirectToSetup;
    }

    private static bool IsSetupPage(string path) =>
        path.Equals("/setup", StringComparison.OrdinalIgnoreCase) ||
        path.StartsWith("/setup/", StringComparison.OrdinalIgnoreCase);
}

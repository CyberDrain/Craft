namespace Craft.Hosting;

/// <summary>What to do with a request that arrived before the HTTP worker pool was ready.</summary>
public enum StartupGateAction
{
    /// <summary>Continue down the pipeline.</summary>
    PassThrough,

    /// <summary>Tell an API caller to come back shortly.</summary>
    ApiUnavailable,

    /// <summary>Show a browser the "starting up" holding page.</summary>
    LoadingPage,
}

/// <summary>
/// Gates traffic while the PowerShell HTTP worker pool is still initialising.
/// <para>
/// Warming a pool of runspaces takes seconds, and on a small container it can take a lot of seconds.
/// Without this gate the first requests either block on an unready pool or fail with an error that
/// looks like a real fault.
/// </para>
/// </summary>
public static class StartupGate
{
    /// <summary>
    /// Decides how to handle <paramref name="path"/> while the pool is still coming up.
    /// </summary>
    /// <param name="path">Request path.</param>
    /// <param name="setupEnabled">Whether the setup wizard is switched on for this host.</param>
    /// <param name="healthEnabled">Whether the health probe is mapped.</param>
    /// <param name="healthPath">Configured health probe path.</param>
    public static StartupGateAction Decide(
        string path, bool setupEnabled, bool healthEnabled, string healthPath)
    {
        path ??= "";

        // Probes and readiness pollers must never be gated — blocking them is how a slow start turns
        // into a platform-triggered restart loop.
        if (path.Equals("/api/setup/health", StringComparison.OrdinalIgnoreCase) ||
            path.StartsWith("/.craft/", StringComparison.OrdinalIgnoreCase) ||
            (healthEnabled && path.Equals(healthPath, StringComparison.OrdinalIgnoreCase)))
        {
            return StartupGateAction.PassThrough;
        }

        // The wizard is reachable during startup — it is what an operator is trying to get to when the
        // host cannot finish coming up on its own.
        if (setupEnabled && IsSetupPath(path)) return StartupGateAction.PassThrough;

        if (path.StartsWith("/api/", StringComparison.OrdinalIgnoreCase))
            return StartupGateAction.ApiUnavailable;

        return StartupGateAction.LoadingPage;
    }

    private static bool IsSetupPath(string path) =>
        path.StartsWith("/api/setup", StringComparison.OrdinalIgnoreCase) ||
        path.Equals("/setup", StringComparison.OrdinalIgnoreCase) ||
        path.StartsWith("/setup/", StringComparison.OrdinalIgnoreCase);
}

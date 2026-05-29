namespace Craft.Services;

/// <summary>
/// Static bridge so downstream PowerShell apps can request a container restart.
/// Uses IHostApplicationLifetime.StopApplication() to gracefully stop the host,
/// which causes the App Service container to restart automatically.
///
/// PS usage: [Craft.Services.AppLifecycleBridge]::RequestRestart("EasyAuth configuration applied")
///           [Craft.Services.AppLifecycleBridge]::IsEasyAuthConfigured()
/// </summary>
public static class AppLifecycleBridge
{
    private static IHostApplicationLifetime? s_lifetime;
    private static ILogger? s_logger;

    public static void Initialize(IHostApplicationLifetime lifetime, ILogger logger)
    {
        s_lifetime = lifetime;
        s_logger = logger;
    }

    /// <summary>
    /// Gracefully stops the application host. In Azure App Service, the container
    /// is automatically restarted by the platform after stopping.
    /// </summary>
    /// <param name="reason">Log message explaining why the restart was requested.</param>
    public static void RequestRestart(string reason = "Restart requested by application")
    {
        var lifetime = s_lifetime ?? throw new InvalidOperationException("AppLifecycleBridge not initialized");
        s_logger?.LogWarning("[Lifecycle] Container restart requested: {Reason}", reason);
        lifetime.StopApplication();
    }

    /// <summary>
    /// Check whether EasyAuth is configured. Convenience method so downstream
    /// apps can check without needing a direct reference to SetupService.
    /// </summary>
    public static bool IsEasyAuthConfigured()
    {
        var authEnabled = Environment.GetEnvironmentVariable("WEBSITE_AUTH_ENABLED");
        return string.Equals(authEnabled, "True", StringComparison.OrdinalIgnoreCase);
    }

    // --- Setup mode gating ---
    private static volatile bool s_setupModeRequested;
    private static volatile bool s_setupCompleted;
    private static string? s_setupCompletedReason;

    /// <summary>
    /// Explicitly enables the Craft setup wizard. Call this from the child app
    /// when it determines that initial SSO setup is needed.
    /// PS usage: [Craft.Services.AppLifecycleBridge]::RequestSetupMode("reason")
    /// </summary>
    public static void RequestSetupMode(string reason = "Setup mode requested by application")
    {
        s_setupModeRequested = true;
        s_logger?.LogWarning("[Lifecycle] Setup mode explicitly enabled: {Reason}", reason);
    }

    /// <summary>
    /// Returns true if the child app has explicitly requested setup mode.
    /// Used by the setup middleware to determine whether to activate the setup wizard.
    /// </summary>
    public static bool IsSetupModeRequested() => s_setupModeRequested;

    /// <summary>
    /// Marks setup as completed — credentials have been applied and the app is
    /// pending restart. Prevents duplicate credential submissions and lets all
    /// setup page instances detect completion via status polling.
    /// </summary>
    public static void MarkSetupCompleted(string reason = "Setup credentials applied")
    {
        s_setupCompleted = true;
        s_setupCompletedReason = reason;
        s_logger?.LogInformation("[Lifecycle] Setup marked as completed: {Reason}", reason);
    }

    /// <summary>
    /// Returns true if setup credentials have already been applied this session.
    /// </summary>
    public static bool IsSetupCompleted() => s_setupCompleted;

    /// <summary>
    /// Returns the reason setup was completed, or null if not yet completed.
    /// </summary>
    public static string? GetSetupCompletedReason() => s_setupCompletedReason;
}

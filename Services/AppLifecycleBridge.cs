namespace Craft.Services;

/// <summary>
/// Static bridge so downstream PowerShell apps can request a container restart,
/// trigger setup mode, or reconcile EasyAuth policy with the current appsettings.
///
/// PS usage: [Craft.Services.AppLifecycleBridge]::RequestRestart("EasyAuth configuration applied")
///           [Craft.Services.AppLifecycleBridge]::IsEasyAuthConfigured()
///           [Craft.Services.AppLifecycleBridge]::ReconcileAuthPolicy("CIPP warmup")
/// </summary>
public static class AppLifecycleBridge
{
    private static IHostApplicationLifetime? s_lifetime;
    private static ILogger? s_logger;
    private static SetupService? s_setupService;

    public static void Initialize(IHostApplicationLifetime lifetime, ILogger logger, SetupService setupService)
    {
        s_lifetime = lifetime;
        s_logger = logger;
        s_setupService = setupService;
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

    /// <summary>
    /// Pushes the current Setup.UnauthenticatedClientAction and Setup.ExcludedPaths into the
    /// live authsettingsV2 via ARM. Idempotent — returns false if the live config already matches
    /// or if EasyAuth isn't configured / no managed identity is available.
    ///
    /// Intended for the post-setup runtime path: PS warmup calls this after confirming EasyAuth
    /// is already configured, so any subsequent appsettings changes (e.g. growing ExcludedPaths)
    /// are reflected without going through the full setup wizard.
    ///
    /// Errors are swallowed and logged; this is best-effort and must not break warmup.
    ///
    /// PS usage: [Craft.Services.AppLifecycleBridge]::ReconcileAuthPolicy("CIPP startup")
    /// </summary>
    public static bool ReconcileAuthPolicy(string reason = "Reconcile requested by app")
    {
        try
        {
            return ReconcileAuthPolicyAsync(reason).GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            s_logger?.LogError(ex, "[Lifecycle] ReconcileAuthPolicy failed ({Reason})", reason);
            return false;
        }
    }

    /// <summary>
    /// Async version of ReconcileAuthPolicy for callers that can await.
    /// </summary>
    public static Task<bool> ReconcileAuthPolicyAsync(string reason = "Reconcile requested by app")
    {
        var svc = s_setupService;
        if (svc == null)
        {
            s_logger?.LogWarning("[Lifecycle] ReconcileAuthPolicy called before Initialize");
            return Task.FromResult(false);
        }
        return svc.ReconcileAuthPolicy(reason);
    }

    private static CraftSettings? s_settings;

    /// <summary>
    /// Diagnostic — log the currently-bound Setup config (UnauthenticatedClientAction, ExcludedPaths).
    /// Use to verify CraftSettings is being populated correctly from appsettings.
    /// PS usage: [Craft.Services.AppLifecycleBridge]::DumpSetupConfig("warmup")
    /// </summary>
    public static void DumpSetupConfig(string reason = "diagnostic dump")
    {
        if (s_settings == null)
        {
            s_logger?.LogWarning("[Lifecycle] DumpSetupConfig called before Initialize ({Reason})", reason);
            return;
        }
        var s = s_settings.Setup;
        s_logger?.LogInformation(
            "[Lifecycle] DumpSetupConfig — UnauthenticatedClientAction={Action}, ExcludedPaths=[{Paths}] (count={Count}) ({Reason})",
            s.UnauthenticatedClientAction,
            string.Join(",", s.ExcludedPaths),
            s.ExcludedPaths.Count,
            reason);
    }

    internal static void SetSettings(CraftSettings settings) => s_settings = settings;
}

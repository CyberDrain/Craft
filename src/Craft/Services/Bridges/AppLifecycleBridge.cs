using Craft.Setup;

// NAMESPACE PINNED — do not change.
// Downstream PowerShell reaches these types by fully-qualified name, e.g.
//   [Craft.Services.RealtimeBridge]::Publish($userId, $jobId, 'start', $data)
// Renaming the namespace compiles fine and then fails at runtime in the hosted app
// ("Unable to find type"). Type forwarding cannot help — it only works across assemblies.
// The folder is free to move; the namespace is a published contract.
namespace Craft.Services;

/// <summary>
/// Static bridge so downstream PowerShell apps can request a container restart,
/// trigger setup mode, or reconcile EasyAuth policy with the current appsettings.
///
/// PS usage: [Craft.Services.AppLifecycleBridge]::RequestRestart("EasyAuth configuration applied")
///           [Craft.Services.AppLifecycleBridge]::IsEasyAuthConfigured()
///           [Craft.Services.AppLifecycleBridge]::ReconcileAuthPolicy("CIPP warmup")
/// </summary>
/// <remarks>
/// Uninitialized policy: mutating setup/restart APIs throw; best-effort reads
/// (<see cref="IsSetupModeRequested"/>, <see cref="IsSetupCompleted"/>, …) soft no-op;
/// <see cref="ReconcileAuthPolicy"/> stays soft (warmup must not fail).
/// </remarks>
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
    public static bool IsEasyAuthConfigured() => SetupService.IsEasyAuthConfigured();

    /// <summary>
    /// Explicitly enables the Craft setup wizard. Call this from the child app
    /// when it determines that initial SSO setup is needed.
    /// PS usage: [Craft.Services.AppLifecycleBridge]::RequestSetupMode("reason")
    /// </summary>
    public static void RequestSetupMode(string reason = "Setup mode requested by application")
    {
        var svc = s_setupService ?? throw new InvalidOperationException("AppLifecycleBridge not initialized");
        svc.RequestSetupMode(reason);
    }

    /// <summary>
    /// Returns true if the child app has explicitly requested setup mode.
    /// Used by the setup middleware to determine whether to activate the setup wizard.
    /// </summary>
    public static bool IsSetupModeRequested() =>
        s_setupService?.IsSetupModeRequested() ?? false;

    /// <summary>
    /// Marks setup as completed — credentials have been applied and the app is
    /// pending restart. Prevents duplicate credential submissions and lets all
    /// setup page instances detect completion via status polling.
    /// </summary>
    public static void MarkSetupCompleted(string reason = "Setup credentials applied")
    {
        var svc = s_setupService ?? throw new InvalidOperationException("AppLifecycleBridge not initialized");
        svc.MarkSetupCompleted(reason);
    }

    /// <summary>
    /// Returns true if setup credentials have already been applied this session.
    /// </summary>
    public static bool IsSetupCompleted() => s_setupService?.IsSetupCompleted() ?? false;

    /// <summary>
    /// Returns the reason setup was completed, or null if not yet completed.
    /// </summary>
    public static string? GetSetupCompletedReason() => s_setupService?.GetSetupCompletedReason();

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
}

namespace Craft.Setup;

/// <summary>
/// Session-scoped setup-mode flags and the static EasyAuth env check.
/// PowerShell reaches these via <c>AppLifecycleBridge</c>; C# callers use
/// <see cref="SetupService"/> (facade) or this type directly.
/// </summary>
public class SetupSessionState
{
    private readonly ILogger<SetupSessionState> _logger;
    private volatile bool _setupModeRequested;
    private volatile bool _setupCompleted;
    private string? _setupCompletedReason;

    public SetupSessionState(ILogger<SetupSessionState> logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// Check whether EasyAuth is fully configured by inspecting environment variables.
    /// </summary>
    public static bool IsEasyAuthConfigured()
    {
        var authEnabled = Environment.GetEnvironmentVariable("WEBSITE_AUTH_ENABLED");
        return string.Equals(authEnabled, "True", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Explicitly enables the Craft setup wizard. Called from the hosted app (via
    /// <c>AppLifecycleBridge.RequestSetupMode</c>) when it cannot self-configure.
    /// </summary>
    public void RequestSetupMode(string reason = "Setup mode requested by application")
    {
        _setupModeRequested = true;
        _logger.LogWarning("[Lifecycle] Setup mode explicitly enabled: {Reason}", reason);
    }

    /// <summary>True once the hosted app has called <see cref="RequestSetupMode"/>.</summary>
    public bool IsSetupModeRequested() => _setupModeRequested;

    /// <summary>
    /// Marks setup as completed for this process — credentials applied, pending restart.
    /// </summary>
    public void MarkSetupCompleted(string reason = "Setup credentials applied")
    {
        _setupCompleted = true;
        _setupCompletedReason = reason;
        _logger.LogInformation("[Lifecycle] Setup marked as completed: {Reason}", reason);
    }

    /// <summary>True if setup credentials have already been applied this session.</summary>
    public bool IsSetupCompleted() => _setupCompleted;

    /// <summary>Reason passed to <see cref="MarkSetupCompleted"/>, or null if not completed.</summary>
    public string? GetSetupCompletedReason() => _setupCompletedReason;
}

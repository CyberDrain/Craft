using Craft.Configuration;

namespace Craft.Setup;

/// <summary>
/// Facade for first-run bootstrap setup. Delegates to
/// <see cref="SetupSessionState"/>, <see cref="SetupProvisioningService"/>, and
/// <see cref="SetupUserBootstrap"/> while keeping the public method signatures and
/// nested DTOs used by SetupEndpoints, AppLifecycleBridge, and SetupModeMiddleware.
/// </summary>
public class SetupService
{
    private readonly SetupSessionState _session;
    private readonly SetupProvisioningService _provisioning;
    private readonly SetupUserBootstrap _users;
    private readonly CraftSettings _settings;

    public SetupService(
        SetupSessionState session,
        SetupProvisioningService provisioning,
        SetupUserBootstrap users,
        CraftSettings settings)
    {
        _session = session;
        _provisioning = provisioning;
        _users = users;
        _settings = settings;
    }

    /// <summary>
    /// Check whether EasyAuth is fully configured by inspecting environment variables.
    /// </summary>
    public static bool IsEasyAuthConfigured() => SetupSessionState.IsEasyAuthConfigured();

    /// <summary>
    /// Explicitly enables the Craft setup wizard. Called from the hosted app (via
    /// <c>AppLifecycleBridge.RequestSetupMode</c>) when it cannot self-configure.
    /// </summary>
    public void RequestSetupMode(string reason = "Setup mode requested by application") =>
        _session.RequestSetupMode(reason);

    /// <summary>True once the hosted app has called <see cref="RequestSetupMode"/>.</summary>
    public bool IsSetupModeRequested() => _session.IsSetupModeRequested();

    /// <summary>
    /// Marks setup as completed for this process — credentials applied, pending restart.
    /// </summary>
    public void MarkSetupCompleted(string reason = "Setup credentials applied") =>
        _session.MarkSetupCompleted(reason);

    /// <summary>True if setup credentials have already been applied this session.</summary>
    public bool IsSetupCompleted() => _session.IsSetupCompleted();

    /// <summary>Reason passed to <see cref="MarkSetupCompleted"/>, or null if not completed.</summary>
    public string? GetSetupCompletedReason() => _session.GetSetupCompletedReason();

    /// <summary>
    /// Resolves the display name for the EasyAuth app registration.
    /// Uses Setup.AuthAppDisplayName if set, otherwise "Craft-EasyAuth-{App.Name}".
    /// </summary>
    public string ResolveAuthAppDisplayName() => _provisioning.ResolveAuthAppDisplayName();

    /// <summary>
    /// Initiates a device code flow. Returns the user_code and verification_uri
    /// for the user to authenticate at microsoft.com/devicelogin.
    /// </summary>
    public Task<DeviceCodeResponse> StartDeviceCodeFlow(CancellationToken ct = default) =>
        _provisioning.StartDeviceCodeFlow(ct);

    /// <summary>
    /// Polls for device code flow completion. Returns the access token once the
    /// user has authenticated, or null if still pending.
    /// </summary>
    public Task<TokenExchangeResult?> PollDeviceCodeFlow(string deviceCode, CancellationToken ct = default) =>
        _provisioning.PollDeviceCodeFlow(deviceCode, ct);

    /// <summary>
    /// Creates a new EasyAuth app registration with a client secret. Existing
    /// registrations in the tenant are never searched for or reused.
    /// Handles app management policy exemption if the tenant blocks password creation.
    /// </summary>
    public Task<AppRegistrationResult> CreateAuthAppRegistration(
        string accessToken, string tenantId, string redirectUri, bool multiTenant = false, CancellationToken ct = default) =>
        _provisioning.CreateAuthAppRegistration(accessToken, tenantId, redirectUri, multiTenant, ct);

    /// <summary>
    /// Configures the App Service with EasyAuth settings using the managed identity.
    /// Sets environment variables and authsettingsV2 via ARM REST API.
    /// </summary>
    public Task ConfigureAppServiceAuth(
        string appId, string clientSecret, string tenantId, bool multiTenant = false, CancellationToken ct = default) =>
        _provisioning.ConfigureAppServiceAuth(appId, clientSecret, tenantId, multiTenant, ct);

    /// <summary>
    /// Reconciles the live authsettingsV2.globalValidation block with current Setup settings.
    /// Idempotent — returns true if the live config was changed.
    /// </summary>
    public Task<bool> ReconcileAuthPolicy(string reason, CancellationToken ct = default) =>
        _provisioning.ReconcileAuthPolicy(reason, ct);

    /// <summary>
    /// Saves app registration details manually (user-provided App ID, Secret, Tenant ID)
    /// and configures the App Service via ARM.
    /// </summary>
    public Task ConfigureManual(
        string appId, string clientSecret, string tenantId, bool multiTenant = false, CancellationToken ct = default) =>
        _provisioning.ConfigureManual(appId, clientSecret, tenantId, multiTenant, ct);

    /// <summary>
    /// Checks the allowedUsers table status: whether it's reachable and whether
    /// it already contains any users.
    /// </summary>
    public Task<AllowedUsersStatus> CheckAllowedUsersStatus(CancellationToken ct = default) =>
        _users.CheckAllowedUsersStatus(ct);

    /// <summary>
    /// Seeds the first user into the allowedUsers table with the roles from
    /// Setup.FirstUserRoles (defaults to "superadmin" when unset).
    /// </summary>
    public Task SeedFirstUser(string upn, CancellationToken ct = default) =>
        _users.SeedFirstUser(upn, ct);

    /// <summary>
    /// Returns setup status information.
    /// </summary>
    public async Task<SetupStatus> GetStatus(CancellationToken ct = default)
    {
        var isConfigured = IsEasyAuthConfigured();
        var siteName = Environment.GetEnvironmentVariable("WEBSITE_SITE_NAME");
        var hasManagedIdentity = !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("IDENTITY_ENDPOINT"));
        var usersStatus = await CheckAllowedUsersStatus(ct);

        return new SetupStatus
        {
            IsEasyAuthConfigured = isConfigured,
            IsSetupCompleted = IsSetupCompleted(),
            SetupCompletedReason = GetSetupCompletedReason(),
            IsRunningInAppService = !string.IsNullOrEmpty(siteName),
            HasManagedIdentity = hasManagedIdentity,
            AppName = _settings.Name,
            AuthAppDisplayName = ResolveAuthAppDisplayName(),
            BootstrapClientId = _settings.Setup.BootstrapClientId,
            UsersStatus = usersStatus
        };
    }

    // ── Result Models ──

    public class TokenExchangeResult
    {
        public string AccessToken { get; set; } = "";
        public string TenantId { get; set; } = "";
    }

    public class AppRegistrationResult
    {
        public string AppId { get; set; } = "";
        public string AppObjectId { get; set; } = "";
        public string ClientSecret { get; set; } = "";
        public string TenantId { get; set; } = "";
        public string DisplayName { get; set; } = "";
    }

    public class SetupStatus
    {
        public bool IsEasyAuthConfigured { get; set; }
        public bool IsSetupCompleted { get; set; }
        public string? SetupCompletedReason { get; set; }
        public bool IsRunningInAppService { get; set; }
        public bool HasManagedIdentity { get; set; }
        public string AppName { get; set; } = "";
        public string AuthAppDisplayName { get; set; } = "";
        public string BootstrapClientId { get; set; } = "";
        public AllowedUsersStatus UsersStatus { get; set; } = new();
    }

    public class AllowedUsersStatus
    {
        public bool Connected { get; set; }
        public bool HasUsers { get; set; }
        public string? Error { get; set; }
    }

    public class DeviceCodeResponse
    {
        public string DeviceCode { get; set; } = "";
        public string UserCode { get; set; } = "";
        public string VerificationUri { get; set; } = "";
        public int ExpiresIn { get; set; }
        public int Interval { get; set; }
        public string Message { get; set; } = "";
    }
}

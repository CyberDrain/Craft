namespace Craft.Configuration;

/// <summary>
/// Bootstrap setup settings — enables a first-run wizard that creates the EasyAuth
/// app registration and configures App Service authentication automatically.
/// When enabled and EasyAuth is not yet configured, Craft serves a built-in setup UI
/// and blocks all application API endpoints until setup is complete.
/// </summary>
public class SetupSettings
{
    /// <summary>
    /// Enable the built-in bootstrap setup mode.
    /// When true, Craft registers setup routes and middleware.
    /// When false, setup routes are never registered regardless of auth state.
    /// </summary>
    public bool Enabled { get; set; }

    /// <summary>
    /// Public client ID used for the PKCE login popup during automated setup.
    /// Defaults to Microsoft's Azure PowerShell first-party app which supports
    /// auth code + PKCE without a client secret.
    /// </summary>
    public string BootstrapClientId { get; set; } = "1950a258-227b-4e31-a9cf-717495945fc2";

    /// <summary>
    /// Display name for the created EasyAuth app registration.
    /// Uses the App.Name setting to generate: "Craft-EasyAuth-{Name}".
    /// Override this to set a custom name.
    /// </summary>
    public string AuthAppDisplayName { get; set; } = "";

    /// <summary>
    /// Action taken when an unauthenticated request arrives.
    /// Applied to globalValidation.unauthenticatedClientAction in authsettingsV2.
    /// Valid values: RedirectToLoginPage, AllowAnonymous, RejectWith401, RejectWith404.
    /// Default is RedirectToLoginPage (suitable for web UIs); APIs should use RejectWith401.
    /// </summary>
    public string UnauthenticatedClientAction { get; set; } = "RedirectToLoginPage";

    /// <summary>
    /// Paths excluded from EasyAuth authentication (e.g. webhook endpoints).
    /// Applied to globalValidation.excludedPaths in authsettingsV2.
    /// Supports App Service glob patterns (e.g. "/api/Public*").
    /// </summary>
    public List<string> ExcludedPaths { get; set; } = [];

    /// <summary>
    /// Identity provider key used for unauthenticated redirect (when UnauthenticatedClientAction
    /// is RedirectToLoginPage). Applied to globalValidation.redirectToProvider in authsettingsV2.
    /// Default "azureactivedirectory" (displayed as "Microsoft" in the Azure portal).
    /// Override only when configuring a non-AAD provider (e.g. "google", "facebook").
    /// </summary>
    public string RedirectToProvider { get; set; } = "azureactivedirectory";

    /// <summary>
    /// Client application IDs allowed to call the app with access tokens.
    /// Applied to identityProviders.azureActiveDirectory.validation.defaultAuthorizationPolicy.allowedApplications.
    /// When empty, no application-level restriction is applied (any valid token for the audience is accepted).
    /// </summary>
    public List<string> AllowedApplications { get; set; } = [];

    /// <summary>
    /// Additional allowed token audiences beyond the auto-generated "api://{appId}".
    /// Applied to identityProviders.azureActiveDirectory.validation.allowedAudiences.
    /// The app's own "api://{appId}" is always included automatically.
    /// </summary>
    public List<string> AllowedAudiences { get; set; } = [];

    /// <summary>
    /// Tenant IDs allowed to authenticate. Controls both the issuer URL and the
    /// WEBSITE_AUTH_AAD_ALLOWED_TENANTS app setting.
    ///
    /// Behavior:
    ///   - Empty (default): single-tenant — issuer is set to the setup tenant ID.
    ///   - One entry: single-tenant — issuer is set to that tenant ID.
    ///   - Multiple entries: issuer is set to "common" and WEBSITE_AUTH_AAD_ALLOWED_TENANTS
    ///     is set to the comma-separated list (Azure enforces the tid claim check).
    ///
    /// The tenant from the setup flow is always included automatically.
    /// </summary>
    public List<string> AllowedTenants { get; set; } = [];

    /// <summary>
    /// When set, the EasyAuth client secret is stored in Azure Key Vault instead of
    /// directly in the app setting. The app setting AUTH_SECRET is then written as a
    /// Key Vault reference (@Microsoft.KeyVault(SecretUri=...)).
    ///
    /// Value is the Key Vault name (e.g. "my-vault" → https://my-vault.vault.azure.net).
    /// If set to the literal string "auto", the site name (WEBSITE_SITE_NAME) is used
    /// as the vault name.
    ///
    /// The managed identity must have Secret Set permission on the vault.
    /// When empty (default), the secret is stored directly in the app setting.
    /// </summary>
    public string KeyVaultName { get; set; } = "";

    /// <summary>
    /// Key Vault secret names under which the bootstrap persists the created SSO app
    /// registration's details (client secret, client/app ID, and multi-tenant flag) so a
    /// downstream app can read its own credentials from the vault. Only written when
    /// <see cref="KeyVaultName"/> is set (same condition that gates the client secret).
    /// The defaults match the names CIPP expects; override any of them in appsettings/env
    /// (e.g. App:Setup:SsoSecretNames:AppSecret) to store under different names.
    /// </summary>
    public SsoSecretNames SsoSecretNames { get; set; } = new();

    /// <summary>
    /// Role(s) assigned to the bootstrap user seeded by /api/setup/seed-user.
    /// When empty (default), the role "superadmin" is used.
    /// Do NOT set defaults here — .NET config binding appends to list initializers, causing duplicates.
    /// Override in appsettings to match the hosted app's role taxonomy, e.g. ["owner"] or ["admin", "authenticated"].
    /// </summary>
    public List<string> FirstUserRoles { get; set; } = [];
}

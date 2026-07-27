namespace Craft.Configuration;

/// <summary>
/// Authentication settings. The host supports Azure AD OIDC out of the box.
/// </summary>
public class AuthSettings
{
    /// <summary>Session cookie name.</summary>
    public string CookieName { get; set; } = "craft-session";

    /// <summary>
    /// Azure Table name for user authorization (UPN → roles).
    /// Override via Auth__UserTableName env var or in appsettings.
    /// Sanitized at runtime (alphanumeric only, 3-63 chars).
    /// </summary>
    public string UserTableName { get; set; } = "allowedUsers";

    /// <summary>
    /// Storage connection string for the allowedUsers table.
    /// If empty, falls back to AzureWebJobsStorage (same storage as the rest of the app).
    /// Set this to isolate the user table in a separate storage account.
    /// </summary>
    public string UserStorageConnection { get; set; } = "";

    /// <summary>
    /// Roles assigned to the dev-mode auto-login principal.
    /// Default: empty — set via appsettings (e.g. ["superadmin", "authenticated", "anonymous"]).
    /// Do NOT set defaults here — .NET config binding appends to list initializers, causing duplicates.
    /// </summary>
    public List<string> DevRoles { get; set; } = [];

    /// <summary>User ID for the dev-mode auto-login principal.</summary>
    public string DevUserId { get; set; } = "00000000-0000-0000-0000-000000000000";

    /// <summary>User details (UPN/email) for the dev-mode auto-login principal.</summary>
    public string DevUserDetails { get; set; } = "developer@localhost";

    /// <summary>Identity provider reported for the dev-mode auto-login principal (aad, github, …).</summary>
    public string DevIdentityProvider { get; set; } = "aad";

    /// <summary>
    /// PowerShell function name dispatched for /api/me. If empty, the literal "me"
    /// is used as the endpoint name. The PS function (or its MeEndpointHandler wrapper)
    /// owns the response shape — /api/me passes status code and body through unchanged.
    /// </summary>
    public string MeEndpointFunction { get; set; } = "";

    /// <summary>
    /// Optional wrapper PowerShell function invoked for /api/me instead of MeEndpointFunction
    /// directly. When set, this handler is called with the standard Request/TriggerMetadata
    /// parameters and is expected to dispatch internally based on Request.Params.CIPPEndpoint
    /// (which is set to MeEndpointFunction).
    /// When empty (default), MeEndpointFunction is invoked directly.
    /// Example (CIPP): "New-CippCoreRequest" with MeEndpointFunction = "me".
    /// </summary>
    public string MeEndpointHandler { get; set; } = "";

    /// <summary>
    /// When true, any user who authenticates against the configured AAD tenant
    /// is allowed in — even if they are not in the allowedUsers table.
    /// Users not in the table get ["authenticated", "anonymous"] as default roles.
    /// The hosted app (e.g. CIPP) can then do its own role resolution (e.g. via Entra group mapping).
    /// When false, only users explicitly listed in the allowedUsers table can log in.
    /// Override via Auth__AllowAllTenantUsers env var.
    /// </summary>
    public bool AllowAllTenantUsers { get; set; } = true;
}

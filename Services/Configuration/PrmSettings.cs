namespace Craft.Configuration;

/// <summary>
/// OAuth 2.0 Protected Resource Metadata (RFC 9728) served by Craft itself at
/// <c>/.well-known/oauth-protected-resource</c>, so OAuth clients (MCP clients in particular) can
/// discover how to obtain a token for the hosted API.
///
/// <para>
/// The document is NOT assembled from configuration — the hosted application writes the complete
/// JSON into one app setting (<see cref="SettingName"/>) and Craft serves it verbatim, so the
/// application controls every field. A literal <c>{origin}</c> anywhere in the JSON is replaced
/// per-request with <c>https://{host}</c>: RFC 9728 requires <c>resource</c> to equal the URL the
/// client is actually connecting to, which only the request knows once custom domains are in play.
/// </para>
///
/// <para>
/// Why not the platform's own PRM (preview): it derives <c>authorization_servers</c> from the
/// EasyAuth provider's <c>openIdIssuer</c> with no independent override — a multi-tenant SSO setup
/// therefore advertises the <c>/common</c> endpoint, which a single-tenant resource app
/// registration cannot authorize on (AADSTS50194). Craft only serves metadata: token issuance and
/// validation remain entirely with Entra and EasyAuth. The platform's PRM must stay dormant for
/// Craft's to be reachable — do NOT set WEBSITE_AUTH_PRM_DEFAULT_WITH_SCOPES (it activates the
/// platform document, which intercepts the well-known path before the container sees the request).
/// The well-known path is appended to EasyAuth's excludedPaths automatically while this feature is
/// enabled.
/// </para>
/// </summary>
public class PrmSettings
{
    /// <summary>
    /// The well-known path (RFC 9728 §3). Suffixed variants
    /// (<c>/.well-known/oauth-protected-resource/api/Foo</c>) identify a specific resource path.
    /// </summary>
    public const string WellKnownPath = "/.well-known/oauth-protected-resource";

    /// <summary>
    /// Authorization server metadata path (RFC 8414). Served when the app setting named by
    /// <see cref="AuthServerSettingName"/> is present — the hosted app uses this to advertise an
    /// authorization server document of its own (typically mirroring Entra's endpoints verbatim
    /// while adding a <c>registration_endpoint</c>, the one thing Entra does not offer). Craft
    /// serves the document; it never implements any of the endpoints named inside it.
    /// </summary>
    public const string AuthServerWellKnownPath = "/.well-known/oauth-authorization-server";

    /// <summary>
    /// OIDC discovery alias (same document): MCP clients are required to try both this and
    /// <see cref="AuthServerWellKnownPath"/> when resolving an authorization server.
    /// </summary>
    public const string OidcWellKnownPath = "/.well-known/openid-configuration";

    /// <summary>
    /// Master switch. When enabled and the app settings below hold valid JSON, Craft serves them
    /// and the setup reconcile keeps the well-known paths in EasyAuth's excludedPaths. When a
    /// setting is absent its document is not served — presence is the per-instance
    /// "an OAuth resource exists here" signal, written and cleared by the hosted app.
    /// </summary>
    public bool Enabled { get; set; }

    /// <summary>
    /// Name of the app setting (environment variable) holding the complete PRM JSON document.
    /// </summary>
    public string SettingName { get; set; } = "CRAFT_PRM";

    /// <summary>
    /// Name of the app setting (environment variable) holding the complete authorization server
    /// metadata JSON document (RFC 8414). Optional — absent means only the PRM is served.
    /// </summary>
    public string AuthServerSettingName { get; set; } = "CRAFT_PRM_AS";
}

namespace Craft.Configuration;

/// <summary>
/// Script repository settings — controls where the host finds PowerShell scripts.
/// </summary>
public class ScriptRepoSettings
{
    /// <summary>
    /// Module directory names to scan for HTTP endpoint functions.
    /// Functions are mapped to /api/{route} endpoints. If a function starts with
    /// "Invoke-", that prefix is stripped for the route (e.g. Invoke-ListUsers → /api/ListUsers).
    /// Functions without the prefix use their full name as the route.
    /// </summary>
    public List<string> HttpModules { get; set; } = [];

    /// <summary>
    /// Optional global handler function name. When set, ALL /API/{endpoint} routes are
    /// dispatched through this single function instead of invoking the route's function
    /// directly. The endpoint name is passed via Request.Params.CIPPEndpoint so the handler
    /// can dispatch internally. Route lookup still happens for 404 detection, but the
    /// handler runs instead of the looked-up function.
    ///
    /// Use this when the hosted app's design assumes every HTTP endpoint goes through a
    /// common router (e.g. CIPP's New-CippCoreRequest, which performs Test-CIPPAccess
    /// authorization, telemetry, and feature-flag checks before dispatching to Invoke-*).
    ///
    /// Empty (default) = each /API/{endpoint} invokes Invoke-{endpoint} directly.
    /// Example (CIPP): "New-CippCoreRequest"
    /// </summary>
    public string HttpHandler { get; set; } = "";

    /// <summary>
    /// Directory names (relative to API/) to scan for background/timer scripts.
    /// </summary>
    public List<string> BackgroundScriptDirs { get; set; } = [];

    /// <summary>
    /// Permission extraction settings. When enabled, the host scans the configured
    /// modules for .ROLE and .FUNCTIONALITY comment-based help metadata and writes
    /// a JSON permissions map at startup. Disable if your app doesn't use RBAC metadata.
    /// </summary>
    public PermissionExtractionSettings PermissionExtraction { get; set; } = new();
}

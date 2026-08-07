namespace Craft.Configuration;

/// <summary>
/// Controls whether and how the host extracts RBAC metadata from PowerShell functions.
/// When enabled, comment-based help tags (.ROLE, .FUNCTIONALITY) are scanned and
/// written to a JSON file for the auth layer to consume at runtime.
/// </summary>
public class PermissionExtractionSettings
{
    /// <summary>Whether permission extraction is enabled.</summary>
    public bool Enabled { get; set; }

    /// <summary>
    /// Module directory names (under API/Modules/) to scan for permission metadata.
    /// Can be the same as or different from HttpModules. For a monolithic module that
    /// contains both HTTP endpoints and background functions, just list it here.
    /// </summary>
    public List<string> Modules { get; set; } = [];

    /// <summary>
    /// Output file path for the generated permissions map (relative to the API base path).
    /// </summary>
    public string OutputFile { get; set; } = "Config/function-permissions.json";
}

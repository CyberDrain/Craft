namespace Craft.Configuration;

/// <summary>
/// A JSON file to preload into a PowerShell variable at worker startup.
/// </summary>
public class GlobalJsonPreload
{
    /// <summary>Path to the JSON file, relative to the API base path.</summary>
    public string File { get; set; } = "";

    /// <summary>Variable name (without $ prefix) to store the parsed content.</summary>
    public string Variable { get; set; } = "";

    /// <summary>
    /// Scope: "global" sets $global:VarName, "env" sets $env:VarName (raw JSON string).
    /// </summary>
    public string Scope { get; set; } = "global";

    /// <summary>
    /// If true, deserializes as a case-insensitive Hashtable instead of PSObject.
    /// Only applies when Scope is "global".
    /// </summary>
    public bool AsHashtable { get; set; }
}

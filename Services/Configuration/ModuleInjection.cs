namespace Craft.Configuration;

/// <summary>
/// Describes a shared variable to inject into a PowerShell module's scope.
/// The host maintains a process-wide Synchronized Hashtable and injects it
/// into the named module's script scope on each worker.
/// </summary>
public class ModuleInjection
{
    /// <summary>Module name to inject into (e.g. "CIPPCore").</summary>
    public string Module { get; set; } = "";

    /// <summary>Variable name in the module's script scope (e.g. "classictoken").</summary>
    public string Variable { get; set; } = "";

    /// <summary>
    /// Unique key for the shared cache instance. Multiple injections with the same
    /// CacheKey share the same Synchronized Hashtable across all workers.
    /// </summary>
    public string CacheKey { get; set; } = "";
}

namespace Craft.PowerShellHost;

/// <summary>
/// Holds exported state from a fully-initialized worker for cloning into new ISS templates.
/// </summary>
public class ExportedModuleState
{
    public List<(string Name, string Definition, string Module)> Functions { get; } = new();
    public List<(string Name, object? Value)> Variables { get; } = new();
    public List<string> BinaryModulePaths { get; } = new();
    /// <summary>
    /// Module manifest paths for modules that have private (non-exported) functions.
    /// These must be imported natively on cloned workers to preserve module scope.
    /// </summary>
    public HashSet<string> NativeImportModulePaths { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Merge a base state with this (overlay) state. Overlay functions win on name collision.
    /// Returns a new ExportedModuleState containing the union.
    /// </summary>
    public ExportedModuleState MergeWith(ExportedModuleState baseState)
    {
        var merged = new ExportedModuleState();

        // Start with base functions, then overlay (branch wins on collision)
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var fn in Functions)
        {
            merged.Functions.Add(fn);
            seen.Add(fn.Name);
        }
        foreach (var fn in baseState.Functions)
        {
            if (!seen.Contains(fn.Name))
                merged.Functions.Add(fn);
        }

        // Merge variables (overlay wins)
        var seenVars = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var v in Variables)
        {
            merged.Variables.Add(v);
            seenVars.Add(v.Name);
        }
        foreach (var v in baseState.Variables)
        {
            if (!seenVars.Contains(v.Name))
                merged.Variables.Add(v);
        }

        // Merge binary module paths (deduplicate)
        var seenPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in BinaryModulePaths)
        {
            merged.BinaryModulePaths.Add(p);
            seenPaths.Add(p);
        }
        foreach (var p in baseState.BinaryModulePaths)
        {
            if (!seenPaths.Contains(p))
                merged.BinaryModulePaths.Add(p);
        }

        // Merge native import module paths
        merged.NativeImportModulePaths.UnionWith(NativeImportModulePaths);
        merged.NativeImportModulePaths.UnionWith(baseState.NativeImportModulePaths);

        return merged;
    }
}

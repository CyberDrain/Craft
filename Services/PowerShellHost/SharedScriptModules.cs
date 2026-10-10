using System.Collections;
using System.Management.Automation;
using System.Management.Automation.Language;

namespace Craft.PowerShellHost;

/// <summary>
/// The app's script modules, each parsed once per process. Every worker instantiates them from these parses with
/// <c>New-Module -ScriptBlock</c>, so the AST, compiled code and source text exist once however many workers there
/// are (measured on CIPP: ~2 MB and ~60 ms CPU per extra worker, against ~19 MB and ~250 ms for a manifest import,
/// which re-reads, re-hashes and re-evaluates every file per worker). Each worker still gets its own module scope.
/// <para>
/// A module is shared only when its root module file holds all of it. Anything a module file alone cannot carry — a
/// binary root module, nested or required modules, type or format data, ScriptsToProcess, or a root module that
/// dot-sources other files (source layouts; each worker would re-parse them) — is imported through the
/// InitialSessionState as before. Without a manifest every function in a shared module is exported, private ones
/// included, as on the cloned workers this replaces.
/// </para>
/// </summary>
internal sealed class SharedScriptModules
{
    private static readonly string[] s_manifestOnlyKeys =
        ["NestedModules", "RequiredModules", "TypesToProcess", "FormatsToProcess", "ScriptsToProcess"];

    /// <summary>Script modules every worker instantiates after its runspace opens, in import order.</summary>
    public IReadOnlyList<(string Name, ScriptBlock Body)> Modules { get; }

    /// <summary>Module manifests imported through the InitialSessionState instead.</summary>
    public IReadOnlyList<string> NativeImports { get; }

    /// <summary>RequiredAssemblies of the shared modules, registered on every InitialSessionState.</summary>
    public IReadOnlyList<string> Assemblies { get; }

    private SharedScriptModules(List<(string, ScriptBlock)> modules, List<string> nativeImports, List<string> assemblies)
    {
        Modules = modules;
        NativeImports = nativeImports;
        Assemblies = assemblies;
    }

    /// <param name="manifests">Module manifests in import order.</param>
    /// <param name="parsed">Root module path → parse, shared across pools so a module in both lists is parsed once.</param>
    public static SharedScriptModules Load(IEnumerable<string> manifests, Dictionary<string, ScriptBlock> parsed, ILogger logger)
    {
        var modules = new List<(string, ScriptBlock)>();
        var nativeImports = new List<string>();
        var assemblies = new List<string>();
        foreach (var manifest in manifests)
        {
            var name = Path.GetFileNameWithoutExtension(manifest);
            if (TryShare(manifest, parsed, assemblies, out var body, out var reason))
                modules.Add((name, body!));
            else
            {
                logger.LogInformation("[Pool] {Module}: imported per worker ({Reason})", name, reason);
                nativeImports.Add(manifest);
            }
        }
        return new SharedScriptModules(modules, nativeImports, assemblies);
    }

    private static bool TryShare(string manifest, Dictionary<string, ScriptBlock> parsed, List<string> assemblies,
        out ScriptBlock? body, out string reason)
    {
        body = null;
        var dir = Path.GetDirectoryName(manifest)!;
        Hashtable data;
        try
        {
            var ast = Parser.ParseFile(manifest, out _, out var errors);
            if (errors.Length > 0 || ast.Find(a => a is HashtableAst, false) is not HashtableAst table)
            {
                reason = "manifest does not parse";
                return false;
            }
            data = (Hashtable)table.SafeGetValue();
        }
        catch (Exception ex)
        {
            reason = $"manifest is not plain data: {ex.Message}";
            return false;
        }

        if (s_manifestOnlyKeys.FirstOrDefault(k => data[k] is not null && !(data[k] is object[] { Length: 0 })) is { } key)
        {
            reason = $"manifest declares {key}";
            return false;
        }
        if ((data["RootModule"] ?? data["ModuleToProcess"]) is not string root
            || !root.EndsWith(".psm1", StringComparison.OrdinalIgnoreCase))
        {
            reason = "root module is not a .psm1";
            return false;
        }

        var rootPath = Path.GetFullPath(Path.Combine(dir, Normalize(root)));
        if (!parsed.TryGetValue(rootPath, out body))
        {
            var ast = Parser.ParseFile(rootPath, out _, out var errors);
            if (errors.Length > 0)
            {
                reason = $"{root} has parse errors";
                return false;
            }
            if (ast.Find(a => a is CommandAst { InvocationOperator: TokenKind.Dot } && !InsideFunction(a), true) != null)
            {
                reason = $"{root} dot-sources other files";
                return false;
            }
            body = parsed[rootPath] = ast.GetScriptBlock();
        }

        foreach (var assembly in data["RequiredAssemblies"] switch { string s => [s], object[] a => a.OfType<string>(), _ => [] })
            assemblies.Add(Path.GetFullPath(Path.Combine(dir, Normalize(assembly))));
        reason = "";
        return true;
    }

    // Dot-sourcing inside a function runs when it is called, not at import, so it does not stop sharing.
    private static bool InsideFunction(Ast ast)
    {
        for (var parent = ast.Parent; parent != null; parent = parent.Parent)
            if (parent is FunctionDefinitionAst) return true;
        return false;
    }

    // Manifests are written on Windows ('.\CIPPCore.psm1'); PowerShell accepts either separator, Path does not.
    private static string Normalize(string path) => path.Replace('\\', Path.DirectorySeparatorChar).Replace('/', Path.DirectorySeparatorChar);
}

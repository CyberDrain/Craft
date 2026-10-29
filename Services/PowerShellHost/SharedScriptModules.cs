using System.Collections;
using System.Collections.Concurrent;
using System.Management.Automation;
using System.Management.Automation.Language;
using System.Management.Automation.Runspaces;

namespace Craft.PowerShellHost;

/// <summary>
/// The app's script modules, each parsed once per process. Every worker instantiates them from these parses with
/// <c>New-Module -ScriptBlock</c>, so the AST, compiled code and source text exist once however many workers there
/// are (measured on CIPP: ~2 MB and ~60 ms CPU per extra worker, against ~19 MB and ~250 ms for a manifest import,
/// which re-reads, re-hashes and re-evaluates every file per worker). Each worker still gets its own module scope.
/// <para>
/// A module is shared only when its root module file holds all of it. Anything a module file alone cannot carry — a
/// binary root module, nested or required modules, type or format data, ScriptsToProcess, a root module that
/// dot-sources other files (source layouts; each worker would re-parse them), or one that calls Export-ModuleMember
/// (the manifest then filters its exports, which New-Module cannot repeat) — is imported through the
/// InitialSessionState as before. A shared module exports what a manifest import would (FunctionsToExport and
/// AliasesToExport filtering the module's own exports) and reports the manifest's ModuleVersion and the module's
/// directory as ModuleBase.
/// </para>
/// </summary>
internal sealed class SharedScriptModules
{
    private static readonly string[] s_manifestOnlyKeys =
        ["NestedModules", "RequiredModules", "TypesToProcess", "FormatsToProcess", "ScriptsToProcess"];

    /// <summary>Script modules every worker instantiates after its runspace opens, in import order.</summary>
    public IReadOnlyList<SharedModule> Modules { get; }

    /// <summary>Module manifests imported through the InitialSessionState instead.</summary>
    public IReadOnlyList<string> NativeImports { get; }

    /// <summary>RequiredAssemblies of the shared modules, registered on every InitialSessionState.</summary>
    public IReadOnlyList<string> Assemblies { get; }

    private SharedScriptModules(List<SharedModule> modules, List<string> nativeImports, List<string> assemblies)
    {
        Modules = modules;
        NativeImports = nativeImports;
        Assemblies = assemblies;
    }

    /// <param name="manifests">Module manifests in import order. Their root modules are parsed in parallel.</param>
    /// <param name="parsed">Root module path → parse, shared across pools so a module in both lists is parsed once.</param>
    public static SharedScriptModules Load(IEnumerable<string> manifests, ConcurrentDictionary<string, ScriptBlock> parsed, ILogger logger)
    {
        var modules = new List<SharedModule>();
        var nativeImports = new List<string>();
        var assemblies = new List<string>();
        var results = manifests.AsParallel().AsOrdered()
            .Select(m => (Manifest: m, Module: TryShare(m, parsed, out var a, out var r), Assemblies: a, Reason: r))
            .ToList();
        foreach (var (manifest, module, moduleAssemblies, reason) in results)
        {
            if (module != null)
            {
                modules.Add(module);
                assemblies.AddRange(moduleAssemblies);
            }
            else
            {
                logger.LogInformation("[Pool] {Module}: imported per worker ({Reason})", Path.GetFileNameWithoutExtension(manifest), reason);
                nativeImports.Add(manifest);
            }
        }
        return new SharedScriptModules(modules, nativeImports, assemblies);
    }

    private static SharedModule? TryShare(string manifest, ConcurrentDictionary<string, ScriptBlock> parsed,
        out List<string> assemblies, out string reason)
    {
        assemblies = [];
        var dir = Path.GetDirectoryName(manifest)!;
        Hashtable data;
        try
        {
            var ast = Parser.ParseFile(manifest, out _, out var errors);
            if (errors.Length > 0 || ast.Find(a => a is HashtableAst, false) is not HashtableAst table)
            {
                reason = "manifest does not parse";
                return null;
            }
            data = (Hashtable)table.SafeGetValue();
        }
        catch (Exception ex)
        {
            reason = $"manifest is not plain data: {ex.Message}";
            return null;
        }

        if (s_manifestOnlyKeys.FirstOrDefault(k => data[k] is not null && !(data[k] is object[] { Length: 0 })) is { } key)
        {
            reason = $"manifest declares {key}";
            return null;
        }
        if ((data["RootModule"] ?? data["ModuleToProcess"]) is not string root
            || !root.EndsWith(".psm1", StringComparison.OrdinalIgnoreCase))
        {
            reason = "root module is not a .psm1";
            return null;
        }

        var rootPath = Path.GetFullPath(Path.Combine(dir, Normalize(root)));
        if (!parsed.TryGetValue(rootPath, out var body))
        {
            var ast = Parser.ParseFile(rootPath, out _, out var errors);
            if (errors.Length > 0)
            {
                reason = $"{root} has parse errors";
                return null;
            }
            if (ast.Find(a => a is CommandAst { InvocationOperator: TokenKind.Dot } && !InsideFunction(a), true) != null)
            {
                reason = $"{root} dot-sources other files";
                return null;
            }
            if (ast.Find(a => a is CommandAst c && string.Equals(c.GetCommandName(), "Export-ModuleMember", StringComparison.OrdinalIgnoreCase), true) != null)
            {
                reason = $"{root} calls Export-ModuleMember";
                return null;
            }
            body = parsed.GetOrAdd(rootPath, GetScriptBlock(ast));
        }

        assemblies.AddRange(Strings(data["RequiredAssemblies"]).Select(a => Path.GetFullPath(Path.Combine(dir, Normalize(a)))));
        reason = "";
        return new SharedModule(Path.GetFileNameWithoutExtension(manifest), body, dir,
            Version.TryParse(data["ModuleVersion"] as string, out var version) ? version : null,
            data.ContainsKey("FunctionsToExport") ? ExportPatterns(body, Strings(data["FunctionsToExport"])) : null,
            data.ContainsKey("AliasesToExport") ? Strings(data["AliasesToExport"]) : ["*"]);
    }

    // Patterns for New-Module -Function, or null when the manifest names every function the module defines. The filter
    // matches every function against every pattern (~150 ms per worker for CIPPCore's 846 names), so a plain-name
    // list is cut to the shortest prefixes that still leave out each hidden function.
    private static string[]? ExportPatterns(ScriptBlock body, string[] functions)
    {
        var patterns = functions.Select(f => WildcardPattern.Get(f, WildcardOptions.IgnoreCase)).ToList();
        var hidden = body.Ast.FindAll(a => a is FunctionDefinitionAst f && !InsideFunction(f), true)
            .Select(a => ((FunctionDefinitionAst)a).Name)
            .Where(name => !patterns.Any(p => p.IsMatch(name)))
            .ToList();
        if (hidden.Count == 0) return null;
        if (functions.Any(WildcardPattern.ContainsWildcardCharacters)) return functions;
        var result = new List<string>();
        AddPrefixes("", functions, hidden, result);
        return [.. result];
    }

    private static void AddPrefixes(string prefix, IEnumerable<string> names, List<string> hidden, List<string> result)
    {
        if (!hidden.Any(h => h.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)))
        {
            result.Add(WildcardPattern.Escape(prefix) + "*");
            return;
        }
        foreach (var name in names.Where(n => n.Length == prefix.Length))
            result.Add(WildcardPattern.Escape(name));
        foreach (var group in names.Where(n => n.Length > prefix.Length).GroupBy(n => char.ToUpperInvariant(n[prefix.Length])))
            AddPrefixes(prefix + group.First()[prefix.Length], group, hidden, result);
    }

    private static string[] Strings(object? value) => value switch
    {
        string s => [s],
        object[] a => [.. a.OfType<string>()],
        _ => [],
    };

    // A ScriptBlock made outside a runspace has no language mode, and so would a module made from it, which then
    // refuses `& $module { }` ("across language boundaries") — the call Worker:ModuleInjections makes.
    private static ScriptBlock GetScriptBlock(ScriptBlockAst ast)
    {
        using var runspace = RunspaceFactory.CreateRunspace(InitialSessionState.CreateDefault2());
        runspace.Open();
        var previous = Runspace.DefaultRunspace;
        Runspace.DefaultRunspace = runspace;
        try { return ast.GetScriptBlock(); }
        finally { Runspace.DefaultRunspace = previous; }
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

/// <param name="Base">The module's directory: New-Module takes ModuleBase from the current location.</param>
/// <param name="Functions">Patterns matching FunctionsToExport, or null when every function is exported.</param>
/// <param name="Aliases">AliasesToExport, or every alias when the manifest leaves it out, as a .psm1 exports them.</param>
public sealed record SharedModule(string Name, ScriptBlock Body, string Base, Version? Version = null,
    string[]? Functions = null, string[]? Aliases = null);

using System.Collections.Concurrent;
using System.Management.Automation;
using System.Management.Automation.Language;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Craft.Configuration;

namespace Craft.PowerShellHost;

public class ScriptRepository
{
    // Cached — see CA1869: a fresh options instance discards the serializer metadata cache each call.
    private static readonly JsonSerializerOptions s_indented = new() { WriteIndented = true };

    private readonly Dictionary<string, FunctionEntry> _functions = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _httpRoutes = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, object> _permissions = new(StringComparer.OrdinalIgnoreCase);
    private readonly ILogger<ScriptRepository> _logger;
    private readonly CraftSettings _settings;
    private HashSet<string>? _moduleFunctionNames;
    private string? _apiBasePath;

    private static readonly Regex RoleRegex = new(@"\.ROLE\s*\r?\n\s*(.+?)(?:\r?\n|$)", RegexOptions.IgnoreCase);
    private static readonly Regex FuncRegex = new(@"\.FUNCTIONALITY\s*\r?\n\s*(.+?)(?:\r?\n|$)", RegexOptions.IgnoreCase);

    public ScriptRepository(ILogger<ScriptRepository> logger, CraftSettings settings)
    {
        _logger = logger;
        _settings = settings;
    }

    public IReadOnlyDictionary<string, string> HttpRoutes => _httpRoutes;
    public IReadOnlyDictionary<string, FunctionEntry> Functions => _functions;

    public void LoadAll(string apiBasePath)
    {
        _apiBasePath = apiBasePath;
        var runtimeBasePath = Path.Combine(AppContext.BaseDirectory, "Runtime");
        var modulesPath = Path.Combine(apiBasePath, "Modules");

        // Scan configured HTTP modules for route table + permissions
        foreach (var moduleName in _settings.Scripts.HttpModules)
        {
            var modulePath = Path.Combine(modulesPath, moduleName);
            if (Directory.Exists(modulePath)) LoadDirectory(modulePath, FunctionCategory.Http);
        }

        // Always load CraftRuntime if it exists (framework-provided scripts in Runtime/)
        var craftRuntimePath = Path.Combine(runtimeBasePath, "CraftRuntime");
        if (Directory.Exists(craftRuntimePath)) LoadDirectory(craftRuntimePath, FunctionCategory.Background);

        // Load built-in HTTP endpoints from Runtime/HTTP/
        var runtimeHttpPath = Path.Combine(runtimeBasePath, "HTTP");
        if (Directory.Exists(runtimeHttpPath)) LoadDirectory(runtimeHttpPath, FunctionCategory.Http);

        // Background scripts from configured directories
        foreach (var dirName in _settings.Scripts.BackgroundScriptDirs)
        {
            if (dirName.Equals("CraftRuntime", StringComparison.OrdinalIgnoreCase)) continue;
            var dirPath = Path.Combine(apiBasePath, dirName);
            if (Directory.Exists(dirPath)) LoadDirectory(dirPath, FunctionCategory.Background);
        }

        // Scan additional modules for permission metadata only (not loaded as endpoints)
        if (_settings.Scripts.PermissionExtraction.Enabled)
        {
            foreach (var moduleName in _settings.Scripts.PermissionExtraction.Modules)
            {
                // Skip if already scanned as an HTTP module
                if (_settings.Scripts.HttpModules.Contains(moduleName, StringComparer.OrdinalIgnoreCase))
                    continue;
                var modulePath = Path.Combine(modulesPath, moduleName);
                if (Directory.Exists(modulePath)) ScanPermissionsOnly(modulePath);
            }
        }

        // Build route table from HTTP-category functions.
        // Route = function name with "Invoke-" prefix stripped (if present).
        // Functions without the prefix use their full name as the route.
        foreach (var (name, entry) in _functions)
        {
            if (entry.Category != FunctionCategory.Http) continue;
            var route = name.StartsWith("Invoke-", StringComparison.OrdinalIgnoreCase)
                ? name["Invoke-".Length..]
                : name;
            _httpRoutes[route] = name;
        }

        if (_settings.Scripts.PermissionExtraction.Enabled)
            GenerateFunctionPermissions(apiBasePath);

        _logger.LogInformation(
            "[System] Scripts: {Core} core, {Http} HTTP ({Routes} routes), {Bg} background",
            _functions.Count(f => f.Value.Category == FunctionCategory.Core),
            _functions.Count(f => f.Value.Category == FunctionCategory.Http),
            _httpRoutes.Count,
            _functions.Count(f => f.Value.Category == FunctionCategory.Background));
    }

    private void LoadDirectory(string dirPath, FunctionCategory category)
    {
        var ps1Files = Directory.GetFiles(dirPath, "*.ps1", SearchOption.AllDirectories);

        // If no .ps1 files, check for a compiled .psm1 module (ModuleBuilder output)
        if (ps1Files.Length == 0)
        {
            var moduleName = Path.GetFileName(dirPath);
            var psm1 = Path.Combine(dirPath, $"{moduleName}.psm1");
            if (File.Exists(psm1))
            {
                LoadCompiledModule(psm1, category);
                return;
            }

            return;
        }

        // Parse files in parallel (AST parse is CPU-bound and independent per file), then merge
        // into the shared dictionaries on one thread so we keep OrdinalIgnoreCase Dictionary semantics.
        var extractPermissions = _settings.Scripts.PermissionExtraction.Enabled;
        var parsed = new ConcurrentBag<Ps1ParseResult>();

        Parallel.ForEach(ps1Files, ps1 =>
        {
            try
            {
                if (TryParsePs1File(ps1, category, extractPermissions, out var result))
                    parsed.Add(result);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to parse {File}", ps1);
            }
        });

        foreach (var result in parsed)
        {
            foreach (var entry in result.Functions)
                _functions[entry.FunctionName] = entry;

            if (result.PermissionKey is not null && result.PermissionValue is not null)
                _permissions[result.PermissionKey] = result.PermissionValue;
        }
    }

    /// <summary>
    /// Parse one .ps1 into function entries (+ optional permission metadata). Thread-safe: no shared state.
    /// </summary>
    private bool TryParsePs1File(string ps1, FunctionCategory category, bool extractPermissions,
        out Ps1ParseResult result)
    {
        result = default!;
        var originalRaw = File.ReadAllText(ps1);
        // Strip 'using' statements — they must go in ISS/worker init, not in ScriptBlocks
        var raw = StripUsingStatements(originalRaw);

        var ast = Parser.ParseInput(raw, out _, out ParseError[] errors);
        if (errors?.Length > 0)
        {
            _logger.LogWarning("Parse errors in {File}: {Errors}", ps1,
                string.Join("; ", errors.Take(3).Select(e => e.Message)));
        }

        var functions = new List<FunctionEntry>();
        var funcDefs = ast.FindAll(a => a is FunctionDefinitionAst, searchNestedScriptBlocks: false)
            .Cast<FunctionDefinitionAst>();

        var any = false;
        foreach (var funcDef in funcDefs)
        {
            any = true;
            functions.Add(new FunctionEntry
            {
                FunctionName = funcDef.Name,
                ScriptBlock = funcDef.Body.GetScriptBlock(),
                SourcePath = ps1,
                Category = category
            });
        }

        if (!any)
        {
            // Bare script with no function definition (e.g. timer scripts)
            var name = Path.GetFileNameWithoutExtension(ps1);
            functions.Add(new FunctionEntry
            {
                FunctionName = name,
                ScriptBlock = ast.GetScriptBlock(),
                SourcePath = ps1,
                Category = category
            });
        }

        string? permKey = null;
        Dictionary<string, string>? permValue = null;
        if (extractPermissions)
        {
            permKey = Path.GetFileNameWithoutExtension(ps1);
            var roleMatch = RoleRegex.Match(originalRaw);
            var funcMatch = FuncRegex.Match(originalRaw);
            if (roleMatch.Success || funcMatch.Success)
            {
                permValue = new Dictionary<string, string>
                {
                    ["Role"] = roleMatch.Success ? roleMatch.Groups[1].Value.Trim() : "",
                    ["Functionality"] = funcMatch.Success ? funcMatch.Groups[1].Value.Trim() : ""
                };
            }
            else
            {
                permKey = null;
            }
        }

        result = new Ps1ParseResult(functions, permKey, permValue);
        return true;
    }

    private readonly record struct Ps1ParseResult(
        List<FunctionEntry> Functions,
        string? PermissionKey,
        Dictionary<string, string>? PermissionValue);

    /// <summary>
    /// Remove top-of-file <c>using namespace|module|assembly</c> lines. Fast path returns the
    /// original string when no <c>using </c> substring exists (common for most CIPP scripts).
    /// </summary>
    private static string StripUsingStatements(string raw)
    {
        if (raw.IndexOf("using ", StringComparison.OrdinalIgnoreCase) < 0)
            return raw;

        StringBuilder? sb = null;
        var lineStart = 0;
        for (var i = 0; i <= raw.Length; i++)
        {
            if (i < raw.Length && raw[i] != '\n')
                continue;

            var lineLen = i - lineStart;
            var line = raw.AsSpan(lineStart, lineLen);
            if (line.Length > 0 && line[^1] == '\r')
                line = line[..^1];

            if (IsUsingDirectiveLine(line))
            {
                if (sb is null)
                {
                    sb = new StringBuilder(raw.Length);
                    sb.Append(raw, 0, lineStart);
                }
            }
            else if (sb is not null)
            {
                // Keep original line text including its terminating \n (except past EOF).
                var take = i < raw.Length ? lineLen + 1 : lineLen;
                sb.Append(raw, lineStart, take);
            }

            lineStart = i + 1;
        }

        return sb?.ToString() ?? raw;
    }

    private static bool IsUsingDirectiveLine(ReadOnlySpan<char> line)
    {
        var trimmed = line.TrimStart();
        return trimmed.StartsWith("using namespace ", StringComparison.OrdinalIgnoreCase)
            || trimmed.StartsWith("using module ", StringComparison.OrdinalIgnoreCase)
            || trimmed.StartsWith("using assembly ", StringComparison.OrdinalIgnoreCase);
    }

    // Regex to find top-level function definitions in compiled .psm1
    private static readonly Regex FunctionBlockRegex = new(
        @"^function\s+([\w-]+)\s*\{",
        RegexOptions.IgnoreCase | RegexOptions.Multiline);

    /// <summary>
    /// Fast discovery of functions from a compiled .psm1 module.
    /// Uses regex instead of PS AST parser (which is too slow for 40K+ line files).
    /// Functions are already loaded via ISS — we just need route mapping and permissions.
    /// </summary>
    private void LoadCompiledModule(string psm1Path, FunctionCategory category)
    {
        try
        {
            var raw = File.ReadAllText(psm1Path);
            var moduleName = Path.GetFileNameWithoutExtension(psm1Path);

            var funcMatches = FunctionBlockRegex.Matches(raw);
            for (int i = 0; i < funcMatches.Count; i++)
            {
                var funcName = funcMatches[i].Groups[1].Value;

                _functions[funcName] = new FunctionEntry
                {
                    FunctionName = funcName,
                    ScriptBlock = ScriptBlock.Create(""),
                    SourcePath = psm1Path,
                    Category = category
                };

                // Extract .ROLE / .FUNCTIONALITY for permissions (if configured)
                if (_settings.Scripts.PermissionExtraction.Enabled)
                {
                    var startPos = funcMatches[i].Index;
                    var endPos = (i + 1 < funcMatches.Count) ? funcMatches[i + 1].Index : raw.Length;
                    var chunkLen = Math.Min(2000, endPos - startPos);
                    var funcHeader = raw.Substring(startPos, chunkLen);

                    var roleMatch = RoleRegex.Match(funcHeader);
                    var funcMatch = FuncRegex.Match(funcHeader);
                    if (roleMatch.Success || funcMatch.Success)
                    {
                        _permissions[funcName] = new Dictionary<string, string>
                        {
                            ["Role"] = roleMatch.Success ? roleMatch.Groups[1].Value.Trim() : "",
                            ["Functionality"] = funcMatch.Success ? funcMatch.Groups[1].Value.Trim() : ""
                        };
                    }
                }
            }

            _logger.LogInformation("[System] Compiled module {Module}: {Count} functions discovered",
                moduleName, funcMatches.Count);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to scan compiled module {File}", psm1Path);
        }
    }

    private void GenerateFunctionPermissions(string apiBasePath)
    {
        var outputFile = _settings.Scripts.PermissionExtraction.OutputFile;
        var jsonPath = Path.Combine(apiBasePath, outputFile);
        var dataDir = Path.GetDirectoryName(jsonPath)!;
        Directory.CreateDirectory(dataDir);
        File.WriteAllText(jsonPath, JsonSerializer.Serialize(_permissions,
            s_indented));
        _logger.LogInformation("[System] Function permissions: {Count} entries -> {Path}", _permissions.Count, jsonPath);
    }

    /// <summary>
    /// Scans a module directory for permission metadata only (does not load functions).
    /// Used for modules listed in PermissionExtraction.Modules that aren't HTTP modules.
    /// </summary>
    private void ScanPermissionsOnly(string dirPath)
    {
        // Check for compiled .psm1 first
        var moduleName = Path.GetFileName(dirPath);
        var psm1 = Path.Combine(dirPath, $"{moduleName}.psm1");
        if (File.Exists(psm1))
        {
            try
            {
                var raw = File.ReadAllText(psm1);
                var funcMatches = FunctionBlockRegex.Matches(raw);
                for (int i = 0; i < funcMatches.Count; i++)
                {
                    var funcName = funcMatches[i].Groups[1].Value;
                    var startPos = funcMatches[i].Index;
                    var endPos = (i + 1 < funcMatches.Count) ? funcMatches[i + 1].Index : raw.Length;
                    var chunkLen = Math.Min(2000, endPos - startPos);
                    var funcHeader = raw.Substring(startPos, chunkLen);

                    var roleMatch = RoleRegex.Match(funcHeader);
                    var funcMatch = FuncRegex.Match(funcHeader);
                    if (roleMatch.Success || funcMatch.Success)
                    {
                        _permissions[funcName] = new Dictionary<string, string>
                        {
                            ["Role"] = roleMatch.Success ? roleMatch.Groups[1].Value.Trim() : "",
                            ["Functionality"] = funcMatch.Success ? funcMatch.Groups[1].Value.Trim() : ""
                        };
                    }
                }
                _logger.LogInformation("[System] Permission scan of compiled module {Module}: {Count} functions",
                    moduleName, funcMatches.Count);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to scan permissions in compiled module {File}", psm1);
            }
            return;
        }

        // Fall back to individual .ps1 files — parallelize like LoadDirectory.
        var ps1Files = Directory.GetFiles(dirPath, "*.ps1", SearchOption.AllDirectories);
        var found = new ConcurrentBag<(string Key, Dictionary<string, string> Value)>();

        Parallel.ForEach(ps1Files, ps1 =>
        {
            try
            {
                var raw = File.ReadAllText(ps1);
                var funcName = Path.GetFileNameWithoutExtension(ps1);
                var roleMatch = RoleRegex.Match(raw);
                var funcMatch = FuncRegex.Match(raw);
                if (roleMatch.Success || funcMatch.Success)
                {
                    found.Add((funcName, new Dictionary<string, string>
                    {
                        ["Role"] = roleMatch.Success ? roleMatch.Groups[1].Value.Trim() : "",
                        ["Functionality"] = funcMatch.Success ? funcMatch.Groups[1].Value.Trim() : ""
                    }));
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to scan permissions in {File}", ps1);
            }
        });

        foreach (var (key, value) in found)
            _permissions[key] = value;
    }

    public FunctionEntry? GetByRoute(string route)
    {
        if (_httpRoutes.TryGetValue(route, out var funcName) &&
            _functions.TryGetValue(funcName, out var entry))
            return entry;
        return null;
    }

    public FunctionEntry? GetByName(string name)
    {
        if (_functions.TryGetValue(name, out var entry)) return entry;
        if (_functions.TryGetValue($"Invoke-{name}", out entry)) return entry;
        return null;
    }

    /// <summary>
    /// Check if a command name exists as a function in any loaded PowerShell module
    /// (CIPPCore, CIPPStandards, etc.) even if not indexed in _functions.
    /// Uses a lazy-loaded set of function names extracted from .psm1 files.
    /// </summary>
    public bool IsModuleFunction(string name)
    {
        if (_moduleFunctionNames != null)
            return _moduleFunctionNames.Contains(name);

        // Build the set lazily from all .psm1 files in Modules/
        _moduleFunctionNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (_apiBasePath == null) return false;

        var modulesPath = Path.Combine(_apiBasePath, "Modules");
        if (!Directory.Exists(modulesPath)) return false;

        foreach (var psm1 in Directory.GetFiles(modulesPath, "*.psm1", SearchOption.AllDirectories))
        {
            try
            {
                var raw = File.ReadAllText(psm1);
                foreach (Match m in FunctionBlockRegex.Matches(raw))
                    _moduleFunctionNames.Add(m.Groups[1].Value);
            }
            catch { /* skip unreadable modules */ }
        }

        _logger.LogInformation("[System] Module function index: {Count} functions across all modules",
            _moduleFunctionNames.Count);
        return _moduleFunctionNames.Contains(name);
    }
}

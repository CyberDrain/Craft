using System.Collections;
using System.Collections.ObjectModel;
using System.Management.Automation;
using System.Management.Automation.Runspaces;
using System.Reflection;

namespace Craft.Services;

public class PowerShellWorker : IDisposable
{
    private static readonly BindingFlags NonPublicInstance = BindingFlags.NonPublic | BindingFlags.Instance;
    private static readonly MethodInfo? s_getJobsMethod = typeof(JobManager).GetMethod(
        "GetJobs", NonPublicInstance,
        null,
        new[] { typeof(Cmdlet), typeof(bool), typeof(bool), typeof(string[]) },
        null);
    private static readonly object[] s_getJobsArgs = { null!, false, false, null! };
    private static HashSet<string>? s_builtinGlobalVars;

    private readonly PowerShell _pwsh;
    private readonly ILogger _logger;
    private bool _initialized;

    public int Id { get; }

    /// <summary>Stopwatch timestamp set at checkout for elapsed-time tracking.</summary>
    internal long CheckoutTimestamp;

    public PowerShellWorker(int id, InitialSessionState iss, ILogger logger)
    {
        Id = id;
        _logger = logger;
        _pwsh = PowerShell.Create(iss);
        _pwsh.Runspace.Name = $"Worker{id}";
    }

    public void Initialize(ScriptRepository repo, string apiBasePath, CraftSettings settings)
    {
        if (_initialized) return;

        // Capture built-in globals for cleanup (once, thread-safe)
        if (s_builtinGlobalVars == null)
        {
            var vars = GetGlobalVariables();
            var set = new HashSet<string>(vars.Count + 3, StringComparer.OrdinalIgnoreCase)
                { "PSScriptRoot", "PSCommandPath", "MyInvocation" };
            foreach (var v in vars) set.Add(v.Name);
            Interlocked.CompareExchange(ref s_builtinGlobalVars, set, null);
        }

        // Warm up (Azure Functions pattern: create+remove dummy function)
        RunScript("New-Item -Path Function:\\ -Name '_warmup_' -Value {} -Force | Out-Null; Remove-Item Function:\\_warmup_ -Force");

        // Common using namespaces needed by HTTP scripts
        RunScript("using namespace System.Net");

        // HttpResponseContext stub
        RunScript(@"
class HttpResponseContext {
    [int]$StatusCode = 200
    [object]$Body = $null
    [hashtable]$Headers = @{}
    [string]$ContentType = 'application/json'
}");

        // CRAFT_ROOT is always set — scripts use $env:CRAFT_ROOT to find the API root
        var resolvedApiBase = apiBasePath.Replace("\\", "/");
        RunScript($"$env:CRAFT_ROOT = '{resolvedApiBase}'");

        // Expose the resolved scheduler config path so PS scripts can load it directly
        var schedulerConfigPath = Path.Combine(apiBasePath, settings.Scheduler.ConfigFile);
        if (File.Exists(schedulerConfigPath))
            RunScript($"$env:CRAFT_SCHEDULER_CONFIG = '{schedulerConfigPath.Replace("\\", "/")}'");

        // Set app-specific root path aliases (e.g. $env:CIPPRootPath)
        foreach (var varName in settings.Worker.RootPathVars)
            RunScript($"$env:{varName} = '{resolvedApiBase}'");

        // Set additional environment variables from config
        foreach (var (key, value) in settings.Worker.EnvVars)
        {
            var resolvedValue = value.Replace("{ApiBasePath}", resolvedApiBase);
            RunScript($"$env:{key} = '{resolvedValue}'");
        }

        // Inject shared caches into module scopes from config.
        // On cloned workers (no modules loaded), falls back to global scope.
        foreach (var injection in settings.Worker.ModuleInjections)
        {
            if (string.IsNullOrEmpty(injection.Module) || string.IsNullOrEmpty(injection.Variable))
                continue;
            var cacheKey = string.IsNullOrEmpty(injection.CacheKey) ? injection.Variable : injection.CacheKey;
            RunScript($@"
$__cache = [Craft.Services.PowerShellRunnerService]::GetSharedCache('{cacheKey}')
$__mod = Get-Module '{injection.Module}' -ErrorAction SilentlyContinue
if ($__mod) {{
    & $__mod {{ $script:{injection.Variable} = $args[0] }} $__cache
}} else {{
    $global:{injection.Variable} = $__cache
}}
Remove-Variable __cache, __mod -ErrorAction SilentlyContinue
");
        }

        // Load shared assemblies from config
        foreach (var asmRelPath in settings.Worker.SharedAssemblies)
        {
            var asmPath = Path.Combine(apiBasePath, asmRelPath).Replace("\\", "/");
            RunScript($@"
if (Test-Path '{asmPath}') {{
    if (-not ([System.AppDomain]::CurrentDomain.GetAssemblies().Location -contains '{asmPath}')) {{
        [void][Reflection.Assembly]::LoadFile('{asmPath}')
    }}
}}");
        }

        // Deploy background scripts as Function:\ items.
        // Module functions are available via auto-import.
        var deployed = 0;
        foreach (var entry in repo.Functions.Values)
        {
            if (entry.Category != FunctionCategory.Background) continue;
            try
            {
                _pwsh.AddCommand("New-Item")
                     .AddParameter("Path", @"Function:\")
                     .AddParameter("Name", entry.FunctionName)
                     .AddParameter("Value", entry.ScriptBlock)
                     .AddParameter("Options", "Constant");
                _pwsh.Invoke();
                deployed++;
            }
            catch { /* function name collision or other issue — skip */ }
            finally { _pwsh.Commands.Clear(); _pwsh.Streams.ClearStreams(); }
        }

        // Pre-load JSON files into PowerShell variables from config
        foreach (var preload in settings.Worker.JsonPreloads)
        {
            if (string.IsNullOrEmpty(preload.File) || string.IsNullOrEmpty(preload.Variable))
                continue;
            var filePath = Path.Combine(apiBasePath, preload.File).Replace("\\", "/");
            switch (preload.Scope.ToLowerInvariant())
            {
                case "env":
                    RunScript($@"
$_fp = '{filePath}'
if (Test-Path $_fp) {{ $env:{preload.Variable} = [System.IO.File]::ReadAllText($_fp) }}");
                    break;
                case "global" when preload.AsHashtable:
                    RunScript($@"
$_fp = '{filePath}'
if (Test-Path $_fp) {{
    $global:{preload.Variable} = [System.Collections.Hashtable]::new([StringComparer]::OrdinalIgnoreCase)
    (Get-Content $_fp -Raw | ConvertFrom-Json -AsHashtable).GetEnumerator() | ForEach-Object {{ $global:{preload.Variable}[$_.Key] = $_.Value }}
}}");
                    break;
                default: // global, non-hashtable
                    RunScript($@"
$_fp = '{filePath}'
if (Test-Path $_fp) {{ $global:{preload.Variable} = Get-Content $_fp -Raw | ConvertFrom-Json }}");
                    break;
            }
        }

        // Run post-init scripts from config
        foreach (var script in settings.Worker.PostInitScripts)
        {
            try { RunScript(script); }
            catch (Exception ex) { _logger.LogWarning("Post-init script failed: {Error}", ex.Message); }
        }

        _initialized = true;
        _logger.LogInformation("Worker{Id}: {Count} functions deployed", Id, deployed);
    }

    /// <summary>
    /// Async invoke — does not block a ThreadPool thread during PS execution.
    /// Includes post-invocation cleanup matching Azure Functions' ResetRunspace.
    /// When a cancellation token is provided and fires, the PowerShell pipeline is
    /// stopped via <see cref="PowerShell.Stop"/> and an <see cref="OperationCanceledException"/> is thrown.
    /// </summary>
    public async Task<Collection<PSObject>> InvokeAsync(string functionName, Dictionary<string, object?> parameters,
        CancellationToken ct = default)
    {
        CancellationTokenRegistration? registration = null;
        try
        {
            _pwsh.AddCommand(functionName);
            foreach (var p in parameters)
                _pwsh.AddParameter(p.Key, p.Value);

            // Register cancellation callback to stop the PS pipeline
            if (ct.CanBeCanceled)
                registration = ct.Register(() => _pwsh.Stop());

            var asyncResult = _pwsh.BeginInvoke();
            var results = await Task.Factory.FromAsync(asyncResult, _pwsh.EndInvoke);

            ct.ThrowIfCancellationRequested();
            return new Collection<PSObject>(results?.ToList() ?? new List<PSObject>());
        }
        finally
        {
            registration?.Dispose();
            Cleanup();
        }
    }

    /// <summary>
    /// Invoke a bare script (no function definition) — for timer scripts etc.
    /// </summary>
    public async Task<Collection<PSObject>> InvokeScriptAsync(ScriptBlock scriptBlock,
        Dictionary<string, object?>? parameters = null, CancellationToken ct = default)
    {
        CancellationTokenRegistration? registration = null;
        try
        {
            _pwsh.AddScript("& $args[0]").AddArgument(scriptBlock);
            if (parameters != null)
                foreach (var p in parameters)
                    _pwsh.AddParameter(p.Key, p.Value);

            if (ct.CanBeCanceled)
                registration = ct.Register(() => _pwsh.Stop());

            var asyncResult = _pwsh.BeginInvoke();
            var results = await Task.Factory.FromAsync(asyncResult, _pwsh.EndInvoke);

            ct.ThrowIfCancellationRequested();
            return new Collection<PSObject>(results?.ToList() ?? new List<PSObject>());
        }
        finally
        {
            registration?.Dispose();
            Cleanup();
        }
    }

    public PSDataStreams Streams => _pwsh.Streams;

    private void Cleanup()
    {
        _pwsh.Commands.Clear();
        _pwsh.Streams.ClearStreams();
        CleanupGlobalVariables();
        CleanupJobs();
    }

    private void CleanupGlobalVariables()
    {
        if (s_builtinGlobalVars == null) return;
        try
        {
            var currentVars = GetGlobalVariables();
            List<string>? toRemove = null;
            foreach (var v in currentVars)
            {
                if (s_builtinGlobalVars.Contains(v.Name)) continue;
                if (v.Options.HasFlag(ScopedItemOptions.Constant)) continue;
                if (v.Module != null) continue;
                if (v.GetType() != typeof(PSVariable)) continue;
                toRemove ??= new();
                toRemove.Add($@"Variable:\{v.Name}");
            }
            if (toRemove != null)
                _pwsh.Runspace.SessionStateProxy.InvokeProvider.Item.Remove(
                    toRemove.ToArray(), recurse: true, force: true, literalPath: true);
        }
        catch { }
    }

    private void CleanupJobs()
    {
        try
        {
            if (s_getJobsMethod == null) return;
            var jobs = (List<Job2>?)s_getJobsMethod.Invoke(_pwsh.Runspace.JobManager, s_getJobsArgs);
            if (jobs?.Count > 0)
            {
                _pwsh.AddCommand("Remove-Job").AddParameter("Force", true).AddParameter("ErrorAction", "SilentlyContinue");
                _pwsh.Invoke(jobs);
                _pwsh.Commands.Clear();
                _pwsh.Streams.ClearStreams();
            }
        }
        catch { }
    }

    private ICollection<PSVariable> GetGlobalVariables()
    {
        var item = _pwsh.Runspace.SessionStateProxy.InvokeProvider.Item.Get(@"Variable:\")[0];
        return (ICollection<PSVariable>)item.BaseObject;
    }

    private void RunScript(string script)
    {
        _pwsh.AddScript(script).Invoke();
        _pwsh.Commands.Clear();
        _pwsh.Streams.ClearStreams();
    }

    /// <summary>
    /// Pre-warm process-level state using configured warmup scripts.
    /// Run once on the first worker — benefits all workers via process-level env vars and shared state.
    /// </summary>
    public void Warmup(CraftSettings settings)
    {
        if (settings.Worker.WarmupScripts.Count == 0) return;

        var combinedScript = string.Join("\n", settings.Worker.WarmupScripts);
        RunScript($@"
try {{
    {combinedScript}
}} catch {{
    # Non-fatal — first request will pay the cold start instead
    Write-Warning ""Warmup failed: $_""
}}
");
    }

    /// <summary>
    /// Export all functions, variables, and aliases from this worker's loaded modules
    /// so they can be injected into a cloned ISS for faster worker init.
    /// Must be called after Initialize() completes.
    /// </summary>
    public ExportedModuleState ExportModuleState()
    {
        var state = new ExportedModuleState();

        // Get all loaded modules and extract their exported functions,
        // plus detect modules that have private (non-exported) functions
        _pwsh.AddScript(@"
            Get-Module | ForEach-Object {
                $mod = $_
                $hasPrivate = $false
                # Compare all commands in the module against exported functions
                $allCommands = & $mod { Get-Command -Module $mod.Name -CommandType Function -ErrorAction SilentlyContinue }
                $exportedNames = [System.Collections.Generic.HashSet[string]]::new(
                    [StringComparer]::OrdinalIgnoreCase)
                $mod.ExportedFunctions.Values | ForEach-Object { $null = $exportedNames.Add($_.Name) }
                foreach ($cmd in $allCommands) {
                    if (-not $exportedNames.Contains($cmd.Name)) {
                        $hasPrivate = $true
                        break
                    }
                }
                $mod.ExportedFunctions.Values | ForEach-Object {
                    [PSCustomObject]@{
                        Module = $mod.Name
                        Name = $_.Name
                        Definition = $_.Definition
                        HasPrivateFunctions = $hasPrivate
                        ModulePath = $mod.Path
                    }
                }
            }
        ");
        var functions = _pwsh.Invoke();
        _pwsh.Commands.Clear();
        _pwsh.Streams.ClearStreams();

        var modulesWithPrivate = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var fn in functions)
        {
            var name = fn.Properties["Name"]?.Value?.ToString();
            var definition = fn.Properties["Definition"]?.Value?.ToString();
            var module = fn.Properties["Module"]?.Value?.ToString();
            if (!string.IsNullOrEmpty(name) && !string.IsNullOrEmpty(definition))
                state.Functions.Add((name, definition, module ?? ""));

            // Track modules that have private functions — these need native import on cloned workers
            if (fn.Properties["HasPrivateFunctions"]?.Value is true)
            {
                var modPath = fn.Properties["ModulePath"]?.Value?.ToString();
                if (!string.IsNullOrEmpty(modPath) && !string.IsNullOrEmpty(module))
                    modulesWithPrivate.Add(modPath);
            }
        }
        state.NativeImportModulePaths.UnionWith(modulesWithPrivate);

        // Get module-level variables that need to be preserved
        _pwsh.AddScript(@"
            Get-Module | ForEach-Object {
                $mod = $_
                $mod.ExportedVariables.Values | ForEach-Object {
                    [PSCustomObject]@{
                        Module = $mod.Name
                        Name = $_.Name
                        Value = $_.Value
                    }
                }
            }
        ");
        var variables = _pwsh.Invoke();
        _pwsh.Commands.Clear();
        _pwsh.Streams.ClearStreams();

        foreach (var v in variables)
        {
            var name = v.Properties["Name"]?.Value?.ToString();
            var value = v.Properties["Value"]?.Value;
            if (!string.IsNullOrEmpty(name))
                state.Variables.Add((name, value));
        }

        // Get loaded module paths for modules that need native import
        // (binary modules with cmdlets can't be cloned via function entries)
        _pwsh.AddScript(@"
            Get-Module | Where-Object { $_.ModuleType -eq 'Binary' } | ForEach-Object {
                $_.Path
            }
        ");
        var binaryModules = _pwsh.Invoke();
        _pwsh.Commands.Clear();
        _pwsh.Streams.ClearStreams();

        foreach (var bm in binaryModules)
        {
            var path = bm.BaseObject?.ToString();
            if (!string.IsNullOrEmpty(path))
                state.BinaryModulePaths.Add(path);
        }

        return state;
    }

    public void Dispose() => _pwsh.Dispose();
}

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

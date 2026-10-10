using System.Collections.ObjectModel;
using System.Management.Automation;
using System.Management.Automation.Runspaces;
using System.Reflection;
using Craft.Configuration;
using Craft.Hosting;

namespace Craft.PowerShellHost;

public class PowerShellWorker : IDisposable
{
    private static readonly BindingFlags NonPublicInstance = BindingFlags.NonPublic | BindingFlags.Instance;
    // Fully qualified: System.Management.Automation.JobManager is the PowerShell SDK's job table,
    // NOT Craft.Orchestration.JobManager. Both are in scope here.
    private static readonly MethodInfo? s_getJobsMethod = typeof(System.Management.Automation.JobManager).GetMethod(
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

    /// <summary>Number of completed invocations on this worker (incremented at reclaim).</summary>
    internal int InvocationCount;

    public PowerShellWorker(int id, InitialSessionState iss, ILogger logger)
    {
        Id = id;
        _logger = logger;
        _pwsh = PowerShell.Create(iss);
        _pwsh.Runspace.Name = $"Worker{id}";
    }

    /// <summary>This worker's runspace. Test-only access — production code goes through _pwsh.</summary>
    internal Runspace Runspace => _pwsh.Runspace;

    public void Initialize(ScriptRepository repo, string apiBasePath, CraftSettings settings,
        IReadOnlyList<(string Name, ScriptBlock Body)>? sharedModules = null)
    {
        if (_initialized) return;

        // Reuse one pipeline thread across invocations instead of spinning a new thread per BeginInvoke
        // (measured ~50% of the PS-invoke cost). Must be set before the runspace opens — the first invocation
        // or SessionStateProxy access below opens it. On by default; see docs/dispatch-analysis.md.
        if (settings.Worker.ReuseRunspaceThread)
            _pwsh.Runspace.ThreadOptions = PSThreadOptions.ReuseThread;

        // Before the built-in globals are captured, so the runspace looks as it would had the ISS imported them.
        foreach (var (name, body) in sharedModules ?? [])
            ImportSharedModule(name, body);

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

        // HttpResponseContext — derive the runspace class from the Craft.dll-compiled base type
        // (Microsoft.Azure.Functions.PowerShellWorker.HttpResponseContext). PowerShell classes are
        // compiled by the PS engine (no Roslyn), so this resolves [HttpResponseContext] by short
        // name in the runtime container image, where Add-Type -TypeDefinition does NOT (no C#
        // compiler). The base type's namespace-qualified name lands in $_.PSObject.TypeNames, so
        // CIPP's New-CippCoreRequest response filter matches it exactly as under Azure Functions.
        RunScript("class HttpResponseContext : Microsoft.Azure.Functions.PowerShellWorker.HttpResponseContext {}");
        //
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

        // Load shared assemblies from config.
        // ISS-level registration happens in PowerShellWorkerPool.RegisterSharedAssemblies(); this
        // runtime LoadFile is a defence-in-depth fallback that surfaces any load failure to the log
        // (RunScript silently swallows streams, so we run a labelled invocation here instead).
        foreach (var asmRelPath in settings.Worker.SharedAssemblies)
        {
            if (string.IsNullOrWhiteSpace(asmRelPath)) continue;
            var asmPath = Path.Combine(apiBasePath, asmRelPath).Replace("\\", "/");
            var asmLabel = Path.GetFileNameWithoutExtension(asmPath);
            try
            {
                _pwsh.AddScript($@"
if (Test-Path -LiteralPath '{asmPath}') {{
    if (-not ([System.AppDomain]::CurrentDomain.GetAssemblies() | Where-Object {{ $_.Location -ieq '{asmPath}' }})) {{
        [void][Reflection.Assembly]::LoadFile('{asmPath}')
    }}
    [System.AppDomain]::CurrentDomain.GetAssemblies() | Where-Object {{ $_.Location -ieq '{asmPath}' }} | Select-Object -First 1 -ExpandProperty FullName
}} else {{
    Write-Error ""SharedAssembly not found: {asmPath}""
}}").Invoke();

                foreach (var err in _pwsh.Streams.Error)
                    _logger.LogError("Worker{Id}: SharedAssembly '{Label}' load error: {Error}", Id, asmLabel, err.ToString());
                foreach (var warn in _pwsh.Streams.Warning)
                    _logger.LogWarning("Worker{Id}: SharedAssembly '{Label}' warning: {Message}", Id, asmLabel, warn.Message);

                _logger.LogDebug("Worker{Id}: SharedAssembly '{Label}' available at {Path}", Id, asmLabel, asmPath);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Worker{Id}: SharedAssembly '{Label}' load threw", Id, asmLabel);
            }
            finally { _pwsh.Commands.Clear(); _pwsh.Streams.ClearStreams(); }
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

        // Capture the clean ExecutionContext baseline once (shared across the pool) so Cleanup can reset
        // per-invocation AsyncLocal state on the reused pipeline thread. Runs on the pipeline thread via
        // RunScript. Never fail worker init over this.
        try
        {
            RunScript("[Craft.PowerShellHost.PipelineExecutionContext]::CaptureBaselineIfNeeded()");
            if (PipelineExecutionContext.BaselineIsDefault)
                _logger.LogWarning("Worker{Id}: ExecutionContext baseline captured as the runtime default; per-invocation reset will no-op.", Id);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Worker{Id}: failed to capture ExecutionContext baseline; per-invocation reset will be skipped.", Id);
        }

        _initialized = true;
        _logger.LogInformation("Worker{Id}: {Count} functions deployed", Id, deployed);
    }

    /// <summary>Instantiate a module from a parse shared by every worker (see <see cref="SharedScriptModules"/>).</summary>
    private void ImportSharedModule(string name, ScriptBlock body)
    {
        try
        {
            _pwsh.AddCommand("New-Module").AddParameter("Name", name).AddParameter("ScriptBlock", body)
                 .AddCommand("Import-Module");
            _pwsh.Invoke();
            foreach (var err in _pwsh.Streams.Error)
                _logger.LogError("Worker{Id}: module {Module} import error: {Error}", Id, name, err.ToString());
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Worker{Id}: module {Module} failed to import", Id, name);
        }
        finally { _pwsh.Commands.Clear(); _pwsh.Streams.ClearStreams(); }
    }

    /// <summary>Modules loaded in this worker's runspace.</summary>
    internal List<PSModuleInfo> LoadedModules()
    {
        try { return _pwsh.AddCommand("Get-Module").Invoke<PSModuleInfo>().ToList(); }
        finally { _pwsh.Commands.Clear(); _pwsh.Streams.ClearStreams(); }
    }

    /// <summary>Functions visible to scripts in this worker (exported module functions + deployed scripts).</summary>
    internal int FunctionCount()
    {
        try { return _pwsh.AddScript("@(Get-ChildItem Function:).Count").Invoke<int>().FirstOrDefault(); }
        finally { _pwsh.Commands.Clear(); _pwsh.Streams.ClearStreams(); }
    }

    /// <summary>
    /// Async invoke — does not block a ThreadPool thread during PS execution.
    /// Includes post-invocation cleanup matching Azure Functions' ResetRunspace.
    /// When a cancellation token is provided and fires, the PowerShell pipeline is
    /// stopped via <see cref="PowerShell.Stop"/>. The resulting <c>PipelineStoppedException</c>
    /// is normalized to an <see cref="OperationCanceledException"/> so timeout callers can
    /// distinguish a cancelled request from a genuine script failure. A pipeline that does not
    /// stop within <see cref="StopGrace"/> throws <see cref="WorkerHungException"/> and leaves
    /// this worker <see cref="IsHung"/>.
    /// </summary>
    public async Task<Collection<PSObject>> InvokeAsync(string functionName, Dictionary<string, object?> parameters,
        CancellationToken ct = default)
    {
        ThrowIfHung(functionName);
        var prof = DispatchProfiler.Enabled;
        long buildTicks = 0, runTicks = 0, copyTicks = 0;
        try
        {
            StampOperationContext();
            var bStart = prof ? System.Diagnostics.Stopwatch.GetTimestamp() : 0;
            _pwsh.AddCommand(functionName);
            foreach (var p in parameters)
                _pwsh.AddParameter(p.Key, p.Value);
            if (prof) buildTicks = System.Diagnostics.Stopwatch.GetTimestamp() - bStart;

            var rStart = prof ? System.Diagnostics.Stopwatch.GetTimestamp() : 0;
            using var outputs = await RunPipelineAsync(functionName, ct);
            if (prof) runTicks = System.Diagnostics.Stopwatch.GetTimestamp() - rStart;

            var cpStart = prof ? System.Diagnostics.Stopwatch.GetTimestamp() : 0;
            var coll = new Collection<PSObject>(outputs.ReadAll());
            if (prof) copyTicks = System.Diagnostics.Stopwatch.GetTimestamp() - cpStart;
            return coll;
        }
        catch (PipelineStoppedException) when (ct.IsCancellationRequested)
        {
            // The stop landed mid-invoke, so EndInvoke threw PipelineStoppedException rather than
            // reaching ThrowIfCancellationRequested. Normalize to OperationCanceledException so
            // timeout callers return 504, not 500.
            throw new OperationCanceledException(ct);
        }
        finally
        {
            // A hung worker's pipeline is still running, and the worker never runs again.
            if (!IsHung)
            {
                var cStart = prof ? System.Diagnostics.Stopwatch.GetTimestamp() : 0;
                Cleanup();
                if (prof) DispatchProfiler.RecordInvokeDetail(buildTicks, runTicks, copyTicks,
                    System.Diagnostics.Stopwatch.GetTimestamp() - cStart);
            }
        }
    }

    /// <summary>
    /// Invoke a bare script (no function definition) — for timer scripts etc.
    /// </summary>
    public async Task<Collection<PSObject>> InvokeScriptAsync(ScriptBlock scriptBlock,
        Dictionary<string, object?>? parameters = null, CancellationToken ct = default)
    {
        ThrowIfHung("script");
        try
        {
            StampOperationContext();
            _pwsh.AddScript("& $args[0]").AddArgument(scriptBlock);
            if (parameters != null)
                foreach (var p in parameters)
                    _pwsh.AddParameter(p.Key, p.Value);

            using var outputs = await RunPipelineAsync("script", ct);
            return new Collection<PSObject>(outputs.ReadAll());
        }
        catch (PipelineStoppedException) when (ct.IsCancellationRequested)
        {
            // See InvokeAsync.
            throw new OperationCanceledException(ct);
        }
        finally
        {
            if (!IsHung) Cleanup();
        }
    }

    /// <summary>
    /// How long a cancelled invocation gets to unwind after the stop request before the worker is
    /// abandoned. See <see cref="Configuration.WorkerSettings.StopGraceSeconds"/>.
    /// </summary>
    public TimeSpan StopGrace { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// True once an invocation failed to stop within <see cref="StopGrace"/>. Its pipeline thread is
    /// blocked in a call PowerShell cannot interrupt; the worker never runs again and the pool replaces it.
    /// </summary>
    public bool IsHung => _hungRun != null;

    /// <summary>The abandoned pipeline of a hung worker; completes if the blocked call ever returns.</summary>
    internal Task? HungRun => _hungRun;

    private volatile Task? _hungRun;
    private int _disposed;

    private void ThrowIfHung(string function)
    {
        if (IsHung)
            throw new WorkerHungException(Id, $"Worker W{Id} is hung and cannot run {function}");
    }

    /// <summary>
    /// Run the queued commands. On cancellation the stop runs on a thread of its own, because
    /// <see cref="PowerShell.Stop"/> blocks until the pipeline unwinds — which a pipeline blocked in .NET
    /// never does. Called from the cancellation callback, as it used to be, it never returned, so the
    /// timeout was never reported and the worker never came back to the pool.
    /// </summary>
    private async Task<PSDataCollection<PSObject>> RunPipelineAsync(string label, CancellationToken ct)
    {
        // A fresh output collection per invocation: after an invocation that threw, the PowerShell object's
        // own output buffer comes back null from the next EndInvoke, silently dropping that call's output.
        var outputs = new PSDataCollection<PSObject>();
        var run = Task.Factory.FromAsync(_pwsh.BeginInvoke<PSObject, PSObject>(null, outputs), _pwsh.EndInvoke);
        Task? stop = null;
        try
        {
            if (ct.CanBeCanceled && !run.IsCompleted)
            {
                var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                using (ct.Register(() => cancelled.TrySetResult()))
                    await Task.WhenAny(run, cancelled.Task);

                if (!run.IsCompleted)
                {
                    stop = Task.Factory.StartNew(StopPipeline, CancellationToken.None,
                        TaskCreationOptions.LongRunning, TaskScheduler.Default);
                    if (await Task.WhenAny(run, Task.Delay(StopGrace, CancellationToken.None)) != run)
                    {
                        Abandon(run, outputs, label);
                        throw new WorkerHungException(Id,
                            $"Worker W{Id} running {label} did not stop within {StopGrace.TotalSeconds:0}s of being cancelled and was abandoned");
                    }
                }
            }

            await run;
            ct.ThrowIfCancellationRequested();
            return outputs;
        }
        catch (Exception ex) when (ex is not WorkerHungException)
        {
            outputs.Dispose();
            throw;
        }
        finally
        {
            // The pipeline has finished, so a stop still in flight returns at once. Wait for it, or it could
            // land on the cleanup pipelines that run next.
            if (stop != null && !IsHung) await stop;
        }
    }

    private void StopPipeline()
    {
        try { _pwsh.Stop(); }
        catch (Exception ex) { _logger.LogDebug(ex, "Worker{Id}: pipeline stop threw", Id); }
    }

    private void Abandon(Task run, PSDataCollection<PSObject> outputs, string label)
    {
        var abandonedAt = System.Diagnostics.Stopwatch.GetTimestamp();
        _hungRun = run;
        _logger.LogError(
            "[Pool] W{Id} {Function} did not stop within {Grace:0}s of being cancelled: it is blocked in a call " +
            "PowerShell cannot interrupt. Abandoning the worker.", Id, label, StopGrace.TotalSeconds);
        _ = run.ContinueWith(t =>
        {
            _ = t.Exception;
            outputs.Dispose();
            _logger.LogWarning("[Pool] Abandoned W{Id} {Function} returned {Seconds:0}s after it was abandoned",
                Id, label, System.Diagnostics.Stopwatch.GetElapsedTime(abandonedAt).TotalSeconds);
        }, CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default);
    }

    public PSDataStreams Streams => _pwsh.Streams;

    /// <summary>
    /// Make the caller's ambient <see cref="OperationContext"/> readable from PowerShell as
    /// $global:CraftOperationContext.
    ///
    /// The pipeline runs on the runspace's reused thread (<c>PSThreadOptions.ReuseThread</c>, set at
    /// worker creation), whose ExecutionContext was captured once when that thread was created — at
    /// pool warmup, before any operation context existed. AsyncLocal values set per invocation never
    /// reach it, so PS code (and .NET bridge calls made from the pipeline thread) reading
    /// <c>OperationContext.Current</c> always sees null. Stamping the context into a global variable
    /// from the calling thread — which does hold the AsyncLocal — is the reliable carrier. The
    /// post-invocation <see cref="CleanupGlobalVariables"/> sweep removes it again, so it cannot go
    /// stale across checkouts.
    /// </summary>
    private void StampOperationContext()
    {
        try
        {
            _pwsh.Runspace.SessionStateProxy.PSVariable.Set("CraftOperationContext", OperationContext.Current);
        }
        catch
        {
            // Diagnostics context is never worth failing the invocation; PS falls back to defaults.
        }
    }

    private void Cleanup()
    {
        _pwsh.Commands.Clear();
        _pwsh.Streams.ClearStreams();
        ResetPipelineExecutionContext();
        CleanupGlobalVariables();
        CleanupJobs();
    }

    /// <summary>
    /// Reset the reused pipeline thread's ExecutionContext to the clean baseline, dropping any AsyncLocal
    /// set during the invocation (which would otherwise leak to the next invocation on this worker). Runs
    /// <see cref="PipelineExecutionContext.Reset"/> as a minimal pipeline so it executes ON the pipeline
    /// thread. SessionState (module $script: vars, injected caches) is not in the ExecutionContext, so it
    /// is unaffected. Never fail an invocation over this — catch, log, continue.
    /// </summary>
    private void ResetPipelineExecutionContext()
    {
        try
        {
            _pwsh.AddScript("[Craft.PowerShellHost.PipelineExecutionContext]::Reset()").Invoke();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Worker{Id}: per-invocation ExecutionContext reset failed; continuing.", Id);
        }
        finally
        {
            _pwsh.Commands.Clear();
            _pwsh.Streams.ClearStreams();
        }
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
        _pwsh.AddScript($@"
try {{
    {combinedScript}
}} catch {{
    Write-Warning ""Warmup failed: $_""
}}
").Invoke();

        // Surface warmup diagnostics before clearing streams — RunScript discards them.
        foreach (var warn in _pwsh.Streams.Warning)
            _logger?.LogWarning("[Warmup] {Message}", warn.Message);
        foreach (var info in _pwsh.Streams.Information)
            _logger?.LogInformation("[Warmup] {Message}", info.MessageData);
        foreach (var err in _pwsh.Streams.Error)
            _logger?.LogError("[Warmup] {Error}", err.Exception?.Message ?? err.ToString());

        _pwsh.Commands.Clear();
        _pwsh.Streams.ClearStreams();
    }

    public void Dispose()
    {
        // Nothing here owns unmanaged resources directly, but suppressing finalization keeps a
        // derived type that adds a finalizer from having to re-implement IDisposable to do it.
        GC.SuppressFinalize(this);
        if (_hungRun is { IsCompleted: false } hung)
        {
            // Closing the runspace would wait on the blocked pipeline, possibly forever.
            _ = hung.ContinueWith(_ => DisposeCore(), CancellationToken.None,
                TaskContinuationOptions.None, TaskScheduler.Default);
            return;
        }
        DisposeCore();
    }

    private void DisposeCore()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1) return;
        // PowerShell.Create(iss) ASSIGNS the runspace rather than creating it lazily, and an assigned
        // runspace is caller-owned — _pwsh.Dispose() does not close it. Left open, the runspace keeps
        // its ReuseThread pipeline thread alive, and a live thread roots the entire session state
        // (every SSFE-injected function of every module) through any GC, however aggressive: measured
        // at ~20 MB retained per recycled worker, for the process lifetime.
        var runspace = _pwsh.Runspace;
        _pwsh.Dispose();
        runspace?.Dispose();
    }
}

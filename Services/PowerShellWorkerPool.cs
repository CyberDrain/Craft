using System.Collections.Concurrent;
using System.Management.Automation.Runspaces;

namespace Craft.Services;

public class PowerShellWorkerPool : IDisposable
{
    private readonly BlockingCollection<PowerShellWorker> _httpPool;
    private readonly BlockingCollection<PowerShellWorker> _bgPool;
    private readonly ILogger<PowerShellWorkerPool> _logger;
    private readonly ScriptRepository _repo;
    private readonly CraftSettings _settings;
    private readonly string _apiBasePath;
    private readonly int _httpPoolSize;
    private readonly int _bgPoolSize;
    private int _nextId;
    private readonly List<int> _httpWorkerIds = new();
    private readonly List<int> _bgWorkerIds = new();

    private readonly ConcurrentDictionary<int, int> _workerFaults = new(); // workerId → consecutive fault count
    private const int MaxConsecutiveFaults = 3;
    private readonly ManualResetEventSlim _ready = new(false);
    private readonly ManualResetEventSlim _httpReady = new(false);
    private readonly ManualResetEventSlim _bgReady = new(false);
    private ExportedModuleState? _httpClonedState;  // Cached state from first HTTP worker
    private ExportedModuleState? _bgClonedState;    // Cached state from first BG worker

    public bool IsReady => _httpReady.IsSet;
    public int HttpAvailable => _httpPool.Count;
    public int BgAvailable => _bgPool.Count;
    public int HttpPoolSize => _httpPoolSize;
    public int BgPoolSize => _bgPoolSize;

    public PowerShellWorkerPool(ScriptRepository repo, ILogger<PowerShellWorkerPool> logger, IConfiguration config, CraftSettings settings)
    {
        _repo = repo;
        _logger = logger;
        _settings = settings;
        _apiBasePath = Path.Combine(AppContext.BaseDirectory, "API");

        _httpPoolSize = Math.Max(1, settings.Worker.HttpPoolSize);
        _bgPoolSize = Math.Max(1, settings.Worker.BgPoolSize);

        _httpPool = new BlockingCollection<PowerShellWorker>(_httpPoolSize);
        _bgPool = new BlockingCollection<PowerShellWorker>(_bgPoolSize);
    }

    public void Initialize()
    {
        // Set PSModulePath at process level BEFORE creating ISS —
        // PowerShell's command discovery reads the process environment, not ISS env vars
        var modulesPath = Path.Combine(_apiBasePath, "Modules");
        if (Directory.Exists(modulesPath))
        {
            var currentPath = Environment.GetEnvironmentVariable("PSModulePath") ?? "";
            if (!currentPath.Contains(modulesPath, StringComparison.OrdinalIgnoreCase))
            {
                Environment.SetEnvironmentVariable("PSModulePath",
                    modulesPath + Path.PathSeparator + currentPath);
                _logger.LogInformation("[System] PSModulePath: prepended {Path}", modulesPath);
            }
        }

        var sw = System.Diagnostics.Stopwatch.StartNew();
        StartupInfoBridge.SetCpuCount(Environment.ProcessorCount);
        StartupInfoBridge.SetPoolConfig(_httpPoolSize, _bgPoolSize);

        // Test whether thread priority control works on this OS
        try
        {
            var original = Thread.CurrentThread.Priority;
            Thread.CurrentThread.Priority = ThreadPriority.BelowNormal;
            var actual = Thread.CurrentThread.Priority;
            Thread.CurrentThread.Priority = original;
            _logger.LogInformation("[System] Thread priority control: {Status} (set BelowNormal, read back {Actual})",
                actual == ThreadPriority.BelowNormal ? "supported" : "ineffective", actual);
        }
        catch (Exception ex)
        {
            _logger.LogWarning("[System] Thread priority control: not supported ({Error})", ex.Message);
        }

        var warmupMode = _settings.Worker.WarmupMode?.Trim() ?? "AfterReady";
        var hasWarmup = _settings.Worker.WarmupScripts.Count > 0;
        if (hasWarmup)
            _logger.LogInformation("[System] WarmupMode: {Mode} ({Count} scripts)", warmupMode, _settings.Worker.WarmupScripts.Count);
        StartupInfoBridge.SetWarmupMode(warmupMode);

        var httpModuleList = _settings.Worker.HttpModules;
        var bgModuleList = _settings.Worker.BgModules;
        var httpHasFilter = httpModuleList.Count > 0;
        var bgHasFilter = bgModuleList.Count > 0;
        bool separateModuleLists = httpHasFilter || bgHasFilter;

        // Log module filtering config
        if (httpHasFilter)
            _logger.LogInformation("[System] HTTP workers: loading {Count} modules: {Modules}", httpModuleList.Count, string.Join(", ", httpModuleList));
        if (bgHasFilter)
            _logger.LogInformation("[System] BG workers: loading {Count} modules: {Modules}", bgModuleList.Count, string.Join(", ", bgModuleList));

        // Compute shared modules (intersection of HTTP and BG lists)
        var sharedModules = httpHasFilter && bgHasFilter
            ? httpModuleList.Intersect(bgModuleList, StringComparer.OrdinalIgnoreCase).ToList()
            : new List<string>();
        var httpOnlyModules = httpHasFilter
            ? httpModuleList.Except(sharedModules, StringComparer.OrdinalIgnoreCase).ToList()
            : new List<string>();
        var bgOnlyModules = bgHasFilter
            ? bgModuleList.Except(sharedModules, StringComparer.OrdinalIgnoreCase).ToList()
            : new List<string>();

        bool useSharedBase = sharedModules.Count > 0 && (httpOnlyModules.Count > 0 || bgOnlyModules.Count > 0);

        if (useSharedBase)
            InitializeWithSharedBase(sw, sharedModules, httpOnlyModules, bgOnlyModules, warmupMode);
        else
            InitializeSimple(sw, separateModuleLists, warmupMode);
    }

    /// <summary>
    /// Shared-base initialization: parse shared modules once in a base worker,
    /// then create HTTP and BG first-workers in parallel by cloning the base
    /// and adding only their unique modules via ISS ImportPSModule.
    /// </summary>
    private void InitializeWithSharedBase(System.Diagnostics.Stopwatch sw,
        List<string> sharedModules, List<string> httpOnlyModules, List<string> bgOnlyModules, string warmupMode)
    {
        StartupInfoBridge.SetModuleCounts(sharedModules.Count, httpOnlyModules.Count, bgOnlyModules.Count);
        _logger.LogInformation("[System] Shared base: {Shared} shared, {HttpOnly} HTTP-only, {BgOnly} BG-only modules",
            sharedModules.Count, httpOnlyModules.Count, bgOnlyModules.Count);

        // ── Base worker: parse shared modules ──────────────────────────────
        var baseISS = BuildISSForModules(sharedModules);
        var baseWorker = new PowerShellWorker(Interlocked.Increment(ref _nextId), baseISS, _logger);
        baseWorker.Initialize(_repo, _apiBasePath, _settings);

        var baseMs = sw.ElapsedMilliseconds;
        _logger.LogInformation("[System] Base worker ready in {Ms}ms, exporting shared state", baseMs);

        var baseState = baseWorker.ExportModuleState();
        StartupInfoBridge.SetBaseWorkerDone(baseMs, baseState.Functions.Count);
        _logger.LogInformation("[System] Base state: {FnCount} functions, {VarCount} variables, {BinCount} binary modules",
            baseState.Functions.Count, baseState.Variables.Count, baseState.BinaryModulePaths.Count);

        // Done with base worker — dispose it
        baseWorker.Dispose();

        // ── HTTP first-worker (cloned base + HTTP-only modules) ────────────
        var httpISS = BuildClonedISSWithModules(baseState, httpOnlyModules);
        var firstHttpWorker = new PowerShellWorker(Interlocked.Increment(ref _nextId), httpISS, _logger);
        firstHttpWorker.Initialize(_repo, _apiBasePath, _settings);
        var httpState = firstHttpWorker.ExportModuleState();

        _httpClonedState = httpState.MergeWith(baseState);

        // Run warmup before signaling ready (BeforeReady and AfterReady both run here;
        // Background mode runs warmup on the first BG worker instead)
        if (!warmupMode.Equals("Background", StringComparison.OrdinalIgnoreCase))
            RunWarmup(firstHttpWorker, sw);

        AddToHttpPool(firstHttpWorker);

        // Signal HTTP ready — one worker can serve requests (warmup already complete)
        _httpReady.Set();
        _ready.Set();
        StartupInfoBridge.SetHttpReady(sw.ElapsedMilliseconds, _httpClonedState.Functions.Count);
        _logger.LogInformation("[System] HTTP ready: 1 worker in {Ms}ms — API accepting requests ({FnCount} functions)",
            sw.ElapsedMilliseconds, _httpClonedState.Functions.Count);

        // Clone remaining HTTP workers (API already serving with first worker)
        if (_httpPoolSize > 1)
        {
            var clonedHttpISS = BuildClonedISS(_httpClonedState);
            var httpRemaining = new List<PowerShellWorker>();
            for (int i = 1; i < _httpPoolSize; i++)
                httpRemaining.Add(new PowerShellWorker(Interlocked.Increment(ref _nextId), clonedHttpISS, _logger));

            Parallel.ForEach(httpRemaining, w => w.Initialize(_repo, _apiBasePath, _settings));
            foreach (var w in httpRemaining)
                AddToHttpPool(w);

            StartupInfoBridge.SetHttpPoolFull(sw.ElapsedMilliseconds);
            _logger.LogInformation("[System] HTTP pool full: {Count} workers in {Ms}ms",
                _httpPoolSize, sw.ElapsedMilliseconds);
        }

        // ── BG first-worker (cloned base + BG-only modules) ────────────────
        var bgISS = BuildClonedISSWithModules(baseState, bgOnlyModules);
        var firstBgWorker = new PowerShellWorker(Interlocked.Increment(ref _nextId), bgISS, _logger);
        firstBgWorker.Initialize(_repo, _apiBasePath, _settings);
        var bgState = firstBgWorker.ExportModuleState();

        _bgClonedState = bgState.MergeWith(baseState);

        // Background: warmup runs on the first BG worker — HTTP pool unaffected
        if (warmupMode.Equals("Background", StringComparison.OrdinalIgnoreCase))
            RunWarmup(firstBgWorker, sw);

        AddToBgPool(firstBgWorker);

        StartupInfoBridge.SetBgReady(sw.ElapsedMilliseconds, _bgClonedState.Functions.Count);
        _logger.LogInformation("[System] BG first worker ready in {Ms}ms — {FnCount} functions",
            sw.ElapsedMilliseconds, _bgClonedState.Functions.Count);

        // Clone remaining BG workers
        if (_bgPoolSize > 1)
        {
            var clonedBgISS = BuildClonedISS(_bgClonedState);
            var bgRemaining = new List<PowerShellWorker>();
            for (int i = 1; i < _bgPoolSize; i++)
                bgRemaining.Add(new PowerShellWorker(Interlocked.Increment(ref _nextId), clonedBgISS, _logger));

            Parallel.ForEach(bgRemaining, w => w.Initialize(_repo, _apiBasePath, _settings));
            foreach (var w in bgRemaining)
                AddToBgPool(w);
        }

        _bgReady.Set();

        // Pre-register all workers in metrics bridge so they appear in snapshots before first use
        RegisterAllWorkers();

        StartupInfoBridge.SetFullyReady(sw.ElapsedMilliseconds);
        _logger.LogInformation("[System] Pool fully ready: {Http} HTTP + {Bg} BG workers in {Ms}ms (base: {BaseMs}ms)",
            _httpPoolSize, _bgPoolSize, sw.ElapsedMilliseconds, baseMs);
    }

    /// <summary>
    /// Simple initialization (no shared base): same-module or single-list config.
    /// Falls back to sequential first-worker + clone pattern.
    /// </summary>
    private void InitializeSimple(System.Diagnostics.Stopwatch sw, bool separateModuleLists, string warmupMode)
    {
        // First HTTP worker: full module import
        var fullHttpISS = BuildISS(isHttp: true);
        var firstWorker = new PowerShellWorker(Interlocked.Increment(ref _nextId), fullHttpISS, _logger);
        firstWorker.Initialize(_repo, _apiBasePath, _settings);

        _httpClonedState = firstWorker.ExportModuleState();

        // Run warmup before signaling ready (BeforeReady and AfterReady both run here;
        // Background mode runs warmup on the first BG worker instead)
        if (!warmupMode.Equals("Background", StringComparison.OrdinalIgnoreCase))
            RunWarmup(firstWorker, sw);

        AddToHttpPool(firstWorker);

        // Signal HTTP ready — one worker can serve requests (warmup already complete)
        _httpReady.Set();
        _ready.Set();
        var firstWorkerMs = sw.ElapsedMilliseconds;
        StartupInfoBridge.SetHttpReady(firstWorkerMs, _httpClonedState.Functions.Count);
        _logger.LogInformation("[System] HTTP ready: 1 worker in {Ms}ms — API accepting requests", firstWorkerMs);

        // Clone remaining HTTP workers (API already serving with first worker)
        if (_httpPoolSize > 1)
        {
            var clonedHttpISS = BuildClonedISS(_httpClonedState);
            var httpRemaining = new List<PowerShellWorker>();
            for (int i = 1; i < _httpPoolSize; i++)
                httpRemaining.Add(new PowerShellWorker(Interlocked.Increment(ref _nextId), clonedHttpISS, _logger));

            Parallel.ForEach(httpRemaining, w => w.Initialize(_repo, _apiBasePath, _settings));
            foreach (var w in httpRemaining)
                AddToHttpPool(w);

            _logger.LogInformation("[System] HTTP pool full: {Count} workers in {Ms}ms",
                _httpPoolSize, sw.ElapsedMilliseconds);
        }

        // BG workers
        if (separateModuleLists)
        {
            var bgSw = System.Diagnostics.Stopwatch.StartNew();
            var fullBgISS = BuildISS(isHttp: false);
            var firstBgWorker = new PowerShellWorker(Interlocked.Increment(ref _nextId), fullBgISS, _logger);
            firstBgWorker.Initialize(_repo, _apiBasePath, _settings);
            _bgClonedState = firstBgWorker.ExportModuleState();

            // Background: warmup runs on the first BG worker — HTTP pool unaffected
            if (warmupMode.Equals("Background", StringComparison.OrdinalIgnoreCase))
                RunWarmup(firstBgWorker, sw);

            AddToBgPool(firstBgWorker);
            StartupInfoBridge.SetBgReady(sw.ElapsedMilliseconds, _bgClonedState.Functions.Count);
            _logger.LogInformation("[System] First BG worker ready in {Ms}ms — BG state: {FnCount} functions, {VarCount} variables",
                bgSw.ElapsedMilliseconds, _bgClonedState.Functions.Count, _bgClonedState.Variables.Count);
        }
        else
        {
            _bgClonedState = _httpClonedState;
        }

        var clonedBgISS = BuildClonedISS(_bgClonedState);
        var bgStart = separateModuleLists ? 1 : 0;
        var bgRemaining = new List<PowerShellWorker>();
        for (int i = bgStart; i < _bgPoolSize; i++)
            bgRemaining.Add(new PowerShellWorker(Interlocked.Increment(ref _nextId), clonedBgISS, _logger));

        if (bgRemaining.Count > 0)
        {
            Parallel.ForEach(bgRemaining, w => w.Initialize(_repo, _apiBasePath, _settings));
            foreach (var w in bgRemaining)
                AddToBgPool(w);
        }

        _bgReady.Set();

        // Pre-register all workers in metrics bridge so they appear in snapshots before first use
        RegisterAllWorkers();

        StartupInfoBridge.SetFullyReady(sw.ElapsedMilliseconds);
        _logger.LogInformation("[System] Pool fully ready: {Http} HTTP + {Bg} BG workers in {Ms}ms",
            _httpPoolSize, _bgPoolSize, sw.ElapsedMilliseconds);
    }

    /// <summary>
    /// Run configured warmup scripts on a worker. Sets process-level env vars
    /// (visible to all workers) and primes caches. Non-fatal on failure.
    /// </summary>
    private void RunWarmup(PowerShellWorker worker, System.Diagnostics.Stopwatch sw)
    {
        if (_settings.Worker.WarmupScripts.Count == 0) return;

        var warmSw = System.Diagnostics.Stopwatch.StartNew();
        worker.Warmup(_settings);
        _logger.LogInformation("[System] Warmup completed in {Ms}ms (at {Total}ms)", warmSw.ElapsedMilliseconds, sw.ElapsedMilliseconds);
        StartupInfoBridge.SetWarmupDone(warmSw.ElapsedMilliseconds);
    }

    /// <summary>
    /// Pre-register all pool workers with WorkerMetricsBridge so they appear
    /// in snapshots even before their first checkout.
    /// </summary>
    private void RegisterAllWorkers()
    {
        foreach (var id in _httpWorkerIds)
            WorkerMetricsBridge.RegisterWorker(id, isHttp: true);
        foreach (var id in _bgWorkerIds)
            WorkerMetricsBridge.RegisterWorker(id, isHttp: false);
    }

    private void AddToHttpPool(PowerShellWorker worker)
    {
        _httpPool.Add(worker);
        _httpWorkerIds.Add(worker.Id);
    }

    private void AddToBgPool(PowerShellWorker worker)
    {
        _bgPool.Add(worker);
        _bgWorkerIds.Add(worker.Id);
    }

    /// <summary>
    /// Block until the pool has finished initializing, or the timeout expires.
    /// </summary>
    public bool WaitForReady(TimeSpan timeout) => _httpReady.Wait(timeout);

    /// <summary>
    /// Block until the BG worker pool is ready (scheduler/background tasks should wait on this).
    /// </summary>
    public bool WaitForBgReady(TimeSpan timeout) => _bgReady.Wait(timeout);

    public PowerShellWorker? CheckoutHttp(TimeSpan timeout)
    {
        // Wait for HTTP pool initialization before attempting checkout
        if (!_httpReady.IsSet)
            _httpReady.Wait(timeout);
        if (_httpPool.TryTake(out var w, timeout))
        {
            w.CheckoutTimestamp = System.Diagnostics.Stopwatch.GetTimestamp();
            WorkerMetricsBridge.RecordCheckout(w.Id, isHttp: true);

            // Boost thread priority so HTTP requests get CPU preference over BG work
            try { Thread.CurrentThread.Priority = ThreadPriority.AboveNormal; }
            catch (Exception ex) { _logger.LogWarning("[Pool] Failed to set HTTP thread priority: {Error}", ex.Message); }

            return w;
        }
        return null;
    }

    public PowerShellWorker CheckoutBackground(CancellationToken ct)
    {
        _bgReady.Wait(ct);
        var w = _bgPool.Take(ct);
        w.CheckoutTimestamp = System.Diagnostics.Stopwatch.GetTimestamp();
        WorkerMetricsBridge.RecordCheckout(w.Id, isHttp: false);

        // Lower thread priority so HTTP workers get CPU preference under contention
        try { Thread.CurrentThread.Priority = ThreadPriority.Lowest; }
        catch (Exception ex) { _logger.LogWarning("[Pool] Failed to set BG thread priority: {Error}", ex.Message); }

        return w;
    }

    public void Reclaim(PowerShellWorker worker, bool isHttp, bool faulted = false)
    {
        // Restore normal thread priority (HTTP was boosted, BG was lowered)
        try { Thread.CurrentThread.Priority = ThreadPriority.Normal; }
        catch (Exception ex) { _logger.LogWarning("[Pool] Failed to restore thread priority: {Error}", ex.Message); }

        // Track elapsed time since checkout
        var elapsedMs = worker.CheckoutTimestamp > 0
            ? (long)System.Diagnostics.Stopwatch.GetElapsedTime(worker.CheckoutTimestamp).TotalMilliseconds
            : 0;
        WorkerMetricsBridge.RecordReclaim(worker.Id, faulted, elapsedMs);
        worker.CheckoutTimestamp = 0;
        worker.InvocationCount++;

        bool needsReplace = false;

        if (faulted)
        {
            var faults = _workerFaults.AddOrUpdate(worker.Id, 1, (_, c) => c + 1);
            if (faults >= MaxConsecutiveFaults)
            {
                _logger.LogWarning("[Pool] Worker W{Id} hit {Faults} consecutive faults, replacing", worker.Id, faults);
                _workerFaults.TryRemove(worker.Id, out _);
                needsReplace = true;
            }
        }
        else
        {
            _workerFaults.TryRemove(worker.Id, out _); // Reset on success

            // Recycle worker after N invocations to reclaim native memory
            var recycleAfter = _settings.Worker.RecycleAfterInvocations;
            if (recycleAfter > 0 && worker.InvocationCount >= recycleAfter)
            {
                _logger.LogInformation("[Pool] Recycling W{Id} after {Count} invocations (native memory reclaim)",
                    worker.Id, worker.InvocationCount);
                needsReplace = true;
            }
        }

        if (needsReplace)
        {
            var oldId = worker.Id;
            WorkerMetricsBridge.DeregisterWorker(oldId);
            worker.Dispose();
            var cloned = isHttp ? _httpClonedState : _bgClonedState;
            var iss = cloned != null ? BuildClonedISS(cloned) : BuildISS(isHttp: isHttp);
            worker = new PowerShellWorker(Interlocked.Increment(ref _nextId), iss, _logger);
            worker.Initialize(_repo, _apiBasePath, _settings);
            WorkerMetricsBridge.RegisterWorker(worker.Id, isHttp);
            _logger.LogInformation("[Pool] Replaced W{OldId} → W{NewId} ({Type})",
                oldId, worker.Id, isHttp ? "HTTP" : "BG");
        }

        if (isHttp) _httpPool.Add(worker); else _bgPool.Add(worker);
    }

    private InitialSessionState BuildISS(bool isHttp)
    {
        var allowList = isHttp ? _settings.Worker.HttpModules : _settings.Worker.BgModules;
        return BuildISSForModules(allowList.Count > 0 ? allowList : null);
    }

    /// <summary>
    /// Register configured SharedAssemblies on the InitialSessionState so the runspace's
    /// type resolver knows about them from creation. This complements the runtime
    /// [Reflection.Assembly]::LoadFile script in PowerShellWorker.Initialize() and ensures
    /// type literals (e.g. [CIPP.TestDataCache]) resolve in cloned/recycled runspaces.
    /// Logs each path so silent load failures during cold-start become observable.
    /// </summary>
    private void RegisterSharedAssemblies(InitialSessionState iss, string buildContext)
    {
        if (_settings.Worker.SharedAssemblies.Count == 0) return;

        foreach (var asmRelPath in _settings.Worker.SharedAssemblies)
        {
            if (string.IsNullOrWhiteSpace(asmRelPath)) continue;

            var asmPath = Path.GetFullPath(Path.Combine(_apiBasePath, asmRelPath));
            if (!File.Exists(asmPath))
            {
                _logger.LogError("[Pool] SharedAssembly missing for {Context}: {Path}", buildContext, asmPath);
                continue;
            }

            try
            {
                var asmName = Path.GetFileNameWithoutExtension(asmPath);
                iss.Assemblies.Add(new SessionStateAssemblyEntry(asmName, asmPath));
                _logger.LogDebug("[Pool] SharedAssembly registered for {Context}: {Name} ({Path})",
                    buildContext, asmName, asmPath);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[Pool] SharedAssembly registration failed for {Context}: {Path}",
                    buildContext, asmPath);
            }
        }
    }

    /// <summary>
    /// Build an ISS that imports only the specified modules.
    /// If moduleList is null, imports all modules (minus SkipModules).
    /// </summary>
    private InitialSessionState BuildISSForModules(List<string>? moduleList)
    {
        var iss = InitialSessionState.CreateDefault();
        if (OperatingSystem.IsWindows())
            iss.ExecutionPolicy = Microsoft.PowerShell.ExecutionPolicy.Bypass;

        RegisterSharedAssemblies(iss, "BuildISSForModules");

        // Copy environment variables into the runspace
        foreach (System.Collections.DictionaryEntry env in Environment.GetEnvironmentVariables())
            iss.EnvironmentVariables.Add(new SessionStateVariableEntry((string)env.Key, env.Value, null));

        // Set PowerShell preference variables based on configured log level.
        // This controls which PS streams actually produce records for CRAFT to capture.
        var logLevel = _settings.FileLogging.ParsedLogLevel;
        if (logLevel <= Microsoft.Extensions.Logging.LogLevel.Trace)
            iss.Variables.Add(new SessionStateVariableEntry("VerbosePreference", "Continue", "Set by CRAFT log level"));
        if (logLevel <= Microsoft.Extensions.Logging.LogLevel.Debug)
            iss.Variables.Add(new SessionStateVariableEntry("DebugPreference", "Continue", "Set by CRAFT log level"));

        var hasAllowList = moduleList != null && moduleList.Count > 0;

        // Import modules via ISS — filtered by allow list when configured
        var modulesPath = Path.Combine(_apiBasePath, "Modules");
        if (Directory.Exists(modulesPath))
        {
            foreach (var moduleDir in Directory.GetDirectories(modulesPath))
            {
                var moduleName = Path.GetFileName(moduleDir);
                if (_settings.Worker.SkipModules.Contains(moduleName, StringComparer.OrdinalIgnoreCase))
                    continue;
                if (hasAllowList && !moduleList!.Contains(moduleName, StringComparer.OrdinalIgnoreCase))
                    continue;

                var manifest = FindModuleManifest(moduleDir, moduleName);
                if (manifest != null)
                {
                    iss.ImportPSModule(new[] { manifest });
                }
            }
        }
        return iss;
    }

    /// <summary>
    /// Build an ISS from a cloned base state plus additional modules via ImportPSModule.
    /// The base functions are injected as SessionStateFunctionEntry (no re-parsing),
    /// then the additional modules are imported normally (only they get parsed).
    /// </summary>
    private InitialSessionState BuildClonedISSWithModules(ExportedModuleState baseState, List<string> additionalModules)
    {
        var iss = InitialSessionState.CreateDefault();
        if (OperatingSystem.IsWindows())
            iss.ExecutionPolicy = Microsoft.PowerShell.ExecutionPolicy.Bypass;

        RegisterSharedAssemblies(iss, "BuildClonedISSWithModules");

        foreach (System.Collections.DictionaryEntry env in Environment.GetEnvironmentVariables())
            iss.EnvironmentVariables.Add(new SessionStateVariableEntry((string)env.Key, env.Value, null));

        // Set PowerShell preference variables based on configured log level
        var logLevel = _settings.FileLogging.ParsedLogLevel;
        if (logLevel <= Microsoft.Extensions.Logging.LogLevel.Trace)
            iss.Variables.Add(new SessionStateVariableEntry("VerbosePreference", "Continue", "Set by CRAFT log level"));
        if (logLevel <= Microsoft.Extensions.Logging.LogLevel.Debug)
            iss.Variables.Add(new SessionStateVariableEntry("DebugPreference", "Continue", "Set by CRAFT log level"));

        // Determine which modules need native import (they have private functions)
        var nativeModules = new HashSet<string>(baseState.NativeImportModulePaths.Select(Path.GetFileNameWithoutExtension)!,
            StringComparer.OrdinalIgnoreCase);

        // Inject pre-parsed functions from the base state (skip natively-imported modules)
        foreach (var (name, definition, module) in baseState.Functions)
        {
            if (nativeModules.Contains(module))
                continue;
            try { iss.Commands.Add(new SessionStateFunctionEntry(name, definition)); }
            catch { }
        }

        // Inject exported variables from base
        foreach (var (name, value) in baseState.Variables)
            iss.Variables.Add(new SessionStateVariableEntry(name, value, null));

        // Import binary modules from base
        foreach (var path in baseState.BinaryModulePaths)
            iss.ImportPSModule(new[] { path });

        // Import modules that have private functions natively
        foreach (var path in baseState.NativeImportModulePaths)
            iss.ImportPSModule(new[] { path });

        // Import additional type-specific modules (only these get parsed by PS)
        var modulesPath = Path.Combine(_apiBasePath, "Modules");
        if (Directory.Exists(modulesPath))
        {
            foreach (var moduleName in additionalModules)
            {
                if (_settings.Worker.SkipModules.Contains(moduleName, StringComparer.OrdinalIgnoreCase))
                    continue;
                var moduleDir = Path.Combine(modulesPath, moduleName);
                if (!Directory.Exists(moduleDir)) continue;

                var manifest = FindModuleManifest(moduleDir, moduleName);
                if (manifest != null)
                    iss.ImportPSModule(new[] { manifest });
            }
        }

        return iss;
    }

    /// <summary>
    /// Build an ISS using pre-parsed function definitions from an existing worker.
    /// Skips file I/O and AST parsing for script modules — functions are injected directly.
    /// Binary modules and modules with private functions still need native import.
    /// </summary>
    private InitialSessionState BuildClonedISS(ExportedModuleState state)
    {
        var iss = InitialSessionState.CreateDefault();
        if (OperatingSystem.IsWindows())
            iss.ExecutionPolicy = Microsoft.PowerShell.ExecutionPolicy.Bypass;

        RegisterSharedAssemblies(iss, "BuildClonedISS");

        // Copy environment variables
        foreach (System.Collections.DictionaryEntry env in Environment.GetEnvironmentVariables())
            iss.EnvironmentVariables.Add(new SessionStateVariableEntry((string)env.Key, env.Value, null));

        // Determine which modules need native import (they have private functions)
        var nativeModules = new HashSet<string>(state.NativeImportModulePaths.Select(Path.GetFileNameWithoutExtension)!,
            StringComparer.OrdinalIgnoreCase);

        // Inject pre-parsed functions — this is the big win, no .psm1 parsing needed.
        // Skip functions from modules that will be natively imported (they bring their own).
        foreach (var (name, definition, module) in state.Functions)
        {
            if (nativeModules.Contains(module))
                continue;
            try
            {
                iss.Commands.Add(new SessionStateFunctionEntry(name, definition));
            }
            catch
            {
                // Skip functions that can't be re-parsed (shouldn't happen with valid definitions)
            }
        }

        // Inject exported variables
        foreach (var (name, value) in state.Variables)
        {
            iss.Variables.Add(new SessionStateVariableEntry(name, value, null));
        }

        // Binary modules must still be imported natively (they contain compiled cmdlets)
        foreach (var path in state.BinaryModulePaths)
        {
            iss.ImportPSModule(new[] { path });
        }

        // Import modules that have private functions — cloning only captures exported
        // functions, so these modules need full import to preserve private function scope
        foreach (var path in state.NativeImportModulePaths)
        {
            iss.ImportPSModule(new[] { path });
        }

        return iss;
    }

    /// <summary>
    /// Find the best module manifest (.psd1) for a module directory.
    /// Prefers {ModuleName}.psd1 over other .psd1 files. Skips build.psd1.
    /// Returns null if no valid manifest found — the module will use its .psm1 via auto-import.
    /// </summary>
    private static string? FindModuleManifest(string moduleDir, string moduleName)
    {
        var manifests = Directory.GetFiles(moduleDir, "*.psd1", SearchOption.AllDirectories);
        // Filter out ModuleBuilder config files
        manifests = Array.FindAll(manifests, m =>
            !Path.GetFileName(m).Equals("build.psd1", StringComparison.OrdinalIgnoreCase));
        if (manifests.Length == 0) return null;
        return Array.Find(manifests, m =>
            Path.GetFileNameWithoutExtension(m).Equals(moduleName, StringComparison.OrdinalIgnoreCase))
            ?? manifests[0];
    }

    public void Dispose()
    {
        try { while (_httpPool.TryTake(out var w)) w.Dispose(); } catch (ObjectDisposedException) { }
        try { while (_bgPool.TryTake(out var w)) w.Dispose(); } catch (ObjectDisposedException) { }
        _httpPool.Dispose();
        _bgPool.Dispose();
    }
}

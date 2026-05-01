using System.Collections.Concurrent;
using System.Management.Automation.Runspaces;

namespace CRAFT.Services;

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

    private readonly ConcurrentDictionary<int, int> _workerFaults = new(); // workerId → consecutive fault count
    private const int MaxConsecutiveFaults = 3;

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

        var httpISS = BuildISS(isHttp: true);
        var bgISS = BuildISS(isHttp: false);
        var sw = System.Diagnostics.Stopwatch.StartNew();

        // First HTTP worker must init sequentially — warmup scripts set process-level state
        // (env vars, auth tokens, caches) that subsequent workers benefit from.
        var firstWorker = new PowerShellWorker(Interlocked.Increment(ref _nextId), httpISS, _logger);
        firstWorker.Initialize(_repo, _apiBasePath, _settings);
        if (_settings.Worker.WarmupScripts.Count > 0)
        {
            var warmSw = System.Diagnostics.Stopwatch.StartNew();
            firstWorker.Warmup(_settings);
            _logger.LogInformation("[System] Pre-warm completed in {Ms}ms", warmSw.ElapsedMilliseconds);
        }
        _httpPool.Add(firstWorker);

        // Remaining workers can init in parallel — ISS is a thread-safe template,
        // and each runspace gets its own isolated session state.
        var remaining = new List<(PowerShellWorker worker, bool isHttp)>();
        for (int i = 1; i < _httpPoolSize; i++)
            remaining.Add((new PowerShellWorker(Interlocked.Increment(ref _nextId), httpISS, _logger), true));
        for (int i = 0; i < _bgPoolSize; i++)
            remaining.Add((new PowerShellWorker(Interlocked.Increment(ref _nextId), bgISS, _logger), false));

        Parallel.ForEach(remaining, entry =>
        {
            entry.worker.Initialize(_repo, _apiBasePath, _settings);
        });

        foreach (var entry in remaining)
        {
            if (entry.isHttp) _httpPool.Add(entry.worker);
            else _bgPool.Add(entry.worker);
        }

        _logger.LogInformation("[System] Pool ready: {Http} HTTP + {Bg} BG workers in {Ms}ms",
            _httpPoolSize, _bgPoolSize, sw.ElapsedMilliseconds);
    }

    public PowerShellWorker? CheckoutHttp(TimeSpan timeout)
    {
        _httpPool.TryTake(out var w, timeout);
        return w;
    }

    public PowerShellWorker CheckoutBackground(CancellationToken ct) => _bgPool.Take(ct);

    public void Reclaim(PowerShellWorker worker, bool isHttp, bool faulted = false)
    {
        if (faulted)
        {
            var faults = _workerFaults.AddOrUpdate(worker.Id, 1, (_, c) => c + 1);
            if (faults >= MaxConsecutiveFaults)
            {
                _logger.LogWarning("[Pool] Worker W{Id} hit {Faults} consecutive faults, replacing", worker.Id, faults);
                _workerFaults.TryRemove(worker.Id, out _);
                worker.Dispose();
                var iss = BuildISS(isHttp: isHttp);
                worker = new PowerShellWorker(Interlocked.Increment(ref _nextId), iss, _logger);
                worker.Initialize(_repo, _apiBasePath, _settings);
            }
        }
        else
        {
            _workerFaults.TryRemove(worker.Id, out _); // Reset on success
        }

        if (isHttp) _httpPool.Add(worker); else _bgPool.Add(worker);
    }

    private InitialSessionState BuildISS(bool isHttp)
    {
        var iss = InitialSessionState.CreateDefault();
        if (OperatingSystem.IsWindows())
            iss.ExecutionPolicy = Microsoft.PowerShell.ExecutionPolicy.Bypass;

        // Copy environment variables into the runspace
        foreach (System.Collections.DictionaryEntry env in Environment.GetEnvironmentVariables())
            iss.EnvironmentVariables.Add(new SessionStateVariableEntry((string)env.Key, env.Value, null));

        // Import all modules via ISS
        var modulesPath = Path.Combine(_apiBasePath, "Modules");
        if (Directory.Exists(modulesPath))
        {
            foreach (var moduleDir in Directory.GetDirectories(modulesPath))
            {
                var moduleName = Path.GetFileName(moduleDir);
                if (_settings.Worker.SkipModules.Contains(moduleName, StringComparer.OrdinalIgnoreCase))
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

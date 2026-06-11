using System.Collections;
using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Management.Automation;
using System.Text.Json;

namespace Craft.Services;

public class PowerShellRunnerService : IDisposable
{
    private readonly ILogger<PowerShellRunnerService> _logger;
    private readonly PowerShellWorkerPool _pool;
    private readonly ScriptRepository _repo;
    private readonly WorkerSettings _workerSettings;
    private readonly AuthSettings _authSettings;

    // Static JsonSerializerOptions — allocated once, reused everywhere
    private static readonly JsonSerializerOptions s_jsonOptions = new() { WriteIndented = false };

    // Shared caches: keyed by name, each is a Synchronized Hashtable.
    // Used for cross-runspace state sharing (e.g. token caches).
    private static readonly ConcurrentDictionary<string, Hashtable> SharedCaches = new(StringComparer.OrdinalIgnoreCase);

    public PowerShellRunnerService(
        ILogger<PowerShellRunnerService> logger,
        PowerShellWorkerPool pool,
        ScriptRepository repo,
        CraftSettings settings)
    {
        _logger = logger;
        _pool = pool;
        _repo = repo;
        _workerSettings = settings.Worker;
        _authSettings = settings.Auth;
    }

    /// <summary>
    /// Returns a named shared cache (Synchronized Hashtable) for cross-runspace sharing.
    /// Creates the cache on first access. Thread-safe.
    /// </summary>
    public static Hashtable GetSharedCache(string name) =>
        SharedCaches.GetOrAdd(name, _ => Hashtable.Synchronized(new Hashtable()));

    /// <summary>
    /// Set a process-level environment variable from any worker runspace.
    /// The value is visible to ALL workers via $env:NAME because it calls
    /// Environment.SetEnvironmentVariable at the .NET level, bypassing any
    /// runspace-scoped isolation.
    /// <para>
    /// PS usage: [Craft.Services.PowerShellRunnerService]::SetProcessEnvVar('CIPP_TIMEZONE', 'America/New_York')
    /// </para>
    /// </summary>
    public static void SetProcessEnvVar(string name, string? value) =>
        Environment.SetEnvironmentVariable(name, value);

    /// <summary>
    /// Read a process-level environment variable. Convenience wrapper so callers
    /// don't need to reference [System.Environment] directly.
    /// </summary>
    public static string? GetProcessEnvVar(string name) =>
        Environment.GetEnvironmentVariable(name);

    /// <summary>
    /// Execute an HTTP request through the PS pipeline for endpoints not in the route table
    /// (e.g., "me"). The actual PS function invoked is Auth.MeEndpointHandler when set
    /// (with the endpoint name passed via Request.Params.CIPPEndpoint so the handler can
    /// dispatch internally); otherwise the endpoint name is invoked as the function directly.
    /// </summary>
    public async Task<ScriptResult> ExecuteHttpEndpoint(string endpoint, Hashtable request)
    {
        var sw = Stopwatch.StartNew();
        PowerShellWorker? worker = null;
        try
        {
            worker = _pool.CheckoutHttp(TimeSpan.FromSeconds(30));
            if (worker == null)
            {
                return new ScriptResult
                {
                    StatusCode = 503,
                    Body = JsonSerializer.Serialize(new { error = "Server busy, please retry" }, s_jsonOptions)
                };
            }

            // Resolve the PS function to invoke: configured wrapper (if set) or the endpoint itself
            var handlerFunction = !string.IsNullOrEmpty(_authSettings.MeEndpointHandler)
                ? _authSettings.MeEndpointHandler
                : endpoint;

            var triggerMetadata = new Hashtable
            {
                ["FunctionName"] = endpoint
            };

            var parameters = new Dictionary<string, object?>
            {
                ["Request"] = request,
                ["TriggerMetadata"] = triggerMetadata
            };

            WorkerMetricsBridge.RecordFunction(worker.Id, endpoint);

            using var cts = _workerSettings.HttpTimeoutSeconds > 0
                ? new CancellationTokenSource(TimeSpan.FromSeconds(_workerSettings.HttpTimeoutSeconds))
                : null;
            var results = await worker.InvokeAsync(handlerFunction, parameters, cts?.Token ?? default);

            foreach (var error in worker.Streams.Error)
                _logger.LogError("[API] PS error in {Function}: {Error}", endpoint, error.ToString());

            var response = ExtractResponse(results);
            sw.Stop();
            _logger.LogInformation("[HTTP] {Function} {StatusCode} {Ms}ms", endpoint, response.StatusCode, sw.ElapsedMilliseconds);

            // Process any orchestrator/queue triggers queued during execution
            await OrchestratorBridge.DrainPendingAsync();
            QueueBridge.DrainPending();

            return response;
        }
        catch (OperationCanceledException) when (sw.ElapsedMilliseconds > 0)
        {
            sw.Stop();
            _logger.LogWarning("[HTTP] {Endpoint} timed out after {Ms}ms (limit: {Limit}s)",
                endpoint, sw.ElapsedMilliseconds, _workerSettings.HttpTimeoutSeconds);
            return new ScriptResult
            {
                StatusCode = 504,
                Body = JsonSerializer.Serialize(new { error = $"Request timed out after {_workerSettings.HttpTimeoutSeconds}s" }, s_jsonOptions)
            };
        }
        catch (Exception ex)
        {
            sw.Stop();
            _logger.LogError(ex, "[HTTP] {Endpoint} failed {Ms}ms", endpoint, sw.ElapsedMilliseconds);
            return new ScriptResult
            {
                StatusCode = 500,
                Body = JsonSerializer.Serialize(new { error = ex.Message }, s_jsonOptions)
            };
        }
        finally
        {
            if (worker != null)
                _pool.Reclaim(worker, true);
        }
    }

    /// <summary>
    /// Discover all HTTP endpoint routes from the ScriptRepository.
    /// Returns route name -> function name mapping.
    /// </summary>
    public Dictionary<string, string> DiscoverHttpEndpoints()
    {
        var endpoints = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (route, funcName) in _repo.HttpRoutes)
        {
            endpoints[route] = funcName;
        }
        _logger.LogInformation("[System] {Count} API endpoints from ScriptRepository", endpoints.Count);
        return endpoints;
    }

    /// <summary>
    /// Execute an HTTP endpoint script with a live HttpContext.
    /// Uses HTTP pool worker with async invoke.
    /// </summary>
    public async Task<ScriptResult> ExecuteHttpScript(string route, HttpContext httpContext)
    {
        var request = await BuildRequestObject(httpContext);
        return await ExecuteHttpScriptInternal(route, request, isHttp: true);
    }

    /// <summary>
    /// Execute an HTTP endpoint script with a pre-captured request snapshot.
    /// Used for background cache refresh. Runs on the HTTP pool since HTTP
    /// endpoint functions require HTTP-specific modules.
    /// </summary>
    public async Task<ScriptResult> ExecuteHttpScript(string route, Hashtable requestSnapshot)
    {
        return await ExecuteHttpScriptInternal(route, requestSnapshot, isHttp: true);
    }

    private async Task<ScriptResult> ExecuteHttpScriptInternal(string route, Hashtable request, bool isHttp)
    {
        var sw = Stopwatch.StartNew();
        var entry = _repo.GetByRoute(route);
        if (entry == null)
        {
            return new ScriptResult
            {
                StatusCode = 404,
                Body = JsonSerializer.Serialize(new { error = $"Endpoint '{route}' not found" }, s_jsonOptions)
            };
        }

        PowerShellWorker? worker = null;
        var poolLabel = isHttp ? "HTTP" : "BG";
        try
        {
            if (isHttp)
            {
                worker = _pool.CheckoutHttp(TimeSpan.FromSeconds(30));
                if (worker == null)
                {
                    _logger.LogWarning("HTTP pool exhausted — no worker available within 30s for {Route}", route);
                    return new ScriptResult
                    {
                        StatusCode = 503,
                        Body = JsonSerializer.Serialize(new { error = "Server busy, please retry" }, s_jsonOptions)
                    };
                }
            }
            else
            {
                worker = _pool.CheckoutBackground(CancellationToken.None);
            }

            var triggerMetadata = new Hashtable
            {
                ["FunctionName"] = entry.FunctionName
            };

            var parameters = new Dictionary<string, object?>
            {
                ["Request"] = request,
                ["TriggerMetadata"] = triggerMetadata
            };

            // Set invocation context for traceability
            var invocation = new OperationContext.Invocation(entry.FunctionName)
            {
                WorkerId = $"W{worker.Id}",
                Category = "HTTP"
            };
            using var opScope = OperationContext.Set(invocation);
            WorkerMetricsBridge.RecordFunction(worker.Id, entry.FunctionName);
            _logger.LogInformation("[{Pool}] {InvocationId} {Function} starting on {Worker}",
                poolLabel, invocation.Id, entry.FunctionName, invocation.WorkerId);

            var timeoutSeconds = isHttp ? _workerSettings.HttpTimeoutSeconds : _workerSettings.BgTimeoutSeconds;
            using var cts = timeoutSeconds > 0
                ? new CancellationTokenSource(TimeSpan.FromSeconds(timeoutSeconds))
                : null;
            var results = await worker.InvokeAsync(entry.FunctionName, parameters, cts?.Token ?? default);

            foreach (var error in worker.Streams.Error)
                _logger.LogError("[API] {InvocationId} PS error in {Function}: {Error}",
                    invocation.Id, entry.FunctionName, error.ToString());
            foreach (var warning in worker.Streams.Warning)
                _logger.LogWarning("[API] {InvocationId} PS warning in {Function}: {Warning}",
                    invocation.Id, entry.FunctionName, warning.ToString());
            foreach (var info in worker.Streams.Information)
                _logger.LogInformation("[API] {InvocationId} PS {Function}: {Info}",
                    invocation.Id, entry.FunctionName, info.ToString());
            foreach (var debug in worker.Streams.Debug)
                _logger.LogDebug("[API] {InvocationId} PS debug in {Function}: {Debug}",
                    invocation.Id, entry.FunctionName, debug.ToString());
            foreach (var verbose in worker.Streams.Verbose)
                _logger.LogTrace("[API] {InvocationId} PS verbose in {Function}: {Verbose}",
                    invocation.Id, entry.FunctionName, verbose.ToString());

            var response = ExtractResponse(results);
            sw.Stop();
            _logger.LogInformation("[{Pool}] {InvocationId} {Function} {StatusCode} {Ms}ms",
                poolLabel, invocation.Id, entry.FunctionName, response.StatusCode, sw.ElapsedMilliseconds);

            // Process any orchestrator/queue triggers queued during execution
            await OrchestratorBridge.DrainPendingAsync();
            QueueBridge.DrainPending();

            return response;
        }
        catch (OperationCanceledException) when (sw.ElapsedMilliseconds > 0)
        {
            sw.Stop();
            var timeoutSeconds = isHttp ? _workerSettings.HttpTimeoutSeconds : _workerSettings.BgTimeoutSeconds;
            _logger.LogWarning("[{Pool}] {Function} timed out after {Ms}ms (limit: {Limit}s)",
                poolLabel, entry?.FunctionName ?? route, sw.ElapsedMilliseconds, timeoutSeconds);
            return new ScriptResult
            {
                StatusCode = 504,
                Body = JsonSerializer.Serialize(new { error = $"Request timed out after {timeoutSeconds}s" }, s_jsonOptions)
            };
        }
        catch (Exception ex)
        {
            sw.Stop();
            _logger.LogError(ex, "[{Pool}] {Function} failed {Ms}ms", poolLabel, entry?.FunctionName ?? route, sw.ElapsedMilliseconds);
            return new ScriptResult
            {
                StatusCode = 500,
                Body = JsonSerializer.Serialize(new { error = ex.Message }, s_jsonOptions)
            };
        }
        finally
        {
            if (worker != null)
                _pool.Reclaim(worker, isHttp);
        }
    }

    /// <summary>
    /// Drain pending orchestrator/queue triggers off the calling thread. Bridges are thread-safe
    /// concurrent queues, so multiple in-flight drain calls are fine — each TryDequeue serialises.
    /// </summary>
    private void DrainBridgesInBackground()
    {
        _ = Task.Run(async () =>
        {
            try
            {
                await OrchestratorBridge.DrainPendingAsync();
                QueueBridge.DrainPending();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[Scheduler] Background bridge drain failed");
            }
        });
    }

    /// <summary>
    /// Execute a script by function name (for scheduler / orchestrator). No HTTP context needed.
    /// Runs on the background pool.
    /// </summary>
    public async Task ExecuteScript(string functionName, Dictionary<string, object>? parameters = null)
    {
        var sw = Stopwatch.StartNew();
        var worker = _pool.CheckoutBackground(CancellationToken.None);

        // Set invocation context — inherits RunName from parent OperationContext if set by JobManager
        var parentRun = OperationContext.Current?.RunName;
        var parentFunction = OperationContext.Current?.Function;
        var invocation = new OperationContext.Invocation(functionName)
        {
            WorkerId = $"W{worker.Id}",
            RunName = parentRun,
            Category = "Job"
        };
        using var opScope = OperationContext.Set(invocation);
        // Show the job name (e.g. "CIPPDBCacheRun-Graph_tenant.com") rather than
        // the generic function name (e.g. "Invoke-CraftTask") in worker metrics
        WorkerMetricsBridge.RecordFunction(worker.Id, parentFunction ?? functionName);

        EventHandler<DataAddedEventArgs>? onError = null;
        EventHandler<DataAddedEventArgs>? onWarning = null;
        EventHandler<DataAddedEventArgs>? onInfo = null;
        EventHandler<DataAddedEventArgs>? onDebug = null;
        EventHandler<DataAddedEventArgs>? onVerbose = null;
        var exceptionOccurred = false;
        try
        {
            var resolvedName = _repo.GetByName(functionName)?.FunctionName ?? functionName;

            _logger.LogInformation("[Scheduler] {InvocationId} {Function} starting on {Worker}{Run}",
                invocation.Id, functionName, invocation.WorkerId,
                parentRun != null ? $" run:{parentRun}" : "");

            // Wire up real-time stream logging — must unsubscribe in finally
            onError = (sender, args) =>
            {
                var records = (PSDataCollection<ErrorRecord>)sender!;
                _logger.LogError("[Scheduler] {InvocationId} PS error in {Function}: {Error}",
                    invocation.Id, functionName, records[args.Index].ToString());
            };
            onWarning = (sender, args) =>
            {
                var records = (PSDataCollection<WarningRecord>)sender!;
                _logger.LogWarning("[Scheduler] {InvocationId} PS warning in {Function}: {Warning}",
                    invocation.Id, functionName, records[args.Index].ToString());
            };
            onInfo = (sender, args) =>
            {
                var records = (PSDataCollection<InformationRecord>)sender!;
                _logger.LogInformation("[Scheduler] {InvocationId} PS {Function}: {Info}",
                    invocation.Id, functionName, records[args.Index].ToString());
            };
            onDebug = (sender, args) =>
            {
                var records = (PSDataCollection<DebugRecord>)sender!;
                _logger.LogDebug("[Scheduler] {InvocationId} PS debug in {Function}: {Debug}",
                    invocation.Id, functionName, records[args.Index].ToString());
            };
            onVerbose = (sender, args) =>
            {
                var records = (PSDataCollection<VerboseRecord>)sender!;
                _logger.LogTrace("[Scheduler] {InvocationId} PS verbose in {Function}: {Verbose}",
                    invocation.Id, functionName, records[args.Index].ToString());
            };
            worker.Streams.Error.DataAdded += onError;
            worker.Streams.Warning.DataAdded += onWarning;
            worker.Streams.Information.DataAdded += onInfo;
            worker.Streams.Debug.DataAdded += onDebug;
            worker.Streams.Verbose.DataAdded += onVerbose;

            var psParams = new Dictionary<string, object?>();
            if (parameters != null)
                foreach (var p in parameters)
                    psParams[p.Key] = UnwrapJsonElement(p.Value);

            using var cts = _workerSettings.BgTimeoutSeconds > 0
                ? new CancellationTokenSource(TimeSpan.FromSeconds(_workerSettings.BgTimeoutSeconds))
                : null;
            await worker.InvokeAsync(resolvedName, psParams, cts?.Token ?? default);

            sw.Stop();
            _logger.LogInformation("[Scheduler] {InvocationId} {Function} completed {Ms}ms",
                invocation.Id, functionName, sw.ElapsedMilliseconds);
        }
        catch (OperationCanceledException) when (sw.ElapsedMilliseconds > 0)
        {
            sw.Stop();
            exceptionOccurred = true;
            _logger.LogWarning("[Scheduler] {InvocationId} {Function} timed out after {Ms}ms (limit: {Limit}s)",
                invocation.Id, functionName, sw.ElapsedMilliseconds, _workerSettings.BgTimeoutSeconds);
            throw new TimeoutException($"Background job '{functionName}' timed out after {_workerSettings.BgTimeoutSeconds}s");
        }
        catch (Exception ex)
        {
            sw.Stop();
            exceptionOccurred = true;
            _logger.LogError(ex, "[Scheduler] {InvocationId} {Function} failed {Ms}ms",
                invocation.Id, functionName, sw.ElapsedMilliseconds);
            throw;  // Let callers (DispatchSingleTask, JobManager, SchedulerService) handle the failure
        }
        finally
        {
            if (onError != null) worker.Streams.Error.DataAdded -= onError;
            if (onWarning != null) worker.Streams.Warning.DataAdded -= onWarning;
            if (onInfo != null) worker.Streams.Information.DataAdded -= onInfo;
            if (onDebug != null) worker.Streams.Debug.DataAdded -= onDebug;
            if (onVerbose != null) worker.Streams.Verbose.DataAdded -= onVerbose;
            _pool.Reclaim(worker, isHttp: false, faulted: exceptionOccurred);
        }

        // Worker has been returned to the pool — drain any orchestrator/queue triggers
        // the script enqueued in the background so the next job can grab the worker now
        // instead of waiting for child-run table writes.
        DrainBridgesInBackground();
    }

    /// <summary>
    /// Execute a script on the background pool and capture its output stream as a string.
    /// Used by OrchestratorService for planner scripts that return JSON task lists.
    /// </summary>
    public async Task<string> ExecuteScriptWithOutput(string functionName, Dictionary<string, object>? parameters = null)
    {
        var sw = Stopwatch.StartNew();
        var worker = _pool.CheckoutBackground(CancellationToken.None);

        // Set invocation context — inherits RunName from parent OperationContext if set by JobManager
        var parentRun = OperationContext.Current?.RunName;
        var parentFunction = OperationContext.Current?.Function;
        var invocation = new OperationContext.Invocation(functionName)
        {
            WorkerId = $"W{worker.Id}",
            RunName = parentRun,
            Category = "Planner"
        };
        using var opScope = OperationContext.Set(invocation);
        WorkerMetricsBridge.RecordFunction(worker.Id, parentFunction ?? functionName);
        EventHandler<DataAddedEventArgs>? onError = null;
        EventHandler<DataAddedEventArgs>? onInfo = null;
        EventHandler<DataAddedEventArgs>? onDebug = null;
        EventHandler<DataAddedEventArgs>? onVerbose = null;
        try
        {
            var resolvedName = _repo.GetByName(functionName)?.FunctionName ?? functionName;

            _logger.LogInformation("[Planner] {InvocationId} {Function} starting on {Worker}",
                invocation.Id, functionName, invocation.WorkerId);

            // Wire up real-time stream logging — must unsubscribe in finally
            onError = (sender, args) =>
            {
                var records = (PSDataCollection<ErrorRecord>)sender!;
                _logger.LogError("[Planner] {InvocationId} PS error in {Function}: {Error}",
                    invocation.Id, functionName, records[args.Index].ToString());
            };
            onInfo = (sender, args) =>
            {
                var records = (PSDataCollection<InformationRecord>)sender!;
                _logger.LogInformation("[Planner] {InvocationId} PS {Function}: {Info}",
                    invocation.Id, functionName, records[args.Index].ToString());
            };
            onDebug = (sender, args) =>
            {
                var records = (PSDataCollection<DebugRecord>)sender!;
                _logger.LogDebug("[Planner] {InvocationId} PS debug in {Function}: {Debug}",
                    invocation.Id, functionName, records[args.Index].ToString());
            };
            onVerbose = (sender, args) =>
            {
                var records = (PSDataCollection<VerboseRecord>)sender!;
                _logger.LogTrace("[Planner] {InvocationId} PS verbose in {Function}: {Verbose}",
                    invocation.Id, functionName, records[args.Index].ToString());
            };
            worker.Streams.Error.DataAdded += onError;
            worker.Streams.Information.DataAdded += onInfo;
            worker.Streams.Debug.DataAdded += onDebug;
            worker.Streams.Verbose.DataAdded += onVerbose;

            var psParams = new Dictionary<string, object?>();
            if (parameters != null)
                foreach (var p in parameters)
                    psParams[p.Key] = p.Value;

            using var cts = _workerSettings.BgTimeoutSeconds > 0
                ? new CancellationTokenSource(TimeSpan.FromSeconds(_workerSettings.BgTimeoutSeconds))
                : null;
            var results = await worker.InvokeAsync(resolvedName, psParams, cts?.Token ?? default);

            sw.Stop();
            _logger.LogInformation("[Planner] {InvocationId} {Function} completed {Ms}ms",
                invocation.Id, functionName, sw.ElapsedMilliseconds);
            var output = string.Join("\n", (results ?? new Collection<PSObject>()).Select(r => r?.ToString() ?? ""));
            // Drain triggered child orchestrators/queue commands after returning — they should
            // not block the planner's caller. (Note: Reclaim still happens in finally below.)
            DrainBridgesInBackground();
            return output;
        }
        catch (OperationCanceledException) when (sw.ElapsedMilliseconds > 0)
        {
            sw.Stop();
            _logger.LogWarning("[Planner] {InvocationId} {Function} timed out after {Ms}ms (limit: {Limit}s)",
                invocation.Id, functionName, sw.ElapsedMilliseconds, _workerSettings.BgTimeoutSeconds);
            throw new TimeoutException($"Planner script '{functionName}' timed out after {_workerSettings.BgTimeoutSeconds}s");
        }
        catch (Exception ex)
        {
            sw.Stop();
            _logger.LogError(ex, "[Planner] {InvocationId} {Function} failed {Ms}ms",
                invocation.Id, functionName, sw.ElapsedMilliseconds);
            throw;
        }
        finally
        {
            if (onError != null) worker.Streams.Error.DataAdded -= onError;
            if (onInfo != null) worker.Streams.Information.DataAdded -= onInfo;
            if (onDebug != null) worker.Streams.Debug.DataAdded -= onDebug;
            if (onVerbose != null) worker.Streams.Verbose.DataAdded -= onVerbose;
            _pool.Reclaim(worker, isHttp: false);
        }
    }

    /// <summary>
    /// Find a script by command name. Checks ScriptRepository (standalone files)
    /// first, then falls back to checking if it exists as a module function.
    /// Returns the function/command name if found, null otherwise.
    /// </summary>
    public string? FindScript(string command)
    {
        var entry = _repo.GetByName(command);
        if (entry != null) return entry.FunctionName;

        // Check if the command exists as a function in the loaded modules
        if (_repo.IsModuleFunction(command)) return command;

        return null;
    }

    // ─── Request building (carried over from previous implementation) ───

    private static async Task<Hashtable> BuildRequestObject(HttpContext httpContext)
    {
        return await BuildRequestFromParts(httpContext.Request);
    }

    /// <summary>
    /// Build a snapshot of the request data that doesn't depend on HttpContext.
    /// Safe to use after the response has been sent.
    /// </summary>
    public static async Task<Hashtable> SnapshotRequest(HttpContext httpContext)
    {
        return await BuildRequestFromParts(httpContext.Request);
    }

    private static async Task<Hashtable> BuildRequestFromParts(HttpRequest httpRequest)
    {
        var query = new Hashtable(StringComparer.OrdinalIgnoreCase);
        foreach (var q in httpRequest.Query)
            query[q.Key] = q.Value.ToString();

        // Headers stored lowercase to match how CIPP's PS code accesses them via dot syntax
        // ($Request.Headers.'x-ms-client-principal'). PowerShell dot-property access on
        // a Hashtable does NOT respect StringComparer.OrdinalIgnoreCase consistently —
        // it can return $null if stored case differs from the requested case. The Hashtable
        // comparer is still case-insensitive for explicit indexer access ($h['Key']).
        var headers = new Hashtable(StringComparer.OrdinalIgnoreCase);
        foreach (var h in httpRequest.Headers)
            headers[h.Key.ToLowerInvariant()] = h.Value.ToString();

        object? body = null;
        if (httpRequest.ContentLength > 0 || httpRequest.ContentType != null)
        {
            httpRequest.EnableBuffering();
            using var reader = new StreamReader(httpRequest.Body, leaveOpen: true);
            var bodyText = await reader.ReadToEndAsync();
            httpRequest.Body.Position = 0;

            if (!string.IsNullOrWhiteSpace(bodyText))
            {
                try
                {
                    using var doc = JsonDocument.Parse(bodyText);
                    body = JsonElementToObject(doc.RootElement);
                }
                catch
                {
                    body = bodyText;
                }
            }
        }

        var routeParams = new Hashtable(StringComparer.OrdinalIgnoreCase);
        foreach (var rv in httpRequest.RouteValues)
        {
            if (rv.Value != null)
                routeParams[rv.Key] = rv.Value.ToString();
        }
        if (routeParams.ContainsKey("endpoint") && !routeParams.ContainsKey("CIPPEndpoint"))
            routeParams["CIPPEndpoint"] = routeParams["endpoint"];

        // Synthesize x-ms-original-url if not present (Azure Functions injects this;
        // ASP.NET Core doesn't). Many PowerShell functions rely on it.
        // Force https — behind a reverse proxy (Docker/App Service) the inner scheme is http.
        var scheme = httpRequest.Headers.TryGetValue("X-Forwarded-Proto", out var proto) && proto.Count > 0
            ? proto[0]!
            : httpRequest.Scheme;
        if (string.Equals(scheme, "http", StringComparison.OrdinalIgnoreCase))
            scheme = "https";
        var fullUrl = $"{scheme}://{httpRequest.Host}{httpRequest.Path}{httpRequest.QueryString}";
        if (!headers.ContainsKey("x-ms-original-url"))
            headers["x-ms-original-url"] = fullUrl;

        return new Hashtable(StringComparer.OrdinalIgnoreCase)
        {
            ["Method"] = httpRequest.Method,
            ["Url"] = fullUrl,
            ["Query"] = query,
            ["Headers"] = headers,
            ["Body"] = body,
            ["Params"] = routeParams
        };
    }

    // ─── Response extraction (carried over) ───

    private static object? JsonElementToObject(JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                var pso = new PSObject();
                foreach (var prop in element.EnumerateObject())
                    pso.Properties.Add(new PSNoteProperty(prop.Name, JsonElementToObject(prop.Value)));
                return pso;
            case JsonValueKind.Array:
                var list = new List<object?>();
                foreach (var item in element.EnumerateArray())
                    list.Add(JsonElementToObject(item));
                return list.ToArray();
            case JsonValueKind.String:
                return element.GetString();
            case JsonValueKind.Number:
                if (element.TryGetInt64(out var l)) return l;
                return element.GetDouble();
            case JsonValueKind.True:
                return true;
            case JsonValueKind.False:
                return false;
            default:
                return null;
        }
    }

    private ScriptResult ExtractResponse(Collection<PSObject>? results)
    {
        if (results == null || results.Count == 0)
        {
            return new ScriptResult { StatusCode = 200, Body = "null" };
        }
        foreach (var result in results.Reverse())
        {
            if (result == null) continue;

            int? statusCode = null;
            object? body = null;
            bool found = false;

            if (result.BaseObject is Hashtable ht)
            {
                if (ht.ContainsKey("StatusCode") || ht.ContainsKey("Body"))
                {
                    statusCode = ParseStatusCode(ht.ContainsKey("StatusCode") ? ht["StatusCode"] : null);
                    body = ht.ContainsKey("Body") ? ht["Body"] : null;
                    found = true;
                }
            }
            else
            {
                var scProp = result.Properties["StatusCode"];
                var bodyProp = result.Properties["Body"];
                if (scProp != null || bodyProp != null)
                {
                    statusCode = ParseStatusCode(scProp?.Value);
                    body = bodyProp?.Value;
                    found = true;
                }
            }

            if (found)
            {
                string jsonBody;
                if (body is string strBody)
                {
                    try
                    {
                        using var doc = JsonDocument.Parse(strBody);
                        jsonBody = strBody;
                    }
                    catch
                    {
                        jsonBody = JsonSerializer.Serialize(body, s_jsonOptions);
                    }
                }
                else
                {
                    jsonBody = ConvertPsObjectToJson(body);
                }
                return new ScriptResult { StatusCode = statusCode ?? 200, Body = jsonBody };
            }
        }

        var fallbackBody = results.Count > 0
            ? ConvertPsObjectToJson(results.Last().BaseObject)
            : "{}";
        return new ScriptResult { StatusCode = 200, Body = fallbackBody };
    }

    private static int ParseStatusCode(object? value)
    {
        if (value == null) return 200;
        if (value is int intCode) return intCode;
        if (value is Enum enumCode) return (int)Convert.ChangeType(enumCode, typeof(int));
        if (int.TryParse(value.ToString(), out var parsed)) return parsed;
        return 200;
    }

    // ─── JSON serialization (carried over, with static JsonSerializerOptions) ───

    private static string ConvertPsObjectToJson(object? obj)
    {
        if (obj == null) return "null";

        if (obj is PSObject pso)
        {
            if (pso.BaseObject == null || pso.BaseObject is PSCustomObject)
            {
                if (!pso.Properties.Any()) return "null";
            }

            if (pso.BaseObject is IDictionary or IList or string or int or long or double or float or bool or decimal)
            {
                obj = pso.BaseObject;
            }
            else
            {
                var dict = new Dictionary<string, object?>();
                foreach (var prop in pso.Properties)
                {
                    try { dict[prop.Name] = UnwrapPsValue(prop.Value); }
                    catch { }
                }
                return JsonSerializer.Serialize(dict, s_jsonOptions);
            }
        }

        if (obj is IDictionary ht)
        {
            var dict = new Dictionary<string, object?>();
            foreach (DictionaryEntry entry in ht)
                dict[entry.Key.ToString()!] = UnwrapPsValue(entry.Value);
            return JsonSerializer.Serialize(dict, s_jsonOptions);
        }

        if (obj is IEnumerable enumerable and not string)
        {
            var list = new List<object?>();
            foreach (var item in enumerable)
                list.Add(UnwrapPsValue(item));
            return JsonSerializer.Serialize(list, s_jsonOptions);
        }

        return JsonSerializer.Serialize(obj, s_jsonOptions);
    }

    private static object? UnwrapPsValue(object? value)
    {
        if (value == null) return null;

        if (value is PSObject pso)
        {
            if (pso.BaseObject == null || pso.BaseObject is PSCustomObject)
                return pso.Properties.Any() ? UnwrapPsProperties(pso) : null;

            if (pso.BaseObject is string or int or long or double or float or bool or decimal)
                return pso.BaseObject;

            if (pso.BaseObject is IDictionary htInner)
            {
                var d = new Dictionary<string, object?>();
                foreach (DictionaryEntry entry in htInner)
                    d[entry.Key.ToString()!] = UnwrapPsValue(entry.Value);
                return d;
            }

            if (pso.BaseObject is IEnumerable enumInner and not string)
            {
                var list = new List<object?>();
                foreach (var item in enumInner)
                    list.Add(UnwrapPsValue(item));
                return list;
            }

            return UnwrapPsProperties(pso);
        }

        if (value is IDictionary ht)
        {
            var dict = new Dictionary<string, object?>();
            foreach (DictionaryEntry entry in ht)
                dict[entry.Key.ToString()!] = UnwrapPsValue(entry.Value);
            return dict;
        }

        if (value is IEnumerable enumerable and not string and not IDictionary)
        {
            var list = new List<object?>();
            foreach (var item in enumerable)
                list.Add(UnwrapPsValue(item));
            return list;
        }

        return value;
    }

    private static Dictionary<string, object?> UnwrapPsProperties(PSObject pso)
    {
        var dict = new Dictionary<string, object?>();
        foreach (var prop in pso.Properties)
        {
            try { dict[prop.Name] = UnwrapPsValue(prop.Value); }
            catch { }
        }
        return dict;
    }

    /// <summary>
    /// Converts System.Text.Json.JsonElement values to native .NET types so PowerShell
    /// can bind them properly (e.g. bool for [switch] parameters).
    /// </summary>
    private static object? UnwrapJsonElement(object? value)
    {
        if (value is JsonElement je)
        {
            return je.ValueKind switch
            {
                JsonValueKind.True => true,
                JsonValueKind.False => false,
                JsonValueKind.Number => je.TryGetInt64(out var l) ? l : (object)je.GetDouble(),
                JsonValueKind.String => je.GetString(),
                JsonValueKind.Null => null,
                _ => je.GetRawText()
            };
        }
        return value;
    }

    public void Dispose()
    {
        _pool.Dispose();
        GC.SuppressFinalize(this);
    }
}

public class ScriptResult
{
    public int StatusCode { get; set; } = 200;
    public string Body { get; set; } = "{}";
}

using System.Collections;
using System.Diagnostics;
using System.Text.Json;
using Craft.Caching;
using Craft.Orchestration;
using Craft.Services;

namespace Craft.Hosting.Endpoints;

/// <summary>
/// Dispatches <c>/API/{endpoint}</c> to the PowerShell function discovered for that route.
/// <para>
/// This is the hot path of the whole runtime: it owns the response cache (including
/// stale-while-revalidate), the concurrency counter used by diagnostics, and the post-response trigger
/// handling that lets a short HTTP handler kick off long-running background work.
/// </para>
/// </summary>
public static class PowerShellDispatchEndpoint
{
    private static readonly string[] DispatchMethods = ["GET", "POST", "PUT", "DELETE", "PATCH"];

    /// <summary>
    /// Only GETs to <c>List*</c> endpoints are cached. The naming convention is the contract: a
    /// handler named <c>List…</c> is asserting it is a side-effect-free read.
    /// </summary>
    public static bool IsCacheableRead(string method, string endpoint) =>
        method == "GET" && endpoint.StartsWith("List", StringComparison.OrdinalIgnoreCase);

    /// <summary>Maps the catch-all PowerShell dispatch route.</summary>
    public static WebApplication MapCraftPowerShellDispatch(
        this WebApplication app,
        IReadOnlyDictionary<string, string> endpoints,
        RequestCounter activeRequests,
        ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(app);
        ArgumentNullException.ThrowIfNull(endpoints);
        ArgumentNullException.ThrowIfNull(activeRequests);
        ArgumentNullException.ThrowIfNull(logger);

        var psRunner = app.Services.GetRequiredService<PowerShellRunnerService>();
        var cache = app.Services.GetRequiredService<CacheService>();
        var orchestrator = app.Services.GetRequiredService<OrchestratorService>();
        var jobManager = app.Services.GetRequiredService<JobManager>();

        app.MapMethods("/API/{endpoint}", DispatchMethods, async (HttpContext context, string endpoint) =>
        {
            var requestSw = Stopwatch.StartNew();
            var concurrent = activeRequests.Increment();

            try
            {
                if (!endpoints.ContainsKey(endpoint))
                {
                    context.Response.StatusCode = 404;
                    context.Response.ContentType = "application/json";
                    await context.Response.WriteAsync($"{{\"error\":\"Endpoint '{endpoint}' not found\"}}");
                    return;
                }

                logger.LogInformation("[HTTP] {Method} /API/{Endpoint} start concurrent={Concurrent}",
                    context.Request.Method, endpoint, concurrent);

                ApplyRequestedInvalidation(context, cache);

                var isReadEndpoint = IsCacheableRead(context.Request.Method, endpoint);

                // Computed once on the read path and reused for the write-back below.
                string? cacheKey = null;

                if (isReadEndpoint)
                {
                    var cached = await TryReadCacheAsync(context, cache, endpoint, logger);
                    cacheKey = cached.Key;

                    if (cached.Response is { } hit)
                    {
                        await ServeCachedAsync(context, cache, psRunner, orchestrator, logger,
                            endpoint, cached.Key!, hit, requestSw);
                        return;
                    }
                }

                logger.LogInformation("[HTTP] /API/{Endpoint} executing concurrent={Concurrent}",
                    endpoint, concurrent);

                var result = await psRunner.ExecuteHttpScript(endpoint, context);

                // Writes can invalidate anything, so they clear the cache regardless of endpoint name.
                if (context.Request.Method != "GET") InvalidateForWrite(context, cache);

                if (isReadEndpoint && cacheKey is not null && result.StatusCode is >= 200 and < 400)
                    await cache.Set(cacheKey, result);

                context.Response.StatusCode = result.StatusCode;
                context.Response.ContentType = HandlerHeaders.ResolveContentType(result.ContentType);
                HandlerHeaders.Apply(context.Response, result.Headers);
                context.Response.Headers["X-Cache"] = "MISS";
                context.Response.Headers["X-Request-Duration"] = $"{requestSw.ElapsedMilliseconds}ms";

                if (result.StatusCode == 200)
                {
                    HandleOrchestratorTrigger(result.Body, psRunner, orchestrator, logger, "[API] Orchestrator triggered");
                    HandleScriptTrigger(result.Body, psRunner, jobManager, logger);
                    result = await HandleCancelTriggerAsync(result, orchestrator, logger, context);
                }

                if (HandlerHeaders.AllowsBody(result.StatusCode))
                    await context.Response.WriteAsync(result.Body);
            }
            finally
            {
                var remaining = activeRequests.Decrement();
                requestSw.Stop();
                logger.LogDebug("[HTTP] /API/{Endpoint} done {ElapsedMs}ms remaining={Remaining}",
                    endpoint, requestSw.ElapsedMilliseconds, remaining);
            }
        });

        return app;
    }

    /// <summary>Honours an explicit <c>?{InvalidateParam}=true</c> on the request.</summary>
    private static void ApplyRequestedInvalidation(HttpContext context, CacheService cache)
    {
        if (!context.Request.Query.ContainsKey(cache.InvalidateParam)) return;
        if (!string.Equals(context.Request.Query[cache.InvalidateParam], "true", StringComparison.OrdinalIgnoreCase))
            return;

        InvalidateScopedOrAll(context, cache);
    }

    private static void InvalidateForWrite(HttpContext context, CacheService cache) =>
        InvalidateScopedOrAll(context, cache);

    /// <summary>
    /// Invalidates just the caller's scope when one is present, otherwise everything — without a scope
    /// there is no way to know which entries a write affected.
    /// </summary>
    private static void InvalidateScopedOrAll(HttpContext context, CacheService cache)
    {
        var scopeParam = cache.ScopeParam;
        var scopeValue = !string.IsNullOrEmpty(scopeParam)
            ? context.Request.Query[scopeParam].ToString()
            : "";

        if (!string.IsNullOrEmpty(scopeValue)) cache.InvalidateByScope(scopeValue);
        else cache.InvalidateAll();
    }

    private static async Task<(string? Key, CachedResponse? Response)> TryReadCacheAsync(
        HttpContext context, CacheService cache, string endpoint, ILogger logger)
    {
        var profiling = CacheProfiler.Enabled;
        var t0 = profiling ? Stopwatch.GetTimestamp() : 0;

        var userRoleHash = CacheService.GetUserRoleHash(context);
        var t1 = profiling ? Stopwatch.GetTimestamp() : 0;

        var key = cache.BuildCacheKey(endpoint, context.Request.Query, userRoleHash);
        var t2 = profiling ? Stopwatch.GetTimestamp() : 0;

        var cached = await cache.Get(key, endpoint);

        if (profiling)
        {
            CacheProfiler.SetLogger(logger);
            CacheProfiler.RecordRequest(t1 - t0, t2 - t1, Stopwatch.GetTimestamp() - t2, cached != null);
        }

        return (key, cached);
    }

    private static async Task ServeCachedAsync(
        HttpContext context, CacheService cache, PowerShellRunnerService psRunner,
        OrchestratorService orchestrator, ILogger logger,
        string endpoint, string cacheKey, CachedResponse cached, Stopwatch requestSw)
    {
        cache.Touch(cacheKey);
        var ttl = cache.GetTtl(endpoint);

        // Snapshot the request BEFORE writing the response: HttpContext is not reliable once
        // Response.WriteAsync has completed, and the background refresh needs the request state.
        Hashtable? requestSnapshot = null;
        if (cache.TryStartRefresh(cacheKey))
            requestSnapshot = await PowerShellRunnerService.SnapshotRequest(context);

        context.Response.StatusCode = cached.Result.StatusCode;
        context.Response.ContentType = HandlerHeaders.ResolveContentType(cached.Result.ContentType);
        HandlerHeaders.Apply(context.Response, cached.Result.Headers);
        context.Response.Headers["X-Cache"] = cached.IsStale ? "HIT-STALE" : "HIT";
        context.Response.Headers["X-Cache-Age"] = $"{cached.Age.TotalSeconds:F0}s";
        context.Response.Headers["X-Cache-TTL"] = $"{ttl.TotalSeconds:F0}s";
        context.Response.Headers["X-Request-Duration"] = $"{requestSw.ElapsedMilliseconds}ms";

        if (HandlerHeaders.AllowsBody(cached.Result.StatusCode))
            await context.Response.WriteAsync(cached.Result.Body);

        if (requestSnapshot is null) return;

        StartBackgroundRefresh(cache, psRunner, orchestrator, logger, endpoint, cacheKey, requestSnapshot);
    }

    /// <summary>
    /// Stale-while-revalidate: the caller already has its (stale) response, so refresh off the request
    /// path. Deliberately fire-and-forget — nothing is waiting on it.
    /// </summary>
    private static void StartBackgroundRefresh(
        CacheService cache, PowerShellRunnerService psRunner, OrchestratorService orchestrator,
        ILogger logger, string endpoint, string cacheKey, Hashtable requestSnapshot)
    {
        var generation = cache.Generation;

        _ = Task.Run(async () =>
        {
            try
            {
                await cache.WaitForBgRefreshSlot(endpoint);
                var fresh = await psRunner.ExecuteHttpScript(endpoint, requestSnapshot);

                if (fresh.StatusCode is >= 200 and < 400)
                {
                    // Generation check: an invalidation may have landed while this was running, and
                    // writing back then would resurrect data the invalidation was meant to drop.
                    cache.SetIfSameGeneration(cacheKey, fresh, generation);

                    HandleOrchestratorTrigger(fresh.Body, psRunner, orchestrator, logger,
                        "[API] Orchestrator triggered via bg refresh");
                }
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Background refresh failed: /API/{Endpoint}", endpoint);
            }
            finally
            {
                cache.ReleaseBgRefreshSlot(endpoint);
                cache.FinishRefresh(cacheKey);
            }
        });
    }

    private static void HandleOrchestratorTrigger(
        string body, PowerShellRunnerService psRunner, OrchestratorService orchestrator,
        ILogger logger, string logPrefix)
    {
        if (!ResponseTriggers.MayContain(body, ResponseTriggers.OrchestratorMarker)) return;

        try
        {
            if (ResponseTriggers.ParseOrchestrator(body) is not { } trigger) return;

            var plannerPath = psRunner.FindScript(trigger.PlannerScript);
            var taskPath = psRunner.FindScript(trigger.TaskScript);
            if (plannerPath is null || taskPath is null) return;

            _ = orchestrator.StartOrResumeRun(
                trigger.Command, plannerPath, taskPath, trigger.Priority, CancellationToken.None);

            logger.LogInformation("{Prefix}: {Command} P{Priority}",
                logPrefix, trigger.Command, trigger.Priority);
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException)
        {
            logger.LogWarning(ex, "Failed to parse orchestrator trigger from response");
        }
    }

    private static void HandleScriptTrigger(
        string body, PowerShellRunnerService psRunner, JobManager jobManager, ILogger logger)
    {
        if (!ResponseTriggers.MayContain(body, ResponseTriggers.ScriptMarker)) return;

        try
        {
            if (ResponseTriggers.ParseScript(body) is not { } trigger) return;

            if (psRunner.FindScript(trigger.Command) is null)
            {
                logger.LogWarning("[API] Script not found for trigger: {Command}", trigger.Command);
                return;
            }

            jobManager.Enqueue(trigger.Command, trigger.Priority, async _ =>
                await psRunner.ExecuteScript(trigger.Command));

            logger.LogInformation("[API] Script triggered: {Command} P{Priority}",
                trigger.Command, trigger.Priority);
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException)
        {
            logger.LogWarning(ex, "Failed to parse script trigger from response");
        }
    }

    /// <summary>
    /// Cancellation is the one trigger that rewrites the response, because the caller needs to know
    /// whether the run was actually found.
    /// </summary>
    private static async Task<ScriptResult> HandleCancelTriggerAsync(
        ScriptResult result, OrchestratorService orchestrator, ILogger logger, HttpContext context)
    {
        if (!ResponseTriggers.MayContain(result.Body, ResponseTriggers.CancelMarker)) return result;

        try
        {
            if (ResponseTriggers.ParseCancel(result.Body) is not { } trigger) return result;

            var (found, cancelledCount) = await orchestrator.CancelRunAsync(trigger.Command);

            // Callers pass the bare name; the registered run is the cmdlet that starts it.
            if (!found)
                (found, cancelledCount) = await orchestrator.CancelRunAsync($"Start-{trigger.Command}");

            if (found)
            {
                logger.LogInformation("[API] Run cancelled: {Command} ({Cancelled} pending tasks)",
                    trigger.Command, cancelledCount);

                result = new ScriptResult
                {
                    StatusCode = 200,
                    Body = JsonSerializer.Serialize(new
                    {
                        name = trigger.Command,
                        cancelled = cancelledCount,
                        message = $"Cancelled {cancelledCount} pending tasks",
                    }),
                };
            }
            else
            {
                result = new ScriptResult
                {
                    StatusCode = 404,
                    Body = JsonSerializer.Serialize(new { error = $"No run found for '{trigger.Command}'" }),
                };
            }

            context.Response.StatusCode = result.StatusCode;
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException)
        {
            logger.LogWarning(ex, "Failed to parse cancel trigger from response");
        }

        return result;
    }
}

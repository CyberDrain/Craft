using System.Collections;
using System.Diagnostics;
using Craft.Caching;
using Craft.Services;

namespace Craft.Hosting.Endpoints;

/// <summary>
/// Dispatches <c>/API/{endpoint}</c> to the PowerShell function discovered for that route.
/// <para>
/// This is the hot path of the whole runtime: it owns the response cache (including
/// stale-while-revalidate) and the concurrency counter used by diagnostics.
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

                cache.ApplyRequestedInvalidation(context);

                // Two separate gates. IsCacheableRead is about the handler — does its name promise a
                // side-effect-free read. The policy is about this request — is caching it worthwhile
                // and safe (see ResponseCachePolicy). Both must agree before the cache is touched at
                // all, so an excluded request neither serves from nor writes to it.
                var useCache = IsCacheableRead(context.Request.Method, endpoint);
                string? cacheBypassReason = null;

                if (useCache && !cache.Policy.Allows(endpoint, context.Request, out cacheBypassReason))
                {
                    useCache = false;   // still a read — just not one this cache should hold

                    logger.LogDebug("[HTTP] /API/{Endpoint} cache bypassed: {Reason}",
                        endpoint, cacheBypassReason);
                }

                // Computed once on the read path and reused for the write-back below.
                string? cacheKey = null;

                if (useCache)
                {
                    var cached = await TryReadCacheAsync(context, cache, endpoint, logger);
                    cacheKey = cached.Key;

                    if (cached.Response is { } hit)
                    {
                        await ServeCachedAsync(context, cache, psRunner, logger,
                            endpoint, cached.Key!, hit, requestSw);
                        return;
                    }
                }

                logger.LogInformation("[HTTP] /API/{Endpoint} executing concurrent={Concurrent}",
                    endpoint, concurrent);

                var result = await psRunner.ExecuteHttpScript(endpoint, context);

                // Writes can invalidate anything, so they clear the cache regardless of endpoint name.
                // Do this even on a client abort: a write may have partially applied before the
                // connection dropped, so dropping the possibly-stale cache is the safe choice.
                if (context.Request.Method != "GET") cache.InvalidateScopedOrAll(context);

                // Client hung up mid-request (navigated away / hit Cancel). The pipeline was already
                // stopped and the worker reclaimed; there is no live connection to write to, and a
                // partial result must not be cached. Bail before the cache write and response write.
                if (context.RequestAborted.IsCancellationRequested)
                {
                    logger.LogInformation("[HTTP] /API/{Endpoint} cancelled by client", endpoint);
                    return;
                }

                if (useCache && cacheKey is not null && result.StatusCode is >= 200 and < 400)
                    await cache.Set(cacheKey, result);

                context.Response.StatusCode = result.StatusCode;
                context.Response.ContentType = HandlerHeaders.ResolveContentType(result.ContentType);
                HandlerHeaders.Apply(context.Response, result.Headers);
                ApiEgressWireCounterMiddleware.TagEndpoint(context, endpoint);
                // BYPASS vs MISS matters when someone asks why an endpoint never caches: MISS means it
                // was eligible and simply had no entry, BYPASS means the policy kept it out.
                context.Response.Headers["X-Cache"] = cacheBypassReason is null ? "MISS" : "BYPASS";
                if (cacheBypassReason is not null)
                    context.Response.Headers["X-Cache-Bypass"] = cacheBypassReason;
                context.Response.Headers["X-Request-Duration"] = $"{requestSw.ElapsedMilliseconds}ms";

                if (HandlerHeaders.AllowsBody(result.StatusCode))
                    await context.Response.Body.WriteAsync(result.BodyBytes);
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

    private static async Task<(string? Key, CachedResponse? Response)> TryReadCacheAsync(
        HttpContext context, CacheService cache, string endpoint, ILogger logger)
    {
        var profiling = CacheProfiler.Enabled;
        var t0 = profiling ? Stopwatch.GetTimestamp() : 0;

        var userKey = CacheService.GetUserKey(context);
        var t1 = profiling ? Stopwatch.GetTimestamp() : 0;

        var key = cache.BuildCacheKey(endpoint, context.Request.Query, userKey);
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
        HttpContext context, CacheService cache, PowerShellRunnerService psRunner, ILogger logger,
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
        ApiEgressWireCounterMiddleware.TagEndpoint(context, endpoint);
        context.Response.Headers["X-Cache"] = cached.IsStale ? "HIT-STALE" : "HIT";
        context.Response.Headers["X-Cache-Age"] = $"{cached.Age.TotalSeconds:F0}s";
        context.Response.Headers["X-Cache-TTL"] = $"{ttl.TotalSeconds:F0}s";
        context.Response.Headers["X-Request-Duration"] = $"{requestSw.ElapsedMilliseconds}ms";

        if (HandlerHeaders.AllowsBody(cached.Result.StatusCode))
            await context.Response.Body.WriteAsync(cached.Result.BodyBytes);

        if (requestSnapshot is null) return;

        StartBackgroundRefresh(cache, psRunner, logger, endpoint, cacheKey, requestSnapshot);
    }

    /// <summary>
    /// Stale-while-revalidate: the caller already has its (stale) response, so refresh off the request
    /// path. Deliberately fire-and-forget — nothing is waiting on it.
    /// </summary>
    private static void StartBackgroundRefresh(
        CacheService cache, PowerShellRunnerService psRunner,
        ILogger logger, string endpoint, string cacheKey, Hashtable requestSnapshot)
    {
        var generation = cache.Generation;

        _ = Task.Run(async () =>
        {
            try
            {
                await cache.WaitForBgRefreshSlot(endpoint);
                var fresh = await psRunner.ExecuteHttpScript(endpoint, requestSnapshot);

                // Generation check: an invalidation may have landed while this was running, and
                // writing back then would resurrect data the invalidation was meant to drop.
                if (fresh.StatusCode is >= 200 and < 400)
                    cache.SetIfSameGeneration(cacheKey, fresh, generation);
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
}

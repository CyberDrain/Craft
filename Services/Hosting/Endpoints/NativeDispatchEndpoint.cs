using System.Collections.Concurrent;
using System.Globalization;
using Craft.Caching;
using Craft.Configuration;
using Craft.Endpoints;

namespace Craft.Hosting.Endpoints;

/// <summary>
/// Maps discovered native endpoints as real ASP.NET routes.
///
/// <para>
/// Mapping each one at its literal path (<c>/API/GetIPInfo</c>) rather than intercepting the
/// PowerShell catch-all (<c>/API/{endpoint}</c>) is what makes migration work per endpoint: ASP.NET
/// route precedence puts a literal segment ahead of a parameter segment, so a native endpoint wins
/// automatically and the PowerShell dispatcher needs no changes at all. The PowerShell function stays
/// loaded and reachable the moment the collision policy is flipped back.
/// </para>
/// </summary>
public static class NativeDispatchEndpoint
{
    // One gate per endpoint that ended up with a concurrency ceiling. Built once at mapping time.
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> s_gates = new(StringComparer.Ordinal);

    // Resolved limits, so the request path never re-reads configuration.
    private static readonly ConcurrentDictionary<string, int> s_queueTimeouts = new(StringComparer.Ordinal);

    public static WebApplication MapCraftNativeEndpoints(
        this WebApplication app,
        IReadOnlyList<NativeEndpointDescriptor> endpoints,
        RequestCounter activeRequests,
        ILogger logger,
        EndpointSettings? settings = null,
        string? meRoute = null)
    {
        ArgumentNullException.ThrowIfNull(app);
        ArgumentNullException.ThrowIfNull(endpoints);
        ArgumentNullException.ThrowIfNull(activeRequests);
        ArgumentNullException.ThrowIfNull(logger);

        settings ??= new EndpointSettings();

        // The handler is discovered at startup and registered as a singleton, so its presence is a
        // startup-time fact — resolve it once here rather than per request.
        var handler = app.Services.GetService<ICraftEndpointHandler>();
        EnsureHandlerRequirement(settings, handler is not null, endpoints);
        var cache = app.Services.GetService<CacheService>();

        if (handler is not null)
        {
            var direct = endpoints
                .Where(e => e.Metadata.Dispatch == EndpointDispatch.Direct)
                .Select(e => e.Route)
                .ToList();

            logger.LogInformation(
                "[Endpoints] Central handler {Handler}: {Count} route(s) dispatch through it",
                handler.GetType().Name, endpoints.Count - direct.Count);

            // Never silent: this list is the set of routes the application's authorization does NOT
            // cover, which makes it the security review checklist.
            if (direct.Count > 0)
            {
                logger.LogInformation(
                    "[Endpoints] {Count} route(s) dispatch DIRECT, bypassing the central handler: {Routes}",
                    direct.Count, string.Join(", ", direct));
            }
        }

        foreach (var endpoint in endpoints)
        {
            var descriptor = endpoint;
            var path = "/API/" + descriptor.Route;

            var limit = settings.ResolveConcurrency(descriptor.Route, descriptor.Metadata.MaxConcurrency);
            var queueTimeout = settings.ResolveQueueTimeout(descriptor.Metadata.QueueTimeoutSeconds);
            s_queueTimeouts[descriptor.Route] = queueTimeout;

            if (limit > 0)
            {
                s_gates[descriptor.Route] = new SemaphoreSlim(limit);
                if (limit != descriptor.Metadata.MaxConcurrency)
                {
                    logger.LogInformation(
                        "[Endpoints] {Route}: in-flight limit {Limit} (config override; endpoint declared {Declared})",
                        descriptor.Route, limit, descriptor.Metadata.MaxConcurrency);
                }
            }
            else
            {
                // A gate may survive a previous mapping in the same process (tests, restarts).
                s_gates.TryRemove(descriptor.Route, out _);
            }

            app.MapMethods(path, descriptor.Metadata.Methods,
                (HttpContext context) => Handle(context, descriptor, handler, cache, activeRequests, logger));

            // The SPA asks /api/me who is signed in; an endpoint on the configured route answers it,
            // through the same gate, filters and central handler as its own path (which for a route
            // named "me" already is /api/me: routes ignore case, so mapping it twice would be ambiguous)
            if (string.Equals(descriptor.Route, meRoute, StringComparison.OrdinalIgnoreCase)
                && !string.Equals(path, "/api/me", StringComparison.OrdinalIgnoreCase))
                app.MapGet("/api/me", (HttpContext context) => Handle(context, descriptor, handler, cache, activeRequests, logger));
        }

        if (endpoints.Count > 0)
        {
            logger.LogInformation("[Endpoints] {Count} native route(s) mapped: {Routes}",
                endpoints.Count, string.Join(", ", endpoints.Select(e => e.Route)));
        }

        return app;
    }

    /// <summary>
    /// Fails startup when the deployment declared its security model depends on a central handler
    /// and none was discovered. Only Central endpoints count: an all-Direct application has nothing
    /// for a handler to protect, so requiring one would reject a configuration that is already
    /// exactly what it claims to be.
    /// </summary>
    internal static void EnsureHandlerRequirement(
        EndpointSettings settings,
        bool handlerPresent,
        IReadOnlyList<NativeEndpointDescriptor> endpoints)
    {
        if (!settings.RequireHandler || handlerPresent) return;

        var central = endpoints
            .Where(e => e.Metadata.Dispatch == EndpointDispatch.Central)
            .Select(e => e.Route)
            .ToList();
        if (central.Count == 0) return;

        throw new InvalidOperationException(
            "App:Endpoints:RequireHandler is true but no ICraftEndpointHandler was discovered, and " +
            $"{central.Count} endpoint(s) declare Central dispatch: {string.Join(", ", central)}. " +
            "Without the handler these routes would serve with no central check at all. Ship a " +
            "handler in a scanned assembly, or set RequireHandler=false if that is intended.");
    }

    /// <summary>
    /// Runs the endpoint, routed through the central handler when the endpoint's dispatch mode asks
    /// for one and the application registered one.
    /// </summary>
    internal static ValueTask<CraftResult> ExecuteAsync(
        CraftRequest request,
        ICraftEndpoint endpoint,
        ICraftEndpointHandler? handler,
        CancellationToken ct) =>
        ExecuteAsync(request, () => endpoint.HandleAsync(request, ct), handler, ct);

    /// <summary>As above, with <paramref name="invokeEndpoint"/> standing in for the endpoint (the response cache).</summary>
    internal static ValueTask<CraftResult> ExecuteAsync(
        CraftRequest request,
        CraftEndpointNext invokeEndpoint,
        ICraftEndpointHandler? handler,
        CancellationToken ct)
    {
        if (handler is null || request.Endpoint.Dispatch != EndpointDispatch.Central)
            return invokeEndpoint();

        return handler.HandleAsync(request, invokeEndpoint, ct);
    }

    /// <summary>Whether this request's response may come from, and go into, the response cache.</summary>
    internal static bool IsCacheable(NativeEndpointDescriptor descriptor, string method) =>
        HttpMethods.IsGet(method) && descriptor.Metadata.Caching switch
        {
            EndpointCaching.Cached => true,
            EndpointCaching.Uncached => false,
            _ => descriptor.Route.StartsWith("List", StringComparison.OrdinalIgnoreCase),
        };

    /// <summary>
    /// The endpoint's result through the cache: a hit is served as stored and refreshed in the background (the
    /// PowerShell path's stale-while-revalidate); a miss runs the endpoint and stores a cacheable result.
    /// </summary>
    private static async ValueTask<CraftResult> FromCacheAsync(
        HttpContext context, CraftRequest request, ICraftEndpoint endpoint, NativeEndpointDescriptor descriptor,
        ICraftEndpointHandler? handler, CacheService cache, ILogger logger, CancellationToken ct)
    {
        var key = cache.BuildCacheKey(descriptor.Route, context.Request.Query, CacheService.GetUserKey(context));
        if (await cache.Get(key, descriptor.Route) is { } hit)
        {
            cache.Touch(key);
            context.Response.Headers["X-Cache"] = hit.IsStale ? "HIT-STALE" : "HIT";
            context.Response.Headers["X-Cache-Age"] = $"{hit.Age.TotalSeconds:F0}s";
            context.Response.Headers["X-Cache-TTL"] = $"{cache.GetTtl(descriptor.Route).TotalSeconds:F0}s";
            if (cache.TryStartRefresh(key)) StartBackgroundRefresh(context, descriptor, handler, cache, key, logger);
            return CraftResult.FromCached(hit.Result);
        }

        var result = await endpoint.HandleAsync(request, ct);
        if (!ct.IsCancellationRequested && result.ToCacheable() is { } cacheable) await cache.Set(key, cacheable);
        context.Response.Headers["X-Cache"] = "MISS";
        return result;
    }

    /// <summary>
    /// Re-runs the request off the request path and replaces the entry. Rebuilt from a snapshot in a scope of its own
    /// (the caller's context is gone by then), and back through the central handler, so a caller who has since lost
    /// access refreshes nothing.
    /// </summary>
    private static void StartBackgroundRefresh(
        HttpContext context, NativeEndpointDescriptor descriptor, ICraftEndpointHandler? handler,
        CacheService cache, string key, ILogger logger)
    {
        var scopes = context.RequestServices.GetRequiredService<IServiceScopeFactory>();
        var path = context.Request.Path;
        var query = context.Request.QueryString;
        var headers = context.Request.Headers.ToDictionary(h => h.Key, h => h.Value, StringComparer.OrdinalIgnoreCase);
        var user = context.User;
        var generation = cache.Generation;

        _ = Task.Run(async () =>
        {
            try
            {
                await cache.WaitForBgRefreshSlot(descriptor.Route);
                await using var scope = scopes.CreateAsyncScope();
                var replay = new DefaultHttpContext { RequestServices = scope.ServiceProvider, User = user };
                replay.Request.Method = HttpMethods.Get;
                replay.Request.Path = path;
                replay.Request.QueryString = query;
                foreach (var (name, value) in headers) replay.Request.Headers[name] = value;

                var endpoint = (ICraftEndpoint)scope.ServiceProvider.GetRequiredService(descriptor.ImplementationType);
                var request = new CraftRequest(replay, descriptor.Route, descriptor.Metadata);
                var fresh = await ExecuteAsync(request, () => endpoint.HandleAsync(request, CancellationToken.None), handler, CancellationToken.None);
                // An invalidation may have landed meanwhile, and writing back would undo it
                if (fresh.ToCacheable() is { } cacheable) cache.SetIfSameGeneration(key, cacheable, generation);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Background refresh failed: /API/{Endpoint}", descriptor.Route);
            }
            finally
            {
                cache.ReleaseBgRefreshSlot(descriptor.Route);
                cache.FinishRefresh(key);
            }
        });
    }

    private static async Task Handle(
        HttpContext context,
        NativeEndpointDescriptor descriptor,
        ICraftEndpointHandler? handler,
        CacheService? cache,
        RequestCounter activeRequests,
        ILogger logger)
    {
        // The PowerShell dispatcher increments this, and the stats history and log lines read it.
        // A native path that skipped it would make concurrency silently under-report as traffic
        // migrated, which is exactly when you would be watching it.
        activeRequests.Increment();

        SemaphoreSlim? gate = null;
        try
        {
            var ct = context.RequestAborted;

            if (s_gates.TryGetValue(descriptor.Route, out var configured))
            {
                var seconds = s_queueTimeouts.TryGetValue(descriptor.Route, out var t)
                    ? t : descriptor.Metadata.QueueTimeoutSeconds;
                if (!await configured.WaitAsync(TimeSpan.FromSeconds(seconds), ct))
                {
                    // Same shape the worker pool sheds with, so a client cannot tell whether the
                    // endpoint it was throttled by is PowerShell or native.
                    context.Response.StatusCode = 503;
                    context.Response.Headers.RetryAfter =
                        seconds.ToString(CultureInfo.InvariantCulture);
                    context.Response.ContentType = "application/json";
                    await context.Response.WriteAsync(
                        """{"error":"Endpoint is at capacity. Please retry."}""", ct);
                    return;
                }
                gate = configured;
            }

            var endpoint = (ICraftEndpoint)context.RequestServices
                .GetRequiredService(descriptor.ImplementationType);

            var request = new CraftRequest(context, descriptor.Route, descriptor.Metadata);

            // Application-wide filters run first, for EVERY endpoint — including Direct ones, which
            // is what distinguishes a filter from the central handler. Telemetry and IP throttling
            // belong here; authorization belongs in the handler, where Direct routes can opt out.
            foreach (var filter in context.RequestServices.GetServices<ICraftEndpointFilter>())
            {
                var shortCircuit = await filter.BeforeAsync(request, ct);
                if (shortCircuit is { } blocked)
                {
                    await blocked.WriteAsync(context, ct);
                    return;
                }
            }

            // As on the PowerShell path: any write can change what any read returns
            if (cache is not null)
            {
                cache.ApplyRequestedInvalidation(context);
                if (!HttpMethods.IsGet(context.Request.Method)) cache.InvalidateScopedOrAll(context);
            }

            string? bypass = null;
            var useCache = cache is not null && IsCacheable(descriptor, context.Request.Method)
                && cache.Policy.Allows(descriptor.Route, context.Request, out bypass);
            if (bypass is not null) context.Response.Headers["X-Cache-Bypass"] = bypass;

            // The cache stands in for the endpoint only, so filters and the central handler still run on a hit
            var result = await ExecuteAsync(request,
                useCache
                    ? () => FromCacheAsync(context, request, endpoint, descriptor, handler, cache!, logger, ct)
                    : () => endpoint.HandleAsync(request, ct),
                handler, ct);
            await result.WriteAsync(context, ct);
        }
        catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
        {
            // The caller hung up. Not an error, and not worth a stack trace.
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "[API] Native endpoint {Route} failed", descriptor.Route);

            // Matches the PowerShell dispatcher's failure shape — clients should not be able to tell
            // which dispatcher produced a 500.
            if (!context.Response.HasStarted)
            {
                context.Response.StatusCode = 500;
                context.Response.ContentType = "application/json";
                await context.Response.WriteAsync(
                    System.Text.Json.JsonSerializer.Serialize(new { error = ex.Message }));
            }
        }
        finally
        {
            gate?.Release();
            activeRequests.Decrement();
        }
    }
}

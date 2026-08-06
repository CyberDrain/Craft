using System.Collections.Concurrent;
using System.Globalization;
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
        EndpointSettings? settings = null)
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
                (HttpContext context) => Handle(context, descriptor, handler, activeRequests, logger));
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
        CancellationToken ct)
    {
        if (handler is null || request.Endpoint.Dispatch != EndpointDispatch.Central)
            return endpoint.HandleAsync(request, ct);

        return handler.HandleAsync(request, () => endpoint.HandleAsync(request, ct), ct);
    }

    private static async Task Handle(
        HttpContext context,
        NativeEndpointDescriptor descriptor,
        ICraftEndpointHandler? handler,
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

            var result = await ExecuteAsync(request, endpoint, handler, ct);
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

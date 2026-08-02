using Craft.Configuration;
using System.Collections.Concurrent;
using System.Globalization;
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
                (HttpContext context) => Handle(context, descriptor, activeRequests, logger));
        }

        if (endpoints.Count > 0)
        {
            logger.LogInformation("[Endpoints] {Count} native route(s) mapped: {Routes}",
                endpoints.Count, string.Join(", ", endpoints.Select(e => e.Route)));
        }

        return app;
    }

    private static async Task Handle(
        HttpContext context,
        NativeEndpointDescriptor descriptor,
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

            var request = new CraftRequest(context, descriptor.Route);

            // Application-wide filters run first. This is where an app that used a PowerShell router
            // (Scripts:HttpHandler) for authorization moves that check to — without it, a native
            // endpoint bypasses the router entirely and loses it.
            foreach (var filter in context.RequestServices.GetServices<ICraftEndpointFilter>())
            {
                var shortCircuit = await filter.BeforeAsync(request, ct);
                if (shortCircuit is { } blocked)
                {
                    await blocked.WriteAsync(context, ct);
                    return;
                }
            }

            var result = await endpoint.HandleAsync(request, ct);
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

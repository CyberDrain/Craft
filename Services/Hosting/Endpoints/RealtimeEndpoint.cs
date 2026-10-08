using System.Text.Json;
using Craft.Auth;
using Craft.Configuration;
using Craft.Realtime;

namespace Craft.Hosting.Endpoints;

/// <summary>
/// Server-sent events channel at <c>/.craft/events</c>, delivering job events published in-process
/// through <c>RealtimeBridge</c>. Pure C# — it never occupies a PowerShell runspace, which is what
/// makes it safe to hold a connection open per browser tab.
/// </summary>
public static class RealtimeEndpoint
{
    /// <summary>The stream's path. Program.cs routes it through the /api compressor.</summary>
    public const string Path = "/.craft/events";

    /// <summary>
    /// Maps the SSE endpoint on nodes that face a browser, when realtime is switched on.
    /// </summary>
    /// <remarks>
    /// Opt-in: off unless <c>App:Realtime:Enabled=true</c> (or <c>CRAFT_REALTIME_ENABLED=true</c>,
    /// which wins). While off the route is never mapped, so <c>/.craft/events</c> falls through like
    /// any unknown path and bridge publishes are dropped.
    /// </remarks>
    public static WebApplication MapCraftRealtimeEndpoint(
        this WebApplication app, CraftRoles roles, CraftSettings settings, ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(app);
        ArgumentNullException.ThrowIfNull(roles);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(logger);

        var browserFacing = roles.Http || roles.Frontend;
        if (!browserFacing) return app;

        if (!settings.Realtime.IsEnabled)
        {
            logger.LogInformation(
                "[System] Realtime SSE endpoint: disabled (set App:Realtime:Enabled=true to enable)");
            return app;
        }

        var realtime = app.Services.GetRequiredService<RealtimeService>();
        var heartbeat = TimeSpan.FromSeconds(Math.Max(5, settings.Realtime.HeartbeatSeconds));

        app.MapGet(Path, async (HttpContext ctx) =>
        {
            // Delivery is identity-gated: a stream is only ever fed this user's own job events.
            var userId = ResolveSignedInUser(ctx);
            if (userId is null) { ctx.Response.StatusCode = 401; return; }

            var (connId, conn) = realtime.Connect(userId);
            if (conn is null) { ctx.Response.StatusCode = 503; return; }   // over MaxConnections

            ctx.Response.Headers["Content-Type"] = "text/event-stream";
            ctx.Response.Headers["Cache-Control"] = "no-cache";
            ctx.Response.Headers["X-Accel-Buffering"] = "no";   // stop nginx buffering the stream
            // No DisableBuffering: every batch of frames is flushed below, which is all the stream needs, and
            // the compressor then encodes a batch as one block instead of flushing after every write.

            var ct = ctx.RequestAborted;
            try
            {
                await ctx.Response.WriteAsync(": connected\n\n", ct);

                // Replay the current frame for each of this user's live jobs so a reconnect resyncs
                // rather than silently missing whatever happened while the tab was away.
                foreach (var frame in realtime.CurrentFrames(userId))
                    await ctx.Response.WriteAsync(frame, ct);
                await ctx.Response.Body.FlushAsync(ct);

                var reader = conn.Reader;
                while (!ct.IsCancellationRequested)
                {
                    using var hb = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    hb.CancelAfter(heartbeat);
                    try
                    {
                        if (await reader.WaitToReadAsync(hb.Token))
                        {
                            while (reader.TryRead(out var frame))
                                await ctx.Response.WriteAsync(frame, ct);
                            await ctx.Response.Body.FlushAsync(ct);
                        }
                    }
                    catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                    {
                        // Heartbeat elapsed, not a disconnect: emit a comment frame so intermediaries
                        // don't tear down an idle connection.
                        await ctx.Response.WriteAsync(": ping\n\n", ct);
                        await ctx.Response.Body.FlushAsync(ct);
                    }
                }
            }
            catch (OperationCanceledException)
            {
                // Client navigated away or closed the tab — the normal way a stream ends.
            }
            finally
            {
                realtime.Disconnect(userId, connId);
            }
        });

        logger.LogInformation("[System] Realtime SSE endpoint: /.craft/events");
        return app;
    }

    /// <summary>
    /// The principal name of a signed-in user, read after <see cref="CraftAuthMiddleware"/> has normalised
    /// the request. The principal must carry a real role, which rules out anonymous callers and app-only
    /// API clients (normalised with no roles). Null when the caller is neither.
    /// </summary>
    internal static string? ResolveSignedInUser(HttpContext ctx)
    {
        var name = ctx.Request.Headers["x-ms-client-principal-name"].ToString();
        var principal = ctx.Request.Headers["x-ms-client-principal"].ToString();
        if (string.IsNullOrEmpty(name) || string.IsNullOrEmpty(principal)) return null;

        try
        {
            using var doc = EasyAuthPrincipal.Decode(principal);
            if (doc.RootElement.ValueKind != JsonValueKind.Object ||
                !doc.RootElement.TryGetProperty("userRoles", out var roles) ||
                roles.ValueKind != JsonValueKind.Array)
                return null;

            foreach (var role in roles.EnumerateArray())
                if (role.ValueKind == JsonValueKind.String && role.GetString() is { Length: > 0 } r &&
                    !r.Equals("anonymous", StringComparison.OrdinalIgnoreCase))
                    return name;
        }
        catch
        {
            // Unreadable principal: not a signed-in user.
        }
        return null;
    }
}

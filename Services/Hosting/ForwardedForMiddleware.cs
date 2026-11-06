using System.Net;

namespace Craft.Hosting;

/// <summary>
/// Replaces <c>X-Forwarded-For</c> with the single client address the trusted proxies vouch for, before
/// anything downstream (rate limiter, egress ledger, the PowerShell app's IP allow-lists and audit log) reads it.
/// <para>
/// Proxies append, they do not overwrite: App Service forwards a client's <c>X-Forwarded-For: 1.2.3.4</c> as
/// <c>1.2.3.4, &lt;real ip&gt;:&lt;port&gt;</c>. Only the entry the outermost trusted proxy appended can be
/// believed, so with N trusted hops the client is the Nth entry from the right. With 0 hops, or when the
/// header is shorter than the trusted chain (the request bypassed the proxy), the socket peer is used.
/// </para>
/// </summary>
public static class ForwardedForMiddleware
{
    /// <summary>Registers the rewrite. Must run before any middleware that reads the client address.</summary>
    public static WebApplication UseCraftForwardedFor(this WebApplication app, int trustedHops, ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(app);
        logger.LogInformation("[System] Trusted proxy hops for X-Forwarded-For: {Hops}", trustedHops);
        app.Use((context, next) =>
        {
            Apply(context, trustedHops);
            return next();
        });
        return app;
    }

    /// <summary>Rewrites the header in place to the resolved client address, or removes it when none is known.</summary>
    public static void Apply(HttpContext context, int trustedHops)
    {
        ArgumentNullException.ThrowIfNull(context);

        var hops = context.Request.Headers["X-Forwarded-For"].ToString()
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        var client = trustedHops > 0 && hops.Length >= trustedHops && IPEndPoint.TryParse(hops[^trustedHops], out var forwarded)
            ? forwarded.Address
            : context.Connection.RemoteIpAddress;

        if (client is null)
            context.Request.Headers.Remove("X-Forwarded-For");
        else
            context.Request.Headers["X-Forwarded-For"] = (client.IsIPv4MappedToIPv6 ? client.MapToIPv4() : client).ToString();
    }
}

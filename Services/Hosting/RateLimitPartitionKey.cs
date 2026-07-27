namespace Craft.Hosting;

/// <summary>
/// Chooses the rate-limiter partition a request counts against.
/// <para>
/// Getting this wrong is a availability problem in both directions: too coarse and one noisy caller
/// exhausts the shared budget for everyone, too fine and the limiter stops limiting anything.
/// </para>
/// </summary>
public static class RateLimitPartitionKey
{
    /// <summary>
    /// Resolves the partition key for <paramref name="context"/>, preferring the authenticated
    /// principal and falling back to the originating client address.
    /// </summary>
    /// <remarks>
    /// The <c>X-Forwarded-For</c> step is load-bearing. Behind Azure App Service the socket peer is the
    /// platform load balancer, so <see cref="ConnectionInfo.RemoteIpAddress"/> is the same value for
    /// every anonymous caller — partitioning on it would collapse them all into one bucket and let any
    /// single client deny service to the rest. Only the first hop is used, since the rest of the header
    /// is caller-supplied and trivially spoofed.
    /// </remarks>
    public static string Resolve(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var principal = context.Request.Headers["x-ms-client-principal-name"].ToString();
        if (!string.IsNullOrEmpty(principal)) return principal;

        var forwardedFor = context.Request.Headers["X-Forwarded-For"].ToString();
        if (!string.IsNullOrEmpty(forwardedFor))
        {
            var firstHop = forwardedFor.Split(',')[0].Trim();
            if (!string.IsNullOrEmpty(firstHop)) return firstHop;
        }

        return context.Connection.RemoteIpAddress?.ToString() ?? "anonymous";
    }
}

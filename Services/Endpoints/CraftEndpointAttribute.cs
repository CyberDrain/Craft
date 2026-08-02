
namespace Craft.Endpoints;

/// <summary>
/// Marks a class as a native endpoint and declares its route and metadata. The C# counterpart of the
/// PowerShell convention where <c>Invoke-GetIPInfo</c> becomes <c>/API/GetIPInfo</c> and the
/// <c>.ROLE</c> / <c>.FUNCTIONALITY</c> doc tags feed the permission map.
/// </summary>
[AttributeUsage(AttributeTargets.Class, Inherited = false)]
public sealed class CraftEndpointAttribute : Attribute
{
    public CraftEndpointAttribute(string route)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(route);
        Route = route.Trim('/');
    }

    /// <summary>
    /// Route segment beneath <c>/API/</c>. Declared explicitly rather than derived from the type name
    /// so that migrating an endpoint from PowerShell cannot change its URL — the existing callers are
    /// the reason this whole exercise has to be invisible from outside.
    /// </summary>
    public string Route { get; }

    /// <summary>HTTP methods this endpoint answers. Defaults to the same set the PS dispatcher accepts.</summary>
    public string[] Methods { get; init; } = ["GET", "POST", "PUT", "DELETE", "PATCH"];

    /// <summary>Equivalent of the PowerShell <c>.ROLE</c> doc tag; feeds function-permissions.json.</summary>
    public string? Role { get; init; }

    /// <summary>Equivalent of the PowerShell <c>.FUNCTIONALITY</c> doc tag.</summary>
    public string? Functionality { get; init; }

    /// <summary>
    /// Ceiling on concurrent executions of THIS endpoint. 0 (default) means unbounded, which is
    /// usually right for an async endpoint and is much of the point of being native.
    ///
    /// <para>
    /// Set it when unbounded concurrency would hurt something downstream. The worker pool is what
    /// bounds PowerShell endpoints today — it sheds to a 503 once saturated — and a native endpoint
    /// has no equivalent, so it is limited only by Kestrel's connection cap. Two things that makes
    /// worse: fanning out to a rate-limited upstream (the failure moves off the host and onto the
    /// bill), and endpoints whose memory scales with concurrency.
    /// </para>
    /// </summary>
    public int MaxConcurrency { get; init; }

    /// <summary>
    /// How long a request waits for a slot when <see cref="MaxConcurrency"/> is reached, before being
    /// shed with a 503. Matches the PowerShell pool's 30s checkout timeout so the client-visible
    /// behaviour does not change as endpoints migrate.
    /// </summary>
    public int QueueTimeoutSeconds { get; init; } = 30;

    /// <summary>
    /// Singleton by default: it matches the process-wide state a PowerShell module holds, costs no
    /// per-request allocation, and lets an endpoint keep a pooled HttpClient in a field.
    /// </summary>
    public ServiceLifetime Lifetime { get; init; } = ServiceLifetime.Singleton;
}


namespace Craft.Endpoints;

/// <summary>
/// An HTTP endpoint written in C# rather than PowerShell, dispatched by CRAFT alongside the
/// PowerShell ones.
///
/// <para>
/// The reason this exists is NOT dispatch latency. Measured on one core at 40 req/s, a PowerShell
/// endpoint that returns a constant costs 3.19ms median against 2.33ms for a C# endpoint on the same
/// host — so PowerShell dispatch is about <b>0.86ms</b>, and the rest is Kestrel, middleware and the
/// container's published-port path, which this changes not at all.
/// </para>
///
/// <para>
/// It exists for two things PowerShell cannot do:
/// <list type="number">
///   <item><description>
///     <b>Await.</b> A PowerShell endpoint holds a runspace AND a thread for the entire duration of
///     every outbound call, which is why throughput on an I/O-bound workload follows
///     <c>HttpPoolSize / upstream_rtt</c> and why runspace checkout was measured at 8.6ms of an 11ms
///     request under saturation. An async endpoint holds neither, so the pool stops being the ceiling.
///   </description></item>
///   <item><description>
///     <b>Stream.</b> PowerShell materialises object graphs, so an endpoint returning a whole table
///     peaked at 648 MiB of RSS with 8 concurrent callers. A C# endpoint writing to the response
///     pipe runs at constant memory.
///   </description></item>
/// </list>
/// </para>
///
/// Implementations are resolved from DI and are singletons by default, so hold pooled clients and
/// caches in fields rather than rebuilding them per request.
/// </summary>
public interface ICraftEndpoint
{
    /// <summary>Handles one request. Never returns null.</summary>
    ValueTask<CraftResult> HandleAsync(CraftRequest request, CancellationToken ct);
}

/// <summary>
/// Runs before every native endpoint, for concerns an application applies across all of them.
///
/// <para>
/// This is not optional infrastructure for some apps: when <c>Scripts:HttpHandler</c> is set, EVERY
/// PowerShell request funnels through one router function, and applications use that router to do
/// authorization. A native endpoint bypasses the router completely, so an app relying on it would
/// silently lose that check on the first endpoint it migrated. A filter is where that check moves to.
/// </para>
///
/// Returning a result short-circuits the request; returning null continues to the endpoint.
/// </summary>
public interface ICraftEndpointFilter
{
    ValueTask<CraftResult?> BeforeAsync(CraftRequest request, CancellationToken ct);
}

/// <summary>
/// Lets an application register its own services when CRAFT is hosting it as a plugin, since in that
/// form the application has no <c>Program.cs</c> of its own to do it in. Discovered in the same
/// assembly scan as the endpoints.
/// </summary>
public interface ICraftServiceModule
{
    void ConfigureServices(IServiceCollection services, IConfiguration configuration);
}

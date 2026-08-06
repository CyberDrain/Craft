
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
/// The single central entrypoint for native endpoints — the C# counterpart of
/// <c>Scripts:HttpHandler</c>, which funnels every PowerShell request through one router function.
/// Applications put cross-endpoint authorization here: resolve the principal, check the plan or the
/// role (the endpoint's declared <see cref="CraftEndpointAttribute.Role"/> is on
/// <see cref="CraftRequest.Endpoint"/>), then invoke the endpoint through the supplied
/// <see cref="CraftEndpointNext"/> — or don't.
///
/// <para>
/// Wraps the endpoint rather than merely preceding it, so a handler can also time the call or
/// post-process the result. Endpoints opt out per-route with
/// <see cref="EndpointDispatch.Direct"/> — the escape hatch <c>Scripts:HttpHandler</c> never needed,
/// because a PowerShell app's public routes (a webhook, a redirect) are exactly the ones that
/// migrate to native first.
/// </para>
///
/// <para>
/// Discovered in the same assembly scan as the endpoints, registered as a singleton. At most one per
/// application: two handlers would make "which auth ran" a function of assembly scan order, so a
/// second one fails startup. Zero is legal — every endpoint then dispatches as if Direct, which is
/// exactly the pre-handler behaviour. Deployments whose security model depends on the handler
/// existing should set <c>App:Endpoints:RequireHandler=true</c> (in CI at minimum).
/// </para>
/// </summary>
public interface ICraftEndpointHandler
{
    /// <summary>Handles one Central-dispatch request. Call <paramref name="invokeEndpoint"/> to run
    /// the endpoint, or return without calling it to short-circuit.</summary>
    ValueTask<CraftResult> HandleAsync(
        CraftRequest request, CraftEndpointNext invokeEndpoint, CancellationToken ct);
}

/// <summary>Invokes the endpoint an <see cref="ICraftEndpointHandler"/> is wrapping.</summary>
public delegate ValueTask<CraftResult> CraftEndpointNext();

/// <summary>
/// Runs before every native endpoint — including <see cref="EndpointDispatch.Direct"/> ones, which
/// is the property that distinguishes it from <see cref="ICraftEndpointHandler"/>. Use a filter for
/// concerns that must also cover the public routes (request telemetry, IP throttling); use the
/// handler for authorization, which is precisely what a public route opts out of.
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

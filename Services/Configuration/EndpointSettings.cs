namespace Craft.Configuration;

/// <summary>
/// Native C# endpoints, hosted alongside the PowerShell ones.
///
/// <para>
/// Off by default. An application opts in by naming the assemblies its endpoints live in; those load
/// at startup and each <c>[CraftEndpoint]</c> type is mapped at its literal route. Because a literal
/// route outranks the PowerShell catch-all, endpoints migrate one at a time with the PowerShell
/// function left in place as the rollback.
/// </para>
/// </summary>
public class EndpointSettings
{
    /// <summary>
    /// Master switch. Default false, so a deployment that names no assemblies pays nothing —
    /// no assembly loading, no reflection, no route mapping.
    /// </summary>
    public bool Enabled { get; set; }

    /// <summary>
    /// Assemblies to scan, absolute or relative to the API base path (e.g.
    /// <c>bin/GeoIpDb.Endpoints.dll</c>). Loaded into the default load context and never unloaded —
    /// see <c>NativeEndpointRegistry</c> for why a collectible context would be a trap.
    /// </summary>
    public List<string> Assemblies { get; set; } = [];

    /// <summary>
    /// What to do when a native endpoint claims a route a PowerShell function already has.
    ///
    /// <list type="bullet">
    ///   <item><description><c>PreferNative</c> (default) — the native endpoint wins and the
    ///   shadowing is logged. This is the mode that makes migration work: flip one endpoint, keep the
    ///   PowerShell one loaded as the rollback.</description></item>
    ///   <item><description><c>PreferPowerShell</c> — instant rollback with no rebuild.</description></item>
    ///   <item><description><c>Fail</c> — refuse to start, naming every collision. The right setting
    ///   for CI, where a route shadow should be caught before it ships.</description></item>
    /// </list>
    ///
    /// <c>Fail</c> is arguably the safer runtime default and is deliberately not the one chosen: on a
    /// config-driven runtime it turns an accidental shadow into a failed rolling deploy. Set it in CI
    /// and leave <c>PreferNative</c> in production.
    /// </summary>
    public string OnCollision { get; set; } = "PreferNative";

    /// <summary>
    /// Blanket in-flight limit for native endpoints that declare none. 0 (default) means unbounded.
    /// </summary>
    /// <remarks>
    /// <para>
    /// With <c>Worker:HttpPoolSize=0</c> there is no runspace pool, and the pool was what implicitly
    /// capped concurrent work — a request could only run if a worker was free. Native endpoints are
    /// async end to end and hold no thread while waiting on an upstream, so nothing stops the process
    /// accepting far more concurrent requests than it can finish. Memory is what gives out first: each
    /// in-flight request holds its request and response buffers.
    /// </para>
    /// <para>
    /// This is a concurrency limit, not a rate limit, and the two solve different problems.
    /// <c>App:RateLimit:*</c> stops one caller monopolising the service and is partitioned per client;
    /// this bounds total simultaneous work regardless of who asked. A service behind a trusted
    /// single caller needs this one and not the other.
    /// </para>
    /// </remarks>
    public int MaxConcurrency { get; set; }

    /// <summary>
    /// Per-route in-flight limits, keyed by route (e.g. <c>"GeoDBDownload": 4</c>). Overrides both the
    /// endpoint's <c>[CraftEndpoint(MaxConcurrency = n)]</c> and <see cref="MaxConcurrency"/>. Set 0 to
    /// explicitly remove a limit the endpoint declared.
    /// </summary>
    /// <remarks>
    /// Resolution order is: this, then the attribute, then <see cref="MaxConcurrency"/>. The attribute
    /// outranks the blanket default deliberately — an endpoint that declares a limit is asserting
    /// something about itself that an operator setting a global default has no way to know, and
    /// silently widening it would turn a safety limit into a footgun. Overriding it still works; it
    /// just has to be said explicitly, by name.
    /// </remarks>
    public Dictionary<string, int> Concurrency { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// How long a request waits for a concurrency slot before being shed with 503. Default 0, meaning
    /// each endpoint's own <c>QueueTimeoutSeconds</c> applies.
    /// </summary>
    public int QueueTimeoutSeconds { get; set; }

    /// <summary>
    /// Resolves the in-flight limit for a route. See <see cref="Concurrency"/> for why the attribute
    /// outranks the blanket default.
    /// </summary>
    public int ResolveConcurrency(string route, int declared)
    {
        if (Concurrency.TryGetValue(route, out var configured)) return Math.Max(0, configured);
        return declared > 0 ? declared : Math.Max(0, MaxConcurrency);
    }

    /// <summary>Resolves the queue timeout for a route, preferring the global override when set.</summary>
    public int ResolveQueueTimeout(int declared) =>
        QueueTimeoutSeconds > 0 ? QueueTimeoutSeconds : declared;
}

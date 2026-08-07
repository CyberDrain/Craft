namespace Craft.Configuration;

/// <summary>
/// Central configuration for the Craft (CyberDrain Runtime for Apps, Functions, Tasks) host.
/// All application-specific behavior is driven by these settings — the host itself
/// is generic. Bind from the "App" section of appsettings.json.
///
/// To onboard a new PowerShell application:
///   1. Place your compiled PS modules in API/Modules/
///   2. Place your frontend build in Frontend/
///   3. Configure — App__* env vars in containers; dotnet user-secrets for local secrets;
///      optional non-secret appsettings.json in a downstream image
///   4. Run the host / container
/// </summary>
public class CraftSettings
{
    /// <summary>Display name of the hosted application (used in logs and diagnostics).</summary>
    // "Craft", not "App": the shipped appsettings.json used to set this explicitly, so the effective
    // default has always been "Craft" in every real deployment. When that file was demoted to a
    // documentation-only example, the C# default became the shipped default — and had to match, or the
    // app would have quietly renamed itself in every log line. Covered by ConfigurationReferenceTests.
    public string Name { get; set; } = "Craft";

    /// <summary>
    /// Controls when Kestrel starts accepting connections (when Azure marks the container as started).
    /// - Immediate: Kestrel starts first, init runs in background (default — shows loading page quickly)
    /// - HttpReady: Kestrel starts after HTTP worker pool is ready (API can serve on first request)
    /// - AllReady: Kestrel starts after all worker pools (HTTP + BG) are fully initialized
    /// Azure App Service has a 230s startup timeout — if init exceeds this, the container is killed.
    /// </summary>
    public string ReadinessMode { get; set; } = "Immediate";

    /// <summary>
    /// Kestrel request timeout in seconds. Controls how long Kestrel will wait for a complete request
    /// (headers + body) before aborting. Does NOT control how long the PowerShell script can run —
    /// use Worker.HttpTimeoutSeconds for that.
    /// 
    /// If not explicitly set (or set to 0):
    ///   - Derives from Worker.HttpTimeoutSeconds if > 0
    ///   - Otherwise defaults to 0 (no timeout)
    /// 
    /// Recommended: Set slightly higher than HttpTimeoutSeconds to give the worker time to respond
    /// before Kestrel drops the connection. Example: HttpTimeoutSeconds=120, KestrelTimeoutSeconds=130.
    /// </summary>
    public int KestrelTimeoutSeconds { get; set; }

    /// <summary>Worker configuration for the PowerShell runspace pools.</summary>
    public WorkerSettings Worker { get; set; } = new();

    /// <summary>Authentication and authorization settings.</summary>
    public AuthSettings Auth { get; set; } = new();

    /// <summary>Task scheduler configuration.</summary>
    public SchedulerSettings Scheduler { get; set; } = new();

    /// <summary>Orchestrator (fan-out/fan-in) configuration.</summary>
    public OrchestratorSettings Orchestrator { get; set; } = new();

    /// <summary>Background concurrency limiter. See <see cref="BackgroundLimiterSettings"/>.</summary>
    public BackgroundLimiterSettings BackgroundLimiter { get; set; } = new();

    /// <summary>Response cache configuration.</summary>
    public CacheSettings Cache { get; set; } = new();

    /// <summary>File-backed log output with size-based rotation.</summary>
    public FileLoggingSettings FileLogging { get; set; } = new();

    /// <summary>Script repository — where to find PowerShell modules, HTTP endpoints, background scripts.</summary>
    public ScriptRepoSettings Scripts { get; set; } = new();

    /// <summary>Bootstrap setup — built-in first-run wizard for EasyAuth + app registration.</summary>
    public SetupSettings Setup { get; set; } = new();

    /// <summary>OAuth protected resource metadata (RFC 9728) served for MCP/OAuth discovery.</summary>
    public PrmSettings Prm { get; set; } = new();

    /// <summary>Historical stats collection — rolling time-series of worker/job metrics.</summary>
    public StatsHistorySettings StatsHistory { get; set; } = new();

    /// <summary>Container restart tracking — detects crash loops and forces worker reallocation.</summary>
    public ContainerHealthSettings ContainerHealth { get; set; } = new();

    /// <summary>Frontend serving policy — CSP header injection (EasyAuth handles auth/redirects).</summary>
    public FrontendSettings Frontend { get; set; } = new();

    /// <summary>
    /// Deployment roles (capabilities) — which parts of the host this process serves. One image, three
    /// independent switches. See <see cref="RolesSettings"/>. Also settable via the CRAFT_SERVE_FRONTEND /
    /// CRAFT_SERVE_API / CRAFT_RUN_BACKGROUND environment variables (which take precedence).
    /// </summary>
    public RolesSettings Roles { get; set; } = new();

    /// <summary>Health probe endpoint configuration. See <see cref="HealthSettings"/>.</summary>
    public HealthSettings Health { get; set; } = new();

    /// <summary>Azure Storage connection policy — see <see cref="StorageSettings"/>. Governs the dev-emulator fallback.</summary>
    public StorageSettings Storage { get; set; } = new();

    /// <summary>
    /// Native C# endpoints hosted alongside the PowerShell ones. Off unless an application names the
    /// assemblies to scan. See <see cref="EndpointSettings"/>.
    /// </summary>
    public EndpointSettings Endpoints { get; set; } = new();

    /// <summary>Kestrel request limits (body size, connection cap). See <see cref="KestrelLimitsSettings"/>.</summary>
    public KestrelLimitsSettings Limits { get; set; } = new();

    /// <summary>Request rate limiting. See <see cref="RateLimitSettings"/>. On by default.</summary>
    public RateLimitSettings RateLimit { get; set; } = new();

    /// <summary>Realtime SSE channel (<c>/.craft/events</c>). See <see cref="RealtimeSettings"/>.</summary>
    public RealtimeSettings Realtime { get; set; } = new();
}

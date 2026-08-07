using Craft.Configuration;

namespace Craft.Hosting;

/// <summary>
/// Which capabilities this process runs, plus the handful of host-wide toggles whose defaults are
/// derived from them.
/// <para>
/// One image, three independent switches:
/// <list type="bullet">
///   <item><description><b>Frontend</b> — serve static web content from <c>Frontend/</c></description></item>
///   <item><description><b>Http</b> — serve <c>/api</c> + auth via the HTTP PowerShell pool</description></item>
///   <item><description><b>Background</b> — scheduler / orchestrator / job manager / stats via the BG pool</description></item>
/// </list>
/// </para>
/// <para>
/// Resolution rule: if <i>any</i> role is explicitly set — <c>CRAFT_SERVE_*</c> / <c>CRAFT_RUN_*</c>
/// environment variables win over <c>App:Roles</c> — then exactly those apply and anything unset is
/// off. If nothing at all is set, all three are on (the combined monolith). Roles are declared by
/// enabling what you want, not by disabling what you don't.
/// </para>
/// </summary>
public sealed class CraftRoles
{
    private CraftRoles(bool frontend, bool http, bool background,
                       bool responseCacheEnabled, bool healthEnabled, string healthPath,
                       bool compressionEnabled)
    {
        Frontend = frontend;
        Http = http;
        Background = background;
        ResponseCacheEnabled = responseCacheEnabled;
        HealthEnabled = healthEnabled;
        HealthPath = healthPath;
        CompressionEnabled = compressionEnabled;
    }

    /// <summary>Serve static web content from <c>Frontend/</c>.</summary>
    public bool Frontend { get; }

    /// <summary>Serve <c>/api</c> and auth endpoints via the HTTP PowerShell pool.</summary>
    public bool Http { get; }

    /// <summary>Run scheduler, orchestrator, job manager and stats via the background pool.</summary>
    public bool Background { get; }

    /// <summary>Whether this host runs any PowerShell at all.</summary>
    public bool RunsPowerShell => Http || Background;

    /// <summary>
    /// True when no role is enabled — a misconfiguration the host cannot serve anything under.
    /// </summary>
    public bool None => !Frontend && !Http && !Background;

    /// <summary>
    /// Response cache. Defaults on only when a node serves BOTH a browser UI and its API (combined or
    /// frontend+http); off for api-only, worker-only and static-only nodes. Overridden by
    /// <c>App:Cache:Enabled</c> or <c>CRAFT_RESPONSE_CACHE</c>.
    /// </summary>
    public bool ResponseCacheEnabled { get; }

    /// <summary>Whether the role-agnostic liveness/readiness endpoint is mapped.</summary>
    public bool HealthEnabled { get; }

    /// <summary>Health endpoint path, always normalised to start with <c>/</c>.</summary>
    public string HealthPath { get; }

    /// <summary>
    /// When false the host serves all static content raw/identity: precompressed <c>.br</c>/<c>.gz</c>
    /// siblings are not served and on-the-fly compression is not applied.
    /// </summary>
    public bool CompressionEnabled { get; }

    /// <summary>
    /// Resolves roles and derived toggles from configuration and the environment.
    /// </summary>
    /// <param name="settings">Bound <c>App:</c> configuration.</param>
    /// <param name="env">
    /// Environment variable lookup. Injected rather than calling
    /// <see cref="Environment.GetEnvironmentVariable(string)"/> directly so this stays a pure function
    /// and can be tested without mutating process state.
    /// </param>
    public static CraftRoles Resolve(CraftSettings settings, Func<string, string?> env)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(env);

        var roleFrontend = EnvFlag.Read(env, "CRAFT_SERVE_FRONTEND") ?? settings.Roles.Frontend;
        var roleHttp = EnvFlag.Read(env, "CRAFT_SERVE_API") ?? settings.Roles.Http;
        var roleBackground = EnvFlag.Read(env, "CRAFT_RUN_BACKGROUND") ?? settings.Roles.Background;

        var anyExplicit = roleFrontend.HasValue || roleHttp.HasValue || roleBackground.HasValue;

        bool frontend, http, background;
        if (anyExplicit)
        {
            frontend = roleFrontend ?? false;
            http = roleHttp ?? false;
            background = roleBackground ?? false;
        }
        else
        {
            frontend = http = background = true;
        }

        var cacheEnabled = settings.Cache.ResolveEnabled(frontend && http, env);
        var healthEnabled = settings.Health.ResolveEnabled(env);
        var healthPath = settings.Health.ResolvePath(env);
        var compressionEnabled = settings.Frontend.ResolveCompressionEnabled(env);

        return new CraftRoles(frontend, http, background,
                              cacheEnabled, healthEnabled, healthPath, compressionEnabled);
    }

    /// <summary>
    /// Resolve roles from <c>IConfiguration</c> without a full <see cref="CraftSettings"/> bind —
    /// only the sections that drive role toggles are read. Used before <c>builder.Build()</c>.
    /// </summary>
    public static CraftRoles Resolve(IConfiguration configuration, Func<string, string?>? env = null)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        var settings = new CraftSettings();
        configuration.GetSection("App:Roles").Bind(settings.Roles);
        configuration.GetSection("App:Cache").Bind(settings.Cache);
        configuration.GetSection("App:Health").Bind(settings.Health);
        configuration.GetSection("App:Frontend").Bind(settings.Frontend);
        return Resolve(settings, env ?? Environment.GetEnvironmentVariable);
    }

    /// <summary>Convenience overload reading the real process environment.</summary>
    public static CraftRoles Resolve(CraftSettings settings) =>
        Resolve(settings, Environment.GetEnvironmentVariable);

    /// <summary>Operator-facing summary, e.g. <c>Frontend+Http+Background</c>.</summary>
    public override string ToString()
    {
        if (None) return "none";
        var parts = new List<string>(3);
        if (Frontend) parts.Add("Frontend");
        if (Http) parts.Add("Http");
        if (Background) parts.Add("Background");
        return string.Join("+", parts);
    }
}

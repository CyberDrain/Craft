namespace Craft.Configuration;

/// <summary>
/// Deployment roles (capabilities). Each is a nullable bool: <c>null</c> = "not explicitly set".
///
/// Resolution (in Program.cs): if ANY of the three is explicitly set (here or via CRAFT_SERVE_*/CRAFT_RUN_*
/// env), the host uses exactly those (unset → off). Otherwise all three default on (the combined monolith).
///
/// Presets that fall out of the flags:
///   frontend        Frontend                       — pure static host (CDN origin), no PowerShell
///   http            Http                           — API node; can queue orchestrations, processed elsewhere
///   background      Background                     — worker node; scheduler + orchestrator processing
///   backend         Http + Background              — self-contained API + workers, no frontend
///   frontend+http   Frontend + Http                — app node without background workers
///   combined        Frontend + Http + Background   — the default monolith
/// </summary>
public class RolesSettings
{
    /// <summary>Serve static web content from Frontend/. Null = not explicitly set.</summary>
    public bool? Frontend { get; set; }

    /// <summary>Serve /api + auth via the HTTP PowerShell pool. Null = not explicitly set.</summary>
    public bool? Http { get; set; }

    /// <summary>Run scheduler / orchestrator / job-manager / stats via the BG pool. Null = not explicitly set.</summary>
    public bool? Background { get; set; }
}

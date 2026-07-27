namespace Craft.Configuration;

/// <summary>
/// Role-agnostic health probe. Enabled by default at <c>/healthz</c>; a deployment can relocate it behind a
/// specific probe URL or turn it off entirely. Overridable via CRAFT_HEALTH_ENABLED / CRAFT_HEALTH_PATH.
/// </summary>
public class HealthSettings
{
    /// <summary>Whether the health endpoint is mapped. Default true.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Path the health endpoint is served at. Default "/healthz". A leading slash is added if missing.</summary>
    public string Path { get; set; } = "/healthz";
}

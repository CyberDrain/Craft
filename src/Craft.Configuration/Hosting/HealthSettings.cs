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

    /// <summary>
    /// Resolved enabled state. <c>CRAFT_HEALTH_ENABLED</c> wins when set; otherwise
    /// <see cref="Enabled"/> applies.
    /// </summary>
    public bool IsEnabled => ResolveEnabled(Environment.GetEnvironmentVariable);

    /// <summary>
    /// Resolved path, always normalised to start with <c>/</c>. <c>CRAFT_HEALTH_PATH</c> wins when set
    /// to a non-blank value; otherwise <see cref="Path"/> applies.
    /// </summary>
    public string ResolvedPath => ResolvePath(Environment.GetEnvironmentVariable);

    /// <summary>Same as <see cref="IsEnabled"/> but with an injectable environment lookup (for tests).</summary>
    public bool ResolveEnabled(Func<string, string?> env)
    {
        ArgumentNullException.ThrowIfNull(env);
        var v = env("CRAFT_HEALTH_ENABLED");
        if (string.IsNullOrWhiteSpace(v)) return Enabled;
        return v.Equals("true", StringComparison.OrdinalIgnoreCase) || v == "1";
    }

    /// <summary>Same as <see cref="ResolvedPath"/> but with an injectable environment lookup (for tests).</summary>
    public string ResolvePath(Func<string, string?> env)
    {
        ArgumentNullException.ThrowIfNull(env);
        var pathEnv = env("CRAFT_HEALTH_PATH");
        var path = !string.IsNullOrWhiteSpace(pathEnv) ? pathEnv.Trim() : Path;
        if (!path.StartsWith('/')) path = "/" + path;
        return path;
    }
}

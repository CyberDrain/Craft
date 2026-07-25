namespace Craft.Hosting;

/// <summary>
/// Parsing for CRAFT's tri-state environment flags.
/// <para>
/// Tri-state matters: "unset" and "false" are different answers. An unset flag falls back to the
/// <c>App:</c> configuration value, while an explicit <c>false</c> overrides it. Collapsing the two
/// into a plain <see cref="bool"/> would make it impossible for a deployment to turn something off
/// via environment variable alone.
/// </para>
/// </summary>
public static class EnvFlag
{
    /// <summary>
    /// Parses a raw environment value: <see langword="null"/> when unset or blank, otherwise
    /// <see langword="true"/> for "true" (any casing) or "1", and <see langword="false"/> for anything else.
    /// </summary>
    public static bool? Parse(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        return value.Equals("true", StringComparison.OrdinalIgnoreCase) || value == "1";
    }

    /// <summary>Reads <paramref name="name"/> through <paramref name="env"/> and parses it.</summary>
    public static bool? Read(Func<string, string?> env, string name) => Parse(env(name));
}

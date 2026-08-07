namespace Craft.Setup;

/// <summary>
/// The first-run setup wizard's HTML pages.
/// <para>
/// The markup lives in <c>Services/Setup/*.html</c> — real files, so editors, formatters and linters
/// can actually parse them — and is compiled into Craft.dll as an embedded resource. Embedded rather
/// than copied to the output directory on purpose: these pages are part of the runtime, not app
/// content, and there is no legitimate reason for a downstream image to replace them. It also keeps
/// them working regardless of the process working directory.
/// </para>
/// <para>
/// The wizard uses device code flow, so no redirect URI is required.
/// </para>
/// </summary>
public static class SetupPages
{
    /// <summary>
    /// Resource name prefix. Set by the <c>LogicalName</c> metadata on the EmbeddedResource items in
    /// Craft.csproj — it is deliberately independent of the folder layout so moving these files does
    /// not silently change the lookup key.
    /// </summary>
    private const string ResourcePrefix = "Craft.Setup.";

    // Read once on first use and cached for the process lifetime. Lazy<T> gives thread-safe
    // single-execution without a lock in the hot path; these pages never change at runtime.
    private static readonly Lazy<string> s_indexHtml = new(() => Load("index.html"));
    private static readonly Lazy<string> s_startupHtml = new(() => Load("startup.html"));

    /// <summary>The setup wizard itself, served at <c>/setup</c>.</summary>
    public static string IndexHtml => s_indexHtml.Value;

    /// <summary>The "still starting up" holding page served to browsers before the app is ready.</summary>
    public static string StartupHtml => s_startupHtml.Value;

    private static string Load(string fileName)
    {
        var resourceName = ResourcePrefix + fileName;
        var assembly = typeof(SetupPages).Assembly;

        using var stream = assembly.GetManifestResourceStream(resourceName);
        if (stream is null)
        {
            // A build configuration error, not a runtime condition — fail loudly rather than serving a
            // blank page that looks like a broken wizard. The available names make the fix obvious.
            var available = string.Join(", ", assembly.GetManifestResourceNames());
            throw new InvalidOperationException(
                $"Embedded setup page '{resourceName}' not found. Check the EmbeddedResource items in " +
                $"Craft.csproj. Available resources: [{available}]");
        }

        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}

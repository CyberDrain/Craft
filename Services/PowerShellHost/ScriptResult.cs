// NAMESPACE PINNED — do not change.
// Downstream PowerShell reaches these types by fully-qualified name, e.g.
//   [Craft.Services.RealtimeBridge]::Publish($userId, $jobId, 'start', $data)
// Renaming the namespace compiles fine and then fails at runtime in the hosted app
// ("Unable to find type"). Type forwarding cannot help — it only works across assemblies.
// The folder is free to move; the namespace is a published contract.
namespace Craft.Services;

public class ScriptResult
{
    public int StatusCode { get; set; } = 200;
    public string Body { get; set; } = "{}";

    /// <summary>
    /// Raw response bytes for a handler that returned a <c>byte[]</c> body (e.g. an image).
    /// When set, <see cref="Body"/> is empty and the response writer must send these bytes
    /// unserialized — pushing them through the JSON path is how images came back as
    /// integer arrays.
    /// </summary>
    public byte[]? BodyBytes { get; set; }

    /// <summary>
    /// Response headers the handler asked for, already normalised by
    /// <see cref="Craft.Hosting.HandlerHeaders.FromPowerShell"/>. Null when the handler set none,
    /// which is the overwhelmingly common case — a redirect's <c>Location</c> is the reason this
    /// exists.
    /// </summary>
    public Dictionary<string, string>? Headers { get; set; }

    /// <summary>
    /// Content type the handler asked for. Null means
    /// <see cref="Craft.Hosting.HandlerHeaders.DefaultContentType"/>.
    /// </summary>
    public string? ContentType { get; set; }
}

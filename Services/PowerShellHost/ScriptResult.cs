// NAMESPACE PINNED — do not change.
// Downstream PowerShell reaches these types by fully-qualified name, e.g.
//   [Craft.Services.RealtimeBridge]::Publish($userId, $jobId, 'start', $data)
// Renaming the namespace compiles fine and then fails at runtime in the hosted app
// ("Unable to find type"). Type forwarding cannot help — it only works across assemblies.
// The folder is free to move; the namespace is a published contract.
using System.Text;

namespace Craft.Services;

public class ScriptResult
{
    public int StatusCode { get; set; } = 200;

    /// <summary>
    /// The response body exactly as it goes on the wire: UTF-8 JSON or text, or a handler's raw
    /// <c>byte[]</c> (an image). Held as bytes so a large body is never also kept as a UTF-16 string.
    /// </summary>
    public byte[] BodyBytes { get; set; } = "{}"u8.ToArray();

    /// <summary>The body as text, decoded on every read. Hot paths use <see cref="BodyBytes"/>.</summary>
    public string Body
    {
        get => Encoding.UTF8.GetString(BodyBytes);
        set => BodyBytes = Encoding.UTF8.GetBytes(value);
    }

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

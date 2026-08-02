using System.IO.Pipelines;
using System.Text;
using System.Text.Json;
using Craft.Hosting;

namespace Craft.Endpoints;

/// <summary>
/// What a native endpoint returns. A small closed set rather than an open <c>IResult</c>, so that
/// every response goes through the same header sanitisation and content-type handling the PowerShell
/// path uses — those are security controls, and a second response path that skips them is how a
/// response-splitting bug reaches production in one dispatcher but not the other.
/// </summary>
public readonly struct CraftResult
{
    private enum Kind { Json, Bytes, Stream, Handled }

    private readonly Kind _kind;
    private readonly string? _text;
    private readonly ReadOnlyMemory<byte> _bytes;
    private readonly Func<PipeWriter, CancellationToken, Task>? _writer;

    private CraftResult(Kind kind, int status, string? contentType,
        IReadOnlyDictionary<string, string>? headers,
        string? text = null,
        ReadOnlyMemory<byte> bytes = default,
        Func<PipeWriter, CancellationToken, Task>? writer = null)
    {
        _kind = kind;
        StatusCode = status;
        ContentType = contentType;
        Headers = headers;
        _text = text;
        _bytes = bytes;
        _writer = writer;
    }

    public int StatusCode { get; }
    public string? ContentType { get; }
    public IReadOnlyDictionary<string, string>? Headers { get; }

    /// <summary>
    /// Body is ALREADY valid JSON and is written verbatim.
    ///
    /// <para>
    /// This is the exact counterpart of the PowerShell path's rule that a Body which is already valid
    /// JSON passes through unserialized, and it is what makes a proxy endpoint cheap: the upstream's
    /// bytes go straight out without being parsed into objects and rebuilt. Measured on the bulk
    /// endpoint, avoiding that round trip took CPU per request from 105.9 to 26.4 core-ms at
    /// batch=100.
    /// </para>
    /// </summary>
    /// <remarks>
    /// Named distinctly from <see cref="Json{T}"/> on purpose. An overload pair of
    /// <c>Json(string)</c> and <c>Json&lt;T&gt;(T)</c> is ambiguous for a string argument, and the
    /// two do opposite things — one passes bytes through, the other serializes. Ambiguity between
    /// "write this verbatim" and "serialize this" is not a resolution the compiler should be making.
    /// </remarks>
    public static CraftResult RawJson(string rawJson, int status = 200,
        IReadOnlyDictionary<string, string>? headers = null) =>
        new(Kind.Json, status, "application/json", headers, text: rawJson);

    /// <summary>Serializes <paramref name="value"/> with System.Text.Json.</summary>
    public static CraftResult Json<T>(T value, int status = 200,
        JsonSerializerOptions? options = null,
        IReadOnlyDictionary<string, string>? headers = null) =>
        new(Kind.Json, status, "application/json", headers,
            text: JsonSerializer.Serialize(value, options));

    public static CraftResult Bytes(ReadOnlyMemory<byte> payload, string contentType,
        int status = 200, IReadOnlyDictionary<string, string>? headers = null) =>
        new(Kind.Bytes, status, contentType, headers, bytes: payload);

    /// <summary>
    /// Writes the body incrementally to the response pipe, at whatever memory the writer chooses to
    /// use rather than at the size of the result.
    ///
    /// <para>
    /// This is the reason the whole native-endpoint mechanism is worth building. The PowerShell
    /// equivalent of a large result is a materialised object graph per concurrent request: the
    /// table-dump endpoint peaked at 648 MiB of RSS with 8 callers, and adding pool workers made it
    /// worse rather than better. Streaming makes that flat.
    /// </para>
    /// </summary>
    public static CraftResult Stream(Func<PipeWriter, CancellationToken, Task> write,
        string contentType = "application/json", int status = 200,
        IReadOnlyDictionary<string, string>? headers = null) =>
        new(Kind.Stream, status, contentType, headers, writer: write);

    /// <summary>
    /// An error body in the same <c>{"error":"..."}</c> shape the PowerShell dispatcher produces, so
    /// a migrated endpoint's failures look identical to its callers.
    /// </summary>
    public static CraftResult Problem(int status, string message) =>
        new(Kind.Json, status, "application/json", null,
            text: JsonSerializer.Serialize(new { error = message }));

    /// <summary>
    /// The endpoint wrote the response itself and the dispatcher should not touch it. For the cases a
    /// closed result set cannot express — server-sent events, a custom protocol, direct BodyWriter use.
    /// </summary>
    public static CraftResult Handled { get; } = new(Kind.Handled, 0, null, null);

    /// <summary>Writes this result to the response.</summary>
    internal async Task WriteAsync(HttpContext context, CancellationToken ct)
    {
        if (_kind == Kind.Handled) return;

        context.Response.StatusCode = StatusCode;
        if (!string.IsNullOrEmpty(ContentType)) context.Response.ContentType = ContentType;

        if (Headers is { Count: > 0 })
        {
            // Through the same sanitiser the PowerShell path uses: it strips CR/LF and refuses
            // reserved names. Duplicating that logic here would eventually let the two drift.
            var sanitised = HandlerHeaders.FromPowerShell(
                Headers.ToDictionary(h => h.Key, h => (object?)h.Value));
            if (sanitised is not null)
            {
                foreach (var (name, value) in sanitised)
                {
                    context.Response.Headers[name] = value;
                }
            }
        }

        switch (_kind)
        {
            case Kind.Json:
                await context.Response.WriteAsync(_text ?? "null", Encoding.UTF8, ct);
                break;

            case Kind.Bytes:
                await context.Response.BodyWriter.WriteAsync(_bytes, ct);
                break;

            case Kind.Stream:
                await _writer!(context.Response.BodyWriter, ct);
                await context.Response.BodyWriter.FlushAsync(ct);
                break;
        }
    }
}

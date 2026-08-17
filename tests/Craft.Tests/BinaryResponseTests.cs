using System.Collections;
using System.Management.Automation;
using Craft.Services;
using Microsoft.Azure.Functions.PowerShellWorker;

namespace Craft.Tests;

/// <summary>
/// A handler's byte[] body used to be pushed through the JSON serializer and reach the client
/// as an integer array ([137,80,78,71,...] instead of a PNG), and a string body with an
/// explicitly non-JSON content type was JSON-validated and re-quoted. These tests pin the
/// content-type-aware extraction: bytes survive verbatim, non-JSON strings pass through raw,
/// and everything that declares (or defaults to) JSON keeps the original behaviour.
/// </summary>
public class BinaryResponseTests
{
    private static readonly byte[] PngHeader = [137, 80, 78, 71, 13, 10, 26, 10];

    private static ScriptResult Extract(object response) =>
        PowerShellRunnerService.ExtractResponse([PSObject.AsPSObject(response)]);

    [Fact]
    public void ByteArrayBody_SurvivesVerbatim_WithDeclaredContentType()
    {
        // the exact shape Invoke-ListUserPhoto returns
        var result = Extract(new HttpResponseContext
        {
            StatusCode = 200,
            Body = PngHeader,
            ContentType = "image/png",
        });

        Assert.Equal(PngHeader, result.BodyBytes);
        Assert.Equal("image/png", result.ContentType);
        Assert.Equal(string.Empty, result.Body);
    }

    [Fact]
    public void ByteArrayBody_WithoutContentType_FallsBackToOctetStream()
    {
        // hashtable form carries no ContentType default, unlike HttpResponseContext
        var result = Extract(new Hashtable
        {
            ["StatusCode"] = 200,
            ["Body"] = PngHeader,
        });

        Assert.Equal(PngHeader, result.BodyBytes);
        Assert.Equal("application/octet-stream", result.ContentType);
    }

    [Fact]
    public void PsObjectWrappedByteArray_IsStillTreatedAsBinary()
    {
        var result = Extract(new Hashtable
        {
            ["StatusCode"] = 200,
            ["Body"] = PSObject.AsPSObject(PngHeader),
            ["ContentType"] = "image/jpeg",
        });

        Assert.Equal(PngHeader, result.BodyBytes);
        Assert.Equal("image/jpeg", result.ContentType);
    }

    [Fact]
    public void StringBody_WithNonJsonContentType_PassesThroughRaw()
    {
        var result = Extract(new HttpResponseContext
        {
            StatusCode = 200,
            Body = "<html><body>hi</body></html>",
            ContentType = "text/html",
        });

        // the old path JSON-quoted this into "<html>..."
        Assert.Equal("<html><body>hi</body></html>", result.Body);
        Assert.Equal("text/html", result.ContentType);
        Assert.Null(result.BodyBytes);
    }

    [Fact]
    public void JsonStringBody_WithDefaultContentType_IsUnchanged()
    {
        // HttpResponseContext defaults ContentType to application/json - the common CIPP case
        var result = Extract(new HttpResponseContext
        {
            StatusCode = 200,
            Body = """{"Results":"ok"}""",
        });

        Assert.Equal("""{"Results":"ok"}""", result.Body);
        Assert.Null(result.BodyBytes);
    }

    [Fact]
    public void NonJsonStringBody_WithJsonContentType_IsStillSerialized()
    {
        // declared JSON keeps the original coercion: a bare string becomes a JSON string
        var result = Extract(new HttpResponseContext
        {
            StatusCode = 200,
            Body = "plain text",
        });

        Assert.Equal("\"plain text\"", result.Body);
    }

    [Fact]
    public void ProblemJsonContentType_CountsAsJson()
    {
        var result = Extract(new HttpResponseContext
        {
            StatusCode = 400,
            Body = "not json",
            ContentType = "application/problem+json",
        });

        // suffix types must not take the raw-passthrough branch
        Assert.Equal("\"not json\"", result.Body);
    }
}

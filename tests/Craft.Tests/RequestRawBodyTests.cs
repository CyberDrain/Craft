using System.Collections;
using System.Text;
using Craft.Services;
using Microsoft.AspNetCore.Http;

namespace Craft.Tests;

/// <summary>
/// An application/octet-stream request reaches PowerShell as <c>$Request.RawBody</c> (byte[]) with
/// <c>Body</c> left null. Reading it as UTF-8 text corrupted every binary upload, and putting the bytes in
/// <c>Body</c> instead made each request seconds slower: handlers that read <c>$Request.Body.someField</c>
/// member-enumerate every byte of an array. The key is always present so a handler can tell this host
/// supplies raw bodies.
/// </summary>
public class RequestRawBodyTests
{
    private static Task<Hashtable> Snapshot(byte[] bytes, string contentType)
    {
        var context = new DefaultHttpContext();
        context.Request.Method = "POST";
        context.Request.Body = new MemoryStream(bytes);
        context.Request.ContentLength = bytes.Length;
        context.Request.ContentType = contentType;
        return PowerShellRunnerService.SnapshotRequest(context);
    }

    [Fact]
    public async Task OctetStream_ArrivesAsExactBytesInRawBody()
    {
        // Every byte value, including ones that are invalid as UTF-8.
        var bytes = Enumerable.Range(0, 256).Select(i => (byte)i).ToArray();

        var request = await Snapshot(bytes, "application/octet-stream");

        Assert.Equal(bytes, Assert.IsType<byte[]>(request["RawBody"]));
        Assert.Null(request["Body"]);
    }

    [Fact]
    public async Task Json_StillParsesIntoBody_WithRawBodyKeyPresentButNull()
    {
        var request = await Snapshot(Encoding.UTF8.GetBytes("""{"packageId":"x"}"""), "application/json");

        Assert.True(request.ContainsKey("RawBody"));
        Assert.Null(request["RawBody"]);
        Assert.Equal("x", Assert.IsType<System.Management.Automation.PSObject>(request["Body"]).Properties["packageId"].Value);
    }

    [Fact]
    public async Task OctetStream_LeavesTheBodyStreamReadableAgain()
    {
        var context = new DefaultHttpContext();
        context.Request.Body = new MemoryStream(new byte[] { 1, 2, 3 });
        context.Request.ContentLength = 3;
        context.Request.ContentType = "application/octet-stream";

        await PowerShellRunnerService.SnapshotRequest(context);

        Assert.Equal(0, context.Request.Body.Position);
    }
}

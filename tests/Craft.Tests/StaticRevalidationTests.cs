using System.Net;
using Craft.Hosting;
using Craft.Hosting.Endpoints;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Craft.Tests;

/// <summary>A browser revalidating a static file with the ETag it was given gets 304 and no body, on every serving path.</summary>
public sealed class StaticRevalidationTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("craft-static-").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* temp dir */ }
    }

    [Theory]
    [InlineData("/app.js", "br")]        // precompressed sibling
    [InlineData("/app.js", null)]        // plain file
    [InlineData("/logo.png", "br")]      // not compressible: plain file
    [InlineData("/users", "br")]         // prerendered page, precompressed
    [InlineData("/users", null)]         // prerendered page, plain
    [InlineData("/some/route", "br")]    // SPA fallback to index.html
    public async Task AMatchingIfNoneMatch_Gets304WithNoBody(string path, string? acceptEncoding)
    {
        foreach (var name in new[] { "app.js", "users.html", "index.html" })
        {
            File.WriteAllText(Path.Combine(_dir, name), new string('a', 4000));
            File.WriteAllText(Path.Combine(_dir, name + ".br"), "precompressed");
        }
        File.WriteAllBytes(Path.Combine(_dir, "logo.png"), new byte[2000]);

        var builder = WebApplication.CreateBuilder();
        builder.WebHost.ConfigureKestrel(o => o.Listen(IPAddress.Loopback, 0));
        builder.Logging.ClearProviders();
        builder.Services.AddCraftResponseCompression();
        var app = builder.Build();
        app.UseResponseCompression();
        var provider = app.UseCraftStaticFiles(_dir, compressionEnabled: true, NullLogger.Instance);
        app.MapCraftFrontendFallback(new FrontendFallbackOptions(provider, null, true, _dir), NullLogger.Instance);
        await app.StartAsync();
        try
        {
            var addr = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First();
            using var handler = new HttpClientHandler { AutomaticDecompression = DecompressionMethods.None };
            using var client = new HttpClient(handler);

            async Task<HttpResponseMessage> Get(string? ifNoneMatch)
            {
                var req = new HttpRequestMessage(HttpMethod.Get, addr + path);
                if (acceptEncoding is not null) req.Headers.TryAddWithoutValidation("Accept-Encoding", acceptEncoding);
                if (ifNoneMatch is not null) req.Headers.TryAddWithoutValidation("If-None-Match", ifNoneMatch);
                return await client.SendAsync(req);
            }

            var first = await Get(null);
            Assert.Equal(HttpStatusCode.OK, first.StatusCode);
            var etag = first.Headers.ETag!.ToString();

            var revalidated = await Get(etag);
            Assert.Equal(HttpStatusCode.NotModified, revalidated.StatusCode);
            Assert.Empty(await revalidated.Content.ReadAsByteArrayAsync());
            Assert.Equal(etag, revalidated.Headers.ETag!.ToString());
            Assert.Equal(first.Headers.CacheControl!.ToString(), revalidated.Headers.CacheControl!.ToString());

            Assert.Equal(HttpStatusCode.NotModified, (await Get($"\"other\", {etag}")).StatusCode);

            var stale = await Get("\"an-older-build\"");
            Assert.Equal(HttpStatusCode.OK, stale.StatusCode);
            Assert.Equal(await first.Content.ReadAsByteArrayAsync(), await stale.Content.ReadAsByteArrayAsync());
        }
        finally { await app.StopAsync(); }
    }
}

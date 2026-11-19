using System.IO.Compression;
using System.Net;
using System.Text;
using Craft.Caching;
using Craft.Configuration;
using Craft.Hosting;
using Craft.Services;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Craft.Tests;

/// <summary>A cache hit sends the encoded body kept on the entry instead of compressing the body again.</summary>
[Collection(CacheBodyBytesTests.Name)]
public class CacheHitEncodingTests
{
    private static readonly byte[] Body = Encoding.UTF8.GetBytes(
        "{\"items\":[" + string.Join(",", Enumerable.Repeat("\"aaaaaaaaaaaaaaaa\"", 4000)) + "]}");

    [Fact]
    public async Task AHit_SendsTheKeptEncodedBody_PerEncoding()
    {
        using var cache = new CacheService(NullLogger<CacheService>.Instance, new CraftSettings(), enabled: true);
        await cache.Set("ListUsers|enc", new ScriptResult { BodyBytes = Body });

        var builder = WebApplication.CreateBuilder();
        builder.WebHost.ConfigureKestrel(o => o.Listen(IPAddress.Loopback, 0));
        builder.Logging.ClearProviders();
        builder.Services.AddCraftResponseCompression();
        var app = builder.Build();
        app.UseResponseCompression();
        app.Run(async ctx =>
        {
            ctx.Response.ContentType = ctx.Request.Query["type"].FirstOrDefault() ?? "application/json";
            var hit = await cache.Get("ListUsers|enc", "ListUsers");
            await cache.WriteHitBodyAsync(ctx, "ListUsers|enc", hit!.Result.BodyBytes);
        });
        await app.StartAsync();
        try
        {
            var addr = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First();
            using var handler = new HttpClientHandler { AutomaticDecompression = DecompressionMethods.None };
            using var client = new HttpClient(handler);

            async Task<(string Encoding, long? Length, byte[] Bytes)> Get(string? accept, string query = "")
            {
                var req = new HttpRequestMessage(HttpMethod.Get, addr + "/" + query);
                if (accept is not null) req.Headers.TryAddWithoutValidation("Accept-Encoding", accept);
                var resp = await client.SendAsync(req);
                return (resp.Content.Headers.ContentEncoding.FirstOrDefault() ?? "", resp.Content.Headers.ContentLength,
                    await resp.Content.ReadAsByteArrayAsync());
            }

            var first = await Get("br");
            var second = await Get("br");
            var gzip = await Get("gzip");
            var plain = await Get(null);
            var image = await Get("br", "?type=image/png");

            Assert.Equal("br", first.Encoding);
            Assert.Equal(first.Bytes.Length, first.Length);
            Assert.Equal(first.Bytes, second.Bytes);
            using var br = new BrotliStream(new MemoryStream(second.Bytes), CompressionMode.Decompress);
            using var plainOfBr = new MemoryStream();
            await br.CopyToAsync(plainOfBr);
            Assert.Equal(Body, plainOfBr.ToArray());

            Assert.Equal("gzip", gzip.Encoding);
            using var gz = new GZipStream(new MemoryStream(gzip.Bytes), CompressionMode.Decompress);
            using var plainOfGzip = new MemoryStream();
            await gz.CopyToAsync(plainOfGzip);
            Assert.Equal(Body, plainOfGzip.ToArray());

            Assert.Equal("", plain.Encoding);
            Assert.Equal(Body, plain.Bytes);
            Assert.Equal("", image.Encoding);
            Assert.Equal(Body, image.Bytes);
        }
        finally { await app.StopAsync(); }
    }
}

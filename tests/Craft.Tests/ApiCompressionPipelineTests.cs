using System.IO.Compression;
using System.Net;
using System.Text;
using Craft.Hosting;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Craft.Tests;

/// <summary>
/// Stands up the real ResponseCompression pipeline on real Kestrel (in-process, dynamic port) to prove
/// /api responses are actually compressed on the wire, and that the egress wire counter — which sits
/// outside the compressor — does not suppress it. The perf-harness caught a live case where /api came
/// back uncompressed; these pin the wiring so a pipeline-ordering regression fails here, not only on a
/// container. Real Kestrel (not TestServer) so the transport matches production.
/// </summary>
public class ApiCompressionPipelineTests : IDisposable
{
    private readonly string _dir =
        Path.Combine(Path.GetTempPath(), "craft-apicomp-" + Guid.NewGuid().ToString("N")[..8]);

    public ApiCompressionPipelineTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* temp dir */ }
        GC.SuppressFinalize(this);
    }

    // A big, highly compressible JSON body — what a List* endpoint looks like to the compressor.
    private static readonly string Payload =
        "{\"ok\":true,\"items\":[" + string.Join(",", Enumerable.Repeat("\"aaaaaaaaaaaaaaaa\"", 4000)) + "]}";

    private static long RawLength => Encoding.UTF8.GetByteCount(Payload);

    private static readonly string[] GetOnly = { "GET" };

    private static bool IsApiPath(HttpContext c) =>
        c.Request.Path.StartsWithSegments("/api", StringComparison.OrdinalIgnoreCase);

    private EgressLedger Ledger() =>
        new(NullLogger<EgressLedger>.Instance, capBytes: 0, flushSeconds: 60,
            Path.Combine(_dir, Guid.NewGuid().ToString("N")[..8] + ".json"));

    /// <summary>
    /// Runs the given pipeline on real Kestrel, GETs /API/thing with Accept-Encoding: gzip (no client
    /// auto-decompression, so a compressed body stays compressed), and returns the response
    /// Content-Encoding + the raw body length. <paramref name="contentType"/> is what the terminal sets.
    /// </summary>
    private async Task<(string encoding, long bytes)> Request(
        Action<WebApplication> configure, string contentType = "application/json", bool routed = false)
    {
        var (enc, body) = await RequestBody(configure, contentType, routed);
        return (enc, body.LongLength);
    }

    private async Task<(string encoding, byte[] body)> RequestBody(
        Action<WebApplication> configure, string contentType = "application/json", bool routed = false,
        string acceptEncoding = "gzip", string? payload = null)
    {
        payload ??= Payload;
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.ConfigureKestrel(o => o.Listen(IPAddress.Loopback, 0)); // dynamic port
        builder.Logging.ClearProviders();
        builder.Services.AddCraftResponseCompression();
        builder.Services.AddSingleton(Ledger());

        var app = builder.Build();
        configure(app);

        // Mirror the dispatcher's write shape: set status + content type, then write the string body.
        async Task Terminal(HttpContext ctx)
        {
            ctx.Response.StatusCode = 200;
            ctx.Response.ContentType = contentType;
            ctx.Response.Headers["X-Cache"] = "MISS";
            await ctx.Response.WriteAsync(payload);
        }

        // routed = the real shape: a mapped endpoint (MapMethods) executed by the endpoint middleware,
        // which minimal hosting adds at the end of the pipeline. terminal = a plain app.Run.
        if (routed) app.MapMethods("/API/{endpoint}", GetOnly, Terminal);
        else app.Run(Terminal);

        await app.StartAsync();
        try
        {
            var addr = app.Services.GetRequiredService<IServer>()
                .Features.Get<IServerAddressesFeature>()!.Addresses.First();

            using var handler = new HttpClientHandler { AutomaticDecompression = DecompressionMethods.None };
            using var client = new HttpClient(handler);
            var req = new HttpRequestMessage(HttpMethod.Get, $"{addr}/API/thing");
            req.Headers.TryAddWithoutValidation("Accept-Encoding", acceptEncoding);

            var resp = await client.SendAsync(req);
            var enc = resp.Content.Headers.ContentEncoding.FirstOrDefault() ?? "";
            return (enc, await resp.Content.ReadAsByteArrayAsync());
        }
        finally { await app.StopAsync(); }
    }

    [Theory]
    [InlineData("br")]
    [InlineData("gzip")]
    public async Task EachEncoding_NegotiatesAlone_AndRoundTrips(string encoding)
    {
        var (enc, body) = await RequestBody(app => app.UseResponseCompression(), acceptEncoding: encoding);
        Assert.Equal(encoding, enc);
        Assert.True(body.LongLength < RawLength, $"expected compressed < {RawLength}, got {body.LongLength}");

        using var input = new MemoryStream(body);
        using Stream decoder = encoding switch
        {
            "br" => new BrotliStream(input, CompressionMode.Decompress),
            _ => new GZipStream(input, CompressionMode.Decompress),
        };
        using var reader = new StreamReader(decoder, Encoding.UTF8);
        Assert.Equal(Payload, await reader.ReadToEndAsync());
    }

    [Fact]
    public async Task StringBody_ReachesEncoderCoalesced_NotAsPipeFragments()
    {
        // Response.WriteAsync(string) arrives at the encoder as ~4 KiB pipe segments. Brotli Fastest
        // compresses each write as an isolated fragment, which made a real 300 KB body ~2.9x the size of
        // compressing it in one go. Coalescing keeps it close to one-shot. Varied JSON, not a repeated
        // string, so the fragment penalty would actually show.
        var varied = "[" + string.Join(",", Enumerable.Range(0, 6000).Select(i =>
            $"{{\"id\":{i},\"tenant\":\"t{i % 97}.onmicrosoft.com\",\"msg\":\"event {i * 7919 % 10007} on {i % 13}\"}}")) + "]";
        var raw = Encoding.UTF8.GetBytes(varied);
        var oneShot = new byte[BrotliEncoder.GetMaxCompressedLength(raw.Length)];
        Assert.True(BrotliEncoder.TryCompress(raw, oneShot, out var oneShotLength, quality: 1, window: 22));

        var (enc, body) = await RequestBody(app => app.UseResponseCompression(), acceptEncoding: "br", payload: varied);

        Assert.Equal("br", enc);
        Assert.True(body.Length < oneShotLength * 1.35,
            $"br Fastest wire {body.Length} B vs one-shot {oneShotLength} B — encoder is being fed fragments");
        using var decoded = new StreamReader(new BrotliStream(new MemoryStream(body), CompressionMode.Decompress));
        Assert.Equal(varied, await decoded.ReadToEndAsync());
    }

    [Fact]
    public async Task BrowserAcceptEncoding_StillPrefersBrotli()
    {
        // Chrome/Firefox send all four at equal q; br must win the tie.
        var (enc, _) = await RequestBody(app => app.UseResponseCompression(), acceptEncoding: "gzip, deflate, br, zstd");
        Assert.Equal("br", enc);
    }

    // ── pipeline shapes, outer→inner, that isolate where compression is lost ──────────────────────────

    [Fact]
    public async Task Bare_ResponseCompression_Compresses()
    {
        var (enc, bytes) = await Request(app => app.UseResponseCompression());
        Assert.Equal("gzip", enc);
        Assert.True(bytes < RawLength, $"expected compressed < {RawLength}, got {bytes}");
    }

    [Fact]
    public async Task WireCounter_ThenResponseCompression_StillCompresses()
    {
        var (enc, bytes) = await Request(app =>
        {
            app.UseMiddleware<ApiEgressWireCounterMiddleware>();
            app.UseResponseCompression();
        });
        Assert.Equal("gzip", enc);
        Assert.True(bytes < RawLength, $"expected compressed < {RawLength}, got {bytes}");
    }

    [Fact]
    public async Task FullProgramShape_UseWhen_WireCounter_ThenCompression_Compresses()
    {
        // Exactly the /api branch from Program.cs.
        var (enc, bytes) = await Request(app =>
            app.UseWhen(IsApiPath, api =>
            {
                api.UseMiddleware<ApiEgressWireCounterMiddleware>();
                api.UseResponseCompression();
            }));
        Assert.Equal("gzip", enc);
        Assert.True(bytes < RawLength, $"expected compressed < {RawLength}, got {bytes}");
    }

    [Fact]
    public async Task ContentTypeWithCharset_StillCompresses()
    {
        // The dispatcher can emit "application/json; charset=utf-8"; ResponseCompression must still match.
        var (enc, bytes) = await Request(app => app.UseResponseCompression(), "application/json; charset=utf-8");
        Assert.Equal("gzip", enc);
        Assert.True(bytes < RawLength, $"expected compressed < {RawLength}, got {bytes}");
    }

    [Fact]
    public async Task RoutedEndpoint_UseWhenApi_Compression_Compresses()
    {
        // The real dispatcher is a MapMethods endpoint, not a terminal app.Run. Minimal hosting inserts
        // routing/endpoint middleware automatically; this proves compression still wraps a routed
        // endpoint's response the way it wraps a terminal one.
        var (enc, bytes) = await Request(
            app => app.UseWhen(IsApiPath, api =>
            {
                api.UseMiddleware<ApiEgressWireCounterMiddleware>();
                api.UseResponseCompression();
            }),
            routed: true);
        Assert.Equal("gzip", enc);
        Assert.True(bytes < RawLength, $"expected compressed < {RawLength}, got {bytes}");
    }

    [Fact]
    public async Task FullRealisticChain_WithCsp_AndEgressLimiter_StillCompresses()
    {
        // Mirrors the real ordering: the /api branch (wire counter + compression) outermost, then a
        // CSP OnStarting header, then the egress limiter (sets the charge flag), then the terminal —
        // to rule out an intervening middleware suppressing compression on the way down.
        var (enc, bytes) = await Request(app =>
        {
            app.UseWhen(IsApiPath, api =>
            {
                api.UseMiddleware<ApiEgressWireCounterMiddleware>();
                api.UseResponseCompression();
            });
            app.Use(async (ctx, next) =>
            {
                ctx.Response.OnStarting(() => { ctx.Response.Headers["Content-Security-Policy"] = "default-src 'self'"; return Task.CompletedTask; });
                await next();
            });
            app.UseMiddleware<ApiEgressLimiterMiddleware>();
        });
        Assert.Equal("gzip", enc);
        Assert.True(bytes < RawLength, $"expected compressed < {RawLength}, got {bytes}");
    }
}

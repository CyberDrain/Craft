using System.IO.Compression;
using System.Net;
using System.Text;
using Craft.Configuration;
using Craft.Hosting;
using Craft.Hosting.Endpoints;
using Craft.Realtime;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Craft.Tests;

/// <summary>
/// The realtime stream goes through the /api compressor on real Kestrel: it negotiates br, and every frame
/// still reaches the browser the moment it is published, because the endpoint flushes and the compressor
/// passes flushes through. A buffered compressor would hold frames until the stream closes, which for SSE
/// is never.
/// </summary>
public class RealtimeCompressionTests
{
    private const string User = "alice@contoso.com";
    private const string Job = "6f1c2b8e-1d2a-4c1e-9f0a-3b2c1d4e5f60";

    private sealed class CountingStream(Stream inner) : Stream
    {
        public long BytesRead;
        public override int Read(byte[] buffer, int offset, int count) { var n = inner.Read(buffer, offset, count); BytesRead += n; return n; }
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        {
            var n = await inner.ReadAsync(buffer, ct);
            BytesRead += n;
            return n;
        }
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    [Fact]
    public async Task Stream_IsCompressed_AndEachFrameArrivesWhileOpen()
    {
        var settings = new CraftSettings { Realtime = new RealtimeSettings { Enabled = true, MaxMessageBytes = 4 * 1024 * 1024 } };
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.ConfigureKestrel(o => o.Listen(IPAddress.Loopback, 0));
        builder.Logging.ClearProviders();
        builder.Services.AddCraftResponseCompression();
        builder.Services.AddSingleton(settings);
        builder.Services.AddSingleton<RealtimeService>();
        var app = builder.Build();
        app.UseWhen(c => c.Request.Path.Equals(RealtimeEndpoint.Path, StringComparison.OrdinalIgnoreCase),
            ev => ev.UseResponseCompression());
        app.MapCraftRealtimeEndpoint(CraftRoles.Resolve(settings, _ => null), settings, NullLogger.Instance);
        var realtime = app.Services.GetRequiredService<RealtimeService>();

        await app.StartAsync();
        try
        {
            var addr = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First();
            using var handler = new HttpClientHandler { AutomaticDecompression = DecompressionMethods.None };
            using var client = new HttpClient(handler);
            var req = new HttpRequestMessage(HttpMethod.Get, $"{addr}{RealtimeEndpoint.Path}");
            req.Headers.TryAddWithoutValidation("Accept-Encoding", "br");
            req.Headers.TryAddWithoutValidation("x-ms-client-principal-name", User);
            req.Headers.TryAddWithoutValidation("x-ms-client-principal",
                Convert.ToBase64String(Encoding.UTF8.GetBytes("{\"userDetails\":\"" + User + "\",\"userRoles\":[\"authenticated\"]}")));
            using var resp = await client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead);
            Assert.Equal("br", Assert.Single(resp.Content.Headers.ContentEncoding));

            var wire = new CountingStream(await resp.Content.ReadAsStreamAsync());
            using var reader = new StreamReader(new BrotliStream(wire, CompressionMode.Decompress), Encoding.UTF8);

            // A queue entry the size a tenant fan-out produces.
            var tasks = Enumerable.Range(0, 300)
                .Select(i => (object?)new Dictionary<string, object?> { ["Name"] = $"tenant{i}.onmicrosoft.com", ["Status"] = "Completed" })
                .ToList();
            realtime.Watch(User, Job, trackRun: false);
            realtime.Notify(Job, "update", new Dictionary<string, object?> { ["Status"] = "Running", ["Tasks"] = tasks });

            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            string? line;
            do { line = await reader.ReadLineAsync(timeout.Token); }
            while (line != null && !line.StartsWith("data: ", StringComparison.Ordinal));

            Assert.NotNull(line);
            Assert.Contains(Job, line);
            Assert.Contains("tenant299.onmicrosoft.com", line);
            Assert.True(wire.BytesRead < Encoding.UTF8.GetByteCount(line) / 4,
                $"expected the frame compressed well below its {Encoding.UTF8.GetByteCount(line)} bytes, {wire.BytesRead} came over the wire");
        }
        finally
        {
            await app.StopAsync();
        }
    }
}

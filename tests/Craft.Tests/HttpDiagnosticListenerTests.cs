using System.Net;
using System.Net.Sockets;
using System.Text;
using Craft.Hosting;
using Microsoft.Extensions.Logging;

namespace Craft.Tests;

public class HttpDiagnosticListenerTests
{
    private sealed class ListLogger : ILogger
    {
        public readonly List<string> Lines = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            lock (Lines) Lines.Add(formatter(state, exception));
        }
    }

    // Counts how often the body is serialised: once for the send, once more for every read of it.
    private sealed class CountingContent(string body) : HttpContent
    {
        public int Serialised;
        protected override async Task SerializeToStreamAsync(Stream stream, TransportContext? context)
        {
            Interlocked.Increment(ref Serialised);
            await stream.WriteAsync(Encoding.UTF8.GetBytes(body));
        }
        protected override bool TryComputeLength(out long length) { length = Encoding.UTF8.GetByteCount(body); return true; }
    }

    private static async Task<int> StartServer(int delayMs, CancellationToken token)
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        _ = Task.Run(async () =>
        {
            while (!token.IsCancellationRequested)
            {
                using var client = await listener.AcceptTcpClientAsync(token);
                var stream = client.GetStream();
                var buffer = new byte[65536];
                var read = 0;
                while (!Encoding.ASCII.GetString(buffer, 0, read).Contains("\r\n\r\n")) read += await stream.ReadAsync(buffer.AsMemory(read), token);
                var head = Encoding.ASCII.GetString(buffer, 0, read);
                var length = int.Parse(head.Split("\r\n").First(l => l.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase)).Split(':')[1], System.Globalization.CultureInfo.InvariantCulture);
                while (read - head.IndexOf("\r\n\r\n", StringComparison.Ordinal) - 4 < length) read += await stream.ReadAsync(buffer.AsMemory(read), token);
                await Task.Delay(delayMs, token);
                await stream.WriteAsync(Encoding.ASCII.GetBytes("HTTP/1.1 204 No Content\r\nContent-Length: 0\r\nConnection: close\r\n\r\n"), token);
            }
        }, token);
        await Task.Yield();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    [Fact]
    public async Task LogsOnlyTheUrlOfASlowRequestAndNeverReadsTheBody()
    {
        using var cts = new CancellationTokenSource();
        var fastPort = await StartServer(0, cts.Token);
        var slowPort = await StartServer(400, cts.Token);
        var logger = new ListLogger();
        using var diagnostics = new HttpDiagnosticListener(logger, slowThresholdMs: 200);
        using var http = new HttpClient();

        var fast = new CountingContent("{\"fast\":true}");
        (await http.PostAsync($"http://127.0.0.1:{fastPort}/fast", fast)).Dispose();
        var slow = new CountingContent("grant_type=refresh_token&refresh_token=secret-value&scope=x");
        (await http.PostAsync($"http://127.0.0.1:{slowPort}/slow", slow)).Dispose();
        cts.Cancel();

        Assert.Equal(1, fast.Serialised);
        Assert.Equal(1, slow.Serialised);
        var line = Assert.Single(logger.Lines, l => l.Contains("[HTTP-SLOW]") && l.Contains("/slow"));
        Assert.EndsWith("ms", line);
        Assert.DoesNotContain("secret-value", line);
        Assert.DoesNotContain(logger.Lines, l => l.Contains("/fast"));
    }
}

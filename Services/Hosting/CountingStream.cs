namespace Craft.Hosting;

/// <summary>
/// A write-through <see cref="Stream"/> decorator that counts the bytes written to it and delegates
/// everything else to the inner stream. Used by <see cref="ApiEgressLimiterMiddleware"/> to measure a
/// response body without buffering it.
/// <para>
/// Assigning <see cref="Microsoft.AspNetCore.Http.HttpResponse.Body"/> to one of these captures every
/// body write path: a direct <c>Response.Body.WriteAsync(...)</c>, and also <c>Response.WriteAsync(...)</c>
/// / <c>Response.BodyWriter</c>, because the framework re-adapts the response pipe writer onto whatever
/// stream <c>Body</c> currently is.
/// </para>
/// </summary>
internal sealed class CountingStream : Stream
{
    private readonly Stream _inner;
    private long _bytesWritten;

    public CountingStream(Stream inner) => _inner = inner ?? throw new ArgumentNullException(nameof(inner));

    /// <summary>Total bytes written through this stream so far.</summary>
    public long BytesWritten => Interlocked.Read(ref _bytesWritten);

    // ── Writes: count, then delegate ──────────────────────────────────────────────────────────────
    // Counting the attempted length (rather than awaiting completion) keeps the hot path allocation-free.
    // A partial write on a faulted response over-counts by at most one buffer, which is harmless and
    // conservative for a cap.

    public override void Write(byte[] buffer, int offset, int count)
    {
        Interlocked.Add(ref _bytesWritten, count);
        _inner.Write(buffer, offset, count);
    }

    public override void Write(ReadOnlySpan<byte> buffer)
    {
        Interlocked.Add(ref _bytesWritten, buffer.Length);
        _inner.Write(buffer);
    }

    public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
    {
        Interlocked.Add(ref _bytesWritten, count);
        return _inner.WriteAsync(buffer, offset, count, cancellationToken);
    }

    public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    {
        Interlocked.Add(ref _bytesWritten, buffer.Length);
        return _inner.WriteAsync(buffer, cancellationToken);
    }

    public override void WriteByte(byte value)
    {
        Interlocked.Increment(ref _bytesWritten);
        _inner.WriteByte(value);
    }

    // ── Everything else: pure delegation so behaviour matches the real response stream exactly ──────

    public override bool CanRead => _inner.CanRead;
    public override bool CanSeek => _inner.CanSeek;
    public override bool CanWrite => _inner.CanWrite;
    public override long Length => _inner.Length;
    public override long Position { get => _inner.Position; set => _inner.Position = value; }
    public override void Flush() => _inner.Flush();
    public override Task FlushAsync(CancellationToken cancellationToken) => _inner.FlushAsync(cancellationToken);
    public override int Read(byte[] buffer, int offset, int count) => _inner.Read(buffer, offset, count);
    public override long Seek(long offset, SeekOrigin origin) => _inner.Seek(offset, origin);
    public override void SetLength(long value) => _inner.SetLength(value);
}

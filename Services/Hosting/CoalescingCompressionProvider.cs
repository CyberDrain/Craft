using Microsoft.AspNetCore.ResponseCompression;

namespace Craft.Hosting;

/// <summary>
/// Feeds an encoder in 64 KiB blocks however the response is written, while still streaming.
/// </summary>
/// <remarks>
/// A string body (<c>Response.WriteAsync</c>) or a <c>CraftResult.Stream</c> writer reaches the encoder
/// through the response pipe, which hands it one ~4 KiB segment per write. Brotli's fast qualities
/// compress each write as an isolated fragment, so a 300 KB ListLogs body at Fastest came out 85 KB
/// instead of 29 KB, and every encoder paid per-call overhead (Brotli Optimal ~10-15% CPU). Coalescing to
/// 64 KiB recovers the ratio (36 KB; exact for bodies up to 64 KiB) and the CPU, and output still leaves
/// every 64 KiB — the body is never materialised. Flushes pass straight through, so an explicit flush
/// (SSE, progressive output) behaves exactly as before; writes of 64 KiB or more bypass the buffer.
/// </remarks>
public sealed class CoalescingCompressionProvider(ICompressionProvider inner) : ICompressionProvider
{
    // Under the 85 KB large-object threshold, so the per-response buffer is a cheap gen0 allocation.
    private const int BlockSize = 64 * 1024;

    public string EncodingName => inner.EncodingName;
    public bool SupportsFlush => inner.SupportsFlush;

    // ponytail: BufferedStream allocates its 64 KiB buffer per compressed response (gen0). Swap for an
    // ArrayPool-backed buffer if allocation rate ever shows up in the GC diagnostics.
    public Stream CreateStream(Stream outputStream) => new BufferedStream(inner.CreateStream(outputStream), BlockSize);
}

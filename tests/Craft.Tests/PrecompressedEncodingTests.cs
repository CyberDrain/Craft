using Craft.Hosting;

namespace Craft.Tests;

/// <summary>
/// Content negotiation for build-time-compressed static assets. This logic was previously duplicated
/// between the static-file middleware and the SPA fallback handler in <c>Program.cs</c>; the two copies
/// had to agree, and nothing checked that they did.
/// </summary>
public class PrecompressedEncodingTests
{
    [Theory]
    [InlineData("br")]
    [InlineData("gzip, deflate, br")]
    [InlineData("BR")]
    [InlineData("deflate, br;q=1.0")]
    public void BrotliIsPreferredWhenAccepted(string header)
    {
        var encoding = PrecompressedEncoding.Negotiate(header);

        Assert.NotNull(encoding);
        Assert.Equal("br", encoding!.Value.ContentEncoding);
        Assert.Equal(".br", encoding.Value.FileSuffix);
    }

    [Theory]
    [InlineData("gzip")]
    [InlineData("gzip, deflate")]
    [InlineData("GZIP")]
    public void GzipIsUsedWhenBrotliIsNotOffered(string header)
    {
        var encoding = PrecompressedEncoding.Negotiate(header);

        Assert.NotNull(encoding);
        Assert.Equal("gzip", encoding!.Value.ContentEncoding);
        Assert.Equal(".gz", encoding.Value.FileSuffix);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("identity")]
    [InlineData("deflate")]
    [InlineData("*")]
    public void NeitherAccepted_FallsThroughToIdentity(string? header)
    {
        // Returning null is what makes the middleware call next() and serve the raw file.
        Assert.Null(PrecompressedEncoding.Negotiate(header));
    }

    [Fact]
    public void QValueOfZero_IsAnExplicitRefusal()
    {
        // q=0 means "not acceptable", not "low preference". Serving Brotli here would send a body the
        // client just said it cannot decode.
        var encoding = PrecompressedEncoding.Negotiate("gzip, br;q=0");

        Assert.NotNull(encoding);
        Assert.Equal("gzip", encoding!.Value.ContentEncoding);
    }

    [Fact]
    public void EverythingRefused_FallsThroughToIdentity()
    {
        Assert.Null(PrecompressedEncoding.Negotiate("br;q=0, gzip;q=0"));
        Assert.Null(PrecompressedEncoding.Negotiate("br;q=0.0"));
    }

    [Fact]
    public void HigherWeightedGzip_BeatsBrotli()
    {
        // Brotli only wins ties; a client that explicitly prefers gzip is honoured.
        var encoding = PrecompressedEncoding.Negotiate("br;q=0.2, gzip;q=0.9");

        Assert.NotNull(encoding);
        Assert.Equal("gzip", encoding!.Value.ContentEncoding);
    }

    [Fact]
    public void EqualWeights_PreferBrotli()
    {
        var encoding = PrecompressedEncoding.Negotiate("gzip;q=0.5, br;q=0.5");
        Assert.Equal("br", encoding!.Value.ContentEncoding);
    }

    [Theory]
    [InlineData("BR;Q=0.9")]
    [InlineData("  br ; q=0.9  ")]
    [InlineData("deflate, br;q=0.9, gzip;q=0.8")]
    public void WhitespaceAndCasingAreTolerated(string header)
    {
        var encoding = PrecompressedEncoding.Negotiate(header);
        Assert.Equal("br", encoding!.Value.ContentEncoding);
    }

    [Fact]
    public void UnparseableWeight_IsTreatedAsUnweighted_NotAsARefusal()
    {
        // A malformed q= must not accidentally disable compression for that client.
        var encoding = PrecompressedEncoding.Negotiate("br;q=abc");
        Assert.Equal("br", encoding!.Value.ContentEncoding);
    }

    [Fact]
    public void SubstringMatchesDoNotCountAsCodings()
    {
        // The old substring implementation matched "br" inside unrelated tokens. An exact token match
        // is what prevents a bogus Content-Encoding being sent.
        Assert.Null(PrecompressedEncoding.Negotiate("brotli-unknown, x-gzip-custom"));
    }

    [Theory]
    [InlineData("/app.js")]
    [InlineData("/styles/site.css")]
    [InlineData("/index.html")]
    [InlineData("/data.json")]
    [InlineData("/icon.svg")]
    [InlineData("/bundle.js.map")]
    [InlineData("/module.wasm")]
    [InlineData("/APP.JS")]
    public void CompressibleAssets_AreEligible(string path) =>
        Assert.True(PrecompressedEncoding.IsCompressiblePath(path));

    [Theory]
    [InlineData("/photo.png")]      // already compressed — recompressing costs CPU and grows the file
    [InlineData("/font.woff2")]
    [InlineData("/archive.zip")]
    [InlineData("/video.mp4")]
    [InlineData("/no-extension")]
    [InlineData("/")]
    [InlineData("")]
    [InlineData(null)]
    public void NonCompressibleOrExtensionlessPaths_AreSkipped(string? path) =>
        Assert.False(PrecompressedEncoding.IsCompressiblePath(path));
}

using Craft.Caching;
using Craft.Configuration;
using Craft.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace Craft.Tests;

/// <summary>
/// A cache hit returns the exact bytes the handler produced, from memory and from disk. A binary body used to be
/// cached as its empty text, so a second request for an image got an empty response.
/// </summary>
[Collection(Name)]
public class CacheBodyBytesTests
{
    /// <summary>Every CacheService shares one _cache folder and deletes it on dispose, so their tests run one at a time.</summary>
    internal const string Name = "ResponseCacheFolder";

    private static readonly byte[] Png = [137, 80, 78, 71, 13, 10, 26, 10, 0, 255, 254];
    private static readonly byte[] Json = "{\"name\":\"Zoë — 東京\"}"u8.ToArray();

    public static TheoryData<long> MemoryBudgets => new() { 64L * 1024 * 1024, 0 };

    [Theory]
    [MemberData(nameof(MemoryBudgets))]
    public async Task AHit_ReturnsTheStoredBytes(long maxMemoryBytes)
    {
        var settings = new CraftSettings();
        settings.Cache.MaxMemoryBytes = maxMemoryBytes;
        using var cache = new CacheService(NullLogger<CacheService>.Instance, settings, enabled: true);

        await cache.Set("ListUserPhoto|bytes-" + maxMemoryBytes, new ScriptResult { BodyBytes = Png, ContentType = "image/png" });
        await cache.Set("ListUsers|bytes-" + maxMemoryBytes, new ScriptResult { BodyBytes = Json });

        var photo = await cache.Get("ListUserPhoto|bytes-" + maxMemoryBytes, "ListUserPhoto");
        var users = await cache.Get("ListUsers|bytes-" + maxMemoryBytes, "ListUsers");

        Assert.Equal(Png, photo!.Result.BodyBytes);
        Assert.Equal("image/png", photo.Result.ContentType);
        Assert.Equal(Json, users!.Result.BodyBytes);
    }
}

[CollectionDefinition(CacheBodyBytesTests.Name)]
public sealed class ResponseCacheFolderSerial;

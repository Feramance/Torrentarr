using System.Net;
using System.Security.Cryptography;
using System.Text;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Torrentarr.Core.Configuration;
using Torrentarr.Infrastructure.Database;
using Torrentarr.Infrastructure.Database.Models;
using Torrentarr.Infrastructure.Services;
using Xunit;

namespace Torrentarr.Infrastructure.Tests.Services;

public class ArrThumbnailServiceTests : IDisposable
{
    private readonly string _configPath;
    private readonly string _prevConfigEnv;

    public ArrThumbnailServiceTests()
    {
        _configPath = Path.Combine(Path.GetTempPath(), $"thumb-test-{Guid.NewGuid():N}.toml");
        File.WriteAllText(_configPath, """
            [Settings]
            ConfigVersion = "6.12.2"

            [WebUI]
            Token = "test"

            [Radarr]
            URI = "http://radarr.local:7878"
            APIKey = "key"
            Category = "radarr"
            Type = "radarr"
            """);
        _prevConfigEnv = Environment.GetEnvironmentVariable("TORRENTARR_CONFIG") ?? "";
        Environment.SetEnvironmentVariable("TORRENTARR_CONFIG", _configPath);
        ConfigurationLoader.TestConfigPathOverride = _configPath;
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("TORRENTARR_CONFIG", string.IsNullOrEmpty(_prevConfigEnv) ? null : _prevConfigEnv);
        ConfigurationLoader.TestConfigPathOverride = null;
        if (File.Exists(_configPath))
            File.Delete(_configPath);
    }

    private static TorrentarrDbContext CreateDb()
    {
        var options = new DbContextOptionsBuilder<TorrentarrDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        return new TorrentarrDbContext(options);
    }

    private static TorrentarrConfig LoadConfig()
    {
        var loader = new ConfigurationLoader();
        return loader.Load();
    }

    private static byte[] FakeJpegBytes() => [0xFF, 0xD8, 0xFF, 0xD9];

    private static IHttpClientFactory CreateFactory(Func<HttpRequestMessage, HttpResponseMessage> handler)
        => new StubHttpClientFactory(handler);

    [Fact]
    public async Task GetThumbnailAsync_FetchesAndCachesOnMiss()
    {
        ClearThumbnailCache("radarr", "radarr", 8101);
        await using var db = CreateDb();
        db.Movies.Add(new MoviesFilesModel
        {
            EntryId = 8101,
            ArrInstance = "radarr",
            ArrId = 42,
            Title = "Movie"
        });
        await db.SaveChangesAsync();

        var fetched = 0;
        var factory = CreateFactory(_ =>
        {
            Interlocked.Increment(ref fetched);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(FakeJpegBytes())
                {
                    Headers = { ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("image/jpeg") }
                }
            };
        });

        var svc = new ArrThumbnailService(db, LoadConfig(), factory);
        var first = await svc.GetThumbnailAsync("radarr", "radarr", 8101);
        var second = await svc.GetThumbnailAsync("radarr", "radarr", 8101);

        first.Should().NotBeNull();
        first!.Value.ContentType.Should().Be("image/jpeg");
        second.Should().NotBeNull();
        fetched.Should().Be(1, "second call should read from disk cache");
    }

    [Fact]
    public async Task GetThumbnailAsync_ReturnsNullForUnknownCategory()
    {
        await using var db = CreateDb();
        var factory = CreateFactory(_ => new HttpResponseMessage(HttpStatusCode.OK));
        var svc = new ArrThumbnailService(db, LoadConfig(), factory);

        var result = await svc.GetThumbnailAsync("radarr", "unknown-cat", 1);
        result.Should().BeNull();
    }

    [Fact]
    public async Task GetThumbnailAsync_RejectsOversizeResponse()
    {
        ClearThumbnailCache("radarr", "radarr", 8102);
        await using var db = CreateDb();
        db.Movies.Add(new MoviesFilesModel
        {
            EntryId = 8102,
            ArrInstance = "radarr",
            ArrId = 99,
            Title = "Big"
        });
        await db.SaveChangesAsync();

        var oversized = new byte[5 * 1024 * 1024 + 1];
        var factory = CreateFactory(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(oversized)
        });

        var svc = new ArrThumbnailService(db, LoadConfig(), factory);
        var result = await svc.GetThumbnailAsync("radarr", "radarr", 8102);
        result.Should().BeNull();
    }

    [Fact]
    public async Task GetThumbnailAsync_ReturnsPreseededCacheFile()
    {
        await using var db = CreateDb();
        db.Movies.Add(new MoviesFilesModel
        {
            EntryId = 3,
            ArrInstance = "radarr",
            ArrId = 7,
            Title = "Cached"
        });
        await db.SaveChangesAsync();

        var cacheDir = Path.Combine(ConfigurationLoader.GetDataDirectoryPath(), "cache", "thumbnails");
        Directory.CreateDirectory(cacheDir);
        var cacheKey = "radarr:radarr:3";
        var cachePath = Path.Combine(cacheDir, Sha256Hex(cacheKey)[..40] + ".bin");
        var bytes = FakeJpegBytes();
        await File.WriteAllBytesAsync(cachePath, bytes);

        var factory = CreateFactory(_ =>
            throw new InvalidOperationException("HTTP should not be called when cache exists"));
        var svc = new ArrThumbnailService(db, LoadConfig(), factory);

        var result = await svc.GetThumbnailAsync("radarr", "radarr", 3);
        result.Should().NotBeNull();
        result!.Value.Bytes.Should().Equal(bytes);
    }

    private static string Sha256Hex(string input)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(input))).ToLowerInvariant();

    private static void ClearThumbnailCache(string arrType, string category, int entryId)
    {
        var cacheDir = Path.Combine(ConfigurationLoader.GetDataDirectoryPath(), "cache", "thumbnails");
        var cacheKey = $"{arrType}:{category}:{entryId}";
        var cachePath = Path.Combine(cacheDir, Sha256Hex(cacheKey)[..40] + ".bin");
        if (File.Exists(cachePath))
            File.Delete(cachePath);
    }

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> handler) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(handler(request));
    }

    private sealed class StubHttpClientFactory(Func<HttpRequestMessage, HttpResponseMessage> handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new HttpClient(new StubHandler(handler));
    }
}

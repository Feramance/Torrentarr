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
            ConfigVersion = "6.12.3"

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

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task GetThumbnailAsync_SkipsHtmlAndRepairsInvalidCache(bool poisonedCache)
    {
        const int id = 8131;
        ClearThumbnailCache("radarr", "radarr", id);
        await using var db = CreateDb();
        db.Movies.Add(new MoviesFilesModel { EntryId = id, ArrId = 42, ArrInstance = "radarr" });
        await db.SaveChangesAsync();
        var cachePath = Path.Combine(ConfigurationLoader.GetDataDirectoryPath(), "cache", "thumbnails",
            Sha256Hex($"radarr:radarr:{id}")[..40] + ".bin");
        if (poisonedCache)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(cachePath)!);
            await File.WriteAllTextAsync(cachePath, "<!DOCTYPE html><html>Login</html>");
            await File.WriteAllTextAsync(cachePath + ".etag", "invalid-poster");
        }
        var requests = new List<string>();
        var svc = new ArrThumbnailService(db, LoadConfig(), CreateFactory(request =>
        {
            requests.Add(request.RequestUri!.AbsolutePath);
            // Even a misleading image MIME type must not admit HTML into the cache.
            var bytes = request.RequestUri.AbsolutePath.StartsWith("/api/")
                ? FakeJpegBytes() : Encoding.UTF8.GetBytes("<!DOCTYPE html><html>Login</html>");
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(bytes)
                {
                    Headers = { ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("image/jpeg") }
                }
            };
        }));
        var poster = await svc.GetThumbnailAsync("radarr", "radarr", id);
        poster.Should().NotBeNull();
        poster!.Value.Bytes.Should().Equal(FakeJpegBytes());
        poster.Value.ETag.Should().NotBe("invalid-poster");
        (await File.ReadAllBytesAsync(cachePath)).Should().Equal(FakeJpegBytes());
        (await svc.GetThumbnailAsync("radarr", "radarr", id))!.Value.Bytes.Should().Equal(FakeJpegBytes());
        requests.Should().Equal("/MediaCover/42/poster.jpg", "/api/v3/MediaCover/42/poster.jpg");
    }

    [Fact]
    public async Task GetThumbnailAsync_DoesNotCacheHtmlWhenNoImageRouteSucceeds()
    {
        const int id = 8132;
        ClearThumbnailCache("radarr", "radarr", id);
        await using var db = CreateDb();
        db.Movies.Add(new MoviesFilesModel { EntryId = id, ArrId = 42, ArrInstance = "radarr" });
        await db.SaveChangesAsync();
        var svc = new ArrThumbnailService(db, LoadConfig(), CreateFactory(_ =>
            new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("<html>Login</html>") }));
        (await svc.GetThumbnailAsync("radarr", "radarr", id)).Should().BeNull();
        var cachePath = Path.Combine(ConfigurationLoader.GetDataDirectoryPath(), "cache", "thumbnails",
            Sha256Hex($"radarr:radarr:{id}")[..40] + ".bin");
        File.Exists(cachePath).Should().BeFalse();
    }

    [Theory]
    [InlineData("FFD8FFD9", "image/jpeg")]
    [InlineData("89504E470D0A1A0A", "image/png")]
    [InlineData("474946383961", "image/gif")]
    [InlineData("524946460000000057454250", "image/webp")]
    public async Task GetThumbnailAsync_UsesImageSignatureForContentType(string hex, string mime)
    {
        const int id = 8133;
        ClearThumbnailCache("radarr", "radarr", id);
        await using var db = CreateDb();
        db.Movies.Add(new MoviesFilesModel { EntryId = id, ArrId = 42, ArrInstance = "radarr" });
        await db.SaveChangesAsync();
        var svc = new ArrThumbnailService(db, LoadConfig(), CreateFactory(_ =>
            new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(Convert.FromHexString(hex)) }));
        (await svc.GetThumbnailAsync("radarr", "radarr", id))!.Value.ContentType.Should().Be(mime);
        (await svc.GetThumbnailAsync("radarr", "radarr", id))!.Value.ContentType.Should().Be(mime);
    }

    [Theory]
    [InlineData("v3", "4.0.0")]
    [InlineData("v5", "5.0.0")]
    public async Task GetThumbnailAsync_SonarrHtmlFallsBackToDiscoveredVersion(string apiVersion, string version)
    {
        const int id = 8134;
        ClearThumbnailCache("sonarr", "radarr", id);
        await using var db = CreateDb();
        db.Series.Add(new SeriesFilesModel { EntryId = id, ArrId = 42, ArrInstance = "radarr" });
        await db.SaveChangesAsync();
        var config = LoadConfig();
        config.ArrInstances.Values.Single().URI = $"http://sonarr-thumbnail-{apiVersion}.local";
        var requests = new List<string>();
        var svc = new ArrThumbnailService(db, config, CreateFactory(request =>
        {
            var path = request.RequestUri!.AbsolutePath;
            requests.Add(path);
            var content = path switch
            {
                "/MediaCover/42/poster.jpg" => (HttpContent)new StringContent("<!DOCTYPE html><html>Login</html>"),
                "/api" => new StringContent($"{{\"current\":\"{apiVersion}\"}}"),
                var p when p == $"/api/{apiVersion}/system/status" => new StringContent($"{{\"version\":\"{version}\"}}"),
                var p when p == $"/api/{apiVersion}/MediaCover/42/poster.jpg" => new ByteArrayContent(FakeJpegBytes()),
                _ => throw new InvalidOperationException($"Unexpected route: {path}")
            };
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
        }));
        var poster = await svc.GetThumbnailAsync("sonarr", "radarr", id);
        poster.Should().NotBeNull();
        poster!.Value.Bytes.Should().Equal(FakeJpegBytes());
        requests.Should().Equal("/MediaCover/42/poster.jpg", "/api",
            $"/api/{apiVersion}/system/status", $"/api/{apiVersion}/MediaCover/42/poster.jpg");
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

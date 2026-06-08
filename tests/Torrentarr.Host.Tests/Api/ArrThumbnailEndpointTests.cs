using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using Torrentarr.Core.Configuration;
using Torrentarr.Infrastructure.Database;
using Xunit;

namespace Torrentarr.Host.Tests.Api;

[Collection("HostWebCatalog")]
public class ArrThumbnailEndpointTests : IClassFixture<ArrCatalogWebApplicationFactory>
{
    private readonly ArrCatalogWebApplicationFactory _factory;

    public ArrThumbnailEndpointTests(ArrCatalogWebApplicationFactory factory)
    {
        _factory = factory;
    }

    [Theory]
    [InlineData("/web/radarr/radarr/movie/999/thumbnail")]
    [InlineData("/web/sonarr/sonarr/series/999/thumbnail")]
    [InlineData("/web/lidarr/lidarr/artist/999/thumbnail")]
    public async Task GetThumbnail_EmptyDb_Returns404(string path)
    {
        _factory.SetConfigEnv();
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TorrentarrDbContext>();
        await CatalogTestDataSeeder.ClearCatalogDataAsync(db);

        var client = _factory.CreateClientWithApiToken();
        var response = await client.GetAsync(path);
        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task GetThumbnail_PreseededCache_Returns200WithImage()
    {
        _factory.SetConfigEnv();
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TorrentarrDbContext>();
        await CatalogTestDataSeeder.SeedRadarrMoviesAsync(db);

        var cacheDir = Path.Combine(ConfigurationLoader.GetDataDirectoryPath(), "cache", "thumbnails");
        Directory.CreateDirectory(cacheDir);
        var cacheKey = "radarr:radarr:1";
        var cachePath = Path.Combine(cacheDir, Sha256Hex(cacheKey)[..40] + ".bin");
        await File.WriteAllBytesAsync(cachePath, [0xFF, 0xD8, 0xFF, 0xD9]);

        var client = _factory.CreateClientWithApiToken();
        var response = await client.GetAsync("/web/radarr/radarr/movie/1/thumbnail");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Content.Headers.ContentType!.MediaType.Should().StartWith("image/");
    }

    private static string Sha256Hex(string input)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(input))).ToLowerInvariant();
}

using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using System.Net;
using System.Text.Json;
using Torrentarr.Infrastructure.Database;
using Xunit;

namespace Torrentarr.Host.Tests.Api;

[Collection("HostWebCatalog")]
public class LidarrArtistsEndpointTests : IClassFixture<ArrCatalogWebApplicationFactory>
{
    private readonly ArrCatalogWebApplicationFactory _factory;

    public LidarrArtistsEndpointTests(ArrCatalogWebApplicationFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task GetLidarrArtists_ReturnsShapeWithRollups()
    {
        _factory.SetConfigEnv();
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TorrentarrDbContext>();
        await CatalogTestDataSeeder.SeedLidarrArtistsAsync(db);

        var client = _factory.CreateClientWithApiToken();
        var response = await client.GetAsync("/web/lidarr/lidarr/artists");
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
        json.GetProperty("category").GetString().Should().Be("lidarr");
        json.GetProperty("counts").GetProperty("available").GetInt32().Should().Be(1);
        json.GetProperty("counts_tracks").GetProperty("monitored").GetInt32().Should().Be(2);
        json.GetProperty("artists").GetArrayLength().Should().Be(1);

        var artist = json.GetProperty("artists")[0].GetProperty("artist");
        artist.GetProperty("albumsMonitored").GetInt32().Should().Be(2);
        artist.GetProperty("albumsAvailable").GetInt32().Should().Be(1);
        artist.GetProperty("albumsMissing").GetInt32().Should().Be(1);
    }

    [Fact]
    public async Task GetLidarrArtistDetail_ReturnsAlbumsAndTracks()
    {
        _factory.SetConfigEnv();
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TorrentarrDbContext>();
        await CatalogTestDataSeeder.SeedLidarrArtistsAsync(db);

        var client = _factory.CreateClientWithApiToken();
        var response = await client.GetAsync("/web/lidarr/lidarr/artist/1");
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
        json.GetProperty("artist").GetProperty("name").GetString().Should().Be("Artist One");
        json.GetProperty("albums").GetArrayLength().Should().Be(2);
        json.GetProperty("albums")[0].GetProperty("tracks").ValueKind.Should().Be(JsonValueKind.Array);
    }

    [Fact]
    public async Task GetLidarrArtistDetail_Returns404ForMissingArtist()
    {
        _factory.SetConfigEnv();
        var client = _factory.CreateClientWithApiToken();
        var response = await client.GetAsync("/web/lidarr/lidarr/artist/999");
        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task GetApiLidarrArtists_MirrorsWebShape()
    {
        _factory.SetConfigEnv();
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TorrentarrDbContext>();
        await CatalogTestDataSeeder.SeedLidarrArtistsAsync(db);

        var client = _factory.CreateClientWithApiToken();
        var response = await client.GetAsync("/api/lidarr/lidarr/artists");
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
        json.TryGetProperty("artists", out _).Should().BeTrue();
        json.TryGetProperty("counts", out _).Should().BeTrue();
        json.TryGetProperty("counts_tracks", out _).Should().BeTrue();
    }
}

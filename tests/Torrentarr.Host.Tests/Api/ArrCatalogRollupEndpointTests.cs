using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using System.Net;
using System.Text.Json;
using Torrentarr.Infrastructure.Database;
using Xunit;

namespace Torrentarr.Host.Tests.Api;

[Collection("HostWebCatalog")]
public class ArrCatalogRollupEndpointTests : IClassFixture<ArrCatalogWebApplicationFactory>
{
    private readonly ArrCatalogWebApplicationFactory _factory;

    public ArrCatalogRollupEndpointTests(ArrCatalogWebApplicationFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task GetArr_ReturnsAggregatedCounts()
    {
        _factory.SetConfigEnv();
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TorrentarrDbContext>();
        await CatalogTestDataSeeder.SeedAllCatalogDataAsync(db);

        var client = _factory.CreateClientWithApiToken();
        var response = await client.GetAsync("/web/arr");
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
        json.GetProperty("counts").GetProperty("radarr").GetProperty("available").GetInt32().Should().Be(1);
        json.GetProperty("counts").GetProperty("radarr").GetProperty("monitored").GetInt32().Should().Be(2);
        json.GetProperty("counts").GetProperty("radarr").GetProperty("missing").GetInt32().Should().Be(1);
        json.GetProperty("counts").GetProperty("sonarr").GetProperty("available").GetInt32().Should().Be(1);
        json.GetProperty("counts").GetProperty("lidarr").GetProperty("available").GetInt32().Should().Be(1);
    }

    [Fact]
    public async Task GetRadarrMovies_ReturnsRollupCounts()
    {
        _factory.SetConfigEnv();
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TorrentarrDbContext>();
        await CatalogTestDataSeeder.SeedRadarrMoviesAsync(db);

        var client = _factory.CreateClientWithApiToken();
        var response = await client.GetAsync("/web/radarr/radarr/movies");
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
        json.GetProperty("counts").GetProperty("available").GetInt32().Should().Be(1);
        json.GetProperty("counts").GetProperty("monitored").GetInt32().Should().Be(2);
        json.GetProperty("counts").GetProperty("missing").GetInt32().Should().Be(1);
    }

    [Fact]
    public async Task GetSonarrSeries_ReturnsRollupCounts()
    {
        _factory.SetConfigEnv();
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TorrentarrDbContext>();
        await CatalogTestDataSeeder.SeedSonarrEpisodesAsync(db);

        var client = _factory.CreateClientWithApiToken();
        var response = await client.GetAsync("/web/sonarr/sonarr/series");
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
        json.GetProperty("counts").GetProperty("available").GetInt32().Should().Be(1);
        json.GetProperty("counts").GetProperty("monitored").GetInt32().Should().Be(2);
        json.GetProperty("counts").GetProperty("missing").GetInt32().Should().Be(1);
    }

    [Fact]
    public async Task GetLidarrAlbums_ReturnsRollupCounts()
    {
        _factory.SetConfigEnv();
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TorrentarrDbContext>();
        await CatalogTestDataSeeder.SeedLidarrArtistsAsync(db);

        var client = _factory.CreateClientWithApiToken();
        var response = await client.GetAsync("/web/lidarr/lidarr/albums");
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
        json.GetProperty("counts").GetProperty("available").GetInt32().Should().Be(1);
        json.GetProperty("counts").GetProperty("monitored").GetInt32().Should().Be(2);
        json.GetProperty("counts_tracks").GetProperty("available").GetInt32().Should().Be(1);
    }

    [Fact]
    public async Task ApiArr_MirrorsWebCounts()
    {
        _factory.SetConfigEnv();
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TorrentarrDbContext>();
        await CatalogTestDataSeeder.SeedRadarrMoviesAsync(db);

        var client = _factory.CreateClientWithApiToken();
        var web = await client.GetAsync("/web/arr");
        var api = await client.GetAsync("/api/arr");

        web.StatusCode.Should().Be(HttpStatusCode.OK);
        api.StatusCode.Should().Be(HttpStatusCode.OK);

        var webJson = JsonDocument.Parse(await web.Content.ReadAsStringAsync()).RootElement;
        var apiJson = JsonDocument.Parse(await api.Content.ReadAsStringAsync()).RootElement;

        apiJson.GetProperty("counts").GetProperty("radarr").GetProperty("available").GetInt32()
            .Should().Be(webJson.GetProperty("counts").GetProperty("radarr").GetProperty("available").GetInt32());
    }
}

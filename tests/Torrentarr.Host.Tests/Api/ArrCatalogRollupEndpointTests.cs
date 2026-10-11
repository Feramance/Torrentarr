using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using System.Net;
using System.Text.Json;
using Torrentarr.Infrastructure.Database;
using Torrentarr.Infrastructure.Database.Models;
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

    [Theory]
    [InlineData("web")]
    [InlineData("api")]
    public async Task RadarrFilters_ApplyBeforePaging_WithFilteredTotalAndGlobalCounts(string prefix)
    {
        _factory.SetConfigEnv();
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TorrentarrDbContext>();
        await CatalogTestDataSeeder.ClearCatalogDataAsync(db);
        string?[] reasons = [null, "", "Not being searched", "Missing", "Quality", "CustomFormat", "Upgrade"];
        for (var i = 0; i < reasons.Length; i++)
            db.Movies.Add(new MoviesFilesModel
            {
                EntryId = i + 1,
                ArrInstance = i % 2 == 0 ? "Radarr" : "radarr",
                Title = $"Movie {i}",
                Reason = reasons[i],
                Monitored = true,
                MovieFileId = i < 3 ? 10 : 0,
                Year = 2020,
                QualityMet = i < 3,
                IsRequest = i >= 3
            });
        db.Movies.Add(new MoviesFilesModel { EntryId = 99, ArrInstance = "Other", Title = "Movie Other", Reason = "Missing" });
        await db.SaveChangesAsync();
        var client = _factory.CreateClientWithApiToken();
        async Task AssertPage(string query, int total, params int[] ids)
        {
            var response = await client.GetAsync($"/{prefix}/radarr/radarr/movies?page_size=2&{query}");
            response.StatusCode.Should().Be(HttpStatusCode.OK);
            var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
            json.GetProperty("total").GetInt32().Should().Be(total);
            json.GetProperty("movies").EnumerateArray().Select(m => m.GetProperty("id").GetInt32()).Should().Equal(ids);
            json.GetProperty("counts").GetProperty("available").GetInt32().Should().Be(3);
            json.GetProperty("counts").GetProperty("missing").GetInt32().Should().Be(4);
        }
        await AssertPage("has_file=false", 4, 4, 5);
        await AssertPage("has_file=false&page=1", 4, 6, 7);
        foreach (var (reason, id) in new[] { ("Missing", 4), ("Quality", 5), ("CustomFormat", 6), ("Upgrade", 7) })
            await AssertPage($"reason={reason}", 1, id);
        await AssertPage("reason=Not%20being%20searched", 3, 1, 2);
        await AssertPage("reason=Not%20being%20searched&page=1", 3, 3);
        await AssertPage("q=Movie%205", 1, 6);
        await AssertPage("has_file=false&reason=Missing&q=Movie&year_min=2020&year_max=2020&monitored=true&quality_met=false&is_request=true", 1, 4);
        await AssertPage("q=absent", 0);
        await AssertPage("has_file=true", 3, 1, 2);
        await AssertPage("reason=all", 7, 1, 2);
        await AssertPage("reason=%20", 7, 1, 2);
        await AssertPage("year_min=2021", 0);
        await AssertPage("year_max=2019", 0);
        await AssertPage("monitored=false", 0);
        await AssertPage("quality_met=true&is_request=false", 3, 1, 2);
        await AssertPage("has_file=false&quality_met=true", 0);
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
    public async Task GetSonarrSeries_FiltersByReasonBeforePaging()
    {
        _factory.SetConfigEnv();
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TorrentarrDbContext>();
        await CatalogTestDataSeeder.SeedSonarrEpisodesAsync(db);

        var client = _factory.CreateClientWithApiToken();
        var response = await client.GetAsync("/web/sonarr/sonarr/series?reason=Missing");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
        json.GetProperty("total").GetInt32().Should().Be(1);
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

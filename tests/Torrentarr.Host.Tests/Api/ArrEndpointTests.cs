using FluentAssertions;
using System.Net;
using System.Text.Json;
using Xunit;

namespace Torrentarr.Host.Tests.Api;

[Collection("HostWeb")]
public class ArrEndpointTests : IClassFixture<TorrentarrWebApplicationFactory>
{
    private readonly TorrentarrWebApplicationFactory _factory;

    public ArrEndpointTests(TorrentarrWebApplicationFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task GetArr_Returns200()
    {
        var client = _factory.CreateClientWithApiToken();

        var response = await client.GetAsync("/web/arr");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task GetRadarrMovies_Returns200_WithPaginatedShape()
    {
        var client = _factory.CreateClientWithApiToken();

        // "radarr" is the category name — empty DB returns empty page
        var response = await client.GetAsync("/web/radarr/radarr/movies");

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var body = await response.Content.ReadAsStringAsync();
        var json = JsonDocument.Parse(body).RootElement;

        json.TryGetProperty("total", out _).Should().BeTrue();
        json.TryGetProperty("movies", out _).Should().BeTrue();
    }

    [Fact]
    public async Task GetSonarrSeries_Returns200_WithPaginatedShape()
    {
        var client = _factory.CreateClientWithApiToken();

        var response = await client.GetAsync("/web/sonarr/sonarr/series");

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var body = await response.Content.ReadAsStringAsync();
        var json = JsonDocument.Parse(body).RootElement;

        json.TryGetProperty("total", out _).Should().BeTrue();
        json.TryGetProperty("series", out _).Should().BeTrue();
    }

    [Fact]
    public async Task GetLidarrAlbums_Returns200_WithPaginatedShape()
    {
        var client = _factory.CreateClientWithApiToken();

        var response = await client.GetAsync("/web/lidarr/lidarr/albums");

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var body = await response.Content.ReadAsStringAsync();
        var json = JsonDocument.Parse(body).RootElement;

        json.TryGetProperty("total", out _).Should().BeTrue();
        json.TryGetProperty("albums", out _).Should().BeTrue();
    }
}

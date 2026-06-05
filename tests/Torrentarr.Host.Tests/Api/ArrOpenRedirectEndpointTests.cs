using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using System.Net;
using Torrentarr.Infrastructure.Database;
using Xunit;

namespace Torrentarr.Host.Tests.Api;

[Collection("HostWebCatalog")]
public class ArrOpenRedirectEndpointTests : IClassFixture<ArrCatalogWebApplicationFactory>
{
    private readonly ArrCatalogWebApplicationFactory _factory;

    public ArrOpenRedirectEndpointTests(ArrCatalogWebApplicationFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task GetArrOpen_Movie_RedirectsToRadarr()
    {
        _factory.SetConfigEnv();
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TorrentarrDbContext>();
        await CatalogTestDataSeeder.SeedRadarrMoviesAsync(db);

        var client = _factory.CreateClientWithApiTokenNoRedirect();
        var response = await client.GetAsync("/web/arr/radarr/open/movie/1");

        response.StatusCode.Should().Be(HttpStatusCode.Redirect);
        response.Headers.Location!.ToString().Should().Be("http://radarr:7878/movie/101");
    }

    [Fact]
    public async Task GetArrOpen_Series_RedirectsToSonarr()
    {
        _factory.SetConfigEnv();
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TorrentarrDbContext>();
        await CatalogTestDataSeeder.SeedSonarrEpisodesAsync(db);

        var client = _factory.CreateClientWithApiTokenNoRedirect();
        var response = await client.GetAsync("/web/arr/sonarr/open/series/1");

        response.StatusCode.Should().Be(HttpStatusCode.Redirect);
        response.Headers.Location!.ToString().Should().Be("http://sonarr:8989/series/201");
    }

    [Fact]
    public async Task GetArrOpen_Artist_RedirectsToLidarr()
    {
        _factory.SetConfigEnv();
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TorrentarrDbContext>();
        await CatalogTestDataSeeder.SeedLidarrArtistsAsync(db);

        var client = _factory.CreateClientWithApiTokenNoRedirect();
        var response = await client.GetAsync("/web/arr/lidarr/open/artist/1");

        response.StatusCode.Should().Be(HttpStatusCode.Redirect);
        response.Headers.Location!.ToString().Should().Be("http://lidarr:8686/artist/401");
    }

    [Fact]
    public async Task GetArrOpen_UnknownSection_Returns404()
    {
        _factory.SetConfigEnv();
        var client = _factory.CreateClientWithApiToken();
        var response = await client.GetAsync("/web/arr/unknown/open/movie/1");
        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task GetArrOpen_MissingEntry_Returns404()
    {
        _factory.SetConfigEnv();
        var client = _factory.CreateClientWithApiToken();
        var response = await client.GetAsync("/web/arr/radarr/open/movie/999");
        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task GetArrOpen_UnknownKind_Returns404()
    {
        _factory.SetConfigEnv();
        var client = _factory.CreateClientWithApiToken();
        var response = await client.GetAsync("/web/arr/radarr/open/episode/1");
        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }
}

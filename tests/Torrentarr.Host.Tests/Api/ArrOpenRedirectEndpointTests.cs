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

        var client = CreateArrClient("http://radarr:7878/api/v3/movie/101", "{\"titleSlug\":\"movie-slug\"}");
        var response = await client.GetAsync("/web/arr/radarr/open/movie/1");

        response.StatusCode.Should().Be(HttpStatusCode.Redirect);
        response.Headers.Location!.ToString().Should().Be("http://radarr:7878/movie/movie-slug");
    }

    [Fact]
    public async Task GetArrOpen_Series_RedirectsToSonarr()
    {
        _factory.SetConfigEnv();
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TorrentarrDbContext>();
        await CatalogTestDataSeeder.SeedSonarrEpisodesAsync(db);

        var client = CreateArrClient("http://sonarr:8989/api/v3/series/201", "{\"titleSlug\":\"series-slug\"}");
        var response = await client.GetAsync("/web/arr/sonarr/open/series/1");

        response.StatusCode.Should().Be(HttpStatusCode.Redirect);
        response.Headers.Location!.ToString().Should().Be("http://sonarr:8989/series/series-slug");
    }

    [Fact]
    public async Task GetArrOpen_Artist_RedirectsToLidarr()
    {
        _factory.SetConfigEnv();
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TorrentarrDbContext>();
        await CatalogTestDataSeeder.SeedLidarrArtistsAsync(db);

        var client = CreateArrClient("http://lidarr:8686/api/v1/artist/401", "{\"foreignArtistId\":\"artist-mbid\"}");
        var response = await client.GetAsync("/web/arr/lidarr/open/artist/401");

        response.StatusCode.Should().Be(HttpStatusCode.Redirect);
        response.Headers.Location!.ToString().Should().Be("http://lidarr:8686/artist/artist-mbid");
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
    [Fact]
    public async Task GetArrOpen_Ajax_ReturnsEscapedSlug()
    {
        _factory.SetConfigEnv();
        using var scope = _factory.Services.CreateScope();
        await CatalogTestDataSeeder.SeedRadarrMoviesAsync(scope.ServiceProvider.GetRequiredService<TorrentarrDbContext>());
        using var client = CreateArrClient("http://radarr:7878/api/v3/movie/101", "{\"titleSlug\":\"a/b?#\"}");
        client.DefaultRequestHeaders.Add("X-Requested-With", "XMLHttpRequest");
        var response = await client.GetAsync("/api/arr/radarr/open/movie/1");
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        (await response.Content.ReadAsStringAsync()).Should().Contain("/movie/a%2Fb%3F%23");
    }

    [Theory]
    [InlineData("{}", HttpStatusCode.NotFound)]
    [InlineData("{\"titleSlug\":123}", HttpStatusCode.NotFound)]
    [InlineData("[]", HttpStatusCode.NotFound)]
    [InlineData("not json", HttpStatusCode.BadGateway)]
    public async Task GetArrOpen_MissingSlug_DoesNotFallBackToNumericId(string json, HttpStatusCode expected)
    {
        _factory.SetConfigEnv();
        using var scope = _factory.Services.CreateScope();
        await CatalogTestDataSeeder.SeedRadarrMoviesAsync(scope.ServiceProvider.GetRequiredService<TorrentarrDbContext>());
        using var client = CreateArrClient("http://radarr:7878/api/v3/movie/101", json);
        (await client.GetAsync("/web/arr/radarr/open/movie/1")).StatusCode.Should().Be(expected);
    }

    private HttpClient CreateArrClient(string expectedUrl, string json)
    {
        var factory = _factory.WithWebHostBuilder(builder => builder.ConfigureServices(services =>
            services.AddHttpClient("ArrOpen").ConfigurePrimaryHttpMessageHandler(() => new ArrHandler(expectedUrl, json))));
        var client = factory.CreateClient(new Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", "test-api-token");
        return client;
    }

    private sealed class ArrHandler(string expectedUrl, string json) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            request.RequestUri!.ToString().Should().Be(expectedUrl);
            request.Headers.GetValues("X-Api-Key").Single().Should().EndWith("-key");
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json) });
        }
    }

}

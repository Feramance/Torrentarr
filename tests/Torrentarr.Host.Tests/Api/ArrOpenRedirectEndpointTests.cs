using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using System.Net;
using Torrentarr.Infrastructure.Database;
using Torrentarr.Infrastructure.Services;
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

    [Fact]
    public async Task SonarrV5_RedirectAndPosterUseDiscoveredApi()
    {
        using var v5 = new SonarrV5Factory();
        v5.SetConfigEnv();
        using var scope = v5.Services.CreateScope();
        await CatalogTestDataSeeder.SeedSonarrEpisodesAsync(scope.ServiceProvider.GetRequiredService<TorrentarrDbContext>());
        using var client = CreateArrClient("http://sonarr-v5:8989/api/v5/series/201", "{\"titleSlug\":\"v5-series\"}", v5);
        var response = await client.GetAsync("/web/arr/sonarr/open/series/1");
        response.StatusCode.Should().Be(HttpStatusCode.Redirect);
        response.Headers.Location!.ToString().Should().Be("http://sonarr-v5:8989/series/v5-series");
        var poster = await client.GetAsync("/web/sonarr/sonarr/series/1/thumbnail");
        poster.StatusCode.Should().Be(HttpStatusCode.OK);
        (await poster.Content.ReadAsByteArrayAsync()).Should().Equal(new byte[] { 0xff, 0xd8, 0xff, 0xd9 });
    }

    private sealed class SonarrV5Factory : ArrCatalogWebApplicationFactory
    {
        protected override string GetTestConfigToml() => base.GetTestConfigToml().Replace("sonarr:8989", "sonarr-v5:8989");
    }

    private HttpClient CreateArrClient(string expectedUrl, string json, ArrCatalogWebApplicationFactory? testFactory = null)
    {
        var factory = (testFactory ?? _factory).WithWebHostBuilder(builder => builder.ConfigureServices(services =>
        {
            services.AddHttpClient("ArrOpen").ConfigurePrimaryHttpMessageHandler(() => new ArrHandler(expectedUrl, json));
            services.AddHttpClient(nameof(ArrThumbnailService)).ConfigurePrimaryHttpMessageHandler(() => new ArrHandler(expectedUrl, json));
        }));
        var client = factory.CreateClient(new Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", "test-api-token");
        return client;
    }

    private sealed class ArrHandler(string expectedUrl, string json) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var version = expectedUrl.Contains("/api/v5/") ? "v5" : "v3";
            if (request.RequestUri!.AbsolutePath == "/MediaCover/201/poster.jpg")
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
            if (request.RequestUri.AbsolutePath == $"/api/{version}/MediaCover/201/poster.jpg")
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(new byte[] { 0xff, 0xd8, 0xff, 0xd9 }) });
            if (request.RequestUri.AbsolutePath == "/api")
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent($"{{\"current\":\"{version}\"}}") });
            if (request.RequestUri.AbsolutePath == $"/api/{version}/system/status")
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent($"{{\"version\":\"{(version == "v5" ? 5 : 4)}.0.0\"}}") });
            request.RequestUri.ToString().Should().Be(expectedUrl);
            request.Headers.GetValues("X-Api-Key").Single().Should().EndWith("-key");
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json) });
        }
    }

}

using FluentAssertions;
using System.Net;
using Microsoft.Extensions.DependencyInjection;
using Torrentarr.Infrastructure.Database;
using System.Text.Json;
using Xunit;

namespace Torrentarr.Host.Tests.Api;

[Collection("HostWeb")]
public class StatisticsEndpointTests(TorrentarrWebApplicationFactory factory)
{
    [Theory]
    [InlineData("/web/statistics/transfers?range=invalid")]
    [InlineData("/web/statistics/arr-activity?range=invalid")]
    [InlineData("/web/statistics/arr-activity?worker=does-not-exist")]
    [InlineData("/web/statistics/transfers?instance=does-not-exist")]
    [InlineData("/web/statistics/catalog?kind=invalid")]
    [InlineData("/web/statistics/catalog?kind=radarr&instance=does-not-exist")]
    public async Task RejectsInvalidSelections(string path)
    {
        factory.SetConfigEnv();
        using var client = factory.CreateClientWithApiToken();
        (await client.GetAsync(path)).StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Transfers_DefaultsTo24HoursAndEmptySeriesWithoutClients()
    {
        factory.SetConfigEnv();
        using var client = factory.CreateClientWithApiToken();
        var response = await client.GetAsync("/web/statistics/transfers");
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        json.RootElement.GetProperty("range").GetString().Should().Be("24h");
        json.RootElement.GetProperty("bucketMinutes").GetInt32().Should().Be(5);
    }
    [Fact]
    public async Task ArrActivity_ReturnsBoundedTypedHistory()
    {
        factory.SetConfigEnv(); using var client = factory.CreateClientWithApiToken();
        var response = await client.GetAsync("/web/statistics/arr-activity?range=1h");
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        json.RootElement.GetProperty("bucketMinutes").GetInt32().Should().Be(1);
        json.RootElement.GetProperty("series").ValueKind.Should().Be(JsonValueKind.Array);
        var daily = await client.GetAsync("/web/statistics/arr-activity?range=24h");
        using var dailyJson = JsonDocument.Parse(await daily.Content.ReadAsStringAsync());
        dailyJson.RootElement.GetProperty("bucketMinutes").GetInt32().Should().Be(60);
    }

}

[Collection("HostWebAuth")]
public class StatisticsAuthTests(AuthEnabledWebApplicationFactory factory)
{
    [Theory]
    [InlineData("/web/statistics/transfers")]
    [InlineData("/web/statistics/arr-activity")]
    [InlineData("/web/statistics/catalog?kind=radarr")]
    public async Task RequiresConfiguredAuthentication(string path)
    {
        factory.SetConfigEnv();
        using var client = factory.CreateClientWithApiTokenNoRedirect();
        client.DefaultRequestHeaders.Authorization = null;
        var response = await client.GetAsync(path);
        response.StatusCode.Should().Be(HttpStatusCode.Redirect);
        response.Headers.Location!.OriginalString.Should().Contain("/login");
    }
}

[Collection("HostWebCatalog")]
public class CatalogStatisticsEndpointTests(ArrCatalogWebApplicationFactory factory)
{
    [Theory]
    [InlineData("radarr", "Radarr", "movies")]
    [InlineData("sonarr", "Sonarr", "episodes")]
    [InlineData("lidarr", "Lidarr", "albums")]
    [InlineData("readarr", "Readarr-Books", "books")]
    public async Task ReturnsCompleteSelectedLibraryCounts(string kind, string instance, string unit)
    {
        factory.SetConfigEnv();
        using var client = factory.CreateClientWithApiToken();
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TorrentarrDbContext>();
        await CatalogTestDataSeeder.SeedAllCatalogDataAsync(db);
        await CatalogTestDataSeeder.SeedReadarrAuthorsAsync(db, clearFirst: false);
        var response = await client.GetAsync($"/web/statistics/catalog?kind={kind}&instance={instance}");
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        json.RootElement.GetProperty("unit").GetString().Should().Be(unit);
        json.RootElement.GetProperty("available").GetInt32().Should().Be(1);
        json.RootElement.GetProperty("monitored").GetInt32().Should().Be(2);
        json.RootElement.GetProperty("missing").GetInt32().Should().Be(1);
    }
}

using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using System.Net;
using System.Text.Json;
using Torrentarr.Infrastructure.Database;
using Xunit;

namespace Torrentarr.Host.Tests.Api;

[Collection("HostWebCatalog")]
public class ReadarrAuthorsEndpointTests : IClassFixture<ArrCatalogWebApplicationFactory>
{
    private readonly ArrCatalogWebApplicationFactory _factory;

    public ReadarrAuthorsEndpointTests(ArrCatalogWebApplicationFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task GetReadarrAuthors_ResolvesCategoryToInstanceKey()
    {
        _factory.SetConfigEnv();
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TorrentarrDbContext>();
        await CatalogTestDataSeeder.SeedReadarrAuthorsAsync(db);

        var client = _factory.CreateClientWithApiToken();
        var response = await client.GetAsync("/web/readarr/readarr-books/authors");
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
        json.GetProperty("authors").GetArrayLength().Should().Be(1);
        json.GetProperty("authors")[0].GetProperty("author").GetProperty("name").GetString()
            .Should().Be("Frank Herbert");
        json.GetProperty("counts").GetProperty("available").GetInt32().Should().Be(1);
        json.GetProperty("counts").GetProperty("monitored").GetInt32().Should().Be(2);
    }

    [Fact]
    public async Task GetReadarrAuthorDetail_ReturnsBooksForInstanceKey()
    {
        _factory.SetConfigEnv();
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TorrentarrDbContext>();
        await CatalogTestDataSeeder.SeedReadarrAuthorsAsync(db);

        var client = _factory.CreateClientWithApiToken();
        var response = await client.GetAsync("/web/readarr/readarr-books/author/1");
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
        json.GetProperty("author").GetProperty("name").GetString().Should().Be("Frank Herbert");
        json.GetProperty("books").GetArrayLength().Should().Be(2);
    }
}

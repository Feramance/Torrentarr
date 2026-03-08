using FluentAssertions;
using System.Net;
using System.Text.Json;
using Xunit;

namespace Torrentarr.Host.Tests.Api;

[Collection("HostWeb")]
public class QbitCategoriesEndpointTests : IClassFixture<TorrentarrWebApplicationFactory>
{
    private readonly TorrentarrWebApplicationFactory _factory;

    public QbitCategoriesEndpointTests(TorrentarrWebApplicationFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task GetQbitCategories_Returns200()
    {
        var client = _factory.CreateClientWithApiToken();

        var response = await client.GetAsync("/web/qbit/categories");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task GetQbitCategories_ReturnsEmptyArray_WhenNoCategories()
    {
        // Default config: Host = "CHANGE_ME" and ManagedCategories = [] → no categories returned
        var client = _factory.CreateClientWithApiToken();

        var response = await client.GetAsync("/web/qbit/categories");
        var body = await response.Content.ReadAsStringAsync();
        var json = JsonDocument.Parse(body).RootElement;

        // /web/qbit/categories returns { categories: [...], ready: bool }
        json.GetProperty("categories").GetArrayLength().Should().Be(0);
    }
}

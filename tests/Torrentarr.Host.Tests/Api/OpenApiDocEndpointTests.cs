using System.Net;
using System.Text.Json;
using FluentAssertions;
using Xunit;

namespace Torrentarr.Host.Tests.Api;

[Collection("HostWeb")]
public class OpenApiDocEndpointTests : IClassFixture<TorrentarrWebApplicationFactory>
{
    private readonly TorrentarrWebApplicationFactory _factory;

    public OpenApiDocEndpointTests(TorrentarrWebApplicationFactory factory)
    {
        _factory = factory;
    }

    [Theory]
    [InlineData("/api/openapi.json")]
    [InlineData("/web/openapi.json")]
    public async Task OpenApiJson_ReturnsCuratedSpec(string path)
    {
        var client = _factory.CreateClientWithApiToken();
        var response = await client.GetAsync(path);
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Headers.CacheControl?.NoStore.Should().BeTrue();

        var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
        json.GetProperty("openapi").GetString().Should().StartWith("3.");
        json.GetProperty("paths").EnumerateObject().Count().Should().BeGreaterThanOrEqualTo(66);
    }

    [Theory]
    [InlineData("/api/docs")]
    [InlineData("/web/docs")]
    public async Task Docs_RedirectsToSwagger(string path)
    {
        var client = _factory.CreateClientWithApiTokenNoRedirect();
        var response = await client.GetAsync(path);
        response.StatusCode.Should().Be(HttpStatusCode.Redirect);
        response.Headers.Location?.OriginalString.Should().Contain("/swagger/index.html");
    }
}

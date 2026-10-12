using FluentAssertions;
using System.Net;
using System.Text.Json;
using Xunit;

namespace Torrentarr.Host.Tests.Api;

[Collection("HostWeb")]
public class StatusEndpointTests : IClassFixture<TorrentarrWebApplicationFactory>
{
    private readonly TorrentarrWebApplicationFactory _factory;

    public StatusEndpointTests(TorrentarrWebApplicationFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task GetStatus_Returns200()
    {
        var client = _factory.CreateClientWithApiToken();

        var response = await client.GetAsync("/web/status");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Theory]
    [InlineData("/web/status")]
    [InlineData("/api/status")]
    public async Task GetStatus_OnlyExposesNamedInstances(string path)
    {
        var response = await _factory.CreateClientWithApiToken().GetAsync(path);
        response.EnsureSuccessStatusCode();
        var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
        json.TryGetProperty("qbit", out _).Should().BeFalse();
        json.GetProperty("qbitInstances").EnumerateObject().Should().BeEmpty();
    }

    [Fact]
    public async Task GetStatus_ArrsMatchConfiguredInstances()
    {
        // Default test config has no Arr instances
        var client = _factory.CreateClientWithApiToken();

        var response = await client.GetAsync("/web/status");
        var body = await response.Content.ReadAsStringAsync();
        var json = JsonDocument.Parse(body).RootElement;

        var arrs = json.GetProperty("arrs");
        arrs.GetArrayLength().Should().Be(0);
    }
}

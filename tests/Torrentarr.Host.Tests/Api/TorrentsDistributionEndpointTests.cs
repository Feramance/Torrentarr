using FluentAssertions;
using System.Net;
using System.Text.Json;
using Xunit;

namespace Torrentarr.Host.Tests.Api;

/// <summary>
/// Integration tests for the torrent distribution endpoints:
///   GET /web/torrents/distribution
///   GET /api/torrents/distribution
/// </summary>
public class TorrentsDistributionEndpointTests : IClassFixture<TorrentarrWebApplicationFactory>
{
    private readonly TorrentarrWebApplicationFactory _factory;

    public TorrentsDistributionEndpointTests(TorrentarrWebApplicationFactory factory)
    {
        _factory = factory;
    }

    // ── GET /web/torrents/distribution ────────────────────────────────────────

    [Fact]
    public async Task GetTorrentsDistribution_Returns200()
    {
        var client = _factory.CreateClient();

        var response = await client.GetAsync("/web/torrents/distribution");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task GetTorrentsDistribution_ResponseHasDistributionObject()
    {
        var client = _factory.CreateClient();

        var response = await client.GetAsync("/web/torrents/distribution");
        var body = await response.Content.ReadAsStringAsync();
        var json = JsonDocument.Parse(body).RootElement;

        json.TryGetProperty("distribution", out var dist).Should().BeTrue("response must have 'distribution' field");
        dist.ValueKind.Should().Be(JsonValueKind.Object);
    }

    [Fact]
    public async Task GetTorrentsDistribution_NoArrInstances_DistributionIsEmpty()
    {
        var client = _factory.CreateClient();

        // Test config has no Arr instances → distribution dictionary is empty
        var response = await client.GetAsync("/web/torrents/distribution");
        var body = await response.Content.ReadAsStringAsync();
        var json = JsonDocument.Parse(body).RootElement;

        var dist = json.GetProperty("distribution");
        dist.EnumerateObject().Should().BeEmpty("no Arr instances configured in test config");
    }

    // ── GET /api/torrents/distribution (mirror) ───────────────────────────────

    [Fact]
    public async Task GetApiTorrentsDistribution_Returns200()
    {
        var client = _factory.CreateClient();

        var response = await client.GetAsync("/api/torrents/distribution");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task GetApiTorrentsDistribution_ResponseHasSameShape()
    {
        var client = _factory.CreateClient();

        var response = await client.GetAsync("/api/torrents/distribution");
        var body = await response.Content.ReadAsStringAsync();
        var json = JsonDocument.Parse(body).RootElement;

        json.TryGetProperty("distribution", out _).Should().BeTrue();
    }
}

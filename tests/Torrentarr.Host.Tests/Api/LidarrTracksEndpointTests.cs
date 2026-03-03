using FluentAssertions;
using System.Net;
using System.Text.Json;
using Xunit;

namespace Torrentarr.Host.Tests.Api;

/// <summary>
/// Integration tests for the Lidarr tracks endpoints:
///   GET /web/lidarr/{category}/tracks
///   GET /api/lidarr/{category}/tracks
/// </summary>
[Collection("HostWeb")]
public class LidarrTracksEndpointTests : IClassFixture<TorrentarrWebApplicationFactory>
{
    private readonly TorrentarrWebApplicationFactory _factory;

    public LidarrTracksEndpointTests(TorrentarrWebApplicationFactory factory)
    {
        _factory = factory;
    }

    // ── GET /web/lidarr/{category}/tracks ─────────────────────────────────────

    [Fact]
    public async Task GetLidarrTracks_Returns200()
    {
        var client = _factory.CreateClient();

        var response = await client.GetAsync("/web/lidarr/lidarr/tracks");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task GetLidarrTracks_ResponseHasTracksArray()
    {
        var client = _factory.CreateClient();

        var response = await client.GetAsync("/web/lidarr/lidarr/tracks");
        var body = await response.Content.ReadAsStringAsync();
        var json = JsonDocument.Parse(body).RootElement;

        json.TryGetProperty("tracks", out var tracks).Should().BeTrue("response must have 'tracks' array");
        tracks.ValueKind.Should().Be(JsonValueKind.Array);
    }

    [Fact]
    public async Task GetLidarrTracks_ResponseHasRequiredPaginationFields()
    {
        var client = _factory.CreateClient();

        var response = await client.GetAsync("/web/lidarr/lidarr/tracks");
        var body = await response.Content.ReadAsStringAsync();
        var json = JsonDocument.Parse(body).RootElement;

        json.TryGetProperty("total", out _).Should().BeTrue("response must have 'total'");
        json.TryGetProperty("page", out _).Should().BeTrue("response must have 'page'");
        json.TryGetProperty("page_size", out _).Should().BeTrue("response must have 'page_size'");
        json.TryGetProperty("category", out _).Should().BeTrue("response must have 'category'");
    }

    [Fact]
    public async Task GetLidarrTracks_ResponseHasCountsObject()
    {
        var client = _factory.CreateClient();

        var response = await client.GetAsync("/web/lidarr/lidarr/tracks");
        var body = await response.Content.ReadAsStringAsync();
        var json = JsonDocument.Parse(body).RootElement;

        json.TryGetProperty("counts", out var counts).Should().BeTrue("response must have 'counts'");
        counts.TryGetProperty("available", out _).Should().BeTrue();
        counts.TryGetProperty("monitored", out _).Should().BeTrue();
        counts.TryGetProperty("missing", out _).Should().BeTrue();
    }

    [Fact]
    public async Task GetLidarrTracks_EmptyDb_TotalIsZero()
    {
        var client = _factory.CreateClient();

        var response = await client.GetAsync("/web/lidarr/lidarr/tracks");
        var body = await response.Content.ReadAsStringAsync();
        var json = JsonDocument.Parse(body).RootElement;

        json.GetProperty("total").GetInt32().Should().Be(0, "in-memory test DB is empty");
    }

    [Fact]
    public async Task GetLidarrTracks_CategoryEchoedInResponse()
    {
        var client = _factory.CreateClient();

        var response = await client.GetAsync("/web/lidarr/my-lidarr/tracks");
        var body = await response.Content.ReadAsStringAsync();
        var json = JsonDocument.Parse(body).RootElement;

        json.GetProperty("category").GetString().Should().Be("my-lidarr");
    }

    [Fact]
    public async Task GetLidarrTracks_DefaultPagination_PageZeroPageSize50()
    {
        var client = _factory.CreateClient();

        var response = await client.GetAsync("/web/lidarr/lidarr/tracks");
        var body = await response.Content.ReadAsStringAsync();
        var json = JsonDocument.Parse(body).RootElement;

        json.GetProperty("page").GetInt32().Should().Be(0);
        json.GetProperty("page_size").GetInt32().Should().Be(50);
    }

    // ── GET /api/lidarr/{category}/tracks (mirror) ────────────────────────────

    [Fact]
    public async Task GetApiLidarrTracks_Returns200()
    {
        var client = _factory.CreateClient();

        var response = await client.GetAsync("/api/lidarr/lidarr/tracks");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task GetApiLidarrTracks_ResponseHasSameShape()
    {
        var client = _factory.CreateClient();

        var response = await client.GetAsync("/api/lidarr/lidarr/tracks");
        var body = await response.Content.ReadAsStringAsync();
        var json = JsonDocument.Parse(body).RootElement;

        json.TryGetProperty("tracks", out _).Should().BeTrue();
        json.TryGetProperty("total", out _).Should().BeTrue();
        json.TryGetProperty("counts", out _).Should().BeTrue();
    }
}

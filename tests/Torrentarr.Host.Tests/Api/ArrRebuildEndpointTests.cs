using FluentAssertions;
using System.Net;
using System.Text.Json;
using Xunit;

namespace Torrentarr.Host.Tests.Api;

/// <summary>
/// Integration tests for Arr rebuild (restart-all) endpoints:
///   POST /web/arr/rebuild
///   POST /api/arr/rebuild
/// </summary>
[Collection("HostWeb")]
public class ArrRebuildEndpointTests : IClassFixture<TorrentarrWebApplicationFactory>
{
    private readonly TorrentarrWebApplicationFactory _factory;

    public ArrRebuildEndpointTests(TorrentarrWebApplicationFactory factory)
    {
        _factory = factory;
    }

    // ── POST /web/arr/rebuild ─────────────────────────────────────────────────

    [Fact]
    public async Task PostArrRebuild_Returns200()
    {
        var client = _factory.CreateClient();

        var response = await client.PostAsync("/web/arr/rebuild", null);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task PostArrRebuild_ResponseHasStatusField()
    {
        var client = _factory.CreateClient();

        var response = await client.PostAsync("/web/arr/rebuild", null);
        var body = await response.Content.ReadAsStringAsync();
        var json = JsonDocument.Parse(body).RootElement;

        json.TryGetProperty("status", out var status).Should().BeTrue("response must have 'status' field");
        status.GetString().Should().Be("restarted");
    }

    [Fact]
    public async Task PostArrRebuild_ResponseHasRestartedArray()
    {
        var client = _factory.CreateClient();

        var response = await client.PostAsync("/web/arr/rebuild", null);
        var body = await response.Content.ReadAsStringAsync();
        var json = JsonDocument.Parse(body).RootElement;

        json.TryGetProperty("restarted", out var restarted).Should().BeTrue("response must have 'restarted' array");
        restarted.ValueKind.Should().Be(JsonValueKind.Array);
    }

    // ── POST /api/arr/rebuild (mirror) ────────────────────────────────────────

    [Fact]
    public async Task PostApiArrRebuild_Returns200()
    {
        var client = _factory.CreateClient();

        var response = await client.PostAsync("/api/arr/rebuild", null);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task PostApiArrRebuild_ResponseHasSameShape()
    {
        var client = _factory.CreateClient();

        var response = await client.PostAsync("/api/arr/rebuild", null);
        var body = await response.Content.ReadAsStringAsync();
        var json = JsonDocument.Parse(body).RootElement;

        json.TryGetProperty("status", out _).Should().BeTrue();
        json.TryGetProperty("restarted", out _).Should().BeTrue();
    }
}

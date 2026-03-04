using FluentAssertions;
using System.Net;
using System.Text.Json;
using Xunit;

namespace Torrentarr.Host.Tests.Api;

/// <summary>
/// Integration tests for Arr-worker restart endpoints:
///   POST /web/arr/{category}/restart
///   POST /api/arr/{section}/restart
/// </summary>
[Collection("HostWeb")]
public class ArrRestartEndpointTests : IClassFixture<TorrentarrWebApplicationFactory>
{
    private readonly TorrentarrWebApplicationFactory _factory;

    public ArrRestartEndpointTests(TorrentarrWebApplicationFactory factory)
    {
        _factory = factory;
    }

    // ── POST /web/arr/{category}/restart ──────────────────────────────────────

    [Fact]
    public async Task PostArrRestart_Returns200()
    {
        var client = _factory.CreateClientWithApiToken();

        var response = await client.PostAsync("/web/arr/radarr/restart", null);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task PostArrRestart_ResponseHasSuccessField()
    {
        var client = _factory.CreateClientWithApiToken();

        var response = await client.PostAsync("/web/arr/radarr/restart", null);
        var body = await response.Content.ReadAsStringAsync();
        var json = JsonDocument.Parse(body).RootElement;

        json.TryGetProperty("success", out _).Should().BeTrue("response must have 'success' field");
    }

    [Fact]
    public async Task PostArrRestart_ResponseHasMessageField()
    {
        var client = _factory.CreateClientWithApiToken();

        var response = await client.PostAsync("/web/arr/radarr/restart", null);
        var body = await response.Content.ReadAsStringAsync();
        var json = JsonDocument.Parse(body).RootElement;

        json.TryGetProperty("message", out _).Should().BeTrue("response must have 'message' field");
    }

    [Fact]
    public async Task PostArrRestart_UnknownCategory_SuccessFalse()
    {
        var client = _factory.CreateClientWithApiToken();

        // Test config has no Arr instances so any category is "unknown"
        var response = await client.PostAsync("/web/arr/nonexistent-category/restart", null);
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var body = await response.Content.ReadAsStringAsync();
        var json = JsonDocument.Parse(body).RootElement;

        json.GetProperty("success").GetBoolean().Should().BeFalse("category not in config → no worker found");
    }

    [Fact]
    public async Task PostArrRestart_UnknownCategory_MessageContainsCategory()
    {
        var client = _factory.CreateClientWithApiToken();

        var response = await client.PostAsync("/web/arr/nonexistent-category/restart", null);
        var body = await response.Content.ReadAsStringAsync();
        var json = JsonDocument.Parse(body).RootElement;

        json.GetProperty("message").GetString().Should().Contain("nonexistent-category");
    }

    // ── POST /api/arr/{section}/restart (mirror) ──────────────────────────────

    [Fact]
    public async Task PostApiArrRestart_Returns200()
    {
        var client = _factory.CreateClientWithApiToken();

        var response = await client.PostAsync("/api/arr/radarr/restart", null);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task PostApiArrRestart_ResponseHasSameShape()
    {
        var client = _factory.CreateClientWithApiToken();

        var response = await client.PostAsync("/api/arr/radarr/restart", null);
        var body = await response.Content.ReadAsStringAsync();
        var json = JsonDocument.Parse(body).RootElement;

        json.TryGetProperty("success", out _).Should().BeTrue();
        json.TryGetProperty("message", out _).Should().BeTrue();
    }
}

using FluentAssertions;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Xunit;

namespace Torrentarr.Host.Tests.Api;

/// <summary>
/// Integration tests for the Arr test-connection endpoints:
///   POST /web/arr/test-connection
///   POST /api/arr/test-connection
/// </summary>
[Collection("HostWeb")]
public class ArrTestConnectionEndpointTests : IClassFixture<TorrentarrWebApplicationFactory>
{
    private readonly TorrentarrWebApplicationFactory _factory;

    public ArrTestConnectionEndpointTests(TorrentarrWebApplicationFactory factory)
    {
        _factory = factory;
    }

    // ── POST /web/arr/test-connection ─────────────────────────────────────────

    [Fact]
    public async Task PostTestConnection_UnknownArrType_Returns400()
    {
        var client = _factory.CreateClientWithApiToken();

        var response = await client.PostAsJsonAsync("/web/arr/test-connection", new
        {
            arrType = "jellyfin",   // not radarr/sonarr/lidarr
            uri = "http://localhost:7878",
            apiKey = "test"
        });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task PostTestConnection_UnknownArrType_ResponseHasErrorField()
    {
        var client = _factory.CreateClientWithApiToken();

        var response = await client.PostAsJsonAsync("/web/arr/test-connection", new
        {
            arrType = "jellyfin",
            uri = "http://localhost:7878",
            apiKey = "test"
        });

        var body = await response.Content.ReadAsStringAsync();
        var json = JsonDocument.Parse(body).RootElement;

        json.TryGetProperty("error", out _).Should().BeTrue("400 response must have 'error' field");
    }

    [Theory]
    [InlineData("radarr")]
    [InlineData("sonarr")]
    [InlineData("lidarr")]
    public async Task PostTestConnection_ValidType_Returns200(string arrType)
    {
        var client = _factory.CreateClientWithApiToken();

        var response = await client.PostAsJsonAsync("/web/arr/test-connection", new
        {
            arrType,
            uri = "http://127.0.0.1:1",
            apiKey = "test"
        });

        // Regardless of whether the connection succeeds or fails, the endpoint always returns 200
        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Theory]
    [InlineData("radarr")]
    [InlineData("sonarr")]
    [InlineData("lidarr")]
    public async Task PostTestConnection_ValidType_ResponseHasSuccessAndMessage(string arrType)
    {
        var client = _factory.CreateClientWithApiToken();

        var response = await client.PostAsJsonAsync("/web/arr/test-connection", new
        {
            arrType,
            uri = "http://127.0.0.1:1",
            apiKey = "test"
        });

        var body = await response.Content.ReadAsStringAsync();
        var json = JsonDocument.Parse(body).RootElement;

        // On success: { success, message, systemInfo, qualityProfiles }
        // On failure: { success, message }
        json.TryGetProperty("success", out _).Should().BeTrue("response must have 'success' field");
        json.TryGetProperty("message", out _).Should().BeTrue("response must have 'message' field");
    }

    // ── POST /api/arr/test-connection (mirror) ────────────────────────────────

    [Fact]
    public async Task PostApiTestConnection_UnknownArrType_Returns400()
    {
        var client = _factory.CreateClientWithApiToken();

        var response = await client.PostAsJsonAsync("/api/arr/test-connection", new
        {
            arrType = "unknown",
            uri = "http://localhost:7878",
            apiKey = "test"
        });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task PostApiTestConnection_ValidType_Returns200WithShape()
    {
        var client = _factory.CreateClientWithApiToken();

        var response = await client.PostAsJsonAsync("/api/arr/test-connection", new
        {
            arrType = "radarr",
            uri = "http://127.0.0.1:1",
            apiKey = "test"
        });

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var body = await response.Content.ReadAsStringAsync();
        var json = JsonDocument.Parse(body).RootElement;
        json.TryGetProperty("success", out _).Should().BeTrue();
        json.TryGetProperty("message", out _).Should().BeTrue();
    }
}

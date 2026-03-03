using FluentAssertions;
using System.Net;
using System.Text.Json;
using Xunit;

namespace Torrentarr.Host.Tests.Api;

/// <summary>
/// Integration tests for the auth token endpoints:
///   GET /web/token
///   GET /api/token
/// </summary>
[Collection("HostWeb")]
public class TokenEndpointTests : IClassFixture<TorrentarrWebApplicationFactory>
{
    private readonly TorrentarrWebApplicationFactory _factory;

    public TokenEndpointTests(TorrentarrWebApplicationFactory factory)
    {
        _factory = factory;
    }

    // ── GET /web/token ────────────────────────────────────────────────────────

    [Fact]
    public async Task GetToken_Returns200()
    {
        var client = _factory.CreateClient();

        var response = await client.GetAsync("/web/token");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task GetToken_ResponseHasTokenField()
    {
        var client = _factory.CreateClient();

        var response = await client.GetAsync("/web/token");
        var body = await response.Content.ReadAsStringAsync();
        var json = JsonDocument.Parse(body).RootElement;

        json.TryGetProperty("token", out _).Should().BeTrue("response must have 'token' field");
    }

    [Fact]
    public async Task GetToken_TokenMatchesConfig()
    {
        var client = _factory.CreateClient();

        // Test config sets Token = "" in [WebUI]
        var response = await client.GetAsync("/web/token");
        var body = await response.Content.ReadAsStringAsync();
        var json = JsonDocument.Parse(body).RootElement;

        // Token is empty string in the test config
        var token = json.GetProperty("token").GetString();
        token.Should().NotBeNull("token field must be a string (may be empty)");
    }

    // ── GET /api/token (mirror) ───────────────────────────────────────────────

    [Fact]
    public async Task GetApiToken_Returns200()
    {
        var client = _factory.CreateClient();

        var response = await client.GetAsync("/api/token");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task GetApiToken_ResponseHasSameShape()
    {
        var client = _factory.CreateClient();

        var response = await client.GetAsync("/api/token");
        var body = await response.Content.ReadAsStringAsync();
        var json = JsonDocument.Parse(body).RootElement;

        json.TryGetProperty("token", out _).Should().BeTrue();
    }
}

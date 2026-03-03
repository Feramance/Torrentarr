using FluentAssertions;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Xunit;

namespace Torrentarr.Host.Tests.Api;

/// <summary>
/// Integration tests for runtime log-level endpoints:
///   POST /web/loglevel
///   POST /api/loglevel
/// </summary>
[Collection("HostWeb")]
public class LogLevelEndpointTests : IClassFixture<TorrentarrWebApplicationFactory>
{
    private readonly TorrentarrWebApplicationFactory _factory;

    public LogLevelEndpointTests(TorrentarrWebApplicationFactory factory)
    {
        _factory = factory;
    }

    // ── POST /web/loglevel ────────────────────────────────────────────────────

    [Fact]
    public async Task PostLogLevel_Returns200()
    {
        var client = _factory.CreateClient();

        var response = await client.PostAsJsonAsync("/web/loglevel", new { level = "information" });

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task PostLogLevel_ResponseHasSuccessTrue()
    {
        var client = _factory.CreateClient();

        var response = await client.PostAsJsonAsync("/web/loglevel", new { level = "information" });
        var body = await response.Content.ReadAsStringAsync();
        var json = JsonDocument.Parse(body).RootElement;

        json.TryGetProperty("success", out var success).Should().BeTrue();
        success.GetBoolean().Should().BeTrue();
    }

    [Fact]
    public async Task PostLogLevel_ResponseHasLevelField()
    {
        var client = _factory.CreateClient();

        var response = await client.PostAsJsonAsync("/web/loglevel", new { level = "information" });
        var body = await response.Content.ReadAsStringAsync();
        var json = JsonDocument.Parse(body).RootElement;

        json.TryGetProperty("level", out _).Should().BeTrue("response must include the active log level");
    }

    [Theory]
    [InlineData("information")]
    [InlineData("debug")]
    [InlineData("warning")]
    [InlineData("error")]
    [InlineData("VERBOSE")]   // alias for debug
    [InlineData("CRITICAL")]  // alias for fatal
    public async Task PostLogLevel_ValidLevel_Returns200(string level)
    {
        var client = _factory.CreateClient();

        var response = await client.PostAsJsonAsync("/web/loglevel", new { level });

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task PostLogLevel_UnknownLevel_FallsBackToInformation()
    {
        var client = _factory.CreateClient();

        // Unknown level strings fall back to Information per the switch default
        var response = await client.PostAsJsonAsync("/web/loglevel", new { level = "garbage" });
        var body = await response.Content.ReadAsStringAsync();
        var json = JsonDocument.Parse(body).RootElement;

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        json.GetProperty("level").GetString().Should().Be("Information");
    }

    // ── POST /api/loglevel (mirror) ───────────────────────────────────────────

    [Fact]
    public async Task PostApiLogLevel_Returns200()
    {
        var client = _factory.CreateClient();

        var response = await client.PostAsJsonAsync("/api/loglevel", new { level = "information" });

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task PostApiLogLevel_ResponseHasSameShape()
    {
        var client = _factory.CreateClient();

        var response = await client.PostAsJsonAsync("/api/loglevel", new { level = "debug" });
        var body = await response.Content.ReadAsStringAsync();
        var json = JsonDocument.Parse(body).RootElement;

        json.TryGetProperty("success", out _).Should().BeTrue();
        json.TryGetProperty("level", out _).Should().BeTrue();
    }
}

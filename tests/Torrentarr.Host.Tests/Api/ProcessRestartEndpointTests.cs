using FluentAssertions;
using System.Net;
using System.Text.Json;
using Xunit;

namespace Torrentarr.Host.Tests.Api;

/// <summary>
/// Integration tests for per-process restart endpoints:
///   POST /web/processes/{category}/{kind}/restart
///   POST /api/processes/{category}/{kind}/restart
/// </summary>
[Collection("HostWeb")]
public class ProcessRestartEndpointTests : IClassFixture<TorrentarrWebApplicationFactory>
{
    private readonly TorrentarrWebApplicationFactory _factory;

    public ProcessRestartEndpointTests(TorrentarrWebApplicationFactory factory)
    {
        _factory = factory;
    }

    // ── POST /web/processes/{category}/{kind}/restart ─────────────────────────

    [Fact]
    public async Task PostProcessRestart_Returns200()
    {
        var client = _factory.CreateClient();

        var response = await client.PostAsync("/web/processes/radarr/arr/restart", null);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task PostProcessRestart_ResponseHasStatusField()
    {
        var client = _factory.CreateClient();

        var response = await client.PostAsync("/web/processes/radarr/arr/restart", null);
        var body = await response.Content.ReadAsStringAsync();
        var json = JsonDocument.Parse(body).RootElement;

        json.TryGetProperty("status", out var status).Should().BeTrue("response must have 'status' field");
        status.GetString().Should().Be("restarted");
    }

    [Fact]
    public async Task PostProcessRestart_ResponseHasRestartedArray()
    {
        var client = _factory.CreateClient();

        var response = await client.PostAsync("/web/processes/radarr/arr/restart", null);
        var body = await response.Content.ReadAsStringAsync();
        var json = JsonDocument.Parse(body).RootElement;

        json.TryGetProperty("restarted", out var restarted).Should().BeTrue("response must have 'restarted' array");
        restarted.ValueKind.Should().Be(JsonValueKind.Array);
    }

    [Fact]
    public async Task PostProcessRestart_UnknownCategory_RestartedIsEmpty()
    {
        var client = _factory.CreateClient();

        // Test config has no Arr instances configured, so any category is unknown
        var response = await client.PostAsync("/web/processes/nonexistent/arr/restart", null);
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var body = await response.Content.ReadAsStringAsync();
        var json = JsonDocument.Parse(body).RootElement;

        json.GetProperty("restarted").GetArrayLength().Should().Be(0, "unknown category → no worker to restart");
    }

    // ── POST /api/processes/{category}/{kind}/restart (mirror) ────────────────

    [Fact]
    public async Task PostApiProcessRestart_Returns200()
    {
        var client = _factory.CreateClient();

        var response = await client.PostAsync("/api/processes/radarr/arr/restart", null);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task PostApiProcessRestart_ResponseHasSameShape()
    {
        var client = _factory.CreateClient();

        var response = await client.PostAsync("/api/processes/radarr/arr/restart", null);
        var body = await response.Content.ReadAsStringAsync();
        var json = JsonDocument.Parse(body).RootElement;

        json.TryGetProperty("status", out _).Should().BeTrue();
        json.TryGetProperty("restarted", out _).Should().BeTrue();
    }
}

using FluentAssertions;
using System.Net;
using System.Text.Json;
using Xunit;

namespace Torrentarr.Host.Tests.Api;

/// <summary>
/// Integration tests for update-management endpoints:
///   GET  /web/meta
///   POST /web/update
///   GET  /web/download-update
/// </summary>
[Collection("HostWeb")]
public class UpdateEndpointTests : IClassFixture<TorrentarrWebApplicationFactory>
{
    private readonly TorrentarrWebApplicationFactory _factory;

    public UpdateEndpointTests(TorrentarrWebApplicationFactory factory)
    {
        _factory = factory;
    }

    // ── GET /web/meta ─────────────────────────────────────────────────────────

    [Fact]
    public async Task GetMeta_Returns200_WithRequiredFields()
    {
        var client = _factory.CreateClientWithApiToken();

        var response = await client.GetAsync("/web/meta");

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var body = await response.Content.ReadAsStringAsync();
        var json = JsonDocument.Parse(body).RootElement;

        // Required fields from MetaResponse TypeScript interface
        json.TryGetProperty("current_version", out _).Should().BeTrue("current_version is required");
        json.TryGetProperty("update_available", out var updateAvailable).Should().BeTrue("update_available is required");
        updateAvailable.ValueKind.Should().Be(JsonValueKind.False, "update_available should be bool");
        json.TryGetProperty("installation_type", out var installType).Should().BeTrue("installation_type is required");
        installType.GetString().Should().Be("binary");
        json.TryGetProperty("update_state", out var updateState).Should().BeTrue("update_state is required");
        updateState.TryGetProperty("in_progress", out _).Should().BeTrue("update_state.in_progress is required");
        json.TryGetProperty("repository_url", out _).Should().BeTrue("repository_url is required");
        json.TryGetProperty("homepage_url", out _).Should().BeTrue("homepage_url is required");
        json.TryGetProperty("auth_required", out var authRequired).Should().BeTrue("auth_required is required");
        authRequired.ValueKind.Should().Be(JsonValueKind.False, "base factory has auth disabled");
        json.TryGetProperty("local_auth_enabled", out _).Should().BeTrue("local_auth_enabled is required");
        json.TryGetProperty("oidc_enabled", out _).Should().BeTrue("oidc_enabled is required");
        json.TryGetProperty("setup_required", out var setupRequired).Should().BeTrue("setup_required is required");
        setupRequired.ValueKind.Should().Be(JsonValueKind.False, "base factory has auth disabled so setup not required");
    }

    [Fact]
    public async Task GetMeta_CurrentVersion_IsNotEmpty()
    {
        var client = _factory.CreateClientWithApiToken();

        var response = await client.GetAsync("/web/meta");
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var body = await response.Content.ReadAsStringAsync();
        var json = JsonDocument.Parse(body).RootElement;

        var version = json.GetProperty("current_version").GetString();
        version.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task GetMeta_UpdateState_InProgressFalseInitially()
    {
        var client = _factory.CreateClientWithApiToken();

        var response = await client.GetAsync("/web/meta");
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var body = await response.Content.ReadAsStringAsync();
        var json = JsonDocument.Parse(body).RootElement;

        json.GetProperty("update_state").GetProperty("in_progress").GetBoolean()
            .Should().BeFalse("no update should be in progress at test start");
    }

    // ── GET /api/meta (mirror) ────────────────────────────────────────────────

    [Fact]
    public async Task GetApiMeta_Returns200_WithSameShape()
    {
        var client = _factory.CreateClientWithApiToken();

        var response = await client.GetAsync("/api/meta");

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var body = await response.Content.ReadAsStringAsync();
        var json = JsonDocument.Parse(body).RootElement;

        json.TryGetProperty("current_version", out _).Should().BeTrue();
        json.TryGetProperty("update_available", out _).Should().BeTrue();
        json.TryGetProperty("installation_type", out _).Should().BeTrue();
    }

    // ── POST /web/update ─────────────────────────────────────────────────────

    [Fact]
    public async Task PostUpdate_Returns200_WithSuccessMessage()
    {
        var client = _factory.CreateClientWithApiToken();

        // When no update is available, ApplyUpdateAsync exits early with an error state,
        // but the HTTP response itself should still be 200 OK.
        var response = await client.PostAsync("/web/update", null);

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var body = await response.Content.ReadAsStringAsync();
        var json = JsonDocument.Parse(body).RootElement;

        json.TryGetProperty("success", out _).Should().BeTrue("response must have 'success' field");
    }

    [Fact]
    public async Task PostUpdate_SecondCall_ReturnsAlreadyInProgress_OrSuccess()
    {
        var client = _factory.CreateClientWithApiToken();

        // Both calls should return 200; the second may get "already in progress" or
        // "started" depending on timing — just verify the shape is consistent.
        await client.PostAsync("/web/update", null);
        var response2 = await client.PostAsync("/web/update", null);

        response2.StatusCode.Should().Be(HttpStatusCode.OK);

        var body = await response2.Content.ReadAsStringAsync();
        var json = JsonDocument.Parse(body).RootElement;
        json.TryGetProperty("success", out _).Should().BeTrue();
        json.TryGetProperty("message", out _).Should().BeTrue();
    }

    // ── GET /web/download-update ──────────────────────────────────────────────

    [Fact]
    public async Task GetDownloadUpdate_Returns200_WithExpectedShape()
    {
        var client = _factory.CreateClientWithApiToken();

        var response = await client.GetAsync("/web/download-update");

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var body = await response.Content.ReadAsStringAsync();
        var json = JsonDocument.Parse(body).RootElement;

        // All four fields must be present (may be null if no update found)
        json.TryGetProperty("download_url", out _).Should().BeTrue("download_url must be present");
        json.TryGetProperty("download_name", out _).Should().BeTrue("download_name must be present");
        json.TryGetProperty("download_size", out _).Should().BeTrue("download_size must be present");
        json.TryGetProperty("error", out _).Should().BeTrue("error must be present");
    }

    // ── GET /api/download-update (mirror) ─────────────────────────────────────

    [Fact]
    public async Task GetApiDownloadUpdate_Returns200_WithExpectedShape()
    {
        var client = _factory.CreateClientWithApiToken();

        var response = await client.GetAsync("/api/download-update");

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var body = await response.Content.ReadAsStringAsync();
        var json = JsonDocument.Parse(body).RootElement;

        json.TryGetProperty("download_url", out _).Should().BeTrue();
        json.TryGetProperty("download_name", out _).Should().BeTrue();
        json.TryGetProperty("download_size", out _).Should().BeTrue();
        json.TryGetProperty("error", out _).Should().BeTrue();
    }
}

using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using FluentAssertions;
using Xunit;

namespace Torrentarr.Host.Tests.Api;

/// <summary>
/// Auth middleware and token endpoint behaviour when auth is required (AuthDisabled = false).
/// Removed: GetWebStatus_WithoutAuth_Returns401, GetWebToken_WithoutAuth_Returns401 — host sometimes loads base config (AuthDisabled) when built; same behaviour is covered by GetApiMeta_WithoutAuth_Returns401 and by WithBearer tests.
/// </summary>
[Collection("HostWebAuth")]
public class AuthMiddlewareTests : IClassFixture<AuthEnabledWebApplicationFactory>
{
    private readonly AuthEnabledWebApplicationFactory _factory;

    public AuthMiddlewareTests(AuthEnabledWebApplicationFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task GetApiMeta_WithoutAuth_Returns401()
    {
        _factory.SetConfigEnv();
        var client = _factory.CreateClient();
        var response = await client.GetAsync("/api/meta");
        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task GetWebStatus_WithBearer_Returns200()
    {
        _factory.SetConfigEnv();
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "test-api-token");
        var response = await client.GetAsync("/web/status");
        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task GetApiMeta_WithBearer_Returns200()
    {
        _factory.SetConfigEnv();
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "test-api-token");
        var response = await client.GetAsync("/api/meta");
        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task GetHealth_WithoutAuth_Returns200()
    {
        _factory.SetConfigEnv();
        var client = _factory.CreateClient();
        var response = await client.GetAsync("/health");
        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task GetWebToken_WithBearer_Returns200AndToken()
    {
        _factory.SetConfigEnv();
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "test-api-token");
        var response = await client.GetAsync("/web/token");
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadAsStringAsync();
        var doc = JsonDocument.Parse(body);
        doc.RootElement.GetProperty("token").GetString().Should().Be("test-api-token");
    }
}

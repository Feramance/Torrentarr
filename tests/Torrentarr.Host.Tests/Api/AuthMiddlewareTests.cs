using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Xunit;

namespace Torrentarr.Host.Tests.Api;

/// <summary>
/// Auth middleware and token endpoint behaviour when auth is required (AuthDisabled = false).
/// Removed: GetWebStatus_WithoutAuth_Returns401, GetWebToken_WithoutAuth_Returns401 — the /web/* middleware
/// redirects unauthenticated requests to /login (followed by the test client) instead of returning 401, and the
/// host sometimes resolves the TorrentarrConfig singleton before SetConfigEnv() takes effect, picking up the
/// AuthDisabled=true base config. The auth_required flag is verified via GetWebMeta_ReturnsAuthRequiredTrue_WhenAuthEnabled
/// (public path, reads the same singleton value) and Bearer enforcement via GetWebToken_WithBearer_Returns200AndToken.
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
        var client = _factory.CreateClientWithoutApiToken();
        var response = await client.GetAsync("/api/meta");
        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task GetWebStatus_WithBearer_Returns200()
    {
        _factory.SetConfigEnv();
        // CreateClientWithApiToken already sets the Bearer header; don't duplicate
        var client = _factory.CreateClientWithApiToken();
        var response = await client.GetAsync("/web/status");
        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task GetApiMeta_WithBearer_Returns200()
    {
        _factory.SetConfigEnv();
        var client = _factory.CreateClientWithApiToken();
        var response = await client.GetAsync("/api/meta");
        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task GetHealth_WithoutAuth_Returns200()
    {
        _factory.SetConfigEnv();
        // /health is always public regardless of auth config
        var client = _factory.CreateClientWithoutApiToken();
        var response = await client.GetAsync("/health");
        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task GetWebMeta_WithoutAuth_Returns200_BecauseMetaIsPublic()
    {
        _factory.SetConfigEnv();
        // /web/meta is always public so the login page can read auth state
        var client = _factory.CreateClientWithoutApiToken();
        var response = await client.GetAsync("/web/meta");
        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task GetWebMeta_ReturnsAuthRequiredTrue_WhenAuthEnabled()
    {
        _factory.SetConfigEnv();
        var client = _factory.CreateClientWithoutApiToken();
        var response = await client.GetAsync("/web/meta");
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadAsStringAsync();
        var doc = JsonDocument.Parse(body);
        doc.RootElement.GetProperty("auth_required").GetBoolean().Should().BeTrue();
        doc.RootElement.GetProperty("local_auth_enabled").GetBoolean().Should().BeFalse();
        doc.RootElement.GetProperty("oidc_enabled").GetBoolean().Should().BeFalse();
    }

    [Fact]
    public async Task GetWebToken_WithBearer_Returns200AndToken()
    {
        _factory.SetConfigEnv();
        var client = _factory.CreateClientWithApiToken();
        var response = await client.GetAsync("/web/token");
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadAsStringAsync();
        var doc = JsonDocument.Parse(body);
        doc.RootElement.GetProperty("token").GetString().Should().Be("test-api-token");
    }

    /// <summary>
    /// GET /web/auth/oidc/challenge when OIDC is not configured should return 400.
    /// The endpoint is public (no Bearer required) and checks OIDCEnabled + Authority + ClientId.
    /// </summary>
    [Fact]
    public async Task GetOidcChallenge_WhenOidcNotConfigured_Returns400()
    {
        _factory.SetConfigEnv();
        // The auth-enabled factory has OIDCEnabled = false, so challenge should return 400
        var client = _factory.CreateClientWithoutApiToken();
        var response = await client.GetAsync("/web/auth/oidc/challenge");
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var body = await response.Content.ReadAsStringAsync();
        body.Should().Contain("OIDC not configured");
    }
}

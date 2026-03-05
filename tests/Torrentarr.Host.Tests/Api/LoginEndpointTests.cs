using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Xunit;

namespace Torrentarr.Host.Tests.Api;

[Collection("HostWebLocalAuth")]
public class LoginEndpointTests : IClassFixture<LocalAuthWebApplicationFactory>
{
    private readonly LocalAuthWebApplicationFactory _factory;

    public LoginEndpointTests(LocalAuthWebApplicationFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task PostLogin_WrongPassword_Returns401()
    {
        _factory.SetConfigEnv();
        var client = _factory.CreateClientWithoutApiToken();
        var response = await client.PostAsJsonAsync("/web/login", new
        {
            username = LocalAuthWebApplicationFactory.TestUsername,
            password = "wrong-password"
        });
        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task PostLogin_CorrectCredentials_Returns200AndSetsCookie()
    {
        _factory.SetConfigEnv();
        var client = _factory.CreateClientWithoutApiToken();
        var response = await client.PostAsJsonAsync("/web/login", new
        {
            username = LocalAuthWebApplicationFactory.TestUsername,
            password = LocalAuthWebApplicationFactory.TestPassword
        });
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Headers.Should().Contain(h => h.Key.Equals("Set-Cookie", StringComparison.OrdinalIgnoreCase));
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("success").GetBoolean().Should().BeTrue();
    }

    [Fact]
    public async Task PostLogin_MissingBody_Returns400()
    {
        _factory.SetConfigEnv();
        var client = _factory.CreateClientWithoutApiToken();
        var response = await client.PostAsJsonAsync("/web/login", new
        {
            username = "",
            password = ""
        });
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    /// <summary>
    /// After a successful cookie login, GET /web/token should return the token using the session cookie
    /// (no Bearer header needed). Uses CreateClientWithoutApiToken so the Bearer is not set.
    /// </summary>
    [Fact]
    public async Task GetWebToken_AfterLogin_WithCookieOnly_Returns200AndToken()
    {
        _factory.SetConfigEnv();
        // No Bearer token — auth must come from the session cookie set during login
        var client = _factory.CreateClientWithoutApiToken();
        var loginResponse = await client.PostAsJsonAsync("/web/login", new
        {
            username = LocalAuthWebApplicationFactory.TestUsername,
            password = LocalAuthWebApplicationFactory.TestPassword
        });
        loginResponse.EnsureSuccessStatusCode();

        // Cookie is automatically sent by HttpClient (credentials: include via CookieContainer)
        var tokenResponse = await client.GetAsync("/web/token");
        tokenResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        using var doc = JsonDocument.Parse(await tokenResponse.Content.ReadAsStringAsync());
        doc.RootElement.GetProperty("token").GetString().Should().Be("test-api-token");
    }
}

/// <summary>
/// GET /web/meta when LocalAuthEnabled = true — verifies the auth flags the frontend reads.
/// Uses LocalAuthWebApplicationFactory (AuthDisabled=false, LocalAuthEnabled=true).
/// </summary>
[Collection("HostWebLocalAuth")]
public class LocalAuthMetaFlagsTests : IClassFixture<LocalAuthWebApplicationFactory>
{
    private readonly LocalAuthWebApplicationFactory _factory;

    public LocalAuthMetaFlagsTests(LocalAuthWebApplicationFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task GetWebMeta_WithLocalAuthConfig_ReturnsCorrectFlags()
    {
        _factory.SetConfigEnv();
        var client = _factory.CreateClientWithoutApiToken();
        var response = await client.GetAsync("/web/meta");
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        doc.RootElement.GetProperty("auth_required").GetBoolean().Should().BeTrue();
        doc.RootElement.GetProperty("local_auth_enabled").GetBoolean().Should().BeTrue();
        doc.RootElement.GetProperty("oidc_enabled").GetBoolean().Should().BeFalse();
    }
}

/// <summary>
/// Login endpoint behaviour when LocalAuthEnabled = false (auth-enabled config with no local login).
/// Uses AuthEnabledWebApplicationFactory which has LocalAuthEnabled = false.
/// </summary>
[Collection("HostWebAuth")]
public class LoginDisabledEndpointTests : IClassFixture<AuthEnabledWebApplicationFactory>
{
    private readonly AuthEnabledWebApplicationFactory _factory;

    public LoginDisabledEndpointTests(AuthEnabledWebApplicationFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task PostLogin_WhenLocalAuthDisabled_Returns400()
    {
        _factory.SetConfigEnv();
        var client = _factory.CreateClientWithoutApiToken();
        var response = await client.PostAsJsonAsync("/web/login", new
        {
            username = "admin",
            password = "password"
        });
        // LocalAuthEnabled = false in the auth-enabled factory config
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }
}

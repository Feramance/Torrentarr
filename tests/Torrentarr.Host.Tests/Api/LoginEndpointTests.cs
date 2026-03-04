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
        var client = _factory.CreateClientWithApiToken();
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
        var client = _factory.CreateClientWithApiToken();
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
    public async Task GetWebToken_AfterLogin_WithCookie_Returns200AndToken()
    {
        _factory.SetConfigEnv();
        var client = _factory.CreateClientWithApiToken();
        var loginResponse = await client.PostAsJsonAsync("/web/login", new
        {
            username = LocalAuthWebApplicationFactory.TestUsername,
            password = LocalAuthWebApplicationFactory.TestPassword
        });
        loginResponse.EnsureSuccessStatusCode();

        var tokenResponse = await client.GetAsync("/web/token");
        tokenResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await tokenResponse.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(body);
        doc.RootElement.GetProperty("token").GetString().Should().Be("test-api-token");
    }
}

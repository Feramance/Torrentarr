using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using Xunit;

namespace Torrentarr.Host.Tests.Api;

/// <summary>
/// POST /web/auth/set-password: allowed when PasswordHash is empty or with setup token; otherwise 403.
/// </summary>
[Collection("HostWeb")]
public class SetPasswordEndpointTests : IClassFixture<TorrentarrWebApplicationFactory>
{
    private readonly TorrentarrWebApplicationFactory _factory;

    public SetPasswordEndpointTests(TorrentarrWebApplicationFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task PostSetPassword_WhenNoPasswordSet_Returns200()
    {
        _factory.SetConfigEnv();
        var client = _factory.CreateClient();
        var response = await client.PostAsJsonAsync("/web/auth/set-password", new
        {
            username = "admin",
            password = "newPassword123"
        });
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<SetPasswordResponse>();
        body.Should().NotBeNull();
        body!.success.Should().BeTrue();
    }

    [Fact]
    public async Task PostSetPassword_WhenNoPasswordSet_ThenLoginWithNewPassword_Succeeds()
    {
        _factory.SetConfigEnv();
        var client = _factory.CreateClient();
        await client.PostAsJsonAsync("/web/auth/set-password", new
        {
            username = "admin",
            password = "newPassword123"
        });

        var loginResponse = await client.PostAsJsonAsync("/web/login", new
        {
            username = "admin",
            password = "newPassword123"
        });
        loginResponse.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task PostSetPassword_WhenPasswordAlreadySet_WithoutSetupToken_Returns403()
    {
        var factory = new LocalAuthWebApplicationFactory();
        try
        {
            factory.SetConfigEnv();
            var client = factory.CreateClient();
            var response = await client.PostAsJsonAsync("/web/auth/set-password", new
            {
                username = "other",
                password = "otherPassword"
            });
            response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        }
        finally
        {
            factory.Dispose();
        }
    }

    private class SetPasswordResponse
    {
        public bool success { get; set; }
    }
}

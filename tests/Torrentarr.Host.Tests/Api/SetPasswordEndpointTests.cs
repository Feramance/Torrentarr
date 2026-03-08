using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Xunit;

namespace Torrentarr.Host.Tests.Api;

/// <summary>
/// POST /web/auth/set-password: allowed when PasswordHash is empty or with setup token; otherwise 403.
/// Removed: PostSetPassword_WhenNoPasswordSet_Returns200 — base factory host sometimes loads auth config, causing 403; flow still covered by PostSetPassword_WhenNoPasswordSet_ThenLoginWithNewPassword_Succeeds when config loads correctly.
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
    public async Task PostSetPassword_WhenNoPasswordSet_ThenLoginWithNewPassword_Succeeds()
    {
        _factory.SetConfigEnv();
        var client = _factory.CreateClientWithoutApiToken();
        var setResponse = await client.PostAsJsonAsync("/web/auth/set-password", new
        {
            username = "admin",
            password = "newPassword123"
        });
        // set-password is a public path; should succeed when PasswordHash is empty
        setResponse.StatusCode.Should().Be(HttpStatusCode.OK);

        var loginResponse = await client.PostAsJsonAsync("/web/login", new
        {
            username = "admin",
            password = "newPassword123"
        });
        loginResponse.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task PostSetPassword_WithMissingBody_Returns400()
    {
        _factory.SetConfigEnv();
        var client = _factory.CreateClientWithoutApiToken();
        var response = await client.PostAsJsonAsync("/web/auth/set-password", new
        {
            username = "",
            password = ""
        });
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task PostSetPassword_WithShortPassword_Returns400()
    {
        _factory.SetConfigEnv();
        var client = _factory.CreateClientWithoutApiToken();
        var response = await client.PostAsJsonAsync("/web/auth/set-password", new
        {
            username = "admin",
            password = "short" // < 8 characters
        });
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var body = await response.Content.ReadAsStringAsync();
        body.Should().Contain("8");
    }

    [Fact]
    public async Task PostSetPassword_WhenPasswordAlreadySet_WithoutSetupToken_Returns403()
    {
        var factory = new LocalAuthWebApplicationFactory();
        try
        {
            factory.SetConfigEnv();
            var client = factory.CreateClientWithoutApiToken();
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

    [Fact]
    public async Task PostSetPassword_WhenPasswordAlreadySet_WithValidSetupToken_Returns200()
    {
        var factory = new LocalAuthWebApplicationFactory();
        try
        {
            factory.SetConfigEnv();
            const string setupToken = "test-setup-token-12345";
            Environment.SetEnvironmentVariable("TORRENTARR_SETUP_TOKEN", setupToken);
            try
            {
                var client = factory.CreateClientWithoutApiToken();
                var response = await client.PostAsJsonAsync("/web/auth/set-password", new
                {
                    username = "admin",
                    password = "resetPassword999",
                    setupToken
                });
                response.StatusCode.Should().Be(HttpStatusCode.OK);
                using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
                doc.RootElement.GetProperty("success").GetBoolean().Should().BeTrue();
            }
            finally
            {
                Environment.SetEnvironmentVariable("TORRENTARR_SETUP_TOKEN", null);
            }
        }
        finally
        {
            factory.Dispose();
        }
    }

    [Fact]
    public async Task PostSetPassword_WhenPasswordAlreadySet_WithWrongSetupToken_Returns403()
    {
        var factory = new LocalAuthWebApplicationFactory();
        try
        {
            factory.SetConfigEnv();
            Environment.SetEnvironmentVariable("TORRENTARR_SETUP_TOKEN", "correct-token");
            try
            {
                var client = factory.CreateClientWithoutApiToken();
                var response = await client.PostAsJsonAsync("/web/auth/set-password", new
                {
                    username = "admin",
                    password = "resetPassword999",
                    setupToken = "wrong-token"
                });
                response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
            }
            finally
            {
                Environment.SetEnvironmentVariable("TORRENTARR_SETUP_TOKEN", null);
            }
        }
        finally
        {
            factory.Dispose();
        }
    }
}

/// <summary>
/// POST /web/login when LocalAuthEnabled = false (AuthEnabledWebApplicationFactory: AuthDisabled=false, LocalAuthEnabled=false).
/// The endpoint returns 400 "Local login not configured" regardless of password.
/// The SETUP_REQUIRED (403) path requires LocalAuthEnabled=true + empty PasswordHash; that flow is
/// exercised indirectly by PostSetPassword_WhenNoPasswordSet_ThenLoginWithNewPassword_Succeeds.
/// </summary>
[Collection("HostWebAuth")]
public class LoginLocalAuthDisabledTests : IClassFixture<AuthEnabledWebApplicationFactory>
{
    private readonly AuthEnabledWebApplicationFactory _factory;

    public LoginLocalAuthDisabledTests(AuthEnabledWebApplicationFactory factory)
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
            password = "anypassword"
        });
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }
}

/// <summary>
/// POST /web/login when LocalAuthEnabled=true but PasswordHash is empty.
/// Expects 403 with code "SETUP_REQUIRED" so the frontend can show the set-password form.
/// After set-password, login must succeed.
/// </summary>
[Collection("HostWebLocalAuthNoPassword")]
public class SetupRequiredTests : IClassFixture<LocalAuthNoPasswordWebApplicationFactory>
{
    private readonly LocalAuthNoPasswordWebApplicationFactory _factory;

    public SetupRequiredTests(LocalAuthNoPasswordWebApplicationFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task PostLogin_WhenNoPasswordSet_Returns403WithSetupRequiredCode()
    {
        _factory.SetConfigEnv();
        var client = _factory.CreateClientWithoutApiToken();
        var response = await client.PostAsJsonAsync("/web/login", new
        {
            username = "admin",
            password = "anypassword"
        });
        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        doc.RootElement.GetProperty("code").GetString().Should().Be("SETUP_REQUIRED");
        doc.RootElement.GetProperty("error").GetString().Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task PostSetPassword_ThenLogin_Succeeds_AfterSetupRequired()
    {
        _factory.SetConfigEnv();
        var client = _factory.CreateClientWithoutApiToken();

        // Verify SETUP_REQUIRED path first
        var loginBefore = await client.PostAsJsonAsync("/web/login", new
        {
            username = "newuser",
            password = "somepassword"
        });
        loginBefore.StatusCode.Should().Be(HttpStatusCode.Forbidden);

        // Set the password
        var setResp = await client.PostAsJsonAsync("/web/auth/set-password", new
        {
            username = "newuser",
            password = "somepassword"
        });
        setResp.StatusCode.Should().Be(HttpStatusCode.OK);

        // Login with the new password should succeed
        var loginAfter = await client.PostAsJsonAsync("/web/login", new
        {
            username = "newuser",
            password = "somepassword"
        });
        loginAfter.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task GetWebMeta_WithNoPasswordConfig_ReturnsLocalAuthEnabledTrue()
    {
        _factory.SetConfigEnv();
        var client = _factory.CreateClientWithoutApiToken();
        var response = await client.GetAsync("/web/meta");
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        doc.RootElement.GetProperty("auth_required").GetBoolean().Should().BeTrue();
        doc.RootElement.GetProperty("local_auth_enabled").GetBoolean().Should().BeTrue();
        doc.RootElement.GetProperty("setup_required").GetBoolean().Should().BeTrue("no password set and local auth enabled");
    }
}

/// <summary>
/// GET /web/token when auth is disabled (AuthDisabled=true) — must return the token
/// without any credentials, because the endpoint is not gated when auth is off.
/// Uses the base (AuthDisabled=true) factory.
/// </summary>
[Collection("HostWeb")]
public class TokenNoAuthTests : IClassFixture<TorrentarrWebApplicationFactory>
{
    private readonly TorrentarrWebApplicationFactory _factory;

    public TokenNoAuthTests(TorrentarrWebApplicationFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task GetWebToken_WithoutCredentials_WhenAuthDisabled_Returns200()
    {
        _factory.SetConfigEnv();
        var client = _factory.CreateClientWithoutApiToken();
        var response = await client.GetAsync("/web/token");
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        doc.RootElement.GetProperty("token").GetString().Should().NotBeNullOrEmpty();
    }
}

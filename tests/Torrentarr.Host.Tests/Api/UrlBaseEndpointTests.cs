using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Torrentarr.Core.Configuration;
using Torrentarr.Core.Services;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Xunit;

namespace Torrentarr.Host.Tests.Api;

[Collection("HostWebUrlBase")]
public class UrlBaseEndpointTests : IClassFixture<UrlBaseWebApplicationFactory>
{
    private readonly UrlBaseWebApplicationFactory _factory;

    public UrlBaseEndpointTests(UrlBaseWebApplicationFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task LiveUrlBaseChange_RefreshesCookiePathsAndClearedHtmlBase()
    {
        _factory.SetConfigEnv();
        using var client = _factory.CreateClientWithPathBase("");
        var reloader = _factory.Services.GetRequiredService<IConfigReloader>();
        var options = _factory.Services.GetRequiredService<IOptionsMonitor<CookieAuthenticationOptions>>();
        var original = new ConfigurationLoader(_factory.TempConfigPath).Load();
        var candidate = new ConfigurationLoader(_factory.TempConfigPath).Load();
        options.Get(CookieAuthenticationDefaults.AuthenticationScheme).Cookie.Path.Should().Be("/torrentarr/");
        try
        {
            candidate.WebUI.UrlBase = "/new";
            candidate.WebUI.AuthDisabled = false;
            candidate.WebUI.LocalAuthEnabled = true;
            candidate.WebUI.Username = "fixture";
            candidate.WebUI.PasswordHash = BCrypt.Net.BCrypt.HashPassword("fixture-password", 10);
            reloader.Apply(candidate).Should().BeTrue();
            var cookie = options.Get(CookieAuthenticationDefaults.AuthenticationScheme);
            cookie.Cookie.Path.Should().Be("/new/");
            cookie.LoginPath.Value.Should().Be("/new/login");
            cookie.AccessDeniedPath.Value.Should().Be("/new/login");
            using var browser = _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
            var login = await browser.PostAsJsonAsync("/new/web/login", new { username = "fixture", password = "fixture-password" });
            login.EnsureSuccessStatusCode();
            login.Headers.GetValues("Set-Cookie").Should().Contain(value => value.Contains("path=/new/", StringComparison.OrdinalIgnoreCase));
            (await browser.GetAsync("/new/web/token")).StatusCode.Should().Be(System.Net.HttpStatusCode.OK);
            candidate = new ConfigurationLoader(_factory.TempConfigPath).Load();
            candidate.WebUI.UrlBase = "";
            reloader.Apply(candidate).Should().BeTrue();
            var html = await client.GetStringAsync("/ui");
            html.Should().Contain("<base href=\"/\">");
            html.Should().NotContain("<base href=\"/torrentarr/\">");
        }
        finally { reloader.Apply(original).Should().BeTrue(); }
    }

    [Fact]
    public void FactoryConfig_IncludesUrlBase()
    {
        File.ReadAllText(_factory.TempConfigPath).Should().Contain("UrlBase = \"torrentarr\"");
    }

    [Fact]
    public async Task ConfigSave_UnderUrlBase_UsesMutationGuard()
    {
        _factory.SetConfigEnv();
        var client = _factory.CreateClientWithPathBase("/torrentarr");
        var original = File.ReadAllText(_factory.TempConfigPath);
        try
        {
            var response = await client.PostAsJsonAsync("/web/config", new { changes = new { } });
            response.StatusCode.Should().Be(HttpStatusCode.OK);
        }
        finally { File.WriteAllText(_factory.TempConfigPath, original); }
    }

    [Fact]
    public async Task GetMeta_UnderUrlBase_ReturnsNormalizedUrlBase()
    {
        _factory.SetConfigEnv();
        var client = _factory.CreateClientWithPathBase("/torrentarr");
        var response = await client.GetAsync("/web/meta");
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
        json.GetProperty("url_base").GetString().Should().Be("/torrentarr");
    }

    [Fact]
    public async Task GetStatus_UnderUrlBase_Returns200()
    {
        _factory.SetConfigEnv();
        var client = _factory.CreateClientWithPathBase("/torrentarr");
        var response = await client.GetAsync("/web/status");
        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Theory]
    [InlineData("/ui")]
    [InlineData("/ui/")]
    [InlineData("/static/index.html")]
    public async Task SpaAliases_UseConfiguredAssetBase(string alias)
    {
        _factory.SetConfigEnv();
        var client = _factory.CreateClientWithPathBase("/torrentarr");
        var response = await client.GetAsync("/torrentarr" + alias);
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var html = await response.Content.ReadAsStringAsync();
        html.Should().Contain("<base href=\"/torrentarr/\">");
        html.Should().Contain("./assets/");
        response.Headers.CacheControl!.NoStore.Should().BeTrue();
    }

    [Fact]
    public async Task GetMeta_WithoutPathBaseClient_ReportsConfiguredUrlBase()
    {
        _factory.SetConfigEnv();
        var client = _factory.CreateClientWithPathBase("");
        var response = await client.GetAsync("/web/meta");
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
        json.GetProperty("url_base").GetString().Should().Be("/torrentarr");
    }

    [Fact]
    public async Task GetMeta_IsPublicUnderUrlBase()
    {
        _factory.SetConfigEnv();
        var client = _factory.CreateClientWithPathBase("/torrentarr", withApiToken: false);
        var response = await client.GetAsync("/web/meta");
        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }
}

[Collection("HostWebUrlBase")]
public class UrlBaseAuthRedirectEndpointTests : IClassFixture<UrlBaseAuthWebApplicationFactory>
{
    private readonly UrlBaseAuthWebApplicationFactory _factory;

    public UrlBaseAuthRedirectEndpointTests(UrlBaseAuthWebApplicationFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task GetProtected_WithoutAuth_RedirectsToLoginUnderUrlBase()
    {
        _factory.SetConfigEnv();
        var client = _factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            BaseAddress = new Uri("http://localhost/torrentarr/"),
            AllowAutoRedirect = false,
        });
        client.DefaultRequestHeaders.Accept.ParseAdd("text/html");

        var response = await client.GetAsync("/web/token");

        response.StatusCode.Should().Be(HttpStatusCode.Redirect);
        response.Headers.Location!.ToString().Should().Be("/torrentarr/login");
    }
}

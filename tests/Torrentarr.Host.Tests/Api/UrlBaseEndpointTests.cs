using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
using System.Net;
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
    public void FactoryConfig_IncludesUrlBase()
    {
        File.ReadAllText(_factory.TempConfigPath).Should().Contain("UrlBase = \"torrentarr\"");
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

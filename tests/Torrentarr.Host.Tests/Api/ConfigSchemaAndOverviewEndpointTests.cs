using FluentAssertions;
using System.Net;
using System.Text.Json;
using Xunit;

namespace Torrentarr.Host.Tests.Api;

[Collection("HostWeb")]
public class ConfigSchemaAndOverviewEndpointTests : IClassFixture<TorrentarrWebApplicationFactory>
{
    private readonly TorrentarrWebApplicationFactory _factory;

    public ConfigSchemaAndOverviewEndpointTests(TorrentarrWebApplicationFactory factory)
    {
        _factory = factory;
    }

    [Theory]
    [InlineData("/web/config/schema")]
    [InlineData("/api/config/schema")]
    public async Task GetConfigSchema_ReturnsVersionAndSections(string path)
    {
        var client = _factory.CreateClientWithApiToken();
        var response = await client.GetAsync(path);
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        json.RootElement.GetProperty("version").GetInt32().Should().Be(1);
        json.RootElement.GetProperty("sections").TryGetProperty("WebUI", out _).Should().BeTrue();
        json.RootElement.GetProperty("sections").TryGetProperty("Arr", out _).Should().BeTrue();
    }

    [Fact]
    public async Task GetQbitOverview_Returns200()
    {
        var client = _factory.CreateClientWithApiToken();
        var response = await client.GetAsync("/web/qbit/overview");
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        json.RootElement.TryGetProperty("categories", out _).Should().BeTrue();
        json.RootElement.TryGetProperty("ready", out var ready).Should().BeTrue();
        ready.GetBoolean().Should().BeTrue();
    }
}

[Collection("HostWeb")]
public class LogSearchEndpointTests : IClassFixture<TorrentarrWebApplicationFactory>
{
    private readonly TorrentarrWebApplicationFactory _factory;

    public LogSearchEndpointTests(TorrentarrWebApplicationFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task GetLogSearch_FindsLines()
    {
        var configDir = Path.GetDirectoryName(_factory.TempConfigPath)!;
        var logsDir = Path.Combine(configDir, "logs");
        Directory.CreateDirectory(logsDir);
        var name = "parity-search.log";
        await File.WriteAllTextAsync(Path.Combine(logsDir, name), "hello\nunique-parity-token-xyz\nbye\n");

        var client = _factory.CreateClientWithApiToken();
        var response = await client.GetAsync($"/web/logs/{name}/search?q=unique-parity-token-xyz");
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadAsStringAsync();
        body.Should().Contain("unique-parity-token-xyz");
    }

    [Fact]
    public async Task GetLogSearch_MissingQuery_Returns400()
    {
        var client = _factory.CreateClientWithApiToken();
        var response = await client.GetAsync("/web/logs/All.log/search");
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task GetApiLogSearch_IsRegistered()
    {
        var client = _factory.CreateClientWithApiToken();
        var response = await client.GetAsync("/api/logs/All.log/search?q=nope");
        ((int)response.StatusCode).Should().BeOneOf(200, 404);
    }
}

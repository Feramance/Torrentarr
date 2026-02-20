using FluentAssertions;
using System.Net;
using System.Text.Json;
using Xunit;

namespace Torrentarr.Host.Tests.Api;

public class LogsEndpointTests : IClassFixture<TorrentarrWebApplicationFactory>
{
    private readonly TorrentarrWebApplicationFactory _factory;

    public LogsEndpointTests(TorrentarrWebApplicationFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task GetLogs_Returns200()
    {
        var client = _factory.CreateClient();

        var response = await client.GetAsync("/web/logs");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task GetLogs_ReturnsFilesArray()
    {
        var client = _factory.CreateClient();

        var response = await client.GetAsync("/web/logs");
        var body = await response.Content.ReadAsStringAsync();
        var json = JsonDocument.Parse(body).RootElement;

        // Should have a "files" array (may be empty if no logs dir yet)
        json.TryGetProperty("files", out var filesEl).Should().BeTrue();
        filesEl.ValueKind.Should().Be(JsonValueKind.Array);
    }

    [Fact]
    public async Task GetLogFile_Returns404_WhenFileDoesNotExist()
    {
        var client = _factory.CreateClient();

        var response = await client.GetAsync("/web/logs/nonexistent-totally-fake-file.log");

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }
}

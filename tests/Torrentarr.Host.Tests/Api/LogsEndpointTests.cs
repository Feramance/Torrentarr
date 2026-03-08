using FluentAssertions;
using System.Net;
using System.Text.Json;
using Xunit;

namespace Torrentarr.Host.Tests.Api;

[Collection("HostWeb")]
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
        var client = _factory.CreateClientWithApiToken();

        var response = await client.GetAsync("/web/logs");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task GetLogs_ReturnsFilesArray()
    {
        var client = _factory.CreateClientWithApiToken();

        var response = await client.GetAsync("/web/logs");
        var body = await response.Content.ReadAsStringAsync();
        var json = JsonDocument.Parse(body).RootElement;

        // Should have a "files" array (may be empty if no logs dir yet)
        json.TryGetProperty("files", out var filesEl).Should().BeTrue();
        filesEl.ValueKind.Should().Be(JsonValueKind.Array);
    }

    [Fact]
    public async Task GetLogs_FilesArrayContainsObjectsWithNameField()
    {
        var client = _factory.CreateClientWithApiToken();

        var response = await client.GetAsync("/web/logs");
        var body = await response.Content.ReadAsStringAsync();
        var json = JsonDocument.Parse(body).RootElement;

        json.TryGetProperty("files", out var filesEl).Should().BeTrue();
        // If any files exist, each element should have name/size/modified fields
        foreach (var file in filesEl.EnumerateArray())
        {
            file.TryGetProperty("name", out _).Should().BeTrue();
            file.TryGetProperty("size", out _).Should().BeTrue();
            file.TryGetProperty("modified", out _).Should().BeTrue();
        }
    }

    [Fact]
    public async Task GetLogFile_Returns404_WhenFileDoesNotExist()
    {
        var client = _factory.CreateClientWithApiToken();

        var response = await client.GetAsync("/web/logs/nonexistent-totally-fake-file.log");

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    // These inputs reach our handler (ASP.NET Core routing does not collapse them).
    // Inputs with literal path separators (%2F decoded to /) are rejected by the routing
    // layer before reaching the handler, so we only test the ones that get through.
    [Theory]
    [InlineData("../secret.log")]      // dot-dot traversal
    [InlineData("../../etc/passwd.log")] // multi-level dot-dot
    [InlineData("file.txt")]           // wrong extension
    public async Task GetLogFile_Returns400_WhenNameIsInvalid(string name)
    {
        var client = _factory.CreateClientWithApiToken();

        var response = await client.GetAsync($"/web/logs/{Uri.EscapeDataString(name)}");

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }
}

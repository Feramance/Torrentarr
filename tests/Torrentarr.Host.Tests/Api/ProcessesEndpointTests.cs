using FluentAssertions;
using System.Net;
using System.Text.Json;
using Xunit;

namespace Torrentarr.Host.Tests.Api;

public class ProcessesEndpointTests : IClassFixture<TorrentarrWebApplicationFactory>
{
    private readonly TorrentarrWebApplicationFactory _factory;

    public ProcessesEndpointTests(TorrentarrWebApplicationFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task GetProcesses_Returns200()
    {
        var client = _factory.CreateClient();

        var response = await client.GetAsync("/web/processes");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task GetProcesses_ReturnsJsonArray()
    {
        var client = _factory.CreateClient();

        var response = await client.GetAsync("/web/processes");
        var body = await response.Content.ReadAsStringAsync();

        // /web/processes returns { processes: [...] }
        var json = JsonDocument.Parse(body).RootElement;
        json.GetProperty("processes").ValueKind.Should().Be(JsonValueKind.Array);
    }

    [Fact]
    public async Task PostRestartAll_Returns200()
    {
        var client = _factory.CreateClient();

        var response = await client.PostAsync("/web/processes/restart_all", null);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }
}

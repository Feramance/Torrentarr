using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using System.Net;
using System.Text.Json;
using Torrentarr.Infrastructure.Services;
using Xunit;

namespace Torrentarr.Host.Tests.Api;

[Collection("HostWeb")]
public class ProcessesEndpointTests : IClassFixture<TorrentarrWebApplicationFactory>
{
    private readonly TorrentarrWebApplicationFactory _factory;

    public ProcessesEndpointTests(TorrentarrWebApplicationFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task GetProcesses_ReturnsOtherSectionEntries_WhenStatePrePopulated()
    {
        using var scope = _factory.Services.CreateScope();
        var stateMgr = scope.ServiceProvider.GetRequiredService<ProcessStateManager>();
        stateMgr.Initialize("Recheck", new ArrProcessState
        {
            Name = "Recheck",
            Category = "Recheck",
            Kind = "category",
            Alive = true,
            CategoryCount = 0
        });
        stateMgr.Initialize("Failed", new ArrProcessState
        {
            Name = "Failed",
            Category = "Failed",
            Kind = "category",
            Alive = true,
            CategoryCount = 0
        });
        stateMgr.Initialize("FreeSpaceManager", new ArrProcessState
        {
            Name = "FreeSpaceManager",
            Category = "FreeSpaceManager",
            Kind = "torrent",
            MetricType = "free-space",
            Alive = true,
            CategoryCount = 0
        });
        stateMgr.Initialize("TrackerSortManager", new ArrProcessState
        {
            Name = "TrackerSortManager",
            Category = "TrackerSortManager",
            Kind = "torrent",
            MetricType = "tracker-sort",
            Alive = true,
            CategoryCount = null
        });

        var client = _factory.CreateClientWithApiToken();
        var response = await client.GetAsync("/web/processes");
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var body = await response.Content.ReadAsStringAsync();
        var json = JsonDocument.Parse(body).RootElement;
        var processes = json.GetProperty("processes");
        processes.ValueKind.Should().Be(JsonValueKind.Array);

        var names = new List<string>();
        foreach (var p in processes.EnumerateArray())
            names.Add(p.GetProperty("name").GetString() ?? "");
        names.Should().Contain("Recheck");
        names.Should().Contain("Failed");
        names.Should().Contain("FreeSpaceManager");
        names.Should().Contain("TrackerSortManager");
    }

    [Fact]
    public async Task GetProcesses_Returns200()
    {
        var client = _factory.CreateClientWithApiToken();

        var response = await client.GetAsync("/web/processes");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task GetProcesses_ReturnsJsonArray()
    {
        var client = _factory.CreateClientWithApiToken();

        var response = await client.GetAsync("/web/processes");
        var body = await response.Content.ReadAsStringAsync();

        // /web/processes returns { processes: [...] }
        var json = JsonDocument.Parse(body).RootElement;
        json.GetProperty("processes").ValueKind.Should().Be(JsonValueKind.Array);
    }

    [Fact]
    public async Task GetProcesses_ProcessObjectContainsStatusField()
    {
        var client = _factory.CreateClientWithApiToken();

        var response = await client.GetAsync("/web/processes");
        var body = await response.Content.ReadAsStringAsync();

        var json = JsonDocument.Parse(body).RootElement;
        var processes = json.GetProperty("processes");

        processes.ValueKind.Should().Be(JsonValueKind.Array);
        if (processes.GetArrayLength() > 0)
        {
            var firstProcess = processes[0];
            firstProcess.GetProperty("status").ValueKind.Should().NotBe(JsonValueKind.Undefined);
        }
    }

    [Fact]
    public async Task GetProcesses_ProcessObjectContainsQueueCountField()
    {
        var client = _factory.CreateClientWithApiToken();

        var response = await client.GetAsync("/web/processes");
        var body = await response.Content.ReadAsStringAsync();

        var json = JsonDocument.Parse(body).RootElement;
        var processes = json.GetProperty("processes");

        processes.ValueKind.Should().Be(JsonValueKind.Array);
        if (processes.GetArrayLength() > 0)
        {
            var firstProcess = processes[0];
            firstProcess.GetProperty("queueCount").ValueKind.Should().NotBe(JsonValueKind.Undefined);
        }
    }

    [Fact]
    public async Task GetProcesses_ProcessObjectContainsCategoryCountField()
    {
        var client = _factory.CreateClientWithApiToken();

        var response = await client.GetAsync("/web/processes");
        var body = await response.Content.ReadAsStringAsync();

        var json = JsonDocument.Parse(body).RootElement;
        var processes = json.GetProperty("processes");

        processes.ValueKind.Should().Be(JsonValueKind.Array);
        if (processes.GetArrayLength() > 0)
        {
            var firstProcess = processes[0];
            firstProcess.GetProperty("categoryCount").ValueKind.Should().NotBe(JsonValueKind.Undefined);
        }
    }

    [Fact]
    public async Task PostRestartAll_Returns200()
    {
        var client = _factory.CreateClientWithApiToken();

        var response = await client.PostAsync("/web/processes/restart_all", null);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }
}

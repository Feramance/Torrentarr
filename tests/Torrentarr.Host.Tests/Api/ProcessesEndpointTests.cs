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

    [Theory]
    [InlineData("/web/processes")]
    [InlineData("/api/processes")]
    public async Task ActivityMetrics_AreNumeric_AndZeroIsDifferentFromMissing(string endpoint)
    {
        var states = _factory.Services.GetRequiredService<ProcessStateManager>();
        states.Initialize("metric-test", new ArrProcessState { Name = "metric-test", Kind = "search", SearchesTriggered = 0, ItemsSearched = 12 });
        using var client = _factory.CreateClientWithApiToken();
        using var response = await client.GetAsync(endpoint);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var item = json.RootElement.GetProperty("processes").EnumerateArray().Single(p => p.GetProperty("name").GetString() == "metric-test");
        item.GetProperty("searchesTriggered").GetInt32().Should().Be(0);
        item.GetProperty("itemsSearched").GetInt32().Should().Be(12);
        item.GetProperty("downloadingCount").ValueKind.Should().Be(JsonValueKind.Null);
    }

    [Fact]
    public async Task StatisticsStream_SendsInitialRevision_AndSubsequentInvalidation()
    {
        using var client = _factory.CreateClientWithApiToken();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var response = await client.GetAsync("/web/stats/events", HttpCompletionOption.ResponseHeadersRead, timeout.Token);
        response.EnsureSuccessStatusCode();
        response.Content.Headers.ContentType!.MediaType.Should().Be("text/event-stream");
        using var reader = new StreamReader(await response.Content.ReadAsStreamAsync(timeout.Token));
        (await reader.ReadLineAsync(timeout.Token)).Should().StartWith("data: ");
        await reader.ReadLineAsync(timeout.Token);
        using (Torrentarr.Core.Configuration.ConfigMutationGuard.EnterMutation(_factory.TempConfigPath))
            _factory.Services.GetRequiredService<StatisticsEvents>().Publish();
        (await reader.ReadLineAsync(timeout.Token)).Should().StartWith("data: ");
    }

    [Theory]
    [InlineData("/web/processes")]
    [InlineData("/api/processes")]
    public async Task GetProcesses_ReturnsOtherSectionEntries_WhenStatePrePopulated(string endpoint)
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
            CategoryCount = 7
        });

        var client = _factory.CreateClientWithApiToken();
        var response = await client.GetAsync(endpoint);
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
        var freeSpace = processes.EnumerateArray().Single(p => p.GetProperty("name").GetString() == "FreeSpaceManager");
        freeSpace.GetProperty("freeSpacePaused").GetInt32().Should().Be(7);
        freeSpace.GetProperty("restartable").GetBoolean().Should().BeFalse();
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

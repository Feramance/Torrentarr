using FluentAssertions;
using Moq;
using Newtonsoft.Json.Linq;
using Torrentarr.Core.Configuration;
using Torrentarr.Core.Interfaces;
using Torrentarr.Core.Models;
using Torrentarr.Infrastructure.Services;
using Xunit;

namespace Torrentarr.Infrastructure.Tests.Services;

public class QbitOverviewBuilderTests
{
    [Theory]
    [InlineData(true, false, 2)]
    [InlineData(false, true, 1)]
    [InlineData(null, true, 2)]
    public async Task Overview_UsesEffectiveOwnerSubcategorySetting(bool? arrOverride, bool clientDefault, int expected)
    {
        var config = new TorrentarrConfig();
        config.TorrentClients["one"] = new() { Host = "fixture", MatchSubcategories = clientDefault };
        config.ArrInstances["Radarr"] = new() { Category = "movies", MatchSubcategories = arrOverride };
        var client = new Mock<ITorrentClient>();
        client.Setup(c => c.GetTorrentsAsync(It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([new() { Category = "movies", Size = 10 }, new() { Category = "movies/4k", Size = 20 }]);
        var registry = new Mock<ITorrentClientRegistry>();
        registry.Setup(r => r.GetClient("one")).Returns(client.Object);
        var result = JObject.FromObject(await QbitOverviewBuilder.BuildAsync(config, registry.Object, null));
        var category = result["categories"]!.Single();
        category["torrentCount"]!.Value<int>().Should().Be(expected);
        category["totalSize"]!.Value<long>().Should().Be(expected == 2 ? 30 : 10);
    }

    [Fact]
    public async Task Overview_SummarizesBeforeCapAndDeduplicatesOverlappingCategories()
    {
        var config = new TorrentarrConfig();
        config.TorrentClients["one"] = new() { Host = "fixture", MatchSubcategories = true, ManagedCategories = ["movies", "movies/4k"] };
        var torrents = Enumerable.Range(0, 650).Select(i => new TorrentInfo
        { Hash = $"hash{i}", Category = "movies/4k", Size = 10, State = i < 600 ? "stalledDL" : "pausedUP" }).ToList();
        var client = new Mock<ITorrentClient>();
        client.Setup(c => c.GetTorrentsAsync(It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>())).ReturnsAsync(torrents);
        var registry = new Mock<ITorrentClientRegistry>();
        registry.Setup(r => r.GetClient("one")).Returns(client.Object);
        var result = JObject.FromObject(await QbitOverviewBuilder.BuildAsync(config, registry.Object, null));
        result["categories"]!.Single(c => c["category"]!.Value<string>() == "movies/4k")["torrents"]!.Count().Should().Be(500);
        result["categories"]!.Single(c => c["category"]!.Value<string>() == "movies/4k")["torrentsTruncated"]!.Value<bool>().Should().BeTrue();
        var summary = result["summaries"]!.Single();
        summary["torrentCount"]!.Value<int>().Should().Be(650);
        summary["totalSize"]!.Value<long>().Should().Be(6500);
        summary["downloading"]!.Value<int>().Should().Be(600);
        summary["stopped"]!.Value<int>().Should().Be(50);
    }
    [Fact]
    public async Task Overview_DisconnectedClientRetainsLastInventoryWithError()
    {
        var config = new TorrentarrConfig();
        config.TorrentClients["one"] = new() { Host = "fixture", ManagedCategories = ["movies"] };
        var client = new Mock<ITorrentClient>();
        client.Setup(c => c.GetTorrentsAsync(It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([new() { Hash = "hash", Category = "movies", Size = 10, State = "seeding" }]);
        var registry = new Mock<ITorrentClientRegistry>();
        registry.Setup(r => r.GetClient("one")).Returns(client.Object);
        var snapshots = new TorrentStatisticsSnapshots();
        await QbitOverviewBuilder.BuildAsync(config, registry.Object, null, snapshots: snapshots);
        registry.Setup(r => r.GetClient("one")).Returns((ITorrentClient?)null);
        var result = JObject.FromObject(await QbitOverviewBuilder.BuildAsync(config, registry.Object, null, snapshots: snapshots));
        result["summaries"]!.Single()["torrentCount"]!.Value<int>().Should().Be(1);
        result["observations"]!["one"]!["Error"]!.Value<string>().Should().Be("Client is disconnected.");
    }

}

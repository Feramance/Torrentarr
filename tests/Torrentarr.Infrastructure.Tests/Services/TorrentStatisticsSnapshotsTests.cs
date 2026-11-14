using FluentAssertions;
using Moq;
using Torrentarr.Core.Configuration;
using Torrentarr.Core.Interfaces;
using Torrentarr.Core.Models;
using Torrentarr.Infrastructure.Services;
using Xunit;

namespace Torrentarr.Infrastructure.Tests.Services;

public class TorrentStatisticsSnapshotsTests
{
    [Fact]
    public async Task CompleteSnapshots_AreSharedAcrossConcurrentRequests_AndIntervalsApplyLive()
    {
        var config = new TorrentarrConfig();
        config.TorrentClients["one"] = new() { TorrentScanIntervalSeconds = 30 };
        config.TorrentClients["two"] = new() { TorrentScanIntervalSeconds = 120 };
        var first = new Mock<ITorrentClient>();
        first.Setup(c => c.GetTorrentsAsync(It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([new TorrentInfo { Hash = "same" }]);
        var second = new Mock<ITorrentClient>();
        second.Setup(c => c.GetTorrentsAsync(It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>())).ReturnsAsync([]);
        var snapshots = new TorrentStatisticsSnapshots();
        await Task.WhenAll(Enumerable.Range(0, 10).Select(_ => snapshots.GetAsync("one", config, first.Object, default)));
        first.Verify(c => c.GetTorrentsAsync(null, null, It.IsAny<CancellationToken>()), Times.Once);
        var empty = await snapshots.GetAsync("two", config, second.Object, default);
        empty.ObservedAt.Should().NotBeNull();
        empty.Torrents.Should().BeEmpty();
        empty.ScanIntervalSeconds.Should().Be(120);
        config.TorrentClients["one"].TorrentScanIntervalSeconds = 5;
        var updated = await snapshots.GetAsync("one", config, first.Object, default);
        updated.ScanIntervalSeconds.Should().Be(5);
        updated.Torrents.Single().QBitInstanceName.Should().Be("one");
        first.Verify(c => c.GetTorrentsAsync(null, null, It.IsAny<CancellationToken>()), Times.Exactly(2));
    }

    [Fact]
    public async Task FailedRefresh_RetainsLastCompleteObservation_AndDoesNotTurnFailureIntoZero()
    {
        var config = new TorrentarrConfig();
        config.TorrentClients["one"] = new() { TorrentScanIntervalSeconds = 30 };
        var client = new Mock<ITorrentClient>();
        client.SetupSequence(c => c.GetTorrentsAsync(It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([new TorrentInfo { Hash = "abc" }]).ThrowsAsync(new IOException("offline"));
        var snapshots = new TorrentStatisticsSnapshots();
        var good = await snapshots.GetAsync("one", config, client.Object, default);
        config.TorrentClients["one"].TorrentScanIntervalSeconds = 5;
        var stale = await snapshots.GetAsync("one", config, client.Object, default);
        stale.ObservedAt.Should().Be(good.ObservedAt);
        stale.Torrents.Single().Hash.Should().Be("abc");
        stale.Error.Should().NotBeNull();
        var absent = new Mock<ITorrentClient>();
        absent.Setup(c => c.GetTorrentsAsync(It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>())).ThrowsAsync(new IOException());
        var unavailable = await snapshots.GetAsync("one", config, absent.Object, default);
        unavailable.ObservedAt.Should().BeNull();
        unavailable.Error.Should().NotBeNull();
    }
    [Fact]
    public async Task ParentAndChildCategories_DoNotDoubleCount_OrUseTruncatedListTotals()
    {
        var config = new TorrentarrConfig();
        config.TorrentClients["one"] = new() { Host = "fixture", ManagedCategories = ["movies", "movies/4k"], MatchSubcategories = true };
        var client = new Mock<ITorrentClient>();
        client.Setup(c => c.GetTorrentsAsync(It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Enumerable.Range(0, 600).Select(i => new TorrentInfo { Hash = i.ToString(), Category = "movies/4k" }).ToList());
        var registry = new Mock<ITorrentClientRegistry>();
        registry.Setup(r => r.GetClient("one")).Returns(client.Object);
        var data = await QbitOverviewBuilder.BuildAsync(config, registry.Object, "one", snapshots: new TorrentStatisticsSnapshots());
        using var json = System.Text.Json.JsonDocument.Parse(System.Text.Json.JsonSerializer.Serialize(data));
        var categories = json.RootElement.GetProperty("categories").EnumerateArray().ToList();
        categories.Single(c => c.GetProperty("category").GetString() == "movies").GetProperty("torrentCount").GetInt32().Should().Be(0);
        var child = categories.Single(c => c.GetProperty("category").GetString() == "movies/4k");
        child.GetProperty("torrentCount").GetInt32().Should().Be(600);
        child.GetProperty("torrents").GetArrayLength().Should().Be(QbitOverviewBuilder.MaxTorrentsPerCategory);
    }

}

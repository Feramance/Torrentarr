using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Torrentarr.Core.Configuration;
using Torrentarr.Core.Interfaces;
using Torrentarr.Core.Models;
using Torrentarr.Infrastructure.Services;
using Xunit;

namespace Torrentarr.Infrastructure.Tests.Services;

public sealed class TorrentClientIsolationTests
{
    private static Mock<ITorrentClient> Client(bool login = true)
    {
        var client = new Mock<ITorrentClient>();
        client.Setup(c => c.LoginAsync(It.IsAny<CancellationToken>())).ReturnsAsync(login);
        client.Setup(c => c.GetVersionAsync(It.IsAny<CancellationToken>())).ReturnsAsync("fake-1");
        return client;
    }

    [Fact]
    public async Task IndependentAdapter_IsAccepted_AndConcurrentConnectIsSingle()
    {
        var client = Client();
        var factory = new Mock<ITorrentClientFactory>();
        factory.SetupGet(f => f.Type).Returns("fake");
        factory.Setup(f => f.Create(It.IsAny<string>(), It.IsAny<TorrentClientInstanceConfig>())).Returns(client.Object);
        var manager = new QBittorrentConnectionManager(NullLogger<QBittorrentConnectionManager>.Instance, [factory.Object]);
        var cfg = new TorrentClientInstanceConfig { Type = "fake", Host = "fixture" };
        await Task.WhenAll(Enumerable.Range(0, 10).Select(_ => manager.InitializeAsync("one", cfg)));
        manager.GetClient("ONE").Should().BeSameAs(client.Object);
        factory.Verify(f => f.Create(It.IsAny<string>(), It.IsAny<TorrentClientInstanceConfig>()), Times.Once);
    }

    [Theory]
    [InlineData("host")]
    [InlineData("password")]
    [InlineData("username")]
    [InlineData("tls")]
    [InlineData("port")]
    [InlineData("type")]
    public async Task ConnectionChange_ReplacesClient(string changed)
    {
        var oldClient = Client(); var newClient = Client();
        var factory = new Mock<ITorrentClientFactory>();
        factory.SetupGet(f => f.Type).Returns("fake");
        factory.SetupSequence(f => f.Create(It.IsAny<string>(), It.IsAny<TorrentClientInstanceConfig>()))
            .Returns(oldClient.Object).Returns(newClient.Object);
        var otherFactory = new Mock<ITorrentClientFactory>();
        otherFactory.SetupGet(f => f.Type).Returns("other");
        otherFactory.Setup(f => f.Create(It.IsAny<string>(), It.IsAny<TorrentClientInstanceConfig>())).Returns(newClient.Object);
        var manager = new QBittorrentConnectionManager(NullLogger<QBittorrentConnectionManager>.Instance, [factory.Object, otherFactory.Object]);
        var cfg = new TorrentClientInstanceConfig { Type = "fake", Host = "fixture" };
        await manager.InitializeAsync("one", cfg);
        switch (changed)
        {
            case "host": cfg.Host = "new"; break;
            case "password": cfg.Password = "new"; break;
            case "username": cfg.UserName = "new"; break;
            case "tls": cfg.SkipTLSVerify = true; break;
            case "port": cfg.Port++; break;
            case "type": cfg.Type = "other"; break;
        }
        await manager.ReconcileAsync(new Dictionary<string, TorrentClientInstanceConfig> { ["one"] = cfg });
        manager.GetClient("one").Should().BeSameAs(newClient.Object);
    }

    [Theory]
    [InlineData("removed")]
    [InlineData("disabled")]
    [InlineData("placeholder")]
    [InlineData("failed")]
    public async Task ObsoleteClient_IsRemoved(string changed)
    {
        var factory = new Mock<ITorrentClientFactory>();
        factory.SetupGet(f => f.Type).Returns("fake");
        factory.SetupSequence(f => f.Create(It.IsAny<string>(), It.IsAny<TorrentClientInstanceConfig>()))
            .Returns(Client().Object).Returns(Client(false).Object);
        var manager = new QBittorrentConnectionManager(NullLogger<QBittorrentConnectionManager>.Instance, [factory.Object]);
        var cfg = new TorrentClientInstanceConfig { Type = "fake", Host = "fixture" };
        await manager.InitializeAsync("one", cfg);
        if (changed == "disabled") cfg.Disabled = true;
        if (changed == "placeholder") cfg.Host = "CHANGE_ME";
        if (changed == "failed") cfg.Host = "new";
        await manager.ReconcileAsync(changed == "removed" ? [] : new Dictionary<string, TorrentClientInstanceConfig> { ["one"] = cfg });
        manager.IsConnected("one").Should().BeFalse();
        manager.GetConnectionInfo().Should().BeEmpty();
    }

    [Fact]
    public void SameHashAndRemotePath_HaveIndependentScanState()
    {
        var tracker = new ImportPathTracker();
        tracker.MarkScanned("one", "/downloads/movie", "abc");
        tracker.IsHashAlreadyScanned("ONE", "ABC").Should().BeTrue();
        tracker.IsHashAlreadyScanned("two", "abc").Should().BeFalse();
        tracker.IsPathAlreadyScanned("two", "/downloads/movie").Should().BeFalse();
    }

    [Fact]
    public void IgnoreAndMetadataCaches_AreScopedByClient()
    {
        var cache = new TorrentCacheService(NullLogger<TorrentCacheService>.Instance);
        cache.AddToIgnoreCache("one", "abc", TimeSpan.FromHours(1));
        cache.SetCategory("one", "abc", "movies"); cache.SetName("one", "abc", "name");
        cache.IsInIgnoreCache("ONE", "ABC").Should().BeTrue();
        cache.IsInIgnoreCache("two", "abc").Should().BeFalse();
        cache.GetCategory("two", "abc").Should().BeNull(); cache.GetName("two", "abc").Should().BeNull();
        cache.MarkFileFiltered("one", "abc"); cache.IsFileFiltered("ONE", "ABC").Should().BeTrue();
    }

    [Fact]
    public void CanonicalAndLegacyCategories_ResolveTheSameSettings()
    {
        var cfg = new TorrentarrConfig();
        var legacy = new QBitConfig
        {
            Host = "fixture",
            ManagedCategories = ["movies"],
            MatchSubcategories = true,
            CategorySeeding = new() { StalledDelay = 40, IgnoreTorrentsYoungerThan = 900 }
        };
        cfg.QBitInstances["one"] = legacy;
        var before = CategoryOwnershipHelper.GetQBitOnlyManagedCategories(cfg);
        var torrent = new TorrentInfo { ClientInstanceId = "one", Category = "movies/4k" };
        var settings = TorrentProcessor.ResolveStalledSettings(cfg, torrent, null);
        cfg.QBitInstances.Remove("one"); cfg.TorrentClients["one"] = legacy;
        CategoryOwnershipHelper.GetQBitOnlyManagedCategories(cfg).Should().BeEquivalentTo(before);
        CategoryOwnershipHelper.ResolveOwningCategory(cfg, "movies/4k", "one").Should().Be("movies");
        TorrentProcessor.ResolveStalledSettings(cfg, torrent, null).Should().Be(settings);
    }
    [Fact]
    public void DisabledLegacyClient_DoesNotDisableCanonicalPolicy()
    {
        var cfg = new TorrentarrConfig();
        cfg.QBitInstances["qBit"] = new() { Disabled = true };
        cfg.TorrentClients["other"] = new()
        {
            Host = "fixture",
            ManagedCategories = ["movies"],
            Trackers = [new() { SortTorrents = true, Priority = 4, AddTags = ["priority"] }]
        };
        TorrentPolicyHelper.EnableTrackerSort(cfg).Should().BeTrue();
        TorrentPolicyHelper.GetAllMonitoredPolicyCategories(cfg).Should().Contain("movies");
        TorrentPolicyHelper.MergeGlobalTrackerTagToPriorityMax(cfg)["priority"].Should().Be(4);
    }

    [Fact]
    public async Task ConfigSaveReconciliation_RevokesWithoutNetworkLogin()
    {
        var factory = new Mock<ITorrentClientFactory>();
        factory.SetupGet(f => f.Type).Returns("fake");
        factory.Setup(f => f.Create(It.IsAny<string>(), It.IsAny<TorrentClientInstanceConfig>())).Returns(Client().Object);
        var manager = new QBittorrentConnectionManager(NullLogger<QBittorrentConnectionManager>.Instance, [factory.Object]);
        var cfg = new TorrentClientInstanceConfig { Type = "fake", Host = "fixture" };
        await manager.InitializeAsync("one", cfg);
        cfg.Host = "new";
        await manager.ReconcileAsync(new Dictionary<string, TorrentClientInstanceConfig> { ["one"] = cfg }, connect: false);
        manager.GetClient("one").Should().BeNull();
        factory.Verify(f => f.Create(It.IsAny<string>(), It.IsAny<TorrentClientInstanceConfig>()), Times.Once);
        await manager.EnsureAllConnectedAsync(new Dictionary<string, TorrentClientInstanceConfig> { ["one"] = cfg });
        manager.IsConnected("one").Should().BeTrue();
        factory.Verify(f => f.Create(It.IsAny<string>(), It.IsAny<TorrentClientInstanceConfig>()), Times.Exactly(2));
    }
    [Fact]
    public void Cleanup_OnlyRemovesAssociationsUnderItsRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "scan-cleanup-" + Guid.NewGuid().ToString("N"));
        var tracker = new ImportPathTracker();
        tracker.MarkScanned("one", Path.Combine(root, "one", "missing"), "abc");
        tracker.MarkScanned("two", Path.Combine(root, "two", "missing"), "abc");
        tracker.ClearIfFolderEmpty(Path.Combine(root, "one"));
        tracker.IsHashAlreadyScanned("one", "abc").Should().BeFalse();
        tracker.IsHashAlreadyScanned("two", "abc").Should().BeTrue();
    }
}

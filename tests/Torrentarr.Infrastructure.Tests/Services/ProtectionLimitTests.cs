using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Torrentarr.Core.Configuration;
using Torrentarr.Core.Interfaces;
using Torrentarr.Core.Models;
using Torrentarr.Infrastructure.Database;
using Torrentarr.Infrastructure.Services;
using Xunit;

namespace Torrentarr.Infrastructure.Tests.Services;

public sealed class ProtectionLimitTests
{
    private static (SeedingService Service, Mock<ITorrentClient> Client, TorrentarrDbContext Db) Create(
        TorrentarrConfig? config = null)
    {
        var db = new TorrentarrDbContext(new DbContextOptionsBuilder<TorrentarrDbContext>()
            .UseSqlite("Data Source=:memory:").Options);
        db.Database.OpenConnection();
        db.Database.EnsureCreated();
        var client = new Mock<ITorrentClient>();
        client.SetupGet(c => c.Capabilities).Returns(TorrentClientCapabilities.QBittorrent);
        client.Setup(c => c.SetShareLimitsAsync(It.IsAny<string>(), It.IsAny<double>(), It.IsAny<long>(), It.IsAny<long>(), It.IsAny<CancellationToken>())).ReturnsAsync(true);
        client.Setup(c => c.SetShareLimitsAsync(It.IsAny<string>(), It.IsAny<double>(), It.IsAny<long>(), It.IsAny<CancellationToken>())).ReturnsAsync(true);
        var registry = new Mock<ITorrentClientRegistry>();
        registry.Setup(r => r.GetClient("client")).Returns(client.Object);
        return (new SeedingService(NullLogger<SeedingService>.Instance, db, config ?? new TorrentarrConfig(), registry.Object), client, db);
    }

    [Theory]
    [InlineData(false, false, false, false, false, false)]
    [InlineData(true, false, false, false, false, false)]
    [InlineData(false, true, false, false, false, false)]
    [InlineData(false, false, true, false, false, false)]
    [InlineData(false, false, false, true, false, false)]
    [InlineData(false, false, false, false, true, false)]
    [InlineData(true, false, false, false, true, false)]
    [InlineData(false, false, false, true, true, false)]
    [InlineData(false, false, false, false, true, true)]
    public async Task ProtectedLimits_ClearAndRestoreOnlyOwnedLimits(bool userChanged, bool revisedWhileProtected,
        bool clearRejected, bool restoreRejected, bool removeRule, bool replaced)
    {
        var config = new TorrentarrConfig();
        config.TorrentClients["client"] = new()
        {
            Trackers = [new() { Uri = "tracker.example", Protection = new() {
            MinimumSeedHours = 48, AllowClientRatioClearance = true, AccountingGraceMinutes = 0, AdditionalSeedHours = 0
        } }]
        };
        var (service, client, db) = Create(config);
        var torrent = new TorrentInfo
        {
            ClientInstanceId = "client",
            Hash = "hash",
            AddedOn = 1,
            Progress = 1,
            IsPrivate = !removeRule,
            Ratio = 0,
            RatioLimit = 1,
            SeedingTimeLimit = 60,
            InactiveSeedingTimeLimit = 30
        };
        List<TorrentTracker> trackers = [new() { Url = "https://tracker.example/announce", Status = 2 }];
        var clearCalls = 0;
        client.Setup(c => c.SetShareLimitsAsync("hash", -1, -1, -1, It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => !clearRejected || ++clearCalls > 1);
        await service.ReconcileProtectionLimitsAsync(torrent, trackers, default);
        client.Verify(c => c.SetShareLimitsAsync("hash", -1, -1, -1, It.IsAny<CancellationToken>()), Times.Once);
        if (clearRejected)
        {
            torrent.RatioLimit.Should().Be(1);
            await service.ReconcileProtectionLimitsAsync(torrent, trackers, default);
            clearCalls.Should().Be(2);
        }
        if (revisedWhileProtected)
        {
            torrent.RatioLimit = 2; torrent.SeedingTimeLimit = 120; torrent.InactiveSeedingTimeLimit = 45;
            await service.ReconcileProtectionLimitsAsync(torrent, trackers, default);
        }
        var ratio = revisedWhileProtected ? 2 : 1;
        var seedTime = revisedWhileProtected ? 120 : 60;
        var inactiveTime = revisedWhileProtected ? 45 : 30;
        var restoreCalls = 0;
        client.Setup(c => c.SetShareLimitsAsync("hash", ratio, seedTime, inactiveTime, It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => !restoreRejected || ++restoreCalls > 1);
        torrent.Ratio = 1;
        torrent.RatioLimit = userChanged ? 2 : -1;
        torrent.SeedingTimeLimit = -1; torrent.InactiveSeedingTimeLimit = -1;
        if (removeRule)
        {
            var updated = new TorrentarrConfig();
            updated.TorrentClients["client"] = new();
            config.Apply(updated, 1);
            if (replaced) torrent.AddedOn++;
        }
        await service.ReconcileProtectionLimitsAsync(torrent, trackers, default);
        var restore = !userChanged && !replaced;
        client.Verify(c => c.SetShareLimitsAsync("hash", ratio, seedTime, inactiveTime, It.IsAny<CancellationToken>()), restore ? Times.Once() : Times.Never());
        if (restoreRejected)
        {
            var key = MaintenancePersistence.Key("client", "hash", "native-limits");
            MaintenancePersistence.Read<ProtectionObservation>(db, key).PreviousRatioLimit.Should().Be(ratio);
            await service.ReconcileProtectionLimitsAsync(torrent, trackers, default);
            restoreCalls.Should().Be(2);
            MaintenancePersistence.Read<ProtectionObservation>(db, key).PreviousRatioLimit.Should().BeNull();
        }
        // A completed restore (or a user edit) releases ownership and cannot be replayed.
        await service.ReconcileProtectionLimitsAsync(torrent, trackers, default);
        client.Verify(c => c.SetShareLimitsAsync("hash", ratio, seedTime, inactiveTime, It.IsAny<CancellationToken>()), restore ? Times.Exactly(restoreRejected ? 2 : 1) : Times.Never());
    }

    [Fact]
    public async Task UnmatchedPrivateTorrent_ClearsNativeLimitsWithoutConfiguredProtection()
    {
        var (service, client, db) = Create();
        var torrent = new TorrentInfo
        {
            ClientInstanceId = "client",
            Hash = "hash",
            AddedOn = 1,
            Progress = 1,
            IsPrivate = true,
            RatioLimit = 1,
            SeedingTimeLimit = 60,
            InactiveSeedingTimeLimit = 30
        };
        List<TorrentTracker> trackers = [new() { Url = "https://unknown.example/announce", Status = 2 }];

        await service.ReconcileProtectionLimitsAsync(torrent, trackers, default);

        client.Verify(c => c.SetShareLimitsAsync("hash", -1, -1, -1, It.IsAny<CancellationToken>()), Times.Once);
        var state = MaintenancePersistence.Read<ProtectionObservation>(
            db, MaintenancePersistence.Key("client", "hash", "native-limits"));
        state.PreviousRatioLimit.Should().Be(1);
        state.PreviousTimeLimit.Should().Be(60);
        state.PreviousInactiveTimeLimit.Should().Be(30);
    }

    [Fact]
    public async Task PublicTorrentWithoutProtection_DoesNotTouchNativeLimits()
    {
        var (service, client, _) = Create();
        var torrent = new TorrentInfo
        {
            ClientInstanceId = "client",
            Hash = "hash",
            AddedOn = 1,
            Progress = 1,
            IsPrivate = false,
            RatioLimit = 1,
            SeedingTimeLimit = 60,
            InactiveSeedingTimeLimit = 30
        };

        await service.ReconcileProtectionLimitsAsync(torrent, [], default);

        client.Verify(c => c.SetShareLimitsAsync(It.IsAny<string>(), It.IsAny<double>(), It.IsAny<long>(), It.IsAny<long>(), It.IsAny<CancellationToken>()), Times.Never);
        client.Verify(c => c.SetShareLimitsAsync(It.IsAny<string>(), It.IsAny<double>(), It.IsAny<long>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task ReAddedTorrent_ReplacesSavedStateAndClearsFreshNativeLimits()
    {
        var config = new TorrentarrConfig();
        config.TorrentClients["client"] = new()
        {
            Trackers = [new() { Uri = "tracker.example", Protection = new() { MinimumSeedHours = 48 } }]
        };
        var (service, client, db) = Create(config);
        var key = MaintenancePersistence.Key("client", "hash", "native-limits");
        MaintenancePersistence.Write(db, key, new ProtectionObservation
        {
            AddedOn = 1,
            PreviousRatioLimit = 9,
            PreviousTimeLimit = 900,
            PreviousInactiveTimeLimit = 90
        });
        var torrent = new TorrentInfo
        {
            ClientInstanceId = "client",
            Hash = "hash",
            AddedOn = 2,
            Progress = 1,
            IsPrivate = true,
            Ratio = 0,
            RatioLimit = 1,
            SeedingTimeLimit = 60,
            InactiveSeedingTimeLimit = 30
        };
        List<TorrentTracker> trackers = [new() { Url = "https://tracker.example/announce", Status = 2 }];

        await service.ReconcileProtectionLimitsAsync(torrent, trackers, default);

        client.Verify(c => c.SetShareLimitsAsync("hash", -1, -1, -1, It.IsAny<CancellationToken>()), Times.Once);
        var state = MaintenancePersistence.Read<ProtectionObservation>(db, key);
        state.AddedOn.Should().Be(2);
        state.PreviousRatioLimit.Should().Be(1);
        state.PreviousTimeLimit.Should().Be(60);
        state.PreviousInactiveTimeLimit.Should().Be(30);
    }

    [Fact]
    public async Task ApplySeedingLimits_UsesFreshTrackerEvidenceToClearNativeLimits()
    {
        var config = new TorrentarrConfig();
        config.TorrentClients["client"] = new()
        {
            Trackers = [new() { Uri = "tracker.example", Protection = new() { MinimumSeedHours = 48 } }]
        };
        var (service, client, _) = Create(config);
        var torrent = new TorrentInfo
        {
            ClientInstanceId = "client",
            Hash = "hash",
            AddedOn = 1,
            Progress = 1,
            IsPrivate = true,
            Ratio = 0,
            RatioLimit = 1,
            SeedingTimeLimit = 60,
            InactiveSeedingTimeLimit = 30
        };
        client.Setup(c => c.GetTorrentTrackersAsync("hash", It.IsAny<CancellationToken>()))
            .ReturnsAsync([new() { Url = "https://tracker.example/announce", Status = 2 }]);

        await service.ApplySeedingLimitsAsync(torrent);

        client.Verify(c => c.GetTorrentTrackersAsync("hash", It.IsAny<CancellationToken>()), Times.Once);
        client.Verify(c => c.SetShareLimitsAsync("hash", -1, -1, -1, It.IsAny<CancellationToken>()), Times.Once);
    }
}

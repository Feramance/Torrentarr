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
        TorrentarrConfig? config = null, TimeProvider? clock = null)
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
        return (new SeedingService(NullLogger<SeedingService>.Instance, db, config ?? new TorrentarrConfig(), registry.Object, clock: clock), client, db);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public void ModernGraceRequiresFreshDistinctObservations(int minutes)
    {
        var config = new TorrentarrConfig
        {
            TorrentClients = new() { ["client"] = new() },
            Trackers = [new()
            {
                ClientIds = ["client"], Uri = "tracker.example",
                Protection = new() { AllowClientRatioClearance = true, AccountingGraceMinutes = minutes }
            }]
        };
        var clock = new TestClock();
        var (service, _, db) = Create(config, clock);
        using var database = db;
        var torrent = new TorrentInfo { ClientInstanceId = "client", Hash = "hash", AddedOn = 1, Progress = 1, Ratio = 2 };
        List<TorrentTracker> trackers = [new() { Url = "https://tracker.example/announce", Status = 2 }];

        service.GetProtectionDecisions(torrent, trackers, observe: true);
        clock.Advance(TimeSpan.FromHours(2));
        service.HnrAllowsDelete(torrent, trackers).Should().BeFalse();
        service.GetProtectionDecisions(torrent, trackers, observe: true);
        service.GetProtectionDecisions(torrent, trackers, observe: true);
        service.HnrAllowsDelete(torrent, trackers).Should().BeFalse();
        clock.Advance(TimeSpan.FromMinutes(1));
        service.GetProtectionDecisions(torrent, trackers, observe: true);
        service.HnrAllowsDelete(torrent, trackers).Should().BeTrue();
    }

    private sealed class TestClock : TimeProvider
    {
        private DateTimeOffset _now = DateTimeOffset.UnixEpoch.AddDays(1);
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan elapsed) => _now += elapsed;
    }

    [Theory]
    [InlineData("ratio", 1, 0, 1, true)]
    [InlineData("ratio", 0, 86400, 1, false)]
    [InlineData("ratio", 1, 0, 0.5, true)]
    [InlineData("time", 0, 86400, 1, true)]
    [InlineData("time", 2, 0, 1, false)]
    [InlineData("time", 2, 86400, 0.5, false)]
    [InlineData("time", 0, 0, 0.05, true)]
    [InlineData("or", 1, 0, 1, true)]
    [InlineData("or", 0, 86400, 1, true)]
    [InlineData("and", 1, 0, 1, false)]
    [InlineData("and", 1, 86400, 1, true)]
    [InlineData("and", 1, 0, 0.5, true)]
    [InlineData("and", 0, 0, 0.05, true)]
    public void ObservationsConfirmLegacyClearance(string mode, double ratio, int seconds, double progress, bool eligible)
    {
        var config = new TorrentarrConfig
        {
            TorrentClients = new() { ["client"] = new() },
            Trackers = [new()
            {
                ClientIds = ["client"], Uri = "tracker.example", HitAndRunMode = mode,
                MinSeedRatio = 1, MinSeedingTimeDays = 1,
                Protection = new() { RequireLegacyClearance = true, MinimumSeedHours = 48, AccountingGraceMinutes = 0 }
            }]
        };
        var (service, _, db) = Create(config);
        using (db)
        {
            var torrent = new TorrentInfo
            {
                ClientInstanceId = "client",
                Hash = "hash",
                Progress = progress,
                Ratio = ratio,
                SeedingTime = seconds
            };
            List<TorrentTracker> trackers = [new() { Url = "https://tracker.example/announce", Status = 2 }];
            service.GetProtectionDecisions(torrent, trackers, observe: true);
            service.HnrAllowsDelete(torrent, trackers).Should().Be(progress < 0.1);
            service.GetProtectionDecisions(torrent, trackers, observe: true);
            service.HnrAllowsDelete(torrent, trackers).Should().Be(eligible);
        }
    }

    [Theory]
    [InlineData("unhealthy")]
    [InlineData("stale")]
    [InlineData("ratio")]
    [InlineData("seed-time")]
    [InlineData("re-added")]
    [InlineData("completion")]
    public void LegacyConfirmationRequiresRecentContinuousEvidence(string interruption)
    {
        var observation = new ProtectionObservation();
        var torrent = new TorrentInfo { AddedOn = 1, CompletionOn = 2, Ratio = 2, SeedingTime = 120 };
        var start = DateTimeOffset.UnixEpoch.AddHours(1);
        var gap = TimeSpan.FromMinutes(2);
        var grace = TimeSpan.FromMinutes(1);
        bool Confirm(int seconds, bool qualifies = true, bool observe = true) =>
            TrackerProtectionEvaluator.ConfirmLegacyClearance(observation, torrent, qualifies, start.AddSeconds(seconds), gap, grace, observe);

        Confirm(0).Should().BeFalse();
        Confirm(59).Should().BeFalse();
        Confirm(60, observe: false).Should().BeFalse();
        Confirm(60).Should().BeTrue();
        var next = 61;
        switch (interruption)
        {
            case "unhealthy": Confirm(next++, qualifies: false).Should().BeFalse(); break;
            case "stale": next = 181; break;
            case "ratio": torrent.Ratio--; break;
            case "seed-time": torrent.SeedingTime--; break;
            case "re-added": torrent.AddedOn++; break;
            case "completion": torrent.CompletionOn++; break;
        }
        Confirm(next, observe: false).Should().BeFalse();
        Confirm(next).Should().BeFalse();
        Confirm(next + 60).Should().BeTrue();
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
        if (!removeRule) await service.ReconcileProtectionLimitsAsync(torrent, trackers, default);
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
    public async Task ApplySeedingLimits_LeavesNativeLimitsToTheInspector()
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
        client.Verify(c => c.SetShareLimitsAsync("hash", -1, -1, -1, It.IsAny<CancellationToken>()), Times.Never);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ApplySeedingLimits_DoesNotChangeNativeLimitsForPublicTorrents(bool categoryProtection)
    {
        var config = new TorrentarrConfig();
        config.TorrentClients["client"] = new()
        {
            CategorySeeding = { HitAndRunMode = categoryProtection ? "and" : "disabled", MinSeedRatio = 1 }
        };
        var (service, client, db) = Create(config);
        await using var context = db;
        var torrent = new TorrentInfo
        {
            ClientInstanceId = "client",
            Hash = "hash",
            AddedOn = 1,
            Progress = 1,
            Ratio = 2,
            RatioLimit = 1,
            SeedingTimeLimit = 60
        };
        client.Setup(c => c.GetTorrentPropertiesAsync("hash", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new TorrentPropertiesRecord { IsPrivate = false });
        client.Setup(c => c.GetTorrentTrackersAsync("hash", It.IsAny<CancellationToken>()))
            .ReturnsAsync([new() { Url = "https://tracker.example/announce", Status = 2 }]);

        await service.ApplySeedingLimitsAsync(torrent);

        torrent.IsPrivate.Should().BeFalse();
        client.Verify(c => c.GetTorrentTrackersAsync("hash", It.IsAny<CancellationToken>()), categoryProtection ? Times.Once() : Times.Never());
        client.Verify(c => c.SetShareLimitsAsync("hash", -1, -1, It.IsAny<CancellationToken>()), Times.Never());
        torrent.RatioLimit.Should().Be(1);
    }
}

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
    [Theory]
    [InlineData(false, false, false, false)]
    [InlineData(true, false, false, false)]
    [InlineData(false, true, false, false)]
    [InlineData(false, false, true, false)]
    [InlineData(false, false, false, true)]
    public async Task ProtectedLimits_ClearAllThree_AndPreserveSubsequentUserChanges(bool userChanged, bool revisedWhileProtected, bool clearRejected, bool restoreRejected)
    {
        using var db = new TorrentarrDbContext(new DbContextOptionsBuilder<TorrentarrDbContext>().UseSqlite("Data Source=:memory:").Options);
        db.Database.OpenConnection(); db.Database.EnsureCreated();
        var config = new TorrentarrConfig();
        config.TorrentClients["client"] = new()
        {
            Trackers = [new() { Uri = "tracker.example", Protection = new() {
            MinimumSeedHours = 48, AllowClientRatioClearance = true, AccountingGraceMinutes = 0, AdditionalSeedHours = 0
        } }]
        };
        var client = new Mock<ITorrentClient>();
        client.SetupGet(c => c.Capabilities).Returns(TorrentClientCapabilities.QBittorrent);
        client.Setup(c => c.SetShareLimitsAsync(It.IsAny<string>(), It.IsAny<double>(), It.IsAny<long>(), It.IsAny<long>(), It.IsAny<CancellationToken>())).ReturnsAsync(true);
        var registry = new Mock<ITorrentClientRegistry>();
        registry.Setup(r => r.GetClient("client")).Returns(client.Object);
        var service = new SeedingService(NullLogger<SeedingService>.Instance, db, config, registry.Object);
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
        await service.ReconcileProtectionLimitsAsync(torrent, trackers, default);
        client.Verify(c => c.SetShareLimitsAsync("hash", ratio, seedTime, inactiveTime, It.IsAny<CancellationToken>()), userChanged ? Times.Never() : Times.Once());
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
        client.Verify(c => c.SetShareLimitsAsync("hash", ratio, seedTime, inactiveTime, It.IsAny<CancellationToken>()), userChanged ? Times.Never() : Times.Exactly(restoreRejected ? 2 : 1));
    }
}

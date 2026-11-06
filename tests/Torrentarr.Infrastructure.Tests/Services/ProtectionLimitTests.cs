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
    [InlineData(false, false, false)]
    [InlineData(true, false, false)]
    [InlineData(false, true, false)]
    [InlineData(false, false, true)]
    public async Task Hnr_DoesNotOverrideLimits_AndRestoresOnlyOwnedLegacyValues(bool userChanged, bool replacedTorrent, bool restoreRejected)
    {
        using var db = new TorrentarrDbContext(new DbContextOptionsBuilder<TorrentarrDbContext>().UseSqlite("Data Source=:memory:").Options);
        db.Database.OpenConnection(); db.Database.EnsureCreated();
        var config = new TorrentarrConfig();
        config.TorrentClients["client"] = new() { Trackers = [new() { Uri = "tracker.example", Protection = new() { MinimumSeedHours = 48 } }] };
        var client = new Mock<ITorrentClient>();
        client.SetupGet(c => c.Capabilities).Returns(TorrentClientCapabilities.QBittorrent);
        var registry = new Mock<ITorrentClientRegistry>();
        registry.Setup(r => r.GetClient("client")).Returns(client.Object);
        var service = new SeedingService(NullLogger<SeedingService>.Instance, db, config, registry.Object);
        var torrent = new TorrentInfo
        {
            ClientInstanceId = "client",
            Hash = "hash",
            AddedOn = 1,
            IsPrivate = true,
            Progress = 1,
            RatioLimit = 1,
            SeedingTimeLimit = 60,
            InactiveSeedingTimeLimit = 30
        };
        List<TorrentTracker> trackers = [new() { Url = "https://tracker.example/announce", Status = 2 }];
        await service.ReconcileProtectionLimitsAsync(torrent, trackers, default);
        client.Verify(c => c.SetShareLimitsAsync(It.IsAny<string>(), It.IsAny<double>(), It.IsAny<long>(), It.IsAny<long>(), It.IsAny<CancellationToken>()), Times.Never());
        torrent.RatioLimit.Should().Be(1);
        var key = MaintenancePersistence.Key("client", "hash", "native-limits");
        MaintenancePersistence.Write(db, key, new ProtectionObservation
        {
            AddedOn = replacedTorrent ? 2 : 1,
            PreviousRatioLimit = 1,
            PreviousTimeLimit = 60,
            PreviousInactiveTimeLimit = 30
        });
        torrent.RatioLimit = userChanged ? 2 : -1; torrent.SeedingTimeLimit = -1; torrent.InactiveSeedingTimeLimit = -1;
        client.Setup(c => c.SetShareLimitsAsync("hash", 1, 60, 30, It.IsAny<CancellationToken>())).ReturnsAsync(!restoreRejected);
        await service.ReconcileProtectionLimitsAsync(torrent, trackers, default);
        client.Verify(c => c.SetShareLimitsAsync("hash", 1, 60, 30, It.IsAny<CancellationToken>()), userChanged || replacedTorrent ? Times.Never() : Times.Once());
        var pending = MaintenancePersistence.Read<ProtectionObservation>(db, key).PreviousRatioLimit;
        if (restoreRejected)
        {
            pending.Should().Be(1);
            client.Setup(c => c.SetShareLimitsAsync("hash", 1, 60, 30, It.IsAny<CancellationToken>())).ReturnsAsync(true);
            await service.ReconcileProtectionLimitsAsync(torrent, trackers, default);
            MaintenancePersistence.Read<ProtectionObservation>(db, key).PreviousRatioLimit.Should().BeNull();
        }
        else pending.Should().BeNull();
    }
}

using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Newtonsoft.Json;
using Torrentarr.Core.Configuration;
using Torrentarr.Core.Interfaces;
using Torrentarr.Core.Models;
using Torrentarr.Core.Services;
using Torrentarr.Infrastructure.Database;
using Torrentarr.Infrastructure.Services;
using Xunit;

namespace Torrentarr.Infrastructure.Tests.Services;

public sealed class RetirementGuardTests
{
    [Theory]
    [InlineData("{}", 0, true)]
    [InlineData("{\"num_complete\":null}", 0, true)]
    [InlineData("{\"num_complete\":-1}", 0, true)]
    [InlineData("{\"num_complete\":0}", 0, true)]
    [InlineData("{\"num_complete\":1}", 0, true)]
    [InlineData("{\"num_complete\":2}", 0, false)]
    [InlineData("{\"num_complete\":3}", 0, false)]
    [InlineData("{\"num_complete\":3}", 61, true)]
    public async Task SeederGuardUsesKnownFreshCounts(string json, int age, bool blocked)
    {
        await using var db = new TorrentarrDbContext(new DbContextOptionsBuilder<TorrentarrDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        var config = Config(2, 0);
        var torrent = JsonConvert.DeserializeObject<TorrentInfo>(json)!;
        torrent.ObservedAtUnixSeconds = 1000 - age;
        var result = await RetirementGuard.EvaluateAsync(db, config, torrent, 1000, default);
        Assert.Equal(blocked, result.Count > 0);
        if (json.Contains(":0"))
            Assert.Contains("Reported seeders=0;", Assert.Single(result).Reason);
    }

    [Fact]
    public async Task InactivityUsesPersistedContinuousEvidenceAndResetsAfterGapsOrChanges()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<TorrentarrDbContext>().UseSqlite(connection).Options;
        await using var db = new TorrentarrDbContext(options);
        await db.Database.EnsureCreatedAsync();
        var config = Config(2, 60);
        var item = new TorrentInfo { Hash = "hash", Category = "movies", AddedOn = 1, Progress = 1, Uploaded = 200, CompletePeers = 2 };
        async Task<bool> Allowed(long seen, TorrentarrDbContext dbContext)
        {
            item.ObservedAtUnixSeconds = seen;
            return (await RetirementGuard.EvaluateAsync(dbContext, config, item, seen, default)).Count == 0;
        }
        Assert.False(await Allowed(1000, db));
        Assert.False(await Allowed(1059, db));
        await using var restarted = new TorrentarrDbContext(options);
        Assert.True(await Allowed(1060, restarted));
        item.CompletePeers = 1;
        Assert.False(await Allowed(1061, restarted));
        item.CompletePeers = 2;
        item.Uploaded++;
        Assert.False(await Allowed(1062, restarted));
        Assert.True(await Allowed(1122, restarted));
        item.Uploaded = 1;
        Assert.False(await Allowed(1123, restarted));
        Assert.True(await Allowed(1183, restarted));
        Assert.False(await Allowed(1244, restarted));
        Assert.True(await Allowed(1304, restarted));
        await RetirementGuard.ForgetAsync(restarted, config, "qBit", "movies", [], default);
        Assert.False(await Allowed(1305, restarted));
        Assert.True(await Allowed(1365, restarted));
        item.AddedOn = 1300;
        Assert.False(await Allowed(1366, restarted));
        Assert.True(await Allowed(1426, restarted));
        config.QBitInstances["qBit"].Host = "other-client";
        Assert.False(await Allowed(1427, restarted));
        Assert.True(await Allowed(1487, restarted));
        var missing = new TorrentInfo
        {
            Hash = item.Hash,
            Category = item.Category,
            AddedOn = item.AddedOn,
            Progress = 1,
            CompletePeers = 2,
            ObservedAtUnixSeconds = 1488
        };
        Assert.Contains(await RetirementGuard.EvaluateAsync(restarted, config, missing, 1488, default),
            blocker => blocker.Code == "retirement.upload-inactivity");
        Assert.False(await Allowed(1489, restarted));
        Assert.True(await Allowed(1549, restarted));
        item.UploadSpeed = 1;
        Assert.False(await Allowed(1550, restarted));
    }

    [Fact]
    public async Task OrdinaryRemovalRechecksFreshCountsWithoutChangingOtherIntents()
    {
        await using var db = new TorrentarrDbContext(new DbContextOptionsBuilder<TorrentarrDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        var config = Config(2, 0);
        var torrent = new TorrentInfo
        {
            Hash = "hash",
            AddedOn = DateTimeOffset.UtcNow.AddHours(-2).ToUnixTimeSeconds(),
            Category = "movies",
            Progress = 1,
            Ratio = 2,
            State = "uploading",
            IsPrivate = false,
            CompletePeers = 3,
            ObservedAtUnixSeconds = DateTimeOffset.UtcNow.ToUnixTimeSeconds()
        };
        var current = JsonConvert.DeserializeObject<TorrentInfo>(JsonConvert.SerializeObject(torrent))!;
        current.CompletePeers = 1;
        var client = new Mock<ITorrentClient>();
        client.Setup(c => c.GetTorrentTrackersAsync(torrent.Hash, It.IsAny<CancellationToken>()))
            .ReturnsAsync([new() { Url = "https://tracker.example/announce", Status = 2 }]);
        client.Setup(c => c.GetTorrentsAsync(null, null, It.IsAny<CancellationToken>())).ReturnsAsync([current]);
        var registry = new Mock<ITorrentClientRegistry>();
        registry.Setup(r => r.GetClient("qBit")).Returns(client.Object);
        var service = new SeedingService(NullLogger<SeedingService>.Instance, db, config, registry.Object);

        Assert.True(await service.ShouldRemoveTorrentAsync(torrent));
        Assert.Contains((await service.EvaluateRemovalAsync(torrent, TorrentRemovalIntent.OrdinaryRetirement)).Blockers,
            blocker => blocker.Code == "retirement.seeders");
        Assert.True((await service.EvaluateRemovalAsync(torrent, TorrentRemovalIntent.SeedingLimit)).Allowed);
        current.CompletePeers = 2;
        Assert.True((await service.EvaluateRemovalAsync(torrent, TorrentRemovalIntent.OrdinaryRetirement)).Allowed);
        torrent.State = "stalledUP";
        torrent.ObservedRetirementState = "stoppedUP";
        current.State = "stoppedUP";
        Assert.True((await service.EvaluateRemovalAsync(torrent, TorrentRemovalIntent.OrdinaryRetirement)).Allowed);
        current.State = "checkingUP";
        Assert.False((await service.EvaluateRemovalAsync(torrent, TorrentRemovalIntent.OrdinaryRetirement)).Allowed);
        current.State = "stoppedUP";
        config.QBitInstances["qBit"].CategorySeeding.HitAndRunMode = "ratio";
        config.QBitInstances["qBit"].CategorySeeding.MinSeedRatio = 3;
        Assert.False((await service.EvaluateRemovalAsync(torrent, TorrentRemovalIntent.OrdinaryRetirement)).Allowed);
    }

    private static TorrentarrConfig Config(int seeders, int seconds)
    {
        var config = new TorrentarrConfig();
        config.QBitInstances["qBit"] = new()
        {
            CategorySeeding = new()
            {
                RemoveTorrent = 1,
                MaxUploadRatio = 1,
                MinimumReportedSeeders = seeders,
                MinimumUploadInactivitySeconds = seconds
            }
        };
        return config;
    }
}

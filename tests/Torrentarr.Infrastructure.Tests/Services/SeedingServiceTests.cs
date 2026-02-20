using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Torrentarr.Core.Configuration;
using Torrentarr.Core.Models;
using Torrentarr.Infrastructure.Database;
using Torrentarr.Infrastructure.Services;
using Xunit;

namespace Torrentarr.Infrastructure.Tests.Services;

/// <summary>
/// Pure-logic tests for SeedingService (no live qBit needed).
/// Tests ExtractTrackerHost (static) and IsHnRSafeToRemoveAsync (pure calculation).
/// </summary>
public class SeedingServiceTests
{
    private static SeedingService CreateService(TorrentarrConfig? config = null)
    {
        var options = new DbContextOptionsBuilder<TorrentarrDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        var dbContext = new TorrentarrDbContext(options);
        var mgr = new QBittorrentConnectionManager(NullLogger<QBittorrentConnectionManager>.Instance);
        return new SeedingService(
            NullLogger<SeedingService>.Instance,
            dbContext,
            config ?? new TorrentarrConfig(),
            mgr);
    }

    // ── ExtractTrackerHost ─────────────────────────────────────────────────────

    [Theory]
    [InlineData("https://tracker.example.com/announce", "tracker.example.com")]
    [InlineData("http://tracker.example.com/announce", "tracker.example.com")]
    [InlineData("udp://tracker.openbitclient.com:1337/announce", "tracker.openbitclient.com")]
    [InlineData("https://tracker.example.com:443/announce", "tracker.example.com")]
    public void ExtractTrackerHost_ReturnsHostOnly(string url, string expected)
    {
        SeedingService.ExtractTrackerHost(url).Should().Be(expected);
    }

    [Fact]
    public void ExtractTrackerHost_EmptyString_ReturnsEmpty()
    {
        SeedingService.ExtractTrackerHost("").Should().BeEmpty();
    }

    [Fact]
    public void ExtractTrackerHost_WhitespaceOnly_ReturnsEmpty()
    {
        SeedingService.ExtractTrackerHost("   ").Should().BeEmpty();
    }

    // ── IsHnRSafeToRemoveAsync (pure logic) ────────────────────────────────────

    private static TorrentInfo MakeTorrent(double progress, double ratio, long seedingTimeSec)
        => new TorrentInfo
        {
            Hash = "aabbcc",
            Name = "Test Torrent",
            Progress = progress,
            Ratio = ratio,
            SeedingTime = seedingTimeSec,
            State = "uploading",
            Tags = ""
        };

    private static TrackerConfig HnrConfig(
        bool hnrMode = true,
        double minRatio = 1.0,
        int minDays = 0,
        int minDlPct = 10,
        double partialRatio = 1.0,
        int buffer = 0)
        => new TrackerConfig
        {
            HitAndRunMode = hnrMode,
            MinSeedRatio = minRatio,
            MinSeedingTime = minDays,
            HitAndRunMinimumDownloadPercent = minDlPct,
            HitAndRunPartialSeedRatio = partialRatio,
            TrackerUpdateBuffer = buffer
        };

    [Fact]
    public async Task IsHnRSafeToRemoveAsync_HnrModeOff_AlwaysTrue()
    {
        var svc = CreateService();
        var torrent = MakeTorrent(1.0, 0, 0);
        var config = HnrConfig(hnrMode: false);

        var result = await svc.IsHnRSafeToRemoveAsync(torrent, config);

        result.Should().BeTrue();
    }

    [Fact]
    public async Task IsHnRSafeToRemoveAsync_ProgressBelowMinDownloadPct_SafeToRemove()
    {
        // progress < minDownloadPercent → safe (haven't downloaded enough to count)
        var svc = CreateService();
        var torrent = MakeTorrent(progress: 0.05, ratio: 0, seedingTimeSec: 0);
        var config = HnrConfig(minDlPct: 10); // 10%

        var result = await svc.IsHnRSafeToRemoveAsync(torrent, config);

        result.Should().BeTrue();
    }

    [Fact]
    public async Task IsHnRSafeToRemoveAsync_PartialProgress_RatioMet_SafeToRemove()
    {
        // partial (between minDl% and 100%) + ratio >= partialRatio → safe
        var svc = CreateService();
        var torrent = MakeTorrent(progress: 0.5, ratio: 1.0, seedingTimeSec: 0);
        var config = HnrConfig(minDlPct: 10, partialRatio: 1.0);

        var result = await svc.IsHnRSafeToRemoveAsync(torrent, config);

        result.Should().BeTrue();
    }

    [Fact]
    public async Task IsHnRSafeToRemoveAsync_PartialProgress_RatioNotMet_NotSafe()
    {
        var svc = CreateService();
        var torrent = MakeTorrent(progress: 0.5, ratio: 0.5, seedingTimeSec: 0);
        var config = HnrConfig(minDlPct: 10, partialRatio: 1.0);

        var result = await svc.IsHnRSafeToRemoveAsync(torrent, config);

        result.Should().BeFalse();
    }

    [Fact]
    public async Task IsHnRSafeToRemoveAsync_FullDownload_RatioMet_Safe()
    {
        var svc = CreateService();
        // minRatio=1.0, no time requirement
        var torrent = MakeTorrent(progress: 1.0, ratio: 1.5, seedingTimeSec: 100);
        var config = HnrConfig(minRatio: 1.0, minDays: 0);

        var result = await svc.IsHnRSafeToRemoveAsync(torrent, config);

        result.Should().BeTrue();
    }

    [Fact]
    public async Task IsHnRSafeToRemoveAsync_FullDownload_TimeMet_Safe()
    {
        var svc = CreateService();
        // minDays=1 → 86400 sec; torrent seeded for 90000 sec
        var torrent = MakeTorrent(progress: 1.0, ratio: 0.0, seedingTimeSec: 90000);
        var config = HnrConfig(minRatio: 0, minDays: 1); // no ratio requirement, time only

        var result = await svc.IsHnRSafeToRemoveAsync(torrent, config);

        result.Should().BeTrue();
    }

    [Fact]
    public async Task IsHnRSafeToRemoveAsync_FullDownload_NeitherRatioNorTimeMet_NotSafe()
    {
        var svc = CreateService();
        var torrent = MakeTorrent(progress: 1.0, ratio: 0.3, seedingTimeSec: 1000);
        var config = HnrConfig(minRatio: 1.0, minDays: 2); // 2 days = 172800 sec

        var result = await svc.IsHnRSafeToRemoveAsync(torrent, config);

        result.Should().BeFalse();
    }
}

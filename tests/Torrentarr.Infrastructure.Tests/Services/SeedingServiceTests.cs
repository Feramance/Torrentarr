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

    [Theory]
    [InlineData("https://sub.tracker.example.com/announce", "sub.tracker.example.com")]
    [InlineData("https://a.b.c.example.org/announce", "a.b.c.example.org")]
    [InlineData("tracker.torrentleech.org", "tracker.torrentleech.org")]
    [InlineData("torrentleech.org", "torrentleech.org")]
    public void ExtractTrackerHost_SubdomainUrls_ReturnsFullHost(string url, string expected)
    {
        // These tests document that ExtractTrackerHost extracts the full hostname
        // Subdomain matching (sub.example.com matches example.com config) is done
        // in GetTrackerConfigAsync, not in ExtractTrackerHost
        SeedingService.ExtractTrackerHost(url).Should().Be(expected);
    }

    [Theory]
    [InlineData("https://tracker.example.com/a/long/path/announce", "tracker.example.com")]
    [InlineData("udp://tracker.example.com:6969/announce.php", "tracker.example.com")]
    public void ExtractTrackerHost_LongPaths_ReturnsHostOnly(string url, string expected)
    {
        SeedingService.ExtractTrackerHost(url).Should().Be(expected);
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

    [Fact]
    public async Task IsHnRSafeToRemoveAsync_FullDownload_BothRatioAndTime_EitherMet_Safe()
    {
        // When both ratio and time requirements are set, either one being met is sufficient
        var svc = CreateService();
        // Ratio met, time not met
        var torrent = MakeTorrent(progress: 1.0, ratio: 1.5, seedingTimeSec: 1000);
        var config = HnrConfig(minRatio: 1.0, minDays: 2);

        var result = await svc.IsHnRSafeToRemoveAsync(torrent, config);

        result.Should().BeTrue();
    }

    // ── IsUploadingState / IsDownloadingState / IsStoppedState ──────────────────

    [Theory]
    [InlineData("uploading", true)]
    [InlineData("stalledupload", true)]
    [InlineData("queuedupload", true)]
    [InlineData("pausedupload", true)]
    [InlineData("forcedupload", true)]
    [InlineData("downloading", false)]
    [InlineData("stalleddownload", false)]
    [InlineData("paused", false)]
    [InlineData("", false)]
    public void IsUploadingState_DetectsUploadingStates(string state, bool expected)
    {
        SeedingService.IsUploadingState(state).Should().Be(expected);
    }

    [Theory]
    [InlineData("downloading", true)]
    [InlineData("stalleddownload", true)]
    [InlineData("queueddownload", true)]
    [InlineData("pauseddownload", true)]
    [InlineData("forceddownload", true)]
    [InlineData("metadata", true)]
    [InlineData("uploading", false)]
    [InlineData("stalledupload", false)]
    [InlineData("", false)]
    public void IsDownloadingState_DetectsDownloadingStates(string state, bool expected)
    {
        SeedingService.IsDownloadingState(state).Should().Be(expected);
    }

    [Theory]
    [InlineData("stoppeddownload", true)]
    [InlineData("stoppedupload", true)]
    [InlineData("stopped", true)]
    [InlineData("STOPPEDDOWNLOAD", true)]
    [InlineData("uploading", false)]
    [InlineData("downloading", false)]
    [InlineData("paused", false)]
    [InlineData("", false)]
    public void IsStoppedState_DetectsStoppedStates(string state, bool expected)
    {
        SeedingService.IsStoppedState(state).Should().Be(expected);
    }

    // ── HnrAllowsDeleteAsync ────────────────────────────────────────────────────

    [Fact]
    public async Task HnrAllowsDeleteAsync_NoHnrTrackers_ReturnsTrue()
    {
        var config = new TorrentarrConfig();
        config.QBitInstances["qBit"] = new QBitConfig
        {
            Trackers = new List<TrackerConfig>() // No HnR trackers
        };
        var svc = CreateService(config);
        var torrent = MakeTorrent(1.0, 0, 0);
        torrent.QBitInstanceName = "qBit";

        var result = await svc.HnrAllowsDeleteAsync(torrent, "test deletion");

        result.Should().BeTrue();
    }

    // ── ShouldRemoveTorrentAsync state-based logic ───────────────────────────────

    [Fact]
    public async Task ShouldRemoveTorrentAsync_DownloadingTorrent_NeverRemoved()
    {
        var config = new TorrentarrConfig();
        config.QBitInstances["qBit"] = new QBitConfig
        {
            CategorySeeding = new CategorySeedingConfig
            {
                RemoveTorrent = 1, // Remove on ratio
                MaxUploadRatio = 0.5
            }
        };
        var svc = CreateService(config);
        var torrent = MakeTorrent(1.0, 1.0, 1000); // Ratio met
        torrent.State = "downloading"; // But downloading, not uploading
        torrent.QBitInstanceName = "qBit";

        var result = await svc.ShouldRemoveTorrentAsync(torrent);

        result.Should().BeFalse(); // Downloading torrents are never removed by seeding limits
    }

    [Fact]
    public async Task ShouldRemoveTorrentAsync_UploadingTorrent_CanBeRemoved()
    {
        var config = new TorrentarrConfig();
        config.QBitInstances["qBit"] = new QBitConfig
        {
            CategorySeeding = new CategorySeedingConfig
            {
                RemoveTorrent = 1, // Remove on ratio
                MaxUploadRatio = 0.5
            }
        };
        var svc = CreateService(config);
        var torrent = MakeTorrent(1.0, 1.0, 1000); // Ratio met
        torrent.State = "uploading";
        torrent.QBitInstanceName = "qBit";

        var result = await svc.ShouldRemoveTorrentAsync(torrent);

        result.Should().BeTrue();
    }
}

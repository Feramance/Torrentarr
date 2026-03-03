using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using System.Reflection;
using Torrentarr.Core.Configuration;
using Torrentarr.Core.Models;
using Torrentarr.Infrastructure.Database;
using Torrentarr.Infrastructure.Services;
using Xunit;

namespace Torrentarr.Infrastructure.Tests.Services;

/// <summary>
/// Tests for SeedingService.GetTrackerList (§3.3 Arr-level tracker merge).
/// Rule: qBit-level trackers are the base; Arr-level overrides any tracker with
/// the same normalised host. Arr-only trackers are added to the merged list.
/// </summary>
public class SeedingServiceTrackerMergeTests
{
    private static SeedingService CreateService(TorrentarrConfig config)
    {
        var options = new DbContextOptionsBuilder<TorrentarrDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        var db = new TorrentarrDbContext(options);
        var mgr = new QBittorrentConnectionManager(NullLogger<QBittorrentConnectionManager>.Instance);
        return new SeedingService(NullLogger<SeedingService>.Instance, db, config, mgr);
    }

    private static List<TrackerConfig> CallGetTrackerList(SeedingService svc, TorrentInfo torrent)
    {
        var method = typeof(SeedingService).GetMethod(
            "GetTrackerList",
            BindingFlags.NonPublic | BindingFlags.Instance);
        return (List<TrackerConfig>)method!.Invoke(svc, new object?[] { torrent })!;
    }

    private static TrackerConfig Tracker(string uri, double? minRatio = null) => new()
    {
        Uri = uri,
        MinSeedRatio = minRatio ?? 1.0
    };

    private static TorrentInfo MakeTorrent(string qbitInstance, string category) => new()
    {
        Hash = "deadbeef",
        Name = "Test",
        QBitInstanceName = qbitInstance,
        Category = category
    };

    // ── No trackers configured ────────────────────────────────────────────────

    [Fact]
    public void GetTrackerList_NoQBitOrArrTrackers_ReturnsEmpty()
    {
        var config = new TorrentarrConfig();
        config.QBitInstances["qBit"] = new QBitConfig { Trackers = [] };
        var svc = CreateService(config);
        var torrent = MakeTorrent("qBit", "radarr");

        var result = CallGetTrackerList(svc, torrent);

        result.Should().BeEmpty();
    }

    // ── qBit-only trackers ────────────────────────────────────────────────────

    [Fact]
    public void GetTrackerList_QBitOnlyTrackers_NoArrMatch_ReturnsQBitList()
    {
        var config = new TorrentarrConfig();
        var qbitTracker = Tracker("https://tracker.example.com/announce", minRatio: 1.0);
        config.QBitInstances["qBit"] = new QBitConfig { Trackers = [qbitTracker] };
        // No Arr instance with matching category
        var svc = CreateService(config);
        var torrent = MakeTorrent("qBit", "radarr");

        var result = CallGetTrackerList(svc, torrent);

        result.Should().ContainSingle();
        result[0].Uri.Should().Be("https://tracker.example.com/announce");
    }

    [Fact]
    public void GetTrackerList_QBitOnlyTrackers_ArrInstanceHasNoTrackers_ReturnsQBitList()
    {
        var config = new TorrentarrConfig();
        var qbitTracker = Tracker("https://tracker.example.com/announce");
        config.QBitInstances["qBit"] = new QBitConfig { Trackers = [qbitTracker] };
        // Arr instance exists but has no Trackers
        config.ArrInstances["Radarr-4K"] = new ArrInstanceConfig
        {
            Category = "radarr",
            Type = "radarr",
            Torrent = new TorrentConfig { Trackers = [] }
        };
        var svc = CreateService(config);
        var torrent = MakeTorrent("qBit", "radarr");

        var result = CallGetTrackerList(svc, torrent);

        result.Should().ContainSingle();
        result[0].Uri.Should().Be("https://tracker.example.com/announce");
    }

    // ── Arr-level override on host collision ──────────────────────────────────

    [Fact]
    public void GetTrackerList_ArrTrackerOverridesQBitOnSameHost()
    {
        var config = new TorrentarrConfig();
        // qBit: minRatio = 1.0
        config.QBitInstances["qBit"] = new QBitConfig
        {
            Trackers = [Tracker("https://tracker.example.com/announce", minRatio: 1.0)]
        };
        // Arr: same host, different ratio → should win
        config.ArrInstances["Radarr"] = new ArrInstanceConfig
        {
            Category = "radarr",
            Type = "radarr",
            Torrent = new TorrentConfig
            {
                Trackers = [Tracker("https://tracker.example.com/other-path", minRatio: 2.5)]
            }
        };
        var svc = CreateService(config);
        var torrent = MakeTorrent("qBit", "radarr");

        var result = CallGetTrackerList(svc, torrent);

        result.Should().ContainSingle("same host → Arr overrides qBit entry");
        result[0].MinSeedRatio.Should().Be(2.5, "Arr-level tracker wins on host collision");
    }

    // ── Arr-only trackers added ───────────────────────────────────────────────

    [Fact]
    public void GetTrackerList_ArrTrackerWithDifferentHost_BothReturned()
    {
        var config = new TorrentarrConfig();
        config.QBitInstances["qBit"] = new QBitConfig
        {
            Trackers = [Tracker("https://tracker-a.com/announce")]
        };
        config.ArrInstances["Radarr"] = new ArrInstanceConfig
        {
            Category = "radarr",
            Type = "radarr",
            Torrent = new TorrentConfig
            {
                Trackers = [Tracker("https://tracker-b.com/announce")]
            }
        };
        var svc = CreateService(config);
        var torrent = MakeTorrent("qBit", "radarr");

        var result = CallGetTrackerList(svc, torrent);

        result.Should().HaveCount(2, "different hosts → both kept in merged list");
        result.Select(t => t.Uri).Should().Contain(
        [
            "https://tracker-a.com/announce",
            "https://tracker-b.com/announce"
        ]);
    }

    // ── Category matching ─────────────────────────────────────────────────────

    [Fact]
    public void GetTrackerList_CategoryMismatch_ArrTrackersNotUsed()
    {
        var config = new TorrentarrConfig();
        var qbitTracker = Tracker("https://tracker.example.com/announce", minRatio: 1.0);
        config.QBitInstances["qBit"] = new QBitConfig { Trackers = [qbitTracker] };
        // Arr instance has category "sonarr" but torrent is in "radarr"
        config.ArrInstances["Sonarr"] = new ArrInstanceConfig
        {
            Category = "sonarr",
            Type = "sonarr",
            Torrent = new TorrentConfig
            {
                Trackers = [Tracker("https://tracker.example.com/announce", minRatio: 9.9)]
            }
        };
        var svc = CreateService(config);
        var torrent = MakeTorrent("qBit", "radarr");  // category=radarr, not sonarr

        var result = CallGetTrackerList(svc, torrent);

        result.Should().ContainSingle();
        result[0].MinSeedRatio.Should().Be(1.0,
            "Arr instance category doesn't match torrent category → qBit value kept");
    }

    // ── qBit instance lookup ──────────────────────────────────────────────────

    [Fact]
    public void GetTrackerList_UnknownQBitInstance_ReturnsEmptyBase()
    {
        var config = new TorrentarrConfig();
        // Config has "qBit" but torrent claims "unknown-instance"
        config.QBitInstances["qBit"] = new QBitConfig
        {
            Trackers = [Tracker("https://tracker.example.com/announce")]
        };
        config.ArrInstances["Radarr"] = new ArrInstanceConfig
        {
            Category = "radarr",
            Type = "radarr",
            Torrent = new TorrentConfig
            {
                Trackers = [Tracker("https://arr-tracker.com/announce")]
            }
        };
        var svc = CreateService(config);
        var torrent = MakeTorrent("unknown-instance", "radarr");

        var result = CallGetTrackerList(svc, torrent);

        // qBit lookup returns empty (no instance), Arr trackers are added
        result.Should().ContainSingle();
        result[0].Uri.Should().Be("https://arr-tracker.com/announce");
    }

    // ── Mixed scenario: override + add ────────────────────────────────────────

    [Fact]
    public void GetTrackerList_MixedScenario_OverrideAndAdd()
    {
        var config = new TorrentarrConfig();
        config.QBitInstances["qBit"] = new QBitConfig
        {
            Trackers =
            [
                Tracker("https://shared-tracker.com/announce", minRatio: 1.0),
                Tracker("https://qbit-only.com/announce",      minRatio: 0.5)
            ]
        };
        config.ArrInstances["Radarr"] = new ArrInstanceConfig
        {
            Category = "radarr",
            Type = "radarr",
            Torrent = new TorrentConfig
            {
                Trackers =
                [
                    Tracker("https://shared-tracker.com/other", minRatio: 3.0), // same host → override
                    Tracker("https://arr-only.com/announce",    minRatio: 2.0)  // new host → add
                ]
            }
        };
        var svc = CreateService(config);
        var torrent = MakeTorrent("qBit", "radarr");

        var result = CallGetTrackerList(svc, torrent);

        result.Should().HaveCount(3, "shared host overridden + qbit-only kept + arr-only added");

        var sharedTracker = result.FirstOrDefault(t => t.Uri?.Contains("shared-tracker") == true);
        sharedTracker.Should().NotBeNull();
        sharedTracker!.MinSeedRatio.Should().Be(3.0, "Arr-level wins on shared host");

        result.Should().Contain(t => t.Uri!.Contains("qbit-only"),
            "qBit-only tracker retained");
        result.Should().Contain(t => t.Uri!.Contains("arr-only"),
            "Arr-only tracker added");
    }
}

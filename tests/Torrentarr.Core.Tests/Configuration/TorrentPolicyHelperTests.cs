using System.Linq;
using FluentAssertions;
using Torrentarr.Core.Configuration;
using Torrentarr.Core.Models;
using Xunit;

namespace Torrentarr.Core.Tests.Configuration;

public class TorrentPolicyHelperTests
{
    [Fact]
    public void GlobalSortTorrentsEnabled_TrueWhenAnyQBitTrackerHasSort()
    {
        var cfg = new TorrentarrConfig();
        cfg.QBitInstances["qBit"] = new QBitConfig();
        cfg.QBitInstances["qBit"].Trackers.Add(
            new TrackerConfig { Uri = "https://example.com/announce", SortTorrents = true });
        TorrentPolicyHelper.GlobalSortTorrentsEnabled(cfg).Should().BeTrue();
    }

    [Fact]
    public void GlobalSortTorrentsEnabled_TrueWhenAnyArrTrackerHasSort()
    {
        var cfg = new TorrentarrConfig();
        cfg.ArrInstances["R"] = new ArrInstanceConfig();
        cfg.ArrInstances["R"].Torrent.Trackers.Add(
            new TrackerConfig { Uri = "https://a.com/x", SortTorrents = true });
        TorrentPolicyHelper.GlobalSortTorrentsEnabled(cfg).Should().BeTrue();
    }

    [Fact]
    public void GlobalSortTorrentsEnabled_FalseWhenNoSortTorrents()
    {
        var cfg = new TorrentarrConfig();
        cfg.QBitInstances["qBit"] = new QBitConfig();
        cfg.QBitInstances["qBit"].Trackers.Add(
            new TrackerConfig { Uri = "https://example.com/announce", SortTorrents = false });
        TorrentPolicyHelper.GlobalSortTorrentsEnabled(cfg).Should().BeFalse();
    }

    [Fact]
    public void EnableTrackerSort_TrueWhenSortTorrents_AndQBitNotDisabled()
    {
        var cfg = new TorrentarrConfig();
        cfg.QBitInstances["qBit"] = new QBitConfig { Disabled = false };
        cfg.QBitInstances["qBit"].Trackers.Add(new TrackerConfig { Uri = "https://example.com/announce", SortTorrents = true });

        TorrentPolicyHelper.EnableTrackerSort(cfg).Should().BeTrue();
    }

    [Fact]
    public void EnableTrackerSort_FalseWhenPrimaryQBitDisabled()
    {
        var cfg = new TorrentarrConfig();
        cfg.QBitInstances["qBit"] = new QBitConfig { Disabled = true };
        cfg.QBitInstances["qBit"].Trackers.Add(new TrackerConfig { SortTorrents = true });

        TorrentPolicyHelper.EnableTrackerSort(cfg).Should().BeFalse();
    }

    [Fact]
    public void EnableFreeSpace_FalseWhenAutoPauseResumeOff_EvenIfThresholdSet()
    {
        var cfg = new TorrentarrConfig();
        cfg.Settings.AutoPauseResume = false;
        cfg.QBitInstances["qBit"] = new QBitConfig { Disabled = false };

        TorrentPolicyHelper.EnableFreeSpace(cfg, freeSpaceGuardActive: true).Should().BeFalse();
    }

    [Fact]
    public void EnableFreeSpace_TrueWhenGuardActive_AndAutoPause_AndQBitEnabled()
    {
        var cfg = new TorrentarrConfig();
        cfg.Settings.AutoPauseResume = true;
        cfg.QBitInstances["qBit"] = new QBitConfig { Disabled = false };

        TorrentPolicyHelper.EnableFreeSpace(cfg, freeSpaceGuardActive: true).Should().BeTrue();
    }

    [Fact]
    public void MergeGlobalTrackerTagToPriorityMax_TakesMaxPriorityPerTag()
    {
        var cfg = new TorrentarrConfig();
        cfg.QBitInstances["qBit"] = new QBitConfig();
        cfg.QBitInstances["qBit"].Trackers.Add(new TrackerConfig
        {
            Uri = "https://a.com/x",
            Priority = 3,
            AddTags = ["vip", "a"]
        });
        cfg.QBitInstances["qBit"].Trackers.Add(new TrackerConfig
        {
            Uri = "https://b.com/y",
            Priority = 10,
            AddTags = ["vip"]
        });

        var map = TorrentPolicyHelper.MergeGlobalTrackerTagToPriorityMax(cfg);
        map["vip"].Should().Be(10);
        map["a"].Should().Be(3);
    }

    [Fact]
    public void TorrentQueuePositionSortKey_ActiveQueueBeforeInactive()
    {
        var active = new TorrentInfo { Priority = 2 };
        var inactive = new TorrentInfo { Priority = 0 };
        var tuples = new[]
        {
            TorrentPolicyHelper.TorrentQueuePositionSortKey(inactive),
            TorrentPolicyHelper.TorrentQueuePositionSortKey(active),
        };
        tuples.OrderBy(t => t.InactiveQueueGroup).ThenBy(t => t.Nq).First().Should().Be(TorrentPolicyHelper.TorrentQueuePositionSortKey(active));
    }

    [Fact]
    public void IsQueueSeedingForSort_IncludesForcedAndCheckingUpload()
    {
        TorrentPolicyHelper.IsQueueSeedingForSort("forcedUP").Should().BeTrue();
        TorrentPolicyHelper.IsQueueSeedingForSort("checkingUP").Should().BeTrue();
        TorrentPolicyHelper.IsQueueSeedingForSort("stoppedUP").Should().BeTrue();
        TorrentPolicyHelper.IsQueueSeedingForSort("pausedUP").Should().BeTrue();
        TorrentPolicyHelper.IsQueueSeedingForSort("stalledDL").Should().BeFalse();
    }

    [Fact]
    public void IsMonitoredPolicyCategory_IncludesArrAndQBitManaged()
    {
        var cfg = new TorrentarrConfig();
        cfg.ArrInstances["R"] = new ArrInstanceConfig { Category = "movies" };
        cfg.QBitInstances["qBit"] = new QBitConfig { ManagedCategories = ["tv"] };

        TorrentPolicyHelper.IsMonitoredPolicyCategory(cfg, "movies").Should().BeTrue();
        TorrentPolicyHelper.IsMonitoredPolicyCategory(cfg, "tv").Should().BeTrue();
        TorrentPolicyHelper.IsMonitoredPolicyCategory(cfg, "other").Should().BeFalse();
    }

    [Fact]
    public void GetAllMonitoredPolicyCategories_ReusesCachedSetPerConfigInstance()
    {
        var cfg = new TorrentarrConfig();
        cfg.ArrInstances["R"] = new ArrInstanceConfig { Category = "movies" };
        cfg.QBitInstances["qBit"] = new QBitConfig { ManagedCategories = ["tv"] };

        var first = TorrentPolicyHelper.GetAllMonitoredPolicyCategories(cfg);
        var second = TorrentPolicyHelper.GetAllMonitoredPolicyCategories(cfg);

        ReferenceEquals(first, second).Should().BeTrue();
    }

    [Fact]
    public void InvalidateMonitoredPolicyCategoriesCache_AllowsRefreshAfterInPlaceConfigChange()
    {
        var cfg = new TorrentarrConfig();
        cfg.ArrInstances["R"] = new ArrInstanceConfig { Category = "movies" };
        _ = TorrentPolicyHelper.GetAllMonitoredPolicyCategories(cfg);
        cfg.ArrInstances["R2"] = new ArrInstanceConfig { Category = "anime" };
        TorrentPolicyHelper.IsMonitoredPolicyCategory(cfg, "anime").Should().BeFalse();

        TorrentPolicyHelper.InvalidateMonitoredPolicyCategoriesCache(cfg);
        TorrentPolicyHelper.IsMonitoredPolicyCategory(cfg, "anime").Should().BeTrue();
    }
}

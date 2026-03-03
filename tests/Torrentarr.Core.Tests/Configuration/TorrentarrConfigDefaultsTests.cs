using FluentAssertions;
using Torrentarr.Core.Configuration;
using Xunit;

namespace Torrentarr.Core.Tests.Configuration;

public class TorrentarrConfigDefaultsTests
{
    [Fact]
    public void Settings_HasExpectedDefaults()
    {
        var config = new TorrentarrConfig();

        config.Settings.ConfigVersion.Should().MatchRegex(@"^\d+\.\d+\.\d+$");
        config.Settings.LoopSleepTimer.Should().Be(5);
        config.Settings.FailedCategory.Should().Be("failed");
    }

    [Fact]
    public void QBitInstances_IsEmptyByDefault()
    {
        var config = new TorrentarrConfig();

        config.QBitInstances.Should().BeEmpty();
    }

    [Fact]
    public void QBitInstances_DoesNotContainKey_WhenNoQBitSection()
    {
        var config = new TorrentarrConfig();

        config.QBitInstances.Should().NotContainKey("qBit");
    }

    [Fact]
    public void QBitInstances_ReturnsConfiguredInstance_WhenQBitPresent()
    {
        var config = new TorrentarrConfig();
        config.QBitInstances["qBit"] = new QBitConfig { Host = "192.168.1.100" };

        config.QBitInstances["qBit"].Host.Should().Be("192.168.1.100");
    }

    [Fact]
    public void WebUI_HasExpectedDefaults()
    {
        var config = new TorrentarrConfig();

        config.WebUI.Port.Should().Be(6969);
        config.WebUI.Theme.Should().Be("Dark");
        config.WebUI.LiveArr.Should().BeTrue();
    }

    [Fact]
    public void ArrInstances_IsEmptyByDefault()
    {
        var config = new TorrentarrConfig();

        config.ArrInstances.Should().BeEmpty();
    }

    [Fact]
    public void Arrs_ReturnsValuesFromArrInstances()
    {
        var config = new TorrentarrConfig();
        config.ArrInstances["Radarr-1"] = new ArrInstanceConfig { Type = "radarr" };
        config.ArrInstances["Sonarr-1"] = new ArrInstanceConfig { Type = "sonarr" };

        config.Arrs.Should().HaveCount(2);
    }

    [Fact]
    public void SeedingModeConfig_HasNoHnRFields_TrackerOnly()
    {
        // HnR settings were removed from SeedingModeConfig in v5.9.1
        // They now only exist in TrackerConfig and CategorySeedingConfig
        var seedingMode = new SeedingModeConfig();

        // Verify SeedingModeConfig has seeding limits but no HnR fields
        seedingMode.MaxUploadRatio.Should().Be(-1);
        seedingMode.MaxSeedingTime.Should().Be(-1);
        seedingMode.RemoveTorrent.Should().Be(-1);
        seedingMode.RemoveDeadTrackers.Should().BeFalse();
    }

    [Fact]
    public void CategorySeedingConfig_HasHnRFields()
    {
        var categorySeeding = new CategorySeedingConfig();

        categorySeeding.HitAndRunMode.Should().Be("disabled");
        categorySeeding.MinSeedRatio.Should().Be(1.0);
        categorySeeding.MinSeedingTimeDays.Should().Be(0);
        categorySeeding.HitAndRunMinimumDownloadPercent.Should().Be(10);
        categorySeeding.HitAndRunPartialSeedRatio.Should().Be(1.0);
        categorySeeding.TrackerUpdateBuffer.Should().Be(0);
    }

    [Fact]
    public void TrackerConfig_HasHnRFields()
    {
        var tracker = new TrackerConfig();

        tracker.HitAndRunMode.Should().BeNull(); // string? defaults to null
        tracker.MinSeedRatio.Should().BeNull();
        tracker.MinSeedingTimeDays.Should().BeNull();
        tracker.HitAndRunMinimumDownloadPercent.Should().BeNull();
        tracker.HitAndRunPartialSeedRatio.Should().BeNull();
        tracker.TrackerUpdateBuffer.Should().BeNull();
    }

    [Fact]
    public void TrackerConfig_CanSetHnRFields()
    {
        var tracker = new TrackerConfig
        {
            HitAndRunMode = "and",
            MinSeedRatio = 1.5,
            MinSeedingTimeDays = 3,
            HitAndRunMinimumDownloadPercent = 15,
            HitAndRunPartialSeedRatio = 2.0,
            TrackerUpdateBuffer = 300
        };

        tracker.HitAndRunMode.Should().Be("and");
        tracker.MinSeedRatio.Should().Be(1.5);
        tracker.MinSeedingTimeDays.Should().Be(3);
        tracker.HitAndRunMinimumDownloadPercent.Should().Be(15);
        tracker.HitAndRunPartialSeedRatio.Should().Be(2.0);
        tracker.TrackerUpdateBuffer.Should().Be(300);
    }
}

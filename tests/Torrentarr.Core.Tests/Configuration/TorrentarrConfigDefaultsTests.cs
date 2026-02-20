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

        config.Settings.ConfigVersion.Should().Be("5.9.0");
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
}

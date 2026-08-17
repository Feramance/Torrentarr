using FluentAssertions;
using Torrentarr.Core.Configuration;
using Torrentarr.Infrastructure.Services;
using Xunit;

namespace Torrentarr.Infrastructure.Tests.Services;

public class SeedingLimitMergeTests
{
    [Fact]
    public void TrackerUnset_KeepsPositiveCategorySeedingLimits()
    {
        var category = new CategorySeedingConfig
        {
            MaxSeedingTime = 86400,
            MaxUploadRatio = 2.0,
            DownloadRateLimitPerTorrent = 100
        };
        var tracker = new TrackerConfig
        {
            MaxSeedingTime = -1,
            MaxUploadRatio = -1
        };

        var merged = SeedingLimitMerge.Merge(category, arrSeedingMode: null, tracker);

        merged.MaxSeedingTime.Should().Be(86400);
        merged.MaxUploadRatio.Should().Be(2.0);
        merged.DownloadRateLimitPerTorrent.Should().Be(100);
    }

    [Fact]
    public void TrackerPositive_OverridesCategoryAndArr()
    {
        var category = new CategorySeedingConfig { MaxSeedingTime = 86400, MaxUploadRatio = 1.0 };
        var arr = new SeedingModeConfig { MaxSeedingTime = 43200, MaxUploadRatio = 1.5 };
        var tracker = new TrackerConfig { MaxSeedingTime = 3600, MaxUploadRatio = 3.0 };

        var merged = SeedingLimitMerge.Merge(category, arr, tracker);

        merged.MaxSeedingTime.Should().Be(3600);
        merged.MaxUploadRatio.Should().Be(3.0);
    }

    [Fact]
    public void ArrSeedingModePositive_OverlaysCategoryWhenTrackerUnset()
    {
        var category = new CategorySeedingConfig { MaxSeedingTime = -1, MaxUploadRatio = -1 };
        var arr = new SeedingModeConfig { MaxSeedingTime = 7200, MaxUploadRatio = 2.5 };
        var tracker = new TrackerConfig { MaxSeedingTime = -1 };

        var merged = SeedingLimitMerge.Merge(category, arr, tracker);

        merged.MaxSeedingTime.Should().Be(7200);
        merged.MaxUploadRatio.Should().Be(2.5);
    }

    [Fact]
    public void AllUnset_RemainsUnlimited()
    {
        var merged = SeedingLimitMerge.Merge(new CategorySeedingConfig(), null, new TrackerConfig());
        merged.MaxSeedingTime.Should().Be(-1);
        merged.MaxUploadRatio.Should().Be(-1);
    }

    [Theory]
    [InlineData(86400, null, 86400)]
    [InlineData(86400, -1, 86400)]
    [InlineData(86400, 0, 86400)]
    [InlineData(86400, 3600, 3600)]
    [InlineData(-1, -1, -1)]
    [InlineData(-1, null, -1)]
    public void MergeMaxEta_TrackerNegativeDoesNotWipeArr(int torrentEta, int? trackerEta, int expected)
    {
        SeedingLimitMerge.MergeMaxEta(torrentEta, trackerEta).Should().Be(expected);
    }
}

using FluentAssertions;
using Torrentarr.Core.Configuration;
using Xunit;

namespace Torrentarr.Core.Tests.Configuration;

public class ConfigValidationHelperTests
{
    [Fact]
    public void ValidateArrCategoryPaths_RejectsOverlappingCategories()
    {
        var config = new TorrentarrConfig
        {
            ArrInstances = new Dictionary<string, ArrInstanceConfig>
            {
                ["Radarr"] = new() { Category = "radarr", Type = "radarr" },
                ["Radarr-4K"] = new() { Category = "radarr/4k", Type = "radarr" }
            }
        };

        var (ok, error) = ConfigValidationHelper.ValidateArrCategoryPaths(config);
        ok.Should().BeFalse();
        error.Should().Contain("Overlapping");
    }

    [Fact]
    public void ValidateAll_AcceptsDistinctCategories()
    {
        var config = new TorrentarrConfig
        {
            ArrInstances = new Dictionary<string, ArrInstanceConfig>
            {
                ["Radarr"] = new() { Category = "radarr", Type = "radarr" },
                ["Sonarr"] = new() { Category = "sonarr", Type = "sonarr" }
            },
            QBitInstances = new Dictionary<string, QBitConfig>
            {
                ["qBit"] = new() { ManagedCategories = new List<string> { "seed" } }
            }
        };

        ConfigValidationHelper.ValidateAll(config).Ok.Should().BeTrue();
    }

    [Fact]
    public void ValidateManagedCategoryPaths_RejectsOverlap()
    {
        var config = new TorrentarrConfig
        {
            QBitInstances = new Dictionary<string, QBitConfig>
            {
                ["qBit"] = new() { ManagedCategories = new List<string> { "seed", "seed/tleech" } }
            }
        };

        var (ok, error) = ConfigValidationHelper.ValidateManagedCategoryPaths(config);
        ok.Should().BeFalse();
        error.Should().Contain("Overlapping qBit ManagedCategories");
    }

    [Fact]
    public void ValidateArrManagedCategoryOverlap_RejectsQBitVsArr()
    {
        var config = new TorrentarrConfig
        {
            ArrInstances = new Dictionary<string, ArrInstanceConfig>
            {
                ["Radarr"] = new() { Category = "radarr", Type = "radarr" }
            },
            QBitInstances = new Dictionary<string, QBitConfig>
            {
                ["qBit"] = new() { ManagedCategories = new List<string> { "radarr/imports" } }
            }
        };

        var (ok, error) = ConfigValidationHelper.ValidateArrManagedCategoryOverlap(config);
        ok.Should().BeFalse();
        error.Should().Contain("overlaps Arr category");
    }

    [Fact]
    public void ValidateArrManagedCategoryOverlap_RejectsArrVsQBit()
    {
        var config = new TorrentarrConfig
        {
            ArrInstances = new Dictionary<string, ArrInstanceConfig>
            {
                ["Radarr"] = new() { Category = "radarr/4k", Type = "radarr" }
            },
            QBitInstances = new Dictionary<string, QBitConfig>
            {
                ["qBit"] = new() { ManagedCategories = new List<string> { "radarr" } }
            }
        };

        var (ok, error) = ConfigValidationHelper.ValidateArrManagedCategoryOverlap(config);
        ok.Should().BeFalse();
        error.Should().Contain("overlaps qBit ManagedCategory");
    }

    [Fact]
    public void ValidateAll_RejectsOnFirstFailure()
    {
        var config = new TorrentarrConfig
        {
            ArrInstances = new Dictionary<string, ArrInstanceConfig>
            {
                ["Radarr"] = new() { Category = "radarr", Type = "radarr" },
                ["Radarr-4K"] = new() { Category = "radarr/4k", Type = "radarr" }
            },
            QBitInstances = new Dictionary<string, QBitConfig>
            {
                ["qBit"] = new() { ManagedCategories = new List<string> { "seed", "seed/tleech" } }
            }
        };

        var (ok, error) = ConfigValidationHelper.ValidateAll(config);
        ok.Should().BeFalse();
        error.Should().Contain("Overlapping Arr categories");
    }
}

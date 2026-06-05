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
}

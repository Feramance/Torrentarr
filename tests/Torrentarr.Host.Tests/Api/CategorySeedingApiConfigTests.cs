using FluentAssertions;
using Torrentarr.Core.Configuration;
using Torrentarr.Core.Models;
using Xunit;

namespace Torrentarr.Host.Tests.Api;

public class CategorySeedingApiConfigTests
{
    [Fact]
    public void ResolveAggregateSeedingConfig_ReturnsNullForMixedChildPolicies()
    {
        var config = new TorrentarrConfig();
        var qbitConfig = new QBitConfig
        {
            MatchSubcategories = true,
            CategorySeeding = new CategorySeedingConfig
            {
                MaxUploadRatio = 1.0,
                Categories =
                [
                    new CategorySeedingCategoryOverride
                    {
                        Name = "tv/4k",
                        MaxUploadRatio = 4.0
                    }
                ]
            }
        };
        var torrents = new List<TorrentInfo>
        {
            new() { Category = "tv" },
            new() { Category = "tv/4k" }
        };

        var result = Program.ResolveAggregateSeedingConfig(config, qbitConfig, "tv", torrents);

        result.Should().BeNull();
    }

    [Fact]
    public void ResolveAggregateSeedingConfig_ReturnsActualChildPolicyForUniformAggregate()
    {
        var config = new TorrentarrConfig();
        var qbitConfig = new QBitConfig
        {
            MatchSubcategories = true,
            CategorySeeding = new CategorySeedingConfig
            {
                MaxUploadRatio = 1.0,
                Categories =
                [
                    new CategorySeedingCategoryOverride
                    {
                        Name = "tv/4k",
                        MaxUploadRatio = 4.0
                    }
                ]
            }
        };
        var torrents = new List<TorrentInfo> { new() { Category = "tv/4k" } };

        var result = Program.ResolveAggregateSeedingConfig(config, qbitConfig, "tv", torrents);

        result.Should().NotBeNull();
        result!.MaxRatio.Should().Be(4.0);
    }
}

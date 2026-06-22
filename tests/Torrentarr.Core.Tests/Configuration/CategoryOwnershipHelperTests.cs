using FluentAssertions;
using Torrentarr.Core.Configuration;
using Xunit;

namespace Torrentarr.Core.Tests.Configuration;

public class CategoryOwnershipHelperTests
{
    [Fact]
    public void GetQBitOnlyManagedCategories_ExcludesArrCategories()
    {
        var cfg = new TorrentarrConfig();
        cfg.QBitInstances["qBit"] = new QBitConfig
        {
            ManagedCategories = ["seed", "manual", "radarr"]
        };
        cfg.ArrInstances["Radarr"] = new ArrInstanceConfig { Category = "radarr" };

        var only = CategoryOwnershipHelper.GetQBitOnlyManagedCategories(cfg);
        only.Should().BeEquivalentTo(["seed", "manual"]);
    }

    [Fact]
    public void ResolveOwningCategory_ExactMatch_Wins()
    {
        var cfg = new TorrentarrConfig();
        cfg.ArrInstances["Sonarr"] = new ArrInstanceConfig { Category = "sonarr" };

        CategoryOwnershipHelper.ResolveOwningCategory(cfg, "sonarr")
            .Should().Be("sonarr");
    }

    [Fact]
    public void ResolveOwningCategory_PrefixMatch_WhenMatchSubcategoriesEnabled()
    {
        var cfg = new TorrentarrConfig();
        cfg.QBitInstances["qBit"] = new QBitConfig
        {
            MatchSubcategories = true,
            ManagedCategories = ["seed"]
        };

        CategoryOwnershipHelper.ResolveOwningCategory(cfg, "seed/tleech", "qBit")
            .Should().Be("seed");
    }

    [Fact]
    public void ResolveOwningCategory_PrefixMatch_Disabled_ReturnsNull()
    {
        var cfg = new TorrentarrConfig();
        cfg.QBitInstances["qBit"] = new QBitConfig
        {
            MatchSubcategories = false,
            ManagedCategories = ["seed"]
        };

        CategoryOwnershipHelper.ResolveOwningCategory(cfg, "seed/tleech", "qBit")
            .Should().BeNull();
    }

    [Fact]
    public void ArrMatchSubcategoriesEffective_UsesPerArrOverride()
    {
        var cfg = new TorrentarrConfig();
        cfg.QBitInstances["qBit"] = new QBitConfig { MatchSubcategories = false };
        cfg.ArrInstances["Sonarr"] = new ArrInstanceConfig
        {
            Category = "sonarr",
            MatchSubcategories = true
        };

        CategoryOwnershipHelper.ArrMatchSubcategoriesEffective(cfg, "Sonarr", "qBit")
            .Should().BeTrue();
    }
}

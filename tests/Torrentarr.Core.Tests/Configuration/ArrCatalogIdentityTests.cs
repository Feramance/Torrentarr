using FluentAssertions;
using Torrentarr.Core.Configuration;
using Xunit;

namespace Torrentarr.Core.Tests.Configuration;

public class ArrCatalogIdentityTests
{
    [Fact]
    public void QueryKeys_IncludesSectionNameAndCategory()
    {
        var config = new TorrentarrConfig
        {
            ArrInstances =
            {
                ["Readarr-Books"] = new ArrInstanceConfig
                {
                    Type = "readarr",
                    Category = "readarr-books"
                }
            }
        };

        var fromCategory = ArrCatalogIdentity.QueryKeys(config, "readarr-books");
        fromCategory.Should().Contain("Readarr-Books");
        fromCategory.Should().Contain("readarr-books");

        var fromName = ArrCatalogIdentity.QueryKeys(config, "Readarr-Books");
        fromName.Should().Contain("Readarr-Books");
        fromName.Should().Contain("readarr-books");
    }

    [Fact]
    public void QueryKeys_FallsBackToSlugWhenUnknown()
    {
        var config = new TorrentarrConfig();
        ArrCatalogIdentity.QueryKeys(config, "lidarr").Should().Equal("lidarr");
    }

    [Fact]
    public void QueryKeys_DoesNotCrossInstances()
    {
        var config = new TorrentarrConfig
        {
            ArrInstances =
            {
                ["Readarr-Books"] = new ArrInstanceConfig { Category = "readarr-books" },
                ["Lidarr-Music"] = new ArrInstanceConfig { Category = "lidarr-music" }
            }
        };

        var keys = ArrCatalogIdentity.QueryKeys(config, "readarr-books");
        keys.Should().NotContain("Lidarr-Music");
        keys.Should().NotContain("lidarr-music");
    }
}

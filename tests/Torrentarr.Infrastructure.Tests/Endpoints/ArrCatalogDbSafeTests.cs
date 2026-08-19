using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Torrentarr.Infrastructure.Database;
using Torrentarr.Infrastructure.Endpoints;
using Xunit;

namespace Torrentarr.Infrastructure.Tests.Endpoints;

public class ArrCatalogDbSafeTests
{
    [Theory]
    [InlineData("/web/radarr/movies/movies", true)]
    [InlineData("/api/sonarr/tv/series", true)]
    [InlineData("/web/readarr/books/authors", true)]
    [InlineData("/web/config", false)]
    public void IsCatalogPath_MatchesArrRoutes(string path, bool expected)
    {
        ArrCatalogDbSafe.IsCatalogPath(new PathString(path)).Should().Be(expected);
    }

    [Fact]
    public void IsSqliteCorruption_DetectsMalformedDiskImage()
    {
        DatabaseRetryExtensions.IsSqliteCorruption(new InvalidOperationException("database disk image is malformed"))
            .Should().BeTrue();
        DatabaseRetryExtensions.IsSqliteCorruption(new InvalidOperationException("locked"))
            .Should().BeFalse();
    }
}

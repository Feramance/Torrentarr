using FluentAssertions;
using Torrentarr.Core.Configuration;
using Torrentarr.Infrastructure.Services;
using Xunit;

namespace Torrentarr.Infrastructure.Tests.Services;

public class SearchYearCursorTests
{
    [Fact]
    public void ShouldFilter_FalseForLidarr()
    {
        var cfg = new ArrInstanceConfig { Type = "lidarr", Search = new SearchConfig { SearchByYear = true } };
        SearchYearCursor.ShouldFilter(cfg).Should().BeFalse();
    }

    [Fact]
    public void ShouldFilter_TrueForRadarrWhenEnabled()
    {
        var cfg = new ArrInstanceConfig { Type = "radarr", Search = new SearchConfig { SearchByYear = true } };
        SearchYearCursor.ShouldFilter(cfg).Should().BeTrue();
    }

    [Fact]
    public void CurrentYear_WalksYearsInOrder_ThenAdvanceWraps()
    {
        var cursor = new SearchYearCursor();
        var cfg = new ArrInstanceConfig { Type = "radarr", Search = new SearchConfig { SearchByYear = true } };
        var years = new[] { 2020, 2010, 2015 };

        cursor.CurrentYear("Radarr", cfg, years).Should().Be(2010);
        cursor.Advance("Radarr").Should().BeTrue();
        cursor.CurrentYear("Radarr", cfg, years).Should().Be(2015);
        cursor.Advance("Radarr").Should().BeTrue();
        cursor.CurrentYear("Radarr", cfg, years).Should().Be(2020);
        cursor.Advance("Radarr").Should().BeFalse("wrapping the last year completes the loop");
        cursor.CurrentYear("Radarr", cfg, years).Should().Be(2010);
    }

    [Fact]
    public void OrderYears_SearchInReverse_IsDescending()
    {
        SearchYearCursor.OrderYears([2010, 2020], searchInReverse: true)
            .Should().Equal(2020, 2010);
    }
}

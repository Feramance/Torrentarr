using FluentAssertions;
using Torrentarr.Core.Configuration;
using Xunit;

namespace Torrentarr.Core.Tests.Configuration;

public class CategoryPathHelperTests
{
    [Theory]
    [InlineData("seed/tleech", "seed/tleech")]
    [InlineData("/seed/tleech/", "seed/tleech")]
    [InlineData("seed//tleech", "seed/tleech")]
    public void NormalizeCategory_CollapsesSeparators(string input, string expected)
    {
        CategoryPathHelper.NormalizeCategory(input).Should().Be(expected);
    }

    [Fact]
    public void IsSubcategoryOf_TrueForDescendant()
    {
        CategoryPathHelper.IsSubcategoryOf("seed/tleech", "seed").Should().BeTrue();
        CategoryPathHelper.IsSubcategoryOf("seed", "seed").Should().BeFalse();
    }

    [Fact]
    public void MatchesConfigured_PrefixReturnsLongestAncestor()
    {
        var configured = new[] { "seed", "seed/tleech" };
        CategoryPathHelper.MatchesConfigured("seed/tleech/foo", configured, prefix: true)
            .Should().Be("seed/tleech");
    }

    [Fact]
    public void FindOverlapConflicts_DetectsParentChild()
    {
        var conflicts = CategoryPathHelper.FindOverlapConflicts(new[] { "radarr", "radarr/4k" });
        conflicts.Should().Contain(("radarr", "radarr/4k"));
    }
}

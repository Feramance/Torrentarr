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

    [Theory]
    [InlineData("seed/tleech", new[] { "seed", "tleech" })]
    [InlineData("", new string[0])]
    public void SplitCategory_ReturnsSegments(string input, string[] expected)
    {
        CategoryPathHelper.SplitCategory(input).Should().Equal(expected);
    }

    [Fact]
    public void CategoryParents_ReturnsAncestors()
    {
        CategoryPathHelper.CategoryParents("a/b/c")
            .Should().Equal("a", "a/b");
    }

    [Theory]
    [InlineData("seed/tleech", true)]
    [InlineData("seed", false)]
    [InlineData(null, false)]
    public void HasSubcategorySeparator_DetectsSlash(object? value, bool expected)
    {
        CategoryPathHelper.HasSubcategorySeparator(value).Should().Be(expected);
    }

    [Theory]
    [InlineData("seed/tleech", "seed/tleech/", true)]
    [InlineData("seed", "SEED", false)]
    [InlineData("seed", "sonarr", false)]
    public void CategoryEquals_NormalizesBeforeCompare(string a, string b, bool expected)
    {
        CategoryPathHelper.CategoryEquals(a, b).Should().Be(expected);
    }

    [Fact]
    public void MatchesConfigured_ExactMatch_WhenPrefixFalse()
    {
        var configured = new[] { "radarr", "sonarr" };
        CategoryPathHelper.MatchesConfigured("radarr", configured, prefix: false)
            .Should().Be("radarr");
        CategoryPathHelper.MatchesConfigured("radarr/4k", configured, prefix: false)
            .Should().BeNull();
    }
}

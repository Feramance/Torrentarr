using FluentAssertions;
using Torrentarr.Core.Configuration;
using Xunit;

namespace Torrentarr.Core.Tests.Configuration;

public class UrlBaseHelperTests
{
    [Theory]
    [InlineData(null, "")]
    [InlineData("", "")]
    [InlineData("   ", "")]
    [InlineData("qbitrr", "/qbitrr")]
    [InlineData("/qbitrr/", "/qbitrr")]
    [InlineData(" torrentarr ", "/torrentarr")]
    [InlineData("/nested/path", "/nested/path")]
    public void NormalizeUrlBase_ReturnsExpected(object? input, string expected)
    {
        UrlBaseHelper.NormalizeUrlBase(input).Should().Be(expected);
    }

    [Fact]
    public void NormalizeUrlBase_AcceptsNonStringObject()
    {
        UrlBaseHelper.NormalizeUrlBase(42).Should().Be("/42");
    }

    [Theory]
    [InlineData("", "/login", "/login")]
    [InlineData("/torrentarr", "/login", "/torrentarr/login")]
    [InlineData("/torrentarr", "login", "/torrentarr/login")]
    public void WithUrlBase_PrefixesConfiguredBase(string urlBase, string path, string expected)
    {
        UrlBaseHelper.WithUrlBase(urlBase, path).Should().Be(expected);
    }

    [Fact]
    public void WithUrlBase_HandlesRequestStylePathBase()
    {
        UrlBaseHelper.WithUrlBase("/torrentarr", "/login").Should().Be("/torrentarr/login");
    }
}

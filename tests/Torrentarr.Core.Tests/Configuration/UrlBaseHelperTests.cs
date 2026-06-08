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
}

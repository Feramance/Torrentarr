using FluentAssertions;
using Torrentarr.Infrastructure.Services;
using Xunit;

namespace Torrentarr.Infrastructure.Tests.Services;

public class WebUIAuthHelpersTests
{
    [Fact]
    public void TokenEquals_SameToken_ReturnsTrue()
    {
        WebUIAuthHelpers.TokenEquals("secret", "secret").Should().BeTrue();
    }

    [Fact]
    public void TokenEquals_DifferentTokens_ReturnsFalse()
    {
        WebUIAuthHelpers.TokenEquals("secret", "other").Should().BeFalse();
    }

    [Fact]
    public void TokenEquals_NullAndEmpty_ReturnsTrue()
    {
        WebUIAuthHelpers.TokenEquals(null, "").Should().BeTrue();
    }

    [Fact]
    public void TokenEquals_NullAndNonNull_ReturnsFalse()
    {
        WebUIAuthHelpers.TokenEquals(null, "x").Should().BeFalse();
    }

    [Theory]
    [InlineData("", "GET", true)]
    [InlineData("/health", "GET", true)]
    [InlineData("/login", "GET", true)]
    [InlineData("/web/meta", "GET", true)]
    [InlineData("/web/login", "POST", true)]
    [InlineData("/web/logout", "GET", true)]
    [InlineData("/web/logout", "POST", true)]
    [InlineData("/web/auth/set-password", "POST", true)]
    [InlineData("/web/auth/oidc/challenge", "GET", true)]
    [InlineData("/web/auth/oidc/challenge", "POST", false)]
    [InlineData("/signin-oidc", "GET", true)]
    [InlineData("/signin-oidc", "POST", false)]
    [InlineData("/ui", "GET", false)]
    [InlineData("/web/token", "GET", false)]
    [InlineData("/web/config", "GET", false)]
    public void IsPublicPath_ReturnsExpected(string path, string method, bool expected)
    {
        WebUIAuthHelpers.IsPublicPath(path, method).Should().Be(expected);
    }
}

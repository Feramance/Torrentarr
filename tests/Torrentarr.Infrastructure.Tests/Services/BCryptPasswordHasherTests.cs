using FluentAssertions;
using Torrentarr.Infrastructure.Services;
using Xunit;

namespace Torrentarr.Infrastructure.Tests.Services;

public class BCryptPasswordHasherTests
{
    private readonly BCryptPasswordHasher _hasher = new();

    [Fact]
    public void HashPassword_ReturnsNonEmptyString()
    {
        var hash = _hasher.HashPassword("test-password");
        hash.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public void HashPassword_DoesNotContainPlainPassword()
    {
        const string password = "secret123";
        var hash = _hasher.HashPassword(password);
        hash.Should().NotContain(password);
    }

    [Fact]
    public void HashPassword_ProducesValidHashFormat()
    {
        var hash = _hasher.HashPassword("test");
        hash.Should().StartWith("$2");
    }

    [Fact]
    public void VerifyPassword_WithCorrectPassword_ReturnsTrue()
    {
        const string password = "correct-password";
        var hash = _hasher.HashPassword(password);
        _hasher.VerifyPassword(password, hash).Should().BeTrue();
    }

    [Fact]
    public void VerifyPassword_WithWrongPassword_ReturnsFalse()
    {
        var hash = _hasher.HashPassword("correct-password");
        _hasher.VerifyPassword("wrong-password", hash).Should().BeFalse();
    }

    [Fact]
    public void VerifyPassword_WithNullPassword_ReturnsFalse()
    {
        var hash = _hasher.HashPassword("any");
        _hasher.VerifyPassword(null!, hash).Should().BeFalse();
    }

    [Fact]
    public void VerifyPassword_WithNullHash_ReturnsFalse()
    {
        _hasher.VerifyPassword("any", null!).Should().BeFalse();
    }

    [Fact]
    public void HashPassword_WithEmptyPassword_Throws()
    {
        var act = () => _hasher.HashPassword("");
        act.Should().Throw<ArgumentException>();
    }
}

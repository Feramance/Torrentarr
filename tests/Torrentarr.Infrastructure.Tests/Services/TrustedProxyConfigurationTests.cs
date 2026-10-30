using System.Net;
using FluentAssertions;
using Torrentarr.Infrastructure.Services;
using Xunit;

namespace Torrentarr.Infrastructure.Tests.Services;

public sealed class TrustedProxyConfigurationTests
{
    [Fact]
    public void EmptyConfiguration_DoesNotTrustAnyAdditionalHosts()
    {
        TrustedProxyConfiguration.Parse(null).Should().BeEmpty();
    }

    [Fact]
    public void ExplicitAddresses_AreParsedAndDeduplicated()
    {
        var proxies = TrustedProxyConfiguration.Parse("192.168.0.240, 2001:db8::1, 192.168.0.240");
        proxies.Should().HaveCount(2);
        proxies.Should().Contain(IPAddress.Parse("192.168.0.240"));
        proxies.Should().Contain(IPAddress.Parse("2001:db8::1"));
    }

    [Theory]
    [InlineData("192.168.0.0/24")]
    [InlineData("any")]
    public void InvalidOrBroadTrustSettings_AreRejected(string value)
    {
        var action = () => TrustedProxyConfiguration.Parse(value);
        action.Should().Throw<ArgumentException>();
    }
}

using FluentAssertions;
using Torrentarr.Infrastructure.Http;
using Xunit;

namespace Torrentarr.Infrastructure.Tests.Http;

public class TlsSkipHelperTests
{
    [Fact]
    public void CreateRestOptions_WhenSkipFalse_DoesNotInstallCallback()
    {
        var options = TlsSkipHelper.CreateRestOptions("https://arr.local", skipTlsVerify: false);
        TlsSkipHelper.HasSkipCallback(options).Should().BeFalse();
    }

    [Fact]
    public void CreateRestOptions_WhenSkipTrue_AcceptsAnyCertificate()
    {
        var options = TlsSkipHelper.CreateRestOptions("https://arr.local", skipTlsVerify: true);
        TlsSkipHelper.HasSkipCallback(options).Should().BeTrue();
        options.RemoteCertificateValidationCallback!(null!, null, null, default).Should().BeTrue();
    }
}

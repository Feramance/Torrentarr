using FluentAssertions;
using Torrentarr.Infrastructure.ApiClients.QBittorrent;
using Xunit;

namespace Torrentarr.Infrastructure.Tests.ApiClients;

public class QBittorrentLoginParserTests
{
    [Fact]
    public void TryAccept_LegacyOkSid_Succeeds()
    {
        var cookies = new[] { new QBittorrentLoginCookie("SID", "abc123") };

        var ok = QBittorrentLoginParser.TryAccept(200, "Ok.", cookies, out var header, out var reason);

        ok.Should().BeTrue();
        header.Should().Be("SID=abc123");
        reason.Should().BeEmpty();
    }

    [Fact]
    public void TryAccept_QBit52NoContentQbtSid_Succeeds()
    {
        var cookies = new[] { new QBittorrentLoginCookie("QBT_SID_8080", "session-token") };

        var ok = QBittorrentLoginParser.TryAccept(204, "", cookies, out var header, out _);

        ok.Should().BeTrue();
        header.Should().Be("QBT_SID_8080=session-token");
    }

    [Fact]
    public void TryAccept_FailsBody_IsRejectedEvenOn200()
    {
        var cookies = new[] { new QBittorrentLoginCookie("SID", "abc123") };

        var ok = QBittorrentLoginParser.TryAccept(200, "Fails.", cookies, out var header, out var reason);

        ok.Should().BeFalse();
        header.Should().BeNull();
        reason.Should().Contain("Fails.");
    }

    [Fact]
    public void TryAccept_Unauthorized_IsRejected()
    {
        var ok = QBittorrentLoginParser.TryAccept(401, "", Array.Empty<QBittorrentLoginCookie>(), out _, out var reason);

        ok.Should().BeFalse();
        reason.Should().Contain("401");
    }

    [Fact]
    public void TryAccept_SuccessWithoutCookie_IsRejected()
    {
        var ok = QBittorrentLoginParser.TryAccept(204, null, Array.Empty<QBittorrentLoginCookie>(), out _, out var reason);

        ok.Should().BeFalse();
        reason.Should().Contain("no Set-Cookie");
    }

    [Fact]
    public void SelectSessionCookie_PrefersSidOverOtherCookies()
    {
        var cookies = new[]
        {
            new QBittorrentLoginCookie("other", "x"),
            new QBittorrentLoginCookie("SID", "session"),
        };

        var selected = QBittorrentLoginParser.SelectSessionCookie(cookies);

        selected.Should().NotBeNull();
        selected!.Value.Name.Should().Be("SID");
        selected.Value.Value.Should().Be("session");
    }
}

using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using System.Net.Sockets;
using Torrentarr.Core.Configuration;
using Torrentarr.Infrastructure.Services;
using Xunit;

namespace Torrentarr.Infrastructure.Tests.Services;

/// <summary>
/// Tests for ConnectivityService.
/// Network probes are injected so CI never makes real HTTP/TCP calls.
/// </summary>
public class ConnectivityServiceTests
{
    private static ConnectivityService CreateService(
        TorrentarrConfig? config = null,
        Func<string, CancellationToken, Task<bool>>? probe = null)
    {
        var manager = new QBittorrentConnectionManager(
            NullLogger<QBittorrentConnectionManager>.Instance);
        return new ConnectivityService(
            NullLogger<ConnectivityService>.Instance,
            manager,
            config ?? new TorrentarrConfig(),
            probe);
    }

    [Fact]
    public void InitialState_IsConnected_IsTrue()
    {
        var svc = CreateService();
        svc.IsConnected.Should().BeTrue("default assumption is that we are online");
    }

    [Fact]
    public void InitialState_LastChecked_IsNull()
    {
        var svc = CreateService();
        svc.LastChecked.Should().BeNull("no check has been performed yet");
    }

    [Fact]
    public async Task IsQBittorrentReachableAsync_NoClientsRegistered_ReturnsFalse()
    {
        var svc = CreateService();

        var result = await svc.IsQBittorrentReachableAsync();

        result.Should().BeFalse();
    }

    [Fact]
    public async Task IsQBittorrentReachableAsync_NoClientsRegistered_DoesNotThrow()
    {
        var svc = CreateService();

        var act = async () => await svc.IsQBittorrentReachableAsync();

        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task IsQBittorrentReachableAsync_CalledWithCancellationToken_DoesNotThrow()
    {
        var svc = CreateService();
        using var cts = new CancellationTokenSource();

        var act = async () => await svc.IsQBittorrentReachableAsync(cts.Token);

        await act.Should().NotThrowAsync();
    }

    [Fact]
    public void GetPingUrls_UsesSettingsPingURLS()
    {
        var config = new TorrentarrConfig
        {
            Settings = { PingURLS = ["one.one.one.one", "dns.google.com"] }
        };

        var svc = CreateService(config);

        svc.GetPingUrls().Should().BeEquivalentTo(new[] { "one.one.one.one", "dns.google.com" });
    }

    [Fact]
    public async Task IsConnectedAsync_HttpProbeSuccess_DoesNotRequireQBit()
    {
        var probed = new List<string>();
        var config = new TorrentarrConfig
        {
            Settings = { PingURLS = ["one.one.one.one"] }
        };
        var svc = CreateService(config, async (host, _) =>
        {
            probed.Add(host);
            await Task.CompletedTask;
            return host == "one.one.one.one";
        });

        var result = await svc.IsConnectedAsync();

        result.Should().BeTrue();
        probed.Should().Contain("one.one.one.one");
        svc.IsConnected.Should().BeTrue();
    }

    [Fact]
    public async Task IsConnectedAsync_AllProbesFail_ReturnsFalse()
    {
        var config = new TorrentarrConfig
        {
            Settings = { PingURLS = ["example.invalid"] }
        };
        var svc = CreateService(config, (_, _) => Task.FromResult(false));

        var result = await svc.IsConnectedAsync();

        result.Should().BeFalse();
        svc.IsConnected.Should().BeFalse();
    }

    [Fact]
    public void ToProbeAuthority_BracketsIPv6Literal()
    {
        ConnectivityService.ToProbeAuthority("2001:4860:4860::8888", AddressFamily.InterNetworkV6)
            .Should().Be("[2001:4860:4860::8888]");
    }

    [Fact]
    public void ToProbeAuthority_LeavesIPv4Unbracketed()
    {
        ConnectivityService.ToProbeAuthority("1.1.1.1", AddressFamily.InterNetwork)
            .Should().Be("1.1.1.1");
    }
}

using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Torrentarr.Infrastructure.Services;
using Xunit;

namespace Torrentarr.Infrastructure.Tests.Services;

/// <summary>
/// Tests for ConnectivityService.
/// Only covers the no-network paths — initial state and the fast-return branch
/// when no qBittorrent clients are registered (no actual network calls needed).
/// </summary>
public class ConnectivityServiceTests
{
    private static ConnectivityService CreateService()
    {
        var manager = new QBittorrentConnectionManager(
            NullLogger<QBittorrentConnectionManager>.Instance);
        return new ConnectivityService(
            NullLogger<ConnectivityService>.Instance, manager);
    }

    // ── Initial state ─────────────────────────────────────────────────────────

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

    // ── IsQBittorrentReachableAsync with no clients ───────────────────────────

    [Fact]
    public async Task IsQBittorrentReachableAsync_NoClientsRegistered_ReturnsFalse()
    {
        // With an empty QBittorrentConnectionManager there are no clients to test,
        // so the method should return false immediately (no network call made).
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
}

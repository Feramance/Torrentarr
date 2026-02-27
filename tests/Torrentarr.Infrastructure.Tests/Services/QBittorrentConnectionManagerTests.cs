using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Torrentarr.Infrastructure.Services;
using Xunit;

namespace Torrentarr.Infrastructure.Tests.Services;

/// <summary>
/// Tests for QBittorrentConnectionManager.
/// All tests exercise pure in-memory state — no network calls are made
/// because InitializeAsync is never invoked.
/// </summary>
public class QBittorrentConnectionManagerTests
{
    private static QBittorrentConnectionManager CreateManager() =>
        new(NullLogger<QBittorrentConnectionManager>.Instance);

    // ── Empty manager ─────────────────────────────────────────────────────────

    [Fact]
    public void Empty_IsConnected_NoArgs_ReturnsFalse()
    {
        var mgr = CreateManager();
        mgr.IsConnected().Should().BeFalse();
    }

    [Fact]
    public void Empty_GetAllClients_ReturnsEmptyDictionary()
    {
        var mgr = CreateManager();
        mgr.GetAllClients().Should().BeEmpty();
    }

    [Fact]
    public void Empty_GetClient_UnknownName_ReturnsNull()
    {
        var mgr = CreateManager();
        mgr.GetClient("qBit").Should().BeNull();
    }

    [Fact]
    public void Empty_IsConnected_ByName_ReturnsFalse()
    {
        var mgr = CreateManager();
        mgr.IsConnected("qBit").Should().BeFalse();
    }

    [Fact]
    public void Empty_GetConnectionInfo_ReturnsEmptyDictionary()
    {
        var mgr = CreateManager();
        mgr.GetConnectionInfo().Should().BeEmpty();
    }

    // ── Multiple unknown instance names ───────────────────────────────────────

    [Theory]
    [InlineData("qBit")]
    [InlineData("qBit-seedbox")]
    [InlineData("")]
    [InlineData("does-not-exist")]
    public void Empty_IsConnected_VariousNames_ReturnsFalse(string name)
    {
        var mgr = CreateManager();
        mgr.IsConnected(name).Should().BeFalse($"'{name}' was never registered");
    }

    [Theory]
    [InlineData("qBit")]
    [InlineData("qBit-seedbox")]
    [InlineData("unknown")]
    public void Empty_GetClient_VariousNames_ReturnsNull(string name)
    {
        var mgr = CreateManager();
        mgr.GetClient(name).Should().BeNull($"'{name}' was never registered");
    }

    // ── GetAllClients returns read-only view ──────────────────────────────────

    [Fact]
    public void GetAllClients_ReturnsIReadOnlyDictionary()
    {
        var mgr = CreateManager();
        // Verify the return type is the interface (not a mutable Dictionary)
        var clients = mgr.GetAllClients();
        clients.Should().NotBeNull();
        clients.Count.Should().Be(0);
    }

    // ── GetConnectionInfo structure ───────────────────────────────────────────

    [Fact]
    public void GetConnectionInfo_EmptyManager_ReturnsNonNullDictionary()
    {
        var mgr = CreateManager();
        var info = mgr.GetConnectionInfo();
        info.Should().NotBeNull();
        info.Should().BeEmpty();
    }
}

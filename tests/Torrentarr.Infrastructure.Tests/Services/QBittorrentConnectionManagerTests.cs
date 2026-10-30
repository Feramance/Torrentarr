using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Torrentarr.Core.Configuration;
using Torrentarr.Core.Interfaces;
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
    [Fact]
    public async Task EmptyVersionResponse_PreventsInitialActivationAndRegisteredSessionRecovery()
    {
        var client = new Mock<ITorrentClient>();
        client.Setup(c => c.LoginAsync(It.IsAny<CancellationToken>())).ReturnsAsync(true);
        client.SetupSequence(c => c.GetVersionAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync("").ReturnsAsync("5.2").ReturnsAsync("");
        var factory = new Mock<ITorrentClientFactory>();
        factory.SetupGet(f => f.Type).Returns("fake");
        factory.Setup(f => f.Create(It.IsAny<string>(), It.IsAny<TorrentClientInstanceConfig>())).Returns(client.Object);
        var manager = new QBittorrentConnectionManager(NullLogger<QBittorrentConnectionManager>.Instance, [factory.Object]);
        var instance = new TorrentClientInstanceConfig { Type = "fake", Host = "fixture" };
        (await manager.InitializeAsync("client", instance)).Should().BeFalse();
        manager.IsConnected("client").Should().BeFalse();
        (await manager.InitializeAsync("client", instance)).Should().BeTrue();
        (await manager.InitializeAsync("client", instance)).Should().BeFalse();
        manager.IsConnected("client").Should().BeFalse();
    }

    [Fact]
    public async Task RejectedCredentials_AreSuppressedUntilConfigurationRevisionChanges()
    {
        var config = new TorrentarrConfig();
        config.Apply(new TorrentarrConfig(), 1);
        var client = new Mock<ITorrentClient>();
        client.SetupGet(c => c.LastConnectionFailure).Returns("qBittorrent rejected credentials (Fails.)");
        client.SetupGet(c => c.LastConnectionFailureKind).Returns(TorrentClientConnectionFailureKind.AuthenticationRejected);
        client.Setup(c => c.LoginAsync(It.IsAny<CancellationToken>())).ReturnsAsync(false);
        var factory = new Mock<ITorrentClientFactory>();
        factory.SetupGet(f => f.Type).Returns("fake");
        factory.Setup(f => f.Create(It.IsAny<string>(), It.IsAny<TorrentClientInstanceConfig>())).Returns(client.Object);
        var manager = new QBittorrentConnectionManager(NullLogger<QBittorrentConnectionManager>.Instance, [factory.Object], config);
        var instance = new TorrentClientInstanceConfig { Type = "fake", Host = "fixture" };

        (await manager.InitializeAsync("client", instance)).Should().BeFalse();
        (await manager.InitializeAsync("client", instance)).Should().BeFalse();
        client.Verify(c => c.LoginAsync(It.IsAny<CancellationToken>()), Times.Once);

        config.Apply(new TorrentarrConfig(), 2);
        (await manager.InitializeAsync("client", instance)).Should().BeFalse();
        client.Verify(c => c.LoginAsync(It.IsAny<CancellationToken>()), Times.Exactly(2));
    }

    [Fact]
    public async Task WorkerReconnectionNotification_InvalidatesOnlyTargetedAuthenticationRejection()
    {
        var first = new Mock<ITorrentClient>();
        var second = new Mock<ITorrentClient>();
        foreach (var client in new[] { first, second })
        {
            client.SetupGet(c => c.LastConnectionFailureKind).Returns(TorrentClientConnectionFailureKind.AuthenticationRejected);
            client.Setup(c => c.LoginAsync(It.IsAny<CancellationToken>())).ReturnsAsync(false);
        }
        var factory = new Mock<ITorrentClientFactory>();
        factory.SetupGet(f => f.Type).Returns("fake");
        factory.Setup(f => f.Create(It.IsAny<string>(), It.IsAny<TorrentClientInstanceConfig>()))
            .Returns((string name, TorrentClientInstanceConfig _) => name == "first" ? first.Object : second.Object);
        var manager = new QBittorrentConnectionManager(
            NullLogger<QBittorrentConnectionManager>.Instance, [factory.Object]);
        var instance = new TorrentClientInstanceConfig { Type = "fake", Host = "fixture" };
        var desired = new[]
        {
            new KeyValuePair<string, TorrentClientInstanceConfig>("first", instance),
            new KeyValuePair<string, TorrentClientInstanceConfig>("second", instance)
        };

        (await manager.ReconcileAsync(desired)).Should().Be(0);
        (await manager.ReconcileAsync(desired)).Should().Be(0);
        first.Verify(c => c.LoginAsync(It.IsAny<CancellationToken>()), Times.Once);
        second.Verify(c => c.LoginAsync(It.IsAny<CancellationToken>()), Times.Once);
        await manager.InvalidateAuthenticationFailureAsync("first");
        (await manager.ReconcileAsync(desired)).Should().Be(0);
        first.Verify(c => c.LoginAsync(It.IsAny<CancellationToken>()), Times.Exactly(2));
        second.Verify(c => c.LoginAsync(It.IsAny<CancellationToken>()), Times.Once);
        manager.GetConnectionErrors()["second"].Should().Contain("Authentication rejected");
    }

    [Fact]
    public async Task ExplicitReconnect_AuthenticatesAgainEvenWhenCurrentSessionIsHealthy()
    {
        var client = new Mock<ITorrentClient>();
        client.Setup(c => c.LoginAsync(It.IsAny<CancellationToken>())).ReturnsAsync(true);
        client.Setup(c => c.GetVersionAsync(It.IsAny<CancellationToken>())).ReturnsAsync("5.2");
        var factory = new Mock<ITorrentClientFactory>();
        factory.SetupGet(f => f.Type).Returns("fake");
        factory.Setup(f => f.Create(It.IsAny<string>(), It.IsAny<TorrentClientInstanceConfig>())).Returns(client.Object);
        var manager = new QBittorrentConnectionManager(NullLogger<QBittorrentConnectionManager>.Instance, [factory.Object]);
        var instance = new TorrentClientInstanceConfig { Type = "fake", Host = "fixture" };
        (await manager.InitializeAsync("client", instance)).Should().BeTrue();
        (await manager.ForceReconnectAsync("client", instance)).Should().BeTrue();
        client.Verify(c => c.LoginAsync(It.IsAny<CancellationToken>()), Times.Exactly(2));
        factory.Verify(f => f.Create(It.IsAny<string>(), It.IsAny<TorrentClientInstanceConfig>()), Times.Exactly(2));
    }

    [Fact]
    public async Task DisconnectOnlyReconciliation_PreservesAuthenticationSuppressionUntilCredentialsChange()
    {
        var config = new TorrentarrConfig();
        config.Apply(new TorrentarrConfig(), 5);
        var client = new Mock<ITorrentClient>();
        client.SetupGet(c => c.LastConnectionFailureKind)
            .Returns(TorrentClientConnectionFailureKind.AuthenticationRejected);
        client.Setup(c => c.LoginAsync(It.IsAny<CancellationToken>())).ReturnsAsync(false);
        var factory = new Mock<ITorrentClientFactory>();
        factory.SetupGet(f => f.Type).Returns("fake");
        factory.Setup(f => f.Create(It.IsAny<string>(), It.IsAny<TorrentClientInstanceConfig>()))
            .Returns(client.Object);
        var manager = new QBittorrentConnectionManager(
            NullLogger<QBittorrentConnectionManager>.Instance, [factory.Object], config);
        var instance = new TorrentClientInstanceConfig { Type = "fake", Host = "fixture", UserName = "operator", Password = "bad" };
        var desired = new[] { new KeyValuePair<string, TorrentClientInstanceConfig>("client", instance) };

        (await manager.ReconcileAsync(desired)).Should().Be(0);
        (await manager.ReconcileAsync(desired, connect: false)).Should().Be(0);
        manager.GetConnectionErrors()["client"].Should().Contain("Authentication rejected");
        (await manager.ReconcileAsync(desired)).Should().Be(0);
        client.Verify(c => c.LoginAsync(It.IsAny<CancellationToken>()), Times.Once);

        instance.Password = "updated";
        (await manager.ReconcileAsync(desired, connect: false)).Should().Be(0);
        manager.GetConnectionErrors().Should().BeEmpty();
        (await manager.ReconcileAsync(desired)).Should().Be(0);
        client.Verify(c => c.LoginAsync(It.IsAny<CancellationToken>()), Times.Exactly(2));
    }

    [Fact]
    public async Task ExplicitReconnect_BypassesCachedAuthenticationFailure()
    {
        var config = new TorrentarrConfig();
        config.Apply(new TorrentarrConfig(), 1);
        var client = new Mock<ITorrentClient>();
        client.SetupGet(c => c.LastConnectionFailure).Returns("qBittorrent rejected credentials (Fails.)");
        client.SetupGet(c => c.LastConnectionFailureKind).Returns(TorrentClientConnectionFailureKind.AuthenticationRejected);
        client.Setup(c => c.LoginAsync(It.IsAny<CancellationToken>())).ReturnsAsync(false);
        var factory = new Mock<ITorrentClientFactory>();
        factory.SetupGet(f => f.Type).Returns("fake");
        factory.Setup(f => f.Create(It.IsAny<string>(), It.IsAny<TorrentClientInstanceConfig>())).Returns(client.Object);
        var manager = new QBittorrentConnectionManager(NullLogger<QBittorrentConnectionManager>.Instance, [factory.Object], config);
        var instance = new TorrentClientInstanceConfig { Type = "fake", Host = "fixture" };
        var desired = new[] { new KeyValuePair<string, TorrentClientInstanceConfig>("client", instance) };
        (await manager.ReconcileAsync(desired)).Should().Be(0);
        (await manager.ReconcileAsync(desired)).Should().Be(0);
        client.Verify(c => c.LoginAsync(It.IsAny<CancellationToken>()), Times.Once);
        manager.GetConnectionErrors()["client"].Should().Contain("Authentication rejected");
        (await manager.ForceReconnectAsync("client", instance)).Should().BeFalse();
        client.Verify(c => c.LoginAsync(It.IsAny<CancellationToken>()), Times.Exactly(2));
    }

    [Fact]
    public async Task TransientLoginFailures_ContinueToRetryWithinTheSameRevision()
    {
        var config = new TorrentarrConfig();
        config.Apply(new TorrentarrConfig(), 1);
        var client = new Mock<ITorrentClient>();
        client.SetupGet(c => c.LastConnectionFailure).Returns("HTTP 503 ServiceUnavailable");
        client.Setup(c => c.LoginAsync(It.IsAny<CancellationToken>())).ReturnsAsync(false);
        var factory = new Mock<ITorrentClientFactory>();
        factory.SetupGet(f => f.Type).Returns("fake");
        factory.Setup(f => f.Create(It.IsAny<string>(), It.IsAny<TorrentClientInstanceConfig>())).Returns(client.Object);
        var manager = new QBittorrentConnectionManager(NullLogger<QBittorrentConnectionManager>.Instance, [factory.Object], config);
        var instance = new TorrentClientInstanceConfig { Type = "fake", Host = "fixture" };

        (await manager.InitializeAsync("client", instance)).Should().BeFalse();
        (await manager.InitializeAsync("client", instance)).Should().BeFalse();
        client.Verify(c => c.LoginAsync(It.IsAny<CancellationToken>()), Times.Exactly(2));
    }

    [Fact]
    public async Task ExistingSession_IsVerifiedInsteadOfAssumingRegisteredMeansConnected()
    {
        var client = new Mock<ITorrentClient>();
        client.Setup(c => c.LoginAsync(It.IsAny<CancellationToken>())).ReturnsAsync(true);
        client.SetupSequence(c => c.GetVersionAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync("5.2").ThrowsAsync(new HttpRequestException("private-url?token=secret"))
            .ReturnsAsync("5.2");
        var factory = new Mock<ITorrentClientFactory>();
        factory.SetupGet(f => f.Type).Returns("fake");
        factory.Setup(f => f.Create(It.IsAny<string>(), It.IsAny<TorrentClientInstanceConfig>())).Returns(client.Object);
        var manager = new QBittorrentConnectionManager(NullLogger<QBittorrentConnectionManager>.Instance, [factory.Object]);
        var instance = new TorrentClientInstanceConfig { Type = "fake", Host = "fixture" };

        (await manager.InitializeAsync("client", instance)).Should().BeTrue();
        (await manager.InitializeAsync("client", instance)).Should().BeFalse();
        manager.IsConnected("client").Should().BeFalse();
        manager.GetConnectionErrors()["client"].Should().NotContain("secret");
        (await manager.InitializeAsync("client", instance)).Should().BeTrue();
        manager.GetConnectionErrors().Should().BeEmpty();
        client.Verify(c => c.LoginAsync(It.IsAny<CancellationToken>()), Times.Exactly(2));
    }

    [Fact]
    public async Task RemovedOrDisabledClient_DoesNotKeepStaleConnectionFailures()
    {
        var client = new Mock<ITorrentClient>();
        client.Setup(c => c.LoginAsync(It.IsAny<CancellationToken>())).ReturnsAsync(false);
        var factory = new Mock<ITorrentClientFactory>();
        factory.SetupGet(f => f.Type).Returns("fake");
        factory.Setup(f => f.Create(It.IsAny<string>(), It.IsAny<TorrentClientInstanceConfig>())).Returns(client.Object);
        var manager = new QBittorrentConnectionManager(NullLogger<QBittorrentConnectionManager>.Instance, [factory.Object]);
        var instance = new TorrentClientInstanceConfig { Type = "fake", Host = "fixture" };
        var desired = new[] { new KeyValuePair<string, TorrentClientInstanceConfig>("client", instance) };

        (await manager.ReconcileAsync(desired)).Should().Be(0);
        manager.GetConnectionErrors().Should().ContainKey("client");
        (await manager.ReconcileAsync([])).Should().Be(0);
        manager.GetConnectionErrors().Should().BeEmpty();

        (await manager.ReconcileAsync(desired)).Should().Be(0);
        instance.Disabled = true;
        (await manager.ReconcileAsync(desired)).Should().Be(0);
        manager.GetConnectionErrors().Should().BeEmpty();
    }

    [Fact]
    public async Task StructuredAuthRejection_IsSuppressedRegardlessOfFailureMessage()
    {
        var config = new TorrentarrConfig();
        config.Apply(new TorrentarrConfig(), 1);
        var client = new Mock<ITorrentClient>();
        client.SetupGet(c => c.LastConnectionFailure).Returns("A nonstandard login response");
        client.SetupGet(c => c.LastConnectionFailureKind).Returns(TorrentClientConnectionFailureKind.AuthenticationRejected);
        client.Setup(c => c.LoginAsync(It.IsAny<CancellationToken>())).ReturnsAsync(false);
        var factory = new Mock<ITorrentClientFactory>();
        factory.SetupGet(f => f.Type).Returns("fake");
        factory.Setup(f => f.Create(It.IsAny<string>(), It.IsAny<TorrentClientInstanceConfig>())).Returns(client.Object);
        var manager = new QBittorrentConnectionManager(NullLogger<QBittorrentConnectionManager>.Instance, [factory.Object], config);
        var instance = new TorrentClientInstanceConfig { Type = "fake", Host = "fixture" };
        (await manager.InitializeAsync("client", instance)).Should().BeFalse();
        (await manager.InitializeAsync("client", instance)).Should().BeFalse();
        client.Verify(c => c.LoginAsync(It.IsAny<CancellationToken>()), Times.Once);
    }
}

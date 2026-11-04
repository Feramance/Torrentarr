using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Torrentarr.Core.Configuration;
using Torrentarr.Core.Interfaces;
using Torrentarr.Infrastructure.Services;
using Xunit;

namespace Torrentarr.Infrastructure.Tests.Services;

public class TorrentClientRecoveryTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task BackgroundRecovery_DoesNotWaitForTorrentInterval_OrRetryRejectedCredentials(bool rejected)
    {
        var config = new TorrentarrConfig { Settings = { LoopSleepTimer = 3600 } };
        config.TorrentClients["one"] = new TorrentClientInstanceConfig { Type = "fake", Host = "fixture" };
        var client = new Mock<ITorrentClient>();
        var attempts = 0;
        client.Setup(c => c.LoginAsync(It.IsAny<CancellationToken>())).Returns(() =>
            Task.FromResult(Interlocked.Increment(ref attempts) > 1));
        client.SetupGet(c => c.LastConnectionFailureKind).Returns(rejected
            ? TorrentClientConnectionFailureKind.AuthenticationRejected
            : TorrentClientConnectionFailureKind.Unknown);
        client.Setup(c => c.GetVersionAsync(It.IsAny<CancellationToken>())).ReturnsAsync("5.3");
        var factory = new Mock<ITorrentClientFactory>();
        factory.SetupGet(f => f.Type).Returns("fake");
        factory.Setup(f => f.Create(It.IsAny<string>(), It.IsAny<TorrentClientInstanceConfig>())).Returns(client.Object);
        using var manager = new QBittorrentConnectionManager(NullLogger<QBittorrentConnectionManager>.Instance, [factory.Object], config);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(25));
        await manager.StartAsync(timeout.Token);
        try
        {
            // Allow the independent 15-second recovery tick, without invoking a torrent cycle.
            await Task.Delay(TimeSpan.FromSeconds(16), timeout.Token);
            if (rejected)
            {
                manager.IsConnected("one").Should().BeFalse();
                attempts.Should().Be(1);
                manager.ConnectionRevision.Should().Be(0);
            }
            else
            {
                manager.IsConnected("one").Should().BeTrue();
                attempts.Should().Be(2);
                var revision = manager.ConnectionRevision;
                await manager.EnsureAllConnectedAsync(config.GetAllTorrentClients(), timeout.Token);
                manager.ConnectionRevision.Should().Be(revision, "healthy probes must not wake torrent processing");
                attempts.Should().Be(2);
            }
        }
        finally { await manager.StopAsync(timeout.Token); }
    }
}

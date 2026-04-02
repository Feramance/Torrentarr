using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Torrentarr.Core.Configuration;
using Torrentarr.Core.Models;
using Torrentarr.Core.Services;
using Torrentarr.Infrastructure.Services;
using Xunit;

namespace Torrentarr.Infrastructure.Tests.Services;

/// <summary>
/// Unit tests for <see cref="TrackerQueueSortService"/> branches that do not require a connected qBit client.
/// </summary>
public class TrackerQueueSortServiceTests
{
    private static TrackerQueueSortService CreateService(
        TorrentarrConfig config,
        Mock<ISeedingService>? seedingMock = null)
    {
        seedingMock ??= new Mock<ISeedingService>();
        var mgr = new QBittorrentConnectionManager(NullLogger<QBittorrentConnectionManager>.Instance);
        return new TrackerQueueSortService(
            NullLogger<TrackerQueueSortService>.Instance,
            config,
            mgr,
            seedingMock.Object);
    }

    [Fact]
    public async Task SortTorrentQueuesByTrackerPriorityAsync_NoSortTorrentsAnywhere_DoesNotCallSeeding()
    {
        var config = new TorrentarrConfig();
        var seedingMock = new Mock<ISeedingService>();
        var svc = CreateService(config, seedingMock);

        await svc.SortTorrentQueuesByTrackerPriorityAsync();

        seedingMock.Verify(
            s => s.GetTrackerConfigAsync(It.IsAny<TorrentInfo>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task SortTorrentQueuesByTrackerPriorityAsync_SortTorrentsOnQBitTrackerButNoConnectedClients_CompletesWithoutCallingSeeding()
    {
        var config = new TorrentarrConfig
        {
            QBitInstances =
            {
                ["qBit"] = new QBitConfig
                {
                    Trackers = [new TrackerConfig { SortTorrents = true }]
                }
            }
        };
        var seedingMock = new Mock<ISeedingService>();
        var svc = CreateService(config, seedingMock);

        await FluentActions.Invoking(() => svc.SortTorrentQueuesByTrackerPriorityAsync())
            .Should().NotThrowAsync();

        seedingMock.Verify(
            s => s.GetTrackerConfigAsync(It.IsAny<TorrentInfo>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task SortTorrentQueuesByTrackerPriorityAsync_SortTorrentsOnArrTrackerButNoConnectedClients_CompletesWithoutCallingSeeding()
    {
        var config = new TorrentarrConfig
        {
            ArrInstances =
            {
                ["Radarr-Movies"] = new ArrInstanceConfig
                {
                    Category = "movies",
                    Torrent =
                    {
                        Trackers = [new TrackerConfig { SortTorrents = true }]
                    }
                }
            }
        };
        var seedingMock = new Mock<ISeedingService>();
        var svc = CreateService(config, seedingMock);

        await FluentActions.Invoking(() => svc.SortTorrentQueuesByTrackerPriorityAsync())
            .Should().NotThrowAsync();

        seedingMock.Verify(
            s => s.GetTrackerConfigAsync(It.IsAny<TorrentInfo>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }
}

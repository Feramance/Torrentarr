using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Torrentarr.Core.Configuration;
using Torrentarr.Core.Interfaces;
using Torrentarr.Core.Models;
using Torrentarr.Infrastructure.Database;
using Torrentarr.Infrastructure.Services;
using Xunit;

namespace Torrentarr.Infrastructure.Tests.Services;

public sealed class TrackerMessagePreservationTests
{
    private const string Message = "skipping tracker announce (unreachable)";
    private const string ConfiguredUrl = "https://tracker.example/announce";

    private static (SeedingService Service, Mock<ITorrentClient> Client) Create(
        bool configureTracker,
        bool removeDeadTrackers)
    {
        var config = new TorrentarrConfig();
        config.TorrentClients["client"] = new()
        {
            Trackers = configureTracker ? [new() { Uri = "tracker.example" }] : []
        };
        config.ArrInstances["Radarr-Movies"] = new()
        {
            Type = "radarr",
            Category = "movies",
            Torrent = new()
            {
                SeedingMode = new()
                {
                    RemoveDeadTrackers = removeDeadTrackers,
                    RemoveTrackerWithMessage = ["unreachable"]
                }
            }
        };
        var db = new TorrentarrDbContext(new DbContextOptionsBuilder<TorrentarrDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        var client = new Mock<ITorrentClient>();
        client.SetupGet(c => c.Capabilities).Returns(TorrentClientCapabilities.QBittorrent);
        client.Setup(c => c.RemoveTrackersAsync(It.IsAny<string>(), It.IsAny<List<string>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);
        var registry = new Mock<ITorrentClientRegistry>();
        registry.Setup(r => r.GetClient("client")).Returns(client.Object);
        return (new SeedingService(NullLogger<SeedingService>.Instance, db, config, registry.Object), client);
    }

    private static TorrentInfo Torrent(bool isPrivate) => new()
    {
        ClientInstanceId = "client",
        Hash = "hash",
        Name = "torrent",
        Category = "movies",
        AddedOn = 1,
        Progress = 1,
        IsPrivate = isPrivate
    };

    private static async Task Run(
        SeedingService service,
        Mock<ITorrentClient> client,
        TorrentInfo torrent,
        params TorrentTracker[] trackers)
    {
        client.Setup(c => c.GetTorrentsAsync(It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([torrent]);
        client.Setup(c => c.GetTorrentTrackersAsync("hash", It.IsAny<CancellationToken>()))
            .ReturnsAsync(trackers.ToList());
        await service.ProcessTrackerMessagesAsync(client.Object, torrent, default);
    }

    [Fact]
    public async Task PrivateConfiguredTracker_IsPreserved()
    {
        var (service, client) = Create(configureTracker: true, removeDeadTrackers: false);
        var torrent = Torrent(isPrivate: true);

        await Run(service, client, torrent,
            new TorrentTracker { Url = ConfiguredUrl, Status = 4, Tier = 0, Msg = Message });

        client.Verify(c => c.RemoveTrackersAsync(It.IsAny<string>(), It.IsAny<List<string>>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task PrivateLastAnnounceUrl_IsPreserved()
    {
        var (service, client) = Create(configureTracker: false, removeDeadTrackers: false);
        var torrent = Torrent(isPrivate: true);

        await Run(service, client, torrent,
            new TorrentTracker { Url = "https://only.example/announce", Status = 4, Tier = 0, Msg = Message });

        client.Verify(c => c.RemoveTrackersAsync(It.IsAny<string>(), It.IsAny<List<string>>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task PrivateTrailingAnnounceUrl_IsPreservedAfterExtraRemoved()
    {
        var (service, client) = Create(configureTracker: false, removeDeadTrackers: false);
        var torrent = Torrent(isPrivate: true);
        var removed = new List<string>();

        client.Setup(c => c.RemoveTrackersAsync("hash", It.IsAny<List<string>>(), It.IsAny<CancellationToken>()))
            .Callback<string, List<string>, CancellationToken>((_, urls, _) => removed.AddRange(urls))
            .ReturnsAsync(true);
        await Run(service, client, torrent,
            new TorrentTracker { Url = "https://extra.example/announce", Status = 4, Tier = 0, Msg = Message },
            new TorrentTracker { Url = "https://last.example/announce", Status = 4, Tier = 0, Msg = Message });

        removed.Should().Equal("https://extra.example/announce");
    }

    [Fact]
    public async Task PublicTracker_IsRemoved()
    {
        var (service, client) = Create(configureTracker: false, removeDeadTrackers: false);
        var torrent = Torrent(isPrivate: false);
        var removed = new List<string>();

        client.Setup(c => c.RemoveTrackersAsync("hash", It.IsAny<List<string>>(), It.IsAny<CancellationToken>()))
            .Callback<string, List<string>, CancellationToken>((_, urls, _) => removed.AddRange(urls))
            .ReturnsAsync(true);
        await Run(service, client, torrent,
            new TorrentTracker { Url = "https://public.example/announce", Status = 4, Tier = 0, Msg = Message });

        removed.Should().Equal("https://public.example/announce");
    }

    [Fact]
    public async Task RemoveDeadTrackers_StillPreservesEveryTracker()
    {
        var (service, client) = Create(configureTracker: false, removeDeadTrackers: true);
        var torrent = Torrent(isPrivate: false);

        await Run(service, client, torrent,
            new TorrentTracker { Url = "https://public.example/announce", Status = 4, Tier = 0, Msg = Message });

        client.Verify(c => c.RemoveTrackersAsync(It.IsAny<string>(), It.IsAny<List<string>>(), It.IsAny<CancellationToken>()), Times.Never);
    }
}

using System.Reflection;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Torrentarr.Core.Configuration;
using Torrentarr.Core.Models;
using Torrentarr.Infrastructure.Database;
using Torrentarr.Infrastructure.Database.Models;
using Torrentarr.Infrastructure.Services;
using Xunit;

namespace Torrentarr.Infrastructure.Tests.Services;

/// <summary>
/// Tagless mode must scope TorrentLibrary reads/writes by (Hash, QbitInstance).
/// </summary>
public class TaglessScopingTests
{
    private static TorrentarrDbContext CreateDb()
    {
        var options = new DbContextOptionsBuilder<TorrentarrDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        return new TorrentarrDbContext(options);
    }

    private static bool InvokeHasTag(object service, TorrentInfo torrent, string tag)
    {
        var method = service.GetType().GetMethod("HasTag", BindingFlags.NonPublic | BindingFlags.Instance);
        method.Should().NotBeNull();
        return (bool)method!.Invoke(service, [torrent, tag])!;
    }

    [Fact]
    public async Task TorrentProcessor_HasTag_ReadsFreeSpacePausedPerQbitInstance()
    {
        await using var db = CreateDb();
        db.TorrentLibrary.AddRange(
            new TorrentLibrary { Hash = "abc", Category = "radarr", QbitInstance = "qBit", FreeSpacePaused = true },
            new TorrentLibrary { Hash = "abc", Category = "radarr", QbitInstance = "qBit-seedbox", FreeSpacePaused = false });
        await db.SaveChangesAsync();

        var config = new TorrentarrConfig { Settings = { Tagless = true } };
        var processor = new TorrentProcessor(
            NullLogger<TorrentProcessor>.Instance,
            new QBittorrentConnectionManager(NullLogger<QBittorrentConnectionManager>.Instance),
            db,
            config,
            new TorrentCacheService(NullLogger<TorrentCacheService>.Instance),
            new DatabaseRestartCoordinator());

        var seedboxTorrent = new TorrentInfo { Hash = "abc", QBitInstanceName = "qBit-seedbox" };
        var primaryTorrent = new TorrentInfo { Hash = "abc", QBitInstanceName = "qBit" };

        InvokeHasTag(processor, seedboxTorrent, "qBitrr-free_space_paused").Should().BeFalse();
        InvokeHasTag(processor, primaryTorrent, "qBitrr-free_space_paused").Should().BeTrue();
    }

    [Fact]
    public async Task SeedingService_HasTag_ReadsAllowedSeedingPerQbitInstance()
    {
        await using var db = CreateDb();
        db.TorrentLibrary.AddRange(
            new TorrentLibrary { Hash = "abc", Category = "radarr", QbitInstance = "qBit", AllowedSeeding = true },
            new TorrentLibrary { Hash = "abc", Category = "radarr", QbitInstance = "qBit-seedbox", AllowedSeeding = false });
        await db.SaveChangesAsync();

        var config = new TorrentarrConfig { Settings = { Tagless = true } };
        var svc = new SeedingService(
            NullLogger<SeedingService>.Instance,
            db,
            config,
            new QBittorrentConnectionManager(NullLogger<QBittorrentConnectionManager>.Instance));

        var seedboxTorrent = new TorrentInfo { Hash = "abc", QBitInstanceName = "qBit-seedbox" };
        var primaryTorrent = new TorrentInfo { Hash = "abc", QBitInstanceName = "qBit" };

        InvokeHasTag(svc, seedboxTorrent, "qBitrr-allowed_seeding").Should().BeFalse();
        InvokeHasTag(svc, primaryTorrent, "qBitrr-allowed_seeding").Should().BeTrue();
    }
}

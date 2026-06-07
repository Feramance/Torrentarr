using System.Reflection;
using FluentAssertions;
using Microsoft.Data.Sqlite;
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
/// Tagless mode maps qBit tags to TorrentLibrary columns keyed by (Hash, QbitInstance).
/// These tests ensure lookups and updates never bleed across qBit instances when the
/// same info-hash exists on more than one client.
/// </summary>
public sealed class TaglessMultiQbitInstanceTests : IDisposable
{
    private const string SharedHash = "abc123sharedhash00000000000000000000";
    private const string FreeSpacePausedTag = "qBitrr-free_space_paused";
    private const string AllowedSeedingTag = "qBitrr-allowed_seeding";

    private readonly string _dbName;
    private readonly SqliteConnection _keepAlive;
    private readonly TorrentarrDbContext _db;

    public TaglessMultiQbitInstanceTests()
    {
        _dbName = $"tagless-{Guid.NewGuid():N}";
        var cs = $"Data Source={_dbName};Mode=Memory;Cache=Shared";
        _keepAlive = new SqliteConnection(cs);
        _keepAlive.Open();
        var options = new DbContextOptionsBuilder<TorrentarrDbContext>()
            .UseSqlite(cs).Options;
        _db = new TorrentarrDbContext(options);
        _db.Database.EnsureCreated();
        SeedCrossInstanceRows();
    }

    public void Dispose()
    {
        _db.Dispose();
        _keepAlive.Dispose();
    }

    private void SeedCrossInstanceRows()
    {
        _db.TorrentLibrary.AddRange(
            new TorrentLibrary
            {
                Hash = SharedHash,
                Category = "radarr-hd",
                QbitInstance = "qBit",
                FreeSpacePaused = true,
                AllowedSeeding = true,
            },
            new TorrentLibrary
            {
                Hash = SharedHash,
                Category = "radarr-hd",
                QbitInstance = "qBit-seedbox",
                FreeSpacePaused = false,
                AllowedSeeding = false,
            });
        _db.SaveChanges();
    }

    private static TorrentarrConfig TaglessConfig()
    {
        var config = new TorrentarrConfig();
        config.Settings.Tagless = true;
        return config;
    }

    private static TorrentInfo TorrentOnInstance(string instanceName) => new()
    {
        Hash = SharedHash,
        QBitInstanceName = instanceName,
    };

    private static bool InvokeHasTag(object service, TorrentInfo torrent, string tag)
    {
        var method = service.GetType().GetMethod(
            "HasTag",
            BindingFlags.NonPublic | BindingFlags.Instance);
        return (bool)method!.Invoke(service, new object[] { torrent, tag })!;
    }

    [Fact]
    public void TorrentProcessor_HasTag_UsesQbitInstance_NotHashAlone()
    {
        var processor = new TorrentProcessor(
            NullLogger<TorrentProcessor>.Instance,
            new QBittorrentConnectionManager(NullLogger<QBittorrentConnectionManager>.Instance),
            _db,
            TaglessConfig(),
            new TorrentCacheService(NullLogger<TorrentCacheService>.Instance));

        var seedbox = TorrentOnInstance("qBit-seedbox");
        InvokeHasTag(processor, seedbox, FreeSpacePausedTag).Should().BeFalse();
        InvokeHasTag(processor, seedbox, AllowedSeedingTag).Should().BeFalse();

        var primary = TorrentOnInstance("qBit");
        InvokeHasTag(processor, primary, FreeSpacePausedTag).Should().BeTrue();
        InvokeHasTag(processor, primary, AllowedSeedingTag).Should().BeTrue();
    }

    [Fact]
    public void SeedingService_HasTag_UsesQbitInstance_NotHashAlone()
    {
        var seeding = new SeedingService(
            NullLogger<SeedingService>.Instance,
            _db,
            TaglessConfig(),
            new QBittorrentConnectionManager(NullLogger<QBittorrentConnectionManager>.Instance));

        var seedbox = TorrentOnInstance("qBit-seedbox");
        InvokeHasTag(seeding, seedbox, FreeSpacePausedTag).Should().BeFalse();
        InvokeHasTag(seeding, seedbox, AllowedSeedingTag).Should().BeFalse();

        var primary = TorrentOnInstance("qBit");
        InvokeHasTag(seeding, primary, FreeSpacePausedTag).Should().BeTrue();
        InvokeHasTag(seeding, primary, AllowedSeedingTag).Should().BeTrue();
    }

    [Fact]
    public async Task SeedingService_ImportedCheck_IsScopedToQbitInstance()
    {
        var entry = await _db.TorrentLibrary
            .FirstAsync(t => t.Hash == SharedHash && t.QbitInstance == "qBit-seedbox");
        entry.Imported = false;
        var primary = await _db.TorrentLibrary
            .FirstAsync(t => t.Hash == SharedHash && t.QbitInstance == "qBit");
        primary.Imported = true;
        await _db.SaveChangesAsync();

        var importedOnSeedbox = await _db.TorrentLibrary
            .AnyAsync(t => t.Hash == SharedHash && t.QbitInstance == "qBit-seedbox" && t.Imported);
        var importedOnPrimary = await _db.TorrentLibrary
            .AnyAsync(t => t.Hash == SharedHash && t.QbitInstance == "qBit" && t.Imported);

        importedOnSeedbox.Should().BeFalse();
        importedOnPrimary.Should().BeTrue();
    }
}

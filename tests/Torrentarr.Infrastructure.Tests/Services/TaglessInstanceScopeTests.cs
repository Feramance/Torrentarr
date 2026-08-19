using System.Reflection;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Torrentarr.Core.Configuration;
using Torrentarr.Core.Models;
using Torrentarr.Infrastructure.ApiClients.QBittorrent;
using Torrentarr.Infrastructure.Database;
using Torrentarr.Infrastructure.Database.Models;
using Torrentarr.Infrastructure.Services;
using Xunit;

namespace Torrentarr.Infrastructure.Tests.Services;

/// <summary>
/// Tagless mode stores per-qBit-instance state in TorrentLibrary (Hash, QbitInstance).
/// HasTag reads must match the instance on the torrent, not any row with the same hash.
/// </summary>
public sealed class TaglessInstanceScopeTests
{
    private static TorrentarrDbContext CreateDbWithDuplicateHashRows()
    {
        var options = new DbContextOptionsBuilder<TorrentarrDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        var db = new TorrentarrDbContext(options);
        db.TorrentLibrary.AddRange(
            new TorrentLibrary
            {
                Hash = "abc123",
                Category = "radarr",
                QbitInstance = "qBit",
                AllowedSeeding = true,
                FreeSpacePaused = true,
                Imported = true
            },
            new TorrentLibrary
            {
                Hash = "abc123",
                Category = "radarr",
                QbitInstance = "qBit-seedbox",
                AllowedSeeding = false,
                FreeSpacePaused = false,
                Imported = false
            });
        db.SaveChanges();
        return db;
    }

    [Theory]
    [InlineData(typeof(TorrentProcessor), "qBitrr-allowed_seeding")]
    [InlineData(typeof(TorrentProcessor), "qBitrr-free_space_paused")]
    [InlineData(typeof(FreeSpaceService), "qBitrr-free_space_paused")]
    [InlineData(typeof(SeedingService), "qBitrr-allowed_seeding")]
    [InlineData(typeof(SeedingService), "qBitrr-free_space_paused")]
    public void HasTag_TaglessMode_ReadsStateForTorrentQbitInstance(Type serviceType, string tag)
    {
        using var db = CreateDbWithDuplicateHashRows();
        var config = new TorrentarrConfig { Settings = { Tagless = true } };
        var torrent = new TorrentInfo
        {
            Hash = "abc123",
            QBitInstanceName = "qBit-seedbox"
        };

        var hasTag = InvokeHasTag(serviceType, db, config, torrent, tag);

        hasTag.Should().BeFalse($"tag {tag} should reflect the seedbox row, not qBit");
    }

    [Fact]
    public void HasTag_TaglessMode_WhenInstanceRowHasFlag_ReturnsTrue()
    {
        using var db = CreateDbWithDuplicateHashRows();
        var config = new TorrentarrConfig { Settings = { Tagless = true } };
        var torrent = new TorrentInfo
        {
            Hash = "abc123",
            QBitInstanceName = "qBit"
        };

        InvokeHasTag(typeof(TorrentProcessor), db, config, torrent, "qBitrr-allowed_seeding")
            .Should().BeTrue();
        InvokeHasTag(typeof(FreeSpaceService), db, config, torrent, "qBitrr-free_space_paused")
            .Should().BeTrue();
    }

    [Fact]
    public void HasTag_TaglessMode_HonorsLiveQbitrrIgnoredTag()
    {
        using var db = CreateDbWithDuplicateHashRows();
        var config = new TorrentarrConfig { Settings = { Tagless = true } };
        var torrent = new TorrentInfo
        {
            Hash = "abc123",
            QBitInstanceName = "qBit-seedbox",
            Tags = "qBitrr-ignored, other"
        };

        InvokeHasTag(typeof(TorrentProcessor), db, config, torrent, "qBitrr-ignored").Should().BeTrue();
        InvokeHasTag(typeof(SeedingService), db, config, torrent, "qBitrr-ignored").Should().BeTrue();

        torrent.Tags = "other";
        InvokeHasTag(typeof(TorrentProcessor), db, config, torrent, "qBitrr-ignored").Should().BeFalse();
    }

    /// <summary>
    /// Regression: Imported flag must be read per (Hash, QbitInstance), not hash-only.
    /// Pre-fix, seedbox row was treated as imported when only the qBit row had Imported=true.
    /// </summary>
    [Fact]
    public async Task IsImportedInDatabase_ScopesByQbitInstance()
    {
        await using var db = CreateDbWithDuplicateHashRows();
        var config = new TorrentarrConfig { Settings = { Tagless = true } };
        var processor = new TorrentProcessor(
            NullLogger<TorrentProcessor>.Instance,
            new QBittorrentConnectionManager(NullLogger<QBittorrentConnectionManager>.Instance),
            db,
            config,
            new TorrentCacheService(NullLogger<TorrentCacheService>.Instance),
            new DatabaseRestartCoordinator());

        var method = typeof(TorrentProcessor).GetMethod(
            "IsImportedInDatabaseAsync",
            BindingFlags.NonPublic | BindingFlags.Instance);
        method.Should().NotBeNull();

        var seedboxImported = await (Task<bool>)method!.Invoke(
            processor,
            new object[] { "abc123", "qBit-seedbox", CancellationToken.None })!;
        var primaryImported = await (Task<bool>)method.Invoke(
            processor,
            new object[] { "abc123", "qBit", CancellationToken.None })!;

        seedboxImported.Should().BeFalse("seedbox row is not imported");
        primaryImported.Should().BeTrue("primary row is imported");
    }

    /// <summary>
    /// Regression: IsReadyForImportAsync must read qBit torrent state from the requested instance,
    /// not the first client that returns a matching hash. Pre-fix, qBit (listed first) could block
    /// seedbox import when the same hash was still downloading on qBit but complete on seedbox.
    /// </summary>
    [Fact]
    public async Task IsReadyForImportAsync_ScopesQbitClientLookupByInstance()
    {
        const string hash = "abc123";
        var completionOn = DateTimeOffset.UtcNow.AddMinutes(-5).ToUnixTimeSeconds();

        await using var db = CreateDbWithDuplicateHashRows();
        var config = new TorrentarrConfig { Settings = { Tagless = true } };
        var manager = new QBittorrentConnectionManager(NullLogger<QBittorrentConnectionManager>.Instance);
        RegisterStubClient(manager, "qBit", new TorrentInfo
        {
            Hash = hash,
            Progress = 0.5,
            State = "stalledDL",
            CompletionOn = 0
        });
        RegisterStubClient(manager, "qBit-seedbox", new TorrentInfo
        {
            Hash = hash,
            Progress = 1.0,
            State = "stalledUP",
            CompletionOn = completionOn
        });

        var processor = new TorrentProcessor(
            NullLogger<TorrentProcessor>.Instance,
            manager,
            db,
            config,
            new TorrentCacheService(NullLogger<TorrentCacheService>.Instance),
            new DatabaseRestartCoordinator());

        var ready = await processor.IsReadyForImportAsync(hash, "qBit-seedbox");

        ready.Should().BeTrue("seedbox completion/seeding state must be used, not qBit's incomplete copy");
    }

    private static void RegisterStubClient(
        QBittorrentConnectionManager manager,
        string instanceName,
        TorrentInfo torrent)
    {
        var clientsField = typeof(QBittorrentConnectionManager)
            .GetField("_clients", BindingFlags.NonPublic | BindingFlags.Instance)!;
        var clients = (Dictionary<string, QBittorrentClient>)clientsField.GetValue(manager)!;
        clients[instanceName] = new StubQBittorrentClient([torrent]);
    }

    private sealed class StubQBittorrentClient : QBittorrentClient
    {
        private readonly List<TorrentInfo> _torrents;

        public StubQBittorrentClient(List<TorrentInfo> torrents)
            : base("127.0.0.1", 1, "u", "p") => _torrents = torrents;

        public override Task<List<TorrentInfo>> GetTorrentsAsync(
            string? category = null,
            string? sort = null,
            CancellationToken cancellationToken = default)
            => Task.FromResult(_torrents);
    }

    private static bool InvokeHasTag(
        Type serviceType,
        TorrentarrDbContext db,
        TorrentarrConfig config,
        TorrentInfo torrent,
        string tag)
    {
        object service = serviceType.Name switch
        {
            nameof(TorrentProcessor) => new TorrentProcessor(
                NullLogger<TorrentProcessor>.Instance,
                new QBittorrentConnectionManager(NullLogger<QBittorrentConnectionManager>.Instance),
                db,
                config,
                new TorrentCacheService(NullLogger<TorrentCacheService>.Instance),
                new DatabaseRestartCoordinator()),
            nameof(FreeSpaceService) => new FreeSpaceService(
                NullLogger<FreeSpaceService>.Instance,
                config,
                new QBittorrentConnectionManager(NullLogger<QBittorrentConnectionManager>.Instance),
                db),
            nameof(SeedingService) => new SeedingService(
                NullLogger<SeedingService>.Instance,
                db,
                config,
                new QBittorrentConnectionManager(NullLogger<QBittorrentConnectionManager>.Instance)),
            _ => throw new ArgumentOutOfRangeException(nameof(serviceType))
        };

        var method = serviceType.GetMethod("HasTag", BindingFlags.NonPublic | BindingFlags.Instance);
        method.Should().NotBeNull();
        return (bool)method!.Invoke(service, new object[] { torrent, tag })!;
    }
}

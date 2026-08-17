using System.Collections.Generic;
using System.IO;
using System.Reflection;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Torrentarr.Core.Configuration;
using Torrentarr.Core.Models;
using Torrentarr.Core.Services;
using Torrentarr.Infrastructure.ApiClients.QBittorrent;
using Torrentarr.Infrastructure.Database;
using Torrentarr.Infrastructure.Services;
using Xunit;

namespace Torrentarr.Infrastructure.Tests.Services;

/// <summary>
/// Tests for TorrentProcessor that exercise the no-network fast-exit paths.
/// All public methods guard against a missing qBittorrent client at the top
/// of their implementation and return early without touching the database or
/// making any HTTP calls, so these tests require no live services.
/// </summary>
public sealed class TorrentProcessorTests : IDisposable
{
    private readonly string _dbName;
    private readonly SqliteConnection _keepAlive;
    private readonly TorrentarrDbContext _db;

    public TorrentProcessorTests()
    {
        _dbName = $"tproc-{Guid.NewGuid():N}";
        var cs = $"Data Source={_dbName};Mode=Memory;Cache=Shared";
        _keepAlive = new SqliteConnection(cs);
        _keepAlive.Open();
        var options = new DbContextOptionsBuilder<TorrentarrDbContext>()
            .UseSqlite(cs).Options;
        _db = new TorrentarrDbContext(options);
        _db.Database.EnsureCreated();
    }

    public void Dispose()
    {
        _db.Dispose();
        _keepAlive.Dispose();
    }

    private TorrentProcessor CreateProcessor(TorrentarrConfig? config = null)
    {
        config ??= new TorrentarrConfig();
        var manager = new QBittorrentConnectionManager(
            NullLogger<QBittorrentConnectionManager>.Instance);
        return new TorrentProcessor(
            NullLogger<TorrentProcessor>.Instance,
            manager,
            _db,
            config,
            new TorrentCacheService(NullLogger<TorrentCacheService>.Instance),
            new DatabaseRestartCoordinator());
    }

    // ── Constructor ────────────────────────────────────────────────────────────

    [Fact]
    public void Constructor_WithDefaultConfig_DoesNotThrow()
    {
        var act = () => CreateProcessor();

        act.Should().NotThrow();
    }

    [Fact]
    public void Constructor_SpecialCategoriesComputedFromConfig()
    {
        var config = new TorrentarrConfig();
        config.Settings.FailedCategory = "failed";
        config.Settings.RecheckCategory = "recheck";

        // Construction must succeed regardless of the category values.
        var act = () => CreateProcessor(config);

        act.Should().NotThrow();
    }

    // ── ProcessTorrentsAsync – no clients ─────────────────────────────────────

    [Fact]
    public async Task ProcessTorrentsAsync_NoQBitClients_DoesNotThrow()
    {
        var svc = CreateProcessor();

        var act = async () => await svc.ProcessTorrentsAsync("radarr-hd");

        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task ProcessTorrentsAsync_SpecialCategoryName_NoQBitClients_DoesNotThrow()
    {
        var config = new TorrentarrConfig();
        config.Settings.FailedCategory = "failed";

        var svc = CreateProcessor(config);

        // No client registered → returns at the client-null guard before the
        // special-category skip, so this must not throw.
        var act = async () => await svc.ProcessTorrentsAsync("failed");

        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task ProcessSingleTorrentAsync_RecheckOnMissingOwningClient_DoesNotUseOtherInstance()
    {
        const string hash = "recheck-hash-0123456789abcdef0123456789abcdef";
        var config = new TorrentarrConfig();
        config.Settings.RecheckCategory = "recheck";

        var manager = new QBittorrentConnectionManager(
            NullLogger<QBittorrentConnectionManager>.Instance);
        RegisterTestClient(manager, "qBit-seedbox", new QBittorrentClient("127.0.0.1", 1, "u", "p"));

        var processor = new TorrentProcessor(
            NullLogger<TorrentProcessor>.Instance,
            manager,
            _db,
            config,
            new TorrentCacheService(NullLogger<TorrentCacheService>.Instance),
            new DatabaseRestartCoordinator());

        var torrent = new TorrentInfo
        {
            Hash = hash,
            Name = "Needs Recheck",
            Category = "recheck",
            State = "error",
            QBitInstanceName = "qBit"
        };
        var stats = new TorrentProcessingStats();
        var method = typeof(TorrentProcessor).GetMethod(
            "ProcessSingleTorrentAsync",
            BindingFlags.NonPublic | BindingFlags.Instance)!;

        var act = async () => await (Task)method.Invoke(processor, new object[] { torrent, "recheck", stats, CancellationToken.None })!;

        await act.Should().NotThrowAsync();
        stats.Failed.Should().Be(0);
    }

    [Fact]
    public async Task ProcessTorrentsAsync_PreCancelledToken_DoesNotThrow()
    {
        var svc = CreateProcessor();
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        // No client → exits before any awaited work involving the token.
        var act = async () => await svc.ProcessTorrentsAsync("radarr-hd", cts.Token);

        await act.Should().NotThrowAsync();
    }

    // ── ProcessSpecialCategoriesAsync ─────────────────────────────────────────

    [Fact]
    public async Task ProcessSpecialCategoriesAsync_DoesNotThrow()
    {
        // This method is now a no-op (handled by the Host orchestrator).
        var svc = CreateProcessor();

#pragma warning disable CS0618 // Intentionally testing the obsolete compatibility shim
        var act = async () => await svc.ProcessSpecialCategoriesAsync();
#pragma warning restore CS0618

        await act.Should().NotThrowAsync();
    }

    // ── ProcessTorrentAsync – no clients ──────────────────────────────────────

    [Fact]
    public async Task ProcessTorrentAsync_NoQBitClients_DoesNotThrow()
    {
        var svc = CreateProcessor();

        var act = async () => await svc.ProcessTorrentAsync("abc123def456");

        await act.Should().NotThrowAsync();
    }

    // ── IsReadyForImportAsync – no clients ────────────────────────────────────

    [Fact]
    public async Task IsReadyForImportAsync_NoQBitClients_ReturnsFalse()
    {
        var svc = CreateProcessor();

        var result = await svc.IsReadyForImportAsync("abc123def456");

        result.Should().BeFalse("no qBittorrent client registered means the torrent cannot be inspected");
    }

    // ── ImportTorrentAsync – no clients ───────────────────────────────────────

    [Fact]
    public async Task ImportTorrentAsync_NoQBitClients_DoesNotThrow()
    {
        var svc = CreateProcessor();

        var act = async () => await svc.ImportTorrentAsync("abc123def456");

        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task ImportTorrentAsync_TriggerSuccess_DoesNotMarkImportedUntilArrConfirms()
    {
        const string hash = "import-hash";
        const string instance = "qBit";
        var tempFile = Path.GetTempFileName();
        try
        {
            _db.TorrentLibrary.Add(new Torrentarr.Infrastructure.Database.Models.TorrentLibrary
            {
                Hash = hash,
                Category = "radarr-hd",
                QbitInstance = instance,
                Imported = false
            });
            await _db.SaveChangesAsync();

            var config = new TorrentarrConfig();
            config.ArrInstances["Radarr-HD"] = new ArrInstanceConfig
            {
                Category = "radarr-hd",
                Type = "radarr"
            };

            var manager = new QBittorrentConnectionManager(
                NullLogger<QBittorrentConnectionManager>.Instance);
            RegisterTestClient(manager, instance, new StubQBittorrentClient(new TorrentInfo
            {
                Hash = hash,
                ContentPath = tempFile,
                QBitInstanceName = instance
            }));

            var importMock = new Mock<IArrImportService>();
            importMock
                .Setup(s => s.TriggerImportAsync(hash, tempFile, "radarr-hd", It.IsAny<CancellationToken>()))
                .ReturnsAsync(new ImportResult { Success = true, Message = "queued" });

            var pathTracker = new ImportPathTracker();
            var processor = new TorrentProcessor(
                NullLogger<TorrentProcessor>.Instance,
                manager,
                _db,
                config,
                new TorrentCacheService(NullLogger<TorrentCacheService>.Instance),
                new DatabaseRestartCoordinator(),
                importMock.Object,
                pathTracker: pathTracker);

            await processor.ImportTorrentAsync(hash, instance);

            var entry = await _db.TorrentLibrary.SingleAsync(t => t.Hash == hash && t.QbitInstance == instance);
            entry.Imported.Should().BeFalse("Torrentarr should wait for Arr to confirm the scan succeeded");
            pathTracker.IsHashAlreadyScanned(hash).Should().BeTrue("successful trigger should still suppress duplicate scans");
        }
        finally
        {
            File.Delete(tempFile);
        }
    }

    /// <summary>
    /// Regression: CF-unmet deletion must honor HnR protection (qBitrr _hnr_allows_delete parity).
    /// Pre-fix, Branch 1 deleted immediately and bypassed HnrAllowsDeleteAsync.
    /// </summary>
    [Fact]
    public async Task ProcessSingleTorrentAsync_CustomFormatUnmet_BlockedByHnr_DoesNotDelete()
    {
        const string hash = "abc123def4567890123456789012345678901234";
        const string category = "radarr-hd";
        var torrent = new TorrentInfo
        {
            Hash = hash,
            Name = "CF Unmet Torrent",
            Category = category,
            State = "uploading",
            QBitInstanceName = "qBit"
        };

        var config = new TorrentarrConfig
        {
            Settings = { Tagless = true }
        };
        config.ArrInstances["Radarr-HD"] = new ArrInstanceConfig
        {
            Category = category,
            Type = "radarr",
            Search = { CustomFormatUnmetSearch = true }
        };

        var importMock = new Mock<IArrImportService>();
        importMock
            .Setup(s => s.IsCustomFormatUnmetAsync(hash, category, It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        var seedingMock = new Mock<ISeedingService>();
        seedingMock
            .Setup(s => s.ApplyTrackerActionsForTorrentAsync(torrent, It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        seedingMock
            .Setup(s => s.GetTrackerConfigAsync(torrent, It.IsAny<CancellationToken>()))
            .ReturnsAsync((TrackerConfig?)null);
        seedingMock
            .Setup(s => s.ShouldRemoveTorrentAsync(torrent, It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);
        seedingMock
            .Setup(s => s.HnrAllowsDeleteAsync(torrent, "CF unmet deletion", It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);

        var manager = new QBittorrentConnectionManager(
            NullLogger<QBittorrentConnectionManager>.Instance);
        RegisterTestClient(manager, "qBit", new QBittorrentClient("127.0.0.1", 1, "u", "p"));

        var processor = new TorrentProcessor(
            NullLogger<TorrentProcessor>.Instance,
            manager,
            _db,
            config,
            new TorrentCacheService(NullLogger<TorrentCacheService>.Instance),
            new DatabaseRestartCoordinator(),
            importMock.Object,
            seedingMock.Object);

        var stats = new TorrentProcessingStats();
        var method = typeof(TorrentProcessor).GetMethod(
            "ProcessSingleTorrentAsync",
            BindingFlags.NonPublic | BindingFlags.Instance)!;

        await (Task)method.Invoke(processor, new object[] { torrent, category, stats, CancellationToken.None })!;

        seedingMock.Verify(
            s => s.HnrAllowsDeleteAsync(torrent, "CF unmet deletion", It.IsAny<CancellationToken>()),
            Times.Once);
        stats.Failed.Should().Be(0, "HnR-blocked CF-unmet torrents must not be deleted");
    }

    [Fact]
    public async Task ProcessSingleTorrentAsync_PendingImport_IsMarkedImportedOnlyAfterArrConfirms()
    {
        const string hash = "pending-import-hash";
        const string category = "radarr-hd";
        const string instance = "qBit";
        _db.TorrentLibrary.Add(new Torrentarr.Infrastructure.Database.Models.TorrentLibrary
        {
            Hash = hash,
            Category = category,
            QbitInstance = instance,
            Imported = false
        });
        await _db.SaveChangesAsync();

        var config = new TorrentarrConfig();
        config.ArrInstances["Radarr-HD"] = new ArrInstanceConfig
        {
            Category = category,
            Type = "radarr"
        };

        var importMock = new Mock<IArrImportService>();
        importMock
            .SetupSequence(s => s.IsImportedAsync(hash, It.IsAny<CancellationToken>()))
            .ReturnsAsync(false)
            .ReturnsAsync(true);
        importMock
            .Setup(s => s.MarkAsImportedAsync(hash, It.IsAny<IEnumerable<string>>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var manager = new QBittorrentConnectionManager(
            NullLogger<QBittorrentConnectionManager>.Instance);
        RegisterTestClient(manager, instance, new StubQBittorrentClient(new TorrentInfo
        {
            Hash = hash,
            Name = "Ready For Import",
            Category = category,
            State = "uploading",
            Progress = 1.0,
            CompletionOn = DateTimeOffset.UtcNow.AddMinutes(-5).ToUnixTimeSeconds(),
            AddedOn = DateTimeOffset.UtcNow.AddHours(-1).ToUnixTimeSeconds(),
            AmountLeft = 0,
            ContentPath = Path.GetTempFileName(),
            QBitInstanceName = instance
        }));

        var cache = new TorrentCacheService(NullLogger<TorrentCacheService>.Instance);
        cache.MarkFileFiltered(hash);
        var pathTracker = new ImportPathTracker();
        pathTracker.MarkScanned("/downloads/completed/item", hash);

        var processor = new TorrentProcessor(
            NullLogger<TorrentProcessor>.Instance,
            manager,
            _db,
            config,
            cache,
            new DatabaseRestartCoordinator(),
            importMock.Object,
            pathTracker: pathTracker);

        var torrent = new TorrentInfo
        {
            Hash = hash,
            Name = "Ready For Import",
            Category = category,
            State = "uploading",
            Progress = 1.0,
            CompletionOn = DateTimeOffset.UtcNow.AddMinutes(-5).ToUnixTimeSeconds(),
            AddedOn = DateTimeOffset.UtcNow.AddHours(-1).ToUnixTimeSeconds(),
            AmountLeft = 0,
            ContentPath = "/downloads/completed/item",
            QBitInstanceName = instance
        };
        var stats = new TorrentProcessingStats();
        var method = typeof(TorrentProcessor).GetMethod(
            "ProcessSingleTorrentAsync",
            BindingFlags.NonPublic | BindingFlags.Instance)!;

        await (Task)method.Invoke(processor, new object[] { torrent, category, stats, CancellationToken.None })!;
        (await _db.TorrentLibrary.SingleAsync(t => t.Hash == hash && t.QbitInstance == instance)).Imported
            .Should().BeFalse("Arr still reports the item in queue on the first pass");

        await (Task)method.Invoke(processor, new object[] { torrent, category, stats, CancellationToken.None })!;
        (await _db.TorrentLibrary.SingleAsync(t => t.Hash == hash && t.QbitInstance == instance)).Imported
            .Should().BeTrue("the database flag should flip only after Arr confirms the scan completed");
        importMock.Verify(
            s => s.MarkAsImportedAsync(hash, It.IsAny<IEnumerable<string>>(), It.IsAny<CancellationToken>()),
            Times.Once);
    }

    /// <summary>
    /// Regression: torrents first seen in a complete/seeding state never run file filtering (Branch 9),
    /// but must still finalize after Arr confirms import once the scan was triggered (f49384b).
    /// </summary>
    [Fact]
    public async Task ProcessSingleTorrentAsync_PendingImport_FinalizesWithoutFileFilterWhenArrConfirms()
    {
        const string hash = "complete-first-hash";
        const string category = "radarr-hd";
        const string instance = "qBit";
        _db.TorrentLibrary.Add(new Torrentarr.Infrastructure.Database.Models.TorrentLibrary
        {
            Hash = hash,
            Category = category,
            QbitInstance = instance,
            Imported = false
        });
        await _db.SaveChangesAsync();

        var config = new TorrentarrConfig();
        config.ArrInstances["Radarr-HD"] = new ArrInstanceConfig
        {
            Category = category,
            Type = "radarr"
        };

        var importMock = new Mock<IArrImportService>();
        importMock
            .Setup(s => s.IsImportedAsync(hash, It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);
        importMock
            .Setup(s => s.MarkAsImportedAsync(hash, It.IsAny<IEnumerable<string>>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var manager = new QBittorrentConnectionManager(
            NullLogger<QBittorrentConnectionManager>.Instance);
        var torrent = new TorrentInfo
        {
            Hash = hash,
            Name = "Cross Seed Complete",
            Category = category,
            State = "uploading",
            Progress = 1.0,
            CompletionOn = DateTimeOffset.UtcNow.AddMinutes(-5).ToUnixTimeSeconds(),
            AddedOn = DateTimeOffset.UtcNow.AddHours(-1).ToUnixTimeSeconds(),
            AmountLeft = 0,
            ContentPath = "/downloads/completed/item",
            QBitInstanceName = instance
        };
        RegisterTestClient(manager, instance, new StubQBittorrentClient(torrent));

        var cache = new TorrentCacheService(NullLogger<TorrentCacheService>.Instance);
        var pathTracker = new ImportPathTracker();
        pathTracker.MarkScanned("/downloads/completed/item", hash);

        var processor = new TorrentProcessor(
            NullLogger<TorrentProcessor>.Instance,
            manager,
            _db,
            config,
            cache,
            new DatabaseRestartCoordinator(),
            importMock.Object,
            pathTracker: pathTracker);

        var stats = new TorrentProcessingStats();
        var method = typeof(TorrentProcessor).GetMethod(
            "ProcessSingleTorrentAsync",
            BindingFlags.NonPublic | BindingFlags.Instance)!;

        cache.IsFileFiltered(hash).Should().BeFalse("complete-first torrents skip Branch 9 file filtering");

        await (Task)method.Invoke(processor, new object[] { torrent, category, stats, CancellationToken.None })!;
        (await _db.TorrentLibrary.SingleAsync(t => t.Hash == hash && t.QbitInstance == instance)).Imported
            .Should().BeTrue("pending imports must finalize without requiring IsFileFiltered");
    }

    private static void RegisterTestClient(
        QBittorrentConnectionManager manager,
        string instanceName,
        QBittorrentClient client)
    {
        var clientsField = typeof(QBittorrentConnectionManager)
            .GetField("_clients", BindingFlags.NonPublic | BindingFlags.Instance)!;
        var clients = (Dictionary<string, QBittorrentClient>)clientsField.GetValue(manager)!;
        clients[instanceName] = client;
    }

    private sealed class StubQBittorrentClient : QBittorrentClient
    {
        private readonly List<TorrentInfo> _torrents;

        public StubQBittorrentClient(params TorrentInfo[] torrents)
            : base("127.0.0.1", 1, "u", "p")
        {
            _torrents = torrents.ToList();
        }

        public override Task<List<TorrentInfo>> GetTorrentsAsync(
            string? category = null,
            string? sort = null,
            CancellationToken cancellationToken = default)
            => Task.FromResult(_torrents);
    }
}

using System.Collections.Generic;
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
            new TorrentCacheService(NullLogger<TorrentCacheService>.Instance));
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
}

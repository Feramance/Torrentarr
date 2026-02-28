using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Torrentarr.Core.Configuration;
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
}

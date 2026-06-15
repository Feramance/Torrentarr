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
/// Regression: CF-unmet deletion must respect Hit &amp; Run protection (qBitrr _hnr_allows_delete parity).
/// </summary>
public sealed class TorrentProcessorCfUnmetHnrTests : IDisposable
{
    private readonly string _dbName;
    private readonly SqliteConnection _keepAlive;
    private readonly TorrentarrDbContext _db;

    public TorrentProcessorCfUnmetHnrTests()
    {
        _dbName = $"tproc-cf-hnr-{Guid.NewGuid():N}";
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

    [Fact]
    public async Task ProcessSingleTorrent_CustomFormatUnmet_ConsultsHnrBeforeDelete()
    {
        const string hash = "abc123def456789abc123def456789abc123def";
        const string category = "radarr-hd";
        var torrent = new TorrentInfo
        {
            Hash = hash,
            Name = "Test Movie",
            Category = category,
            State = "uploading",
            Progress = 1.0,
            QBitInstanceName = "qBit",
            AddedOn = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
        };

        var config = new TorrentarrConfig();
        config.ArrInstances["Radarr-hd"] = new ArrInstanceConfig
        {
            Category = category,
            Search = new SearchConfig { CustomFormatUnmetSearch = true },
        };
        config.QBitInstances["qBit"] = new QBitConfig
        {
            Host = "127.0.0.1",
            Port = 1,
            UserName = "u",
            Password = "p",
        };

        var mockImport = new Mock<IArrImportService>();
        mockImport
            .Setup(i => i.IsCustomFormatUnmetAsync(hash, category, It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        var mockSeeding = new Mock<ISeedingService>();
        mockSeeding
            .Setup(s => s.ApplyTrackerActionsForTorrentAsync(It.IsAny<TorrentInfo>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        mockSeeding
            .Setup(s => s.HnrAllowsDeleteAsync(torrent, "CF unmet deletion", It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);

        var manager = new QBittorrentConnectionManager(NullLogger<QBittorrentConnectionManager>.Instance);
        RegisterClient(manager, "qBit", new QBittorrentClient("127.0.0.1", 1, "u", "p"));

        var processor = new TorrentProcessor(
            NullLogger<TorrentProcessor>.Instance,
            manager,
            _db,
            config,
            new TorrentCacheService(NullLogger<TorrentCacheService>.Instance),
            mockImport.Object,
            mockSeeding.Object);

        var stats = new TorrentProcessingStats();
        await InvokeProcessSingleTorrentAsync(processor, torrent, category, stats);

        mockSeeding.Verify(
            s => s.HnrAllowsDeleteAsync(torrent, "CF unmet deletion", It.IsAny<CancellationToken>()),
            Times.Once,
            "CF-unmet deletion must consult HnR before deleting (qBitrr _process_single_torrent_delete_cfunmet parity)");
    }

    private static void RegisterClient(QBittorrentConnectionManager manager, string name, QBittorrentClient client)
    {
        var field = typeof(QBittorrentConnectionManager)
            .GetField("_clients", BindingFlags.NonPublic | BindingFlags.Instance);
        var clients = (Dictionary<string, QBittorrentClient>)field!.GetValue(manager)!;
        clients[name] = client;
    }

    private static async Task InvokeProcessSingleTorrentAsync(
        TorrentProcessor processor,
        TorrentInfo torrent,
        string category,
        TorrentProcessingStats stats)
    {
        var method = typeof(TorrentProcessor).GetMethod(
            "ProcessSingleTorrentAsync",
            BindingFlags.NonPublic | BindingFlags.Instance);
        var task = (Task)method!.Invoke(processor, new object?[] { torrent, category, stats, CancellationToken.None })!;
        await task;
    }
}

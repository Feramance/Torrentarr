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
/// Tests for ArrSyncService that exercise only the code paths that do NOT
/// make real network calls — i.e., early-return branches when an instance is
/// not found in the config, has an unconfigured URI, or has an unknown type.
/// </summary>
public sealed class ArrSyncServiceTests : IDisposable
{
    private readonly string _dbName;
    private readonly SqliteConnection _keepAlive;
    private readonly TorrentarrDbContext _db;

    public ArrSyncServiceTests()
    {
        _dbName = $"arrsync-{Guid.NewGuid():N}";
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

    private ArrSyncService CreateService(TorrentarrConfig? config = null)
    {
        config ??= new TorrentarrConfig();
        return new ArrSyncService(NullLogger<ArrSyncService>.Instance, config, _db);
    }

    private static ArrInstanceConfig MakeInstance(string type, string uri = "http://localhost:7878")
        => new() { Category = "test-cat", Type = type, URI = uri, APIKey = "key" };

    // ── SyncAsync – fast exits ─────────────────────────────────────────────────

    [Fact]
    public async Task SyncAsync_InstanceNotFound_DoesNotThrow()
    {
        var svc = CreateService();

        var act = async () => await svc.SyncAsync("nonexistent");

        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task SyncAsync_UnconfiguredUri_ChangeMeValue_DoesNotThrow()
    {
        var config = new TorrentarrConfig
        {
            ArrInstances = new Dictionary<string, ArrInstanceConfig>
            {
                ["test"] = MakeInstance("radarr", "CHANGE_ME")
            }
        };
        var svc = CreateService(config);

        var act = async () => await svc.SyncAsync("test");

        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task SyncAsync_EmptyUri_DoesNotThrow()
    {
        var config = new TorrentarrConfig
        {
            ArrInstances = new Dictionary<string, ArrInstanceConfig>
            {
                ["test"] = MakeInstance("sonarr", "")
            }
        };
        var svc = CreateService(config);

        var act = async () => await svc.SyncAsync("test");

        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task SyncAsync_UnknownArrType_DoesNotThrow()
    {
        // Switch default branch: logs a warning and returns, no network call.
        var config = new TorrentarrConfig
        {
            ArrInstances = new Dictionary<string, ArrInstanceConfig>
            {
                ["test"] = MakeInstance("plex")
            }
        };
        var svc = CreateService(config);

        var act = async () => await svc.SyncAsync("test");

        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task SyncAsync_PreCancelledToken_InstanceNotFound_DoesNotThrow()
    {
        var svc = CreateService();
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        // No async work is started when the instance is not found, so the
        // pre-cancelled token is irrelevant and must not cause an exception.
        var act = async () => await svc.SyncAsync("nonexistent", cts.Token);

        await act.Should().NotThrowAsync();
    }

    // ── SyncQueueAsync – fast exits ────────────────────────────────────────────

    [Fact]
    public async Task SyncQueueAsync_InstanceNotFound_DoesNotThrow()
    {
        var svc = CreateService();

        var act = async () => await svc.SyncQueueAsync("nonexistent");

        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task SyncQueueAsync_UnconfiguredUri_ChangeMeValue_DoesNotThrow()
    {
        var config = new TorrentarrConfig
        {
            ArrInstances = new Dictionary<string, ArrInstanceConfig>
            {
                ["test"] = MakeInstance("radarr", "CHANGE_ME")
            }
        };
        var svc = CreateService(config);

        var act = async () => await svc.SyncQueueAsync("test");

        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task SyncQueueAsync_EmptyUri_DoesNotThrow()
    {
        var config = new TorrentarrConfig
        {
            ArrInstances = new Dictionary<string, ArrInstanceConfig>
            {
                ["test"] = MakeInstance("sonarr", "")
            }
        };
        var svc = CreateService(config);

        var act = async () => await svc.SyncQueueAsync("test");

        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task SyncQueueAsync_UnknownArrType_DoesNotThrow()
    {
        // The switch has no default case for unknown types — falls through silently.
        var config = new TorrentarrConfig
        {
            ArrInstances = new Dictionary<string, ArrInstanceConfig>
            {
                ["test"] = MakeInstance("plex")
            }
        };
        var svc = CreateService(config);

        var act = async () => await svc.SyncQueueAsync("test");

        await act.Should().NotThrowAsync();
    }
}

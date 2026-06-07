using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Torrentarr.Infrastructure.Database;
using Torrentarr.Infrastructure.Database.Models;
using Xunit;

namespace Torrentarr.Infrastructure.Tests.Database;

public class TorrentLibraryFreeSpaceHelperTests : IDisposable
{
    private readonly string _dbName;
    private readonly SqliteConnection _keepAlive;
    private readonly TorrentarrDbContext _db;

    public TorrentLibraryFreeSpaceHelperTests()
    {
        _dbName = $"fsp-{Guid.NewGuid():N}";
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
    public async Task SetFreeSpacePausedAsync_NoExistingRow_UpsertsWithPausedTrue()
    {
        await TorrentLibraryFreeSpaceHelper.SetFreeSpacePausedAsync(
            _db, "abc123", "radarr-1080", "qBit", paused: true);

        var entry = await _db.TorrentLibrary.SingleAsync(t => t.Hash == "abc123");
        entry.FreeSpacePaused.Should().BeTrue();
        entry.Category.Should().Be("radarr-1080");
        entry.QbitInstance.Should().Be("qBit");
    }

    [Fact]
    public async Task SetFreeSpacePausedAsync_ExistingRow_UpdatesPausedFlag()
    {
        _db.TorrentLibrary.Add(new TorrentLibrary
        {
            Hash = "def456",
            Category = "sonarr-tv",
            QbitInstance = "qBit",
            FreeSpacePaused = false,
        });
        await _db.SaveChangesAsync();

        await TorrentLibraryFreeSpaceHelper.SetFreeSpacePausedAsync(
            _db, "def456", "sonarr-tv", "qBit", paused: true);

        _db.ChangeTracker.Clear();
        var entry = await _db.TorrentLibrary.AsNoTracking().SingleAsync(t => t.Hash == "def456");
        entry.FreeSpacePaused.Should().BeTrue();
    }

    [Fact]
    public async Task SetFreeSpacePausedAsync_ClearOnMissingRow_IsNoOp()
    {
        await TorrentLibraryFreeSpaceHelper.SetFreeSpacePausedAsync(
            _db, "missing", "radarr-1080", "qBit", paused: false);

        (await _db.TorrentLibrary.CountAsync()).Should().Be(0);
    }
}

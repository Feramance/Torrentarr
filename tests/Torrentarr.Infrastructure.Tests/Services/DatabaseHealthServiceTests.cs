using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Torrentarr.Infrastructure.Database;
using Torrentarr.Infrastructure.Services;
using Xunit;

namespace Torrentarr.Infrastructure.Tests.Services;

/// <summary>
/// Tests for DatabaseHealthService using a named in-memory SQLite database.
/// Each test instance gets its own isolated in-memory DB via a unique GUID name.
/// The keepalive connection prevents the named in-memory database from being
/// destroyed between the OpenAsync/CloseAsync calls the service makes internally.
/// </summary>
public sealed class DatabaseHealthServiceTests : IDisposable
{
    private readonly string _dbName;
    private readonly SqliteConnection _keepAlive;
    private readonly TorrentarrDbContext _dbContext;
    private readonly DatabaseHealthService _svc;

    public DatabaseHealthServiceTests()
    {
        // Unique name per test instance — named shared-cache keeps data alive across
        // multiple connection open/close cycles within the test.
        _dbName = $"dbhealth-{Guid.NewGuid():N}";
        var connectionString = $"Data Source={_dbName};Mode=Memory;Cache=Shared";

        // Keep one connection open so the named in-memory database is not destroyed
        // when the service closes its own connections internally.
        _keepAlive = new SqliteConnection(connectionString);
        _keepAlive.Open();

        var options = new DbContextOptionsBuilder<TorrentarrDbContext>()
            .UseSqlite(connectionString)
            .Options;

        _dbContext = new TorrentarrDbContext(options);
        _dbContext.Database.EnsureCreated();

        _svc = new DatabaseHealthService(
            NullLogger<DatabaseHealthService>.Instance, _dbContext);
    }

    public void Dispose()
    {
        _dbContext.Dispose();
        _keepAlive.Dispose();
    }

    // ── CheckHealthAsync ──────────────────────────────────────────────────────

    [Fact]
    public async Task BackupNormalizesPathsAndPreservesExistingCopyOnFailure()
    {
        var directory = Directory.CreateTempSubdirectory("torrentarr-backup-");
        try
        {
            var source = Path.Combine(directory.FullName, "source.db");
            var destination = Path.Combine(directory.FullName, "backup.db");
            var logger = NullLogger<DatabaseHealthService>.Instance;
            await using (var connection = new SqliteConnection($"Data Source={source};Pooling=False"))
            {
                await connection.OpenAsync();
                await using var command = connection.CreateCommand();
                command.CommandText = "CREATE TABLE test (value INTEGER); INSERT INTO test VALUES (42);";
                await command.ExecuteNonQueryAsync();
            }
            (await DatabaseHealthService.BackupFileAsync(source, Path.GetRelativePath(Directory.GetCurrentDirectory(), destination), logger)).Should().BeTrue();
            (await DatabaseHealthService.BackupFileAsync(source, source, logger)).Should().BeFalse();
            var previous = await File.ReadAllBytesAsync(destination);
            await File.WriteAllTextAsync(source, "invalid sqlite");
            (await DatabaseHealthService.BackupFileAsync(source, destination, logger)).Should().BeFalse();
            (await File.ReadAllBytesAsync(destination)).Should().Equal(previous);
            directory.GetFiles().Should().HaveCount(2);
        }
        finally { directory.Delete(recursive: true); }
    }

    [Fact]
    public void DatabaseRestartRequiresAContinuousErrorWindow()
    {
        var clock = new RestartClock();
        var coordinator = new DatabaseRestartCoordinator { Clock = clock };
        coordinator.RecordDatabaseError();
        clock.Now = clock.Now.AddMinutes(4);
        coordinator.RecordDatabaseError();
        coordinator.RecordDatabaseSuccess();
        clock.Now = clock.Now.AddMinutes(2);
        coordinator.RecordDatabaseError();
        coordinator.RestartRequested.Should().BeFalse();
        clock.Now = clock.Now.AddMinutes(4);
        coordinator.RecordDatabaseError();
        clock.Now = clock.Now.AddMinutes(2);
        coordinator.RecordDatabaseError();
        coordinator.RestartRequested.Should().BeTrue();
    }

    private sealed class RestartClock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = DateTimeOffset.UnixEpoch;
        public override DateTimeOffset GetUtcNow() => Now;
    }

    [Fact]
    public async Task CheckHealthAsync_InMemoryDb_IsHealthy()
    {
        var result = await _svc.CheckHealthAsync();

        result.IsHealthy.Should().BeTrue();
    }

    [Fact]
    public async Task CheckHealthAsync_InMemoryDb_MessageIndicatesPassed()
    {
        var result = await _svc.CheckHealthAsync();

        result.Message.Should().Contain("passed");
    }

    [Fact]
    public async Task CheckHealthAsync_CheckedAt_IsRecentUtcNow()
    {
        var before = DateTime.UtcNow.AddSeconds(-1);

        var result = await _svc.CheckHealthAsync();

        result.CheckedAt.Should().BeOnOrAfter(before);
    }

    [Fact]
    public async Task CheckHealthAsync_WithCancellationToken_DoesNotThrow()
    {
        using var cts = new CancellationTokenSource();
        var act = async () => await _svc.CheckHealthAsync(cts.Token);

        await act.Should().NotThrowAsync();
    }

    // ── CheckpointWalAsync ────────────────────────────────────────────────────

    [Fact]
    public async Task CheckpointWalAsync_InMemoryDb_ReturnsTrue()
    {
        // WAL checkpoint on a non-WAL in-memory DB returns 0 pages busy —
        // the service always returns true from this branch.
        var result = await _svc.CheckpointWalAsync();

        result.Should().BeTrue();
    }

    [Fact]
    public async Task CheckpointWalAsync_WithCancellationToken_DoesNotThrow()
    {
        using var cts = new CancellationTokenSource();
        var act = async () => await _svc.CheckpointWalAsync(cts.Token);

        await act.Should().NotThrowAsync();
    }

    // ── VacuumAsync ───────────────────────────────────────────────────────────

    [Fact]
    public async Task VacuumAsync_InMemoryDb_ReturnsTrue()
    {
        var result = await _svc.VacuumAsync();

        result.Should().BeTrue();
    }

    [Fact]
    public async Task VacuumAsync_WithCancellationToken_DoesNotThrow()
    {
        using var cts = new CancellationTokenSource();
        var act = async () => await _svc.VacuumAsync(cts.Token);

        await act.Should().NotThrowAsync();
    }

    // ── GetStatsAsync ─────────────────────────────────────────────────────────

    [Fact]
    public async Task GetStatsAsync_InMemoryDb_DatabasePathMatchesDbName()
    {
        var stats = await _svc.GetStatsAsync();

        // GetDatabasePath() parses the connection string; for "Data Source=<name>;..."
        // SqliteConnectionStringBuilder.DataSource returns the bare name.
        stats.DatabasePath.Should().Be(_dbName);
    }

    [Fact]
    public async Task GetStatsAsync_InMemoryDb_HasPositivePageCount()
    {
        var stats = await _svc.GetStatsAsync();

        stats.PageCount.Should().BeGreaterThan(0);
    }

    [Fact]
    public async Task GetStatsAsync_InMemoryDb_HasPositivePageSize()
    {
        var stats = await _svc.GetStatsAsync();

        stats.PageSize.Should().BeGreaterThan(0);
    }

    [Fact]
    public async Task GetStatsAsync_InMemoryDb_HasJournalMode()
    {
        var stats = await _svc.GetStatsAsync();

        stats.JournalMode.Should().NotBeEmpty();
    }

    [Fact]
    public async Task GetStatsAsync_InMemoryDb_SizeBytesIsZero()
    {
        // Named in-memory SQLite has no physical file so FileInfo.Exists == false
        // → SizeBytes stays at 0.
        var stats = await _svc.GetStatsAsync();

        stats.SizeBytes.Should().Be(0);
    }

    [Fact]
    public async Task GetStatsAsync_WithCancellationToken_DoesNotThrow()
    {
        using var cts = new CancellationTokenSource();
        var act = async () => await _svc.GetStatsAsync(cts.Token);

        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task MaintainAsync_InMemoryDb_ReturnsTrue()
    {
        var result = await _svc.MaintainAsync();
        result.Should().BeTrue();
    }

    [Fact]
    public async Task MaintainAsync_BusyCheckpointPreservesCommittedWalRows()
    {
        var directory = Directory.CreateTempSubdirectory("torrentarr-wal-");
        try
        {
            var path = Path.Combine(directory.FullName, "test.db");
            var connectionString = new SqliteConnectionStringBuilder
            {
                DataSource = path,
                Pooling = false,
                DefaultTimeout = 1
            }.ToString();
            await using var writer = new SqliteConnection(connectionString);
            await writer.OpenAsync();
            await using var write = writer.CreateCommand();
            write.CommandText = "PRAGMA journal_mode=WAL; CREATE TABLE sample (value INTEGER); INSERT INTO sample VALUES (1);";
            await write.ExecuteNonQueryAsync();

            await using var reader = new SqliteConnection(connectionString);
            await reader.OpenAsync();
            await using var read = reader.CreateCommand();
            read.CommandText = "BEGIN;";
            await read.ExecuteNonQueryAsync();
            read.CommandText = "SELECT COUNT(*) FROM sample;";
            (await read.ExecuteScalarAsync()).Should().Be(1L);

            write.CommandText = "INSERT INTO sample VALUES (2);";
            await write.ExecuteNonQueryAsync();
            write.CommandText = "PRAGMA wal_checkpoint(TRUNCATE);";
            (await write.ExecuteScalarAsync()).Should().Be(1L, "the reader still holds the older snapshot");

            var options = new DbContextOptionsBuilder<TorrentarrDbContext>().UseSqlite(connectionString).Options;
            await using var db = new TorrentarrDbContext(options);
            var health = new DatabaseHealthService(NullLogger<DatabaseHealthService>.Instance, db);
            (await health.MaintainAsync()).Should().BeTrue();

            File.Exists(path + "-wal").Should().BeTrue();
            await using var verification = new SqliteConnection(connectionString);
            await verification.OpenAsync();
            await using var count = verification.CreateCommand();
            count.CommandText = "SELECT COUNT(*) FROM sample;";
            (await count.ExecuteScalarAsync()).Should().Be(2L);
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }
}

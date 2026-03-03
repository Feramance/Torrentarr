using System.Data;
using Torrentarr.Core.Services;
using Torrentarr.Infrastructure.Database;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.Logging;

namespace Torrentarr.Infrastructure.Services;

/// <summary>
/// Service for database health monitoring and maintenance.
/// Implements WAL checkpoint, VACUUM, and repair operations.
/// </summary>
public class DatabaseHealthService : IDatabaseHealthService
{
    private readonly ILogger<DatabaseHealthService> _logger;
    private readonly TorrentarrDbContext _dbContext;
    private readonly string _dbPath;

    public DatabaseHealthService(
        ILogger<DatabaseHealthService> logger,
        TorrentarrDbContext dbContext)
    {
        _logger = logger;
        _dbContext = dbContext;
        _dbPath = GetDatabasePath();
    }

    public async Task<DatabaseHealthResult> CheckHealthAsync(CancellationToken cancellationToken = default)
    {
        var result = new DatabaseHealthResult
        {
            CheckedAt = DateTime.UtcNow
        };

        try
        {
            var stats = await GetStatsAsync(cancellationToken);
            result.SizeBytes = stats.SizeBytes;
            result.WalSizeBytes = stats.WalSizeBytes;
            result.PageCount = stats.PageCount;

            var connection = _dbContext.Database.GetDbConnection();
            await connection.OpenAsync(cancellationToken);

            await using var command = connection.CreateCommand();
            command.CommandText = "PRAGMA integrity_check";
            var checkResult = await command.ExecuteScalarAsync(cancellationToken);
            await connection.CloseAsync();

            if (checkResult?.ToString() == "ok")
            {
                result.IsHealthy = true;
                result.Message = "Database integrity check passed";
                _logger.LogDebug("Database health check passed: {Size}MB, WAL: {WalSize}MB",
                    result.SizeBytes / 1024.0 / 1024.0,
                    result.WalSizeBytes / 1024.0 / 1024.0);
            }
            else
            {
                result.IsHealthy = false;
                result.Message = $"Integrity check failed: {checkResult}";
                _logger.LogWarning("Database integrity check failed: {Result}", checkResult);
            }
        }
        catch (Exception ex)
        {
            result.IsHealthy = false;
            result.Message = $"Health check error: {ex.Message}";
            _logger.LogError(ex, "Error checking database health");
        }

        return result;
    }

    public async Task<bool> CheckpointWalAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            _logger.LogInformation("Starting WAL checkpoint for database: {Path}", _dbPath);

            var connection = _dbContext.Database.GetDbConnection();
            await connection.OpenAsync(cancellationToken);

            await using var command = connection.CreateCommand();
            command.CommandText = "PRAGMA wal_checkpoint(TRUNCATE)";
            var result = await command.ExecuteReaderAsync(cancellationToken);

            if (await result.ReadAsync(cancellationToken))
            {
                var busy = result.GetInt32(0);
                var logPages = result.GetInt32(1);
                var checkpointedPages = result.GetInt32(2);

                await connection.CloseAsync();

                if (busy == 0)
                {
                    _logger.LogInformation(
                        "WAL checkpoint successful: {Checkpointed} frames checkpointed, {LogPages} pages in log",
                        checkpointedPages, logPages);
                    return true;
                }
                else
                {
                    _logger.LogWarning(
                        "WAL checkpoint partially successful: busy={Busy}, log={LogPages}, checkpointed={CheckpointedPages}",
                        busy, logPages, checkpointedPages);
                    return true;
                }
            }

            await connection.CloseAsync();
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "WAL checkpoint failed");
            return false;
        }
    }

    public async Task<bool> VacuumAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var stats = await GetStatsAsync(cancellationToken);
            _logger.LogInformation(
                "Running VACUUM on database: {Path} (current size: {Size}MB)",
                _dbPath, stats.SizeBytes / 1024.0 / 1024.0);

            var connection = _dbContext.Database.GetDbConnection();
            await connection.OpenAsync(cancellationToken);

            await using var command = connection.CreateCommand();
            command.CommandTimeout = 300;
            command.CommandText = "VACUUM";
            await command.ExecuteNonQueryAsync(cancellationToken);

            await connection.CloseAsync();

            var newStats = await GetStatsAsync(cancellationToken);
            var savedBytes = stats.SizeBytes - newStats.SizeBytes;

            _logger.LogInformation(
                "VACUUM completed successfully. Size reduced from {OldSize}MB to {NewSize}MB (saved {Saved}MB)",
                stats.SizeBytes / 1024.0 / 1024.0,
                newStats.SizeBytes / 1024.0 / 1024.0,
                savedBytes / 1024.0 / 1024.0);

            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "VACUUM failed");
            return false;
        }
    }

    public async Task<bool> RepairAsync(CancellationToken cancellationToken = default)
    {
        _logger.LogWarning("Attempting database repair via dump/restore...");

        var backupPath = $"{_dbPath}.backup";
        var tempPath = $"{_dbPath}.temp";

        try
        {
            if (File.Exists(_dbPath))
            {
                _logger.LogInformation("Creating backup: {BackupPath}", backupPath);
                File.Copy(_dbPath, backupPath, overwrite: true);
            }

            _logger.LogInformation("Dumping recoverable data from database...");

            var sourceConn = _dbContext.Database.GetDbConnection();
            await sourceConn.OpenAsync(cancellationToken);

            var tempConn = new SqliteConnection($"Data Source={tempPath}");
            await tempConn.OpenAsync(cancellationToken);

            var dumpCommand = sourceConn.CreateCommand();
            dumpCommand.CommandText = ".dump";

            await using var tempCmd = tempConn.CreateCommand();
            tempCmd.CommandText = dumpCommand.CommandText;

            await tempConn.CloseAsync();
            await sourceConn.CloseAsync();

            _logger.LogInformation("Database repair completed successfully");
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Database repair failed");

            if (File.Exists(backupPath))
            {
                _logger.LogWarning("Restoring from backup...");
                try
                {
                    File.Copy(backupPath, _dbPath, overwrite: true);
                    _logger.LogInformation("Backup restored successfully");
                }
                catch (Exception restoreEx)
                {
                    _logger.LogError(restoreEx, "Failed to restore backup");
                }
            }

            return false;
        }
        finally
        {
            if (File.Exists(tempPath))
            {
                try { File.Delete(tempPath); } catch { }
            }
        }
    }

    public async Task<DatabaseStats> GetStatsAsync(CancellationToken cancellationToken = default)
    {
        var stats = new DatabaseStats
        {
            DatabasePath = _dbPath
        };

        try
        {
            var fileInfo = new FileInfo(_dbPath);
            if (fileInfo.Exists)
            {
                stats.SizeBytes = fileInfo.Length;
            }

            var walPath = $"{_dbPath}-wal";
            if (File.Exists(walPath))
            {
                stats.WalSizeBytes = new FileInfo(walPath).Length;
            }

            var connection = _dbContext.Database.GetDbConnection();
            await connection.OpenAsync(cancellationToken);

            await using (var command = connection.CreateCommand())
            {
                command.CommandText = "PRAGMA page_count";
                stats.PageCount = Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken));
            }

            await using (var command = connection.CreateCommand())
            {
                command.CommandText = "PRAGMA page_size";
                stats.PageSize = Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken));
            }

            await using (var command = connection.CreateCommand())
            {
                command.CommandText = "PRAGMA freelist_count";
                stats.FreePages = Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken));
            }

            await using (var command = connection.CreateCommand())
            {
                command.CommandText = "PRAGMA journal_mode";
                stats.JournalMode = (await command.ExecuteScalarAsync(cancellationToken))?.ToString() ?? "";
            }

            await connection.CloseAsync();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting database stats");
        }

        return stats;
    }

    private string GetDatabasePath()
    {
        var connection = _dbContext.Database.GetDbConnection();
        var connectionString = connection.ConnectionString;

        var builder = new SqliteConnectionStringBuilder(connectionString);
        return builder.DataSource;
    }
}

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
    private readonly IOperationalIssueReporter? _issues;

    public DatabaseHealthService(
        ILogger<DatabaseHealthService> logger,
        TorrentarrDbContext dbContext, IOperationalIssueReporter? issues = null)
    {
        _logger = logger;
        _issues = issues;
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
            result.Message = $"Health check error: {DiagnosticText.Error(ex)}";
            _logger.LogError(ex, "Error checking database health");
        }

        if (_issues != null)
        {
            var issue = new OperationalIssueReport("database.health", "system", "Database health could not be confirmed", "Persistent state may be unavailable; safety-sensitive work remains held back.", "Inspect database logs, storage access and recovery status.");
            if (result.IsHealthy) await _issues.ResolveAsync(issue, cancellationToken); else await _issues.ReportAsync(issue.WithEvidence($"Database={_dbPath}; checked={result.CheckedAt:O}; size={result.SizeBytes} bytes; WAL={result.WalSizeBytes} bytes; pages={result.PageCount}; {result.Message}"), cancellationToken);
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

    public Task<bool> BackupAsync(string destPath, CancellationToken cancellationToken = default)
        => BackupFileAsync(_dbPath, destPath, _logger, cancellationToken);

    public static async Task<bool> BackupFileAsync(string sourcePath, string destinationPath, ILogger logger, CancellationToken ct = default)
    {
        string? temporary = null;
        try
        {
            if (!File.Exists(sourcePath)) throw new FileNotFoundException("Database file not found.", sourcePath);
            var destination = Path.GetFullPath(destinationPath);
            if (File.Exists(destination) && string.Equals(ResolvePath(new FileInfo(sourcePath)),
                ResolvePath(new FileInfo(destination)), OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
                throw new IOException("Backup destination resolves to the source database.");
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            temporary = destination + "." + Guid.NewGuid().ToString("N") + ".tmp";

            static SqliteConnection Open(string path, SqliteOpenMode mode) => new(
                new SqliteConnectionStringBuilder { DataSource = path, Mode = mode, Pooling = false }.ToString());
            await using (var source = Open(sourcePath, SqliteOpenMode.ReadOnly))
            await using (var backup = Open(temporary, SqliteOpenMode.ReadWriteCreate))
            {
                await source.OpenAsync(ct);
                await backup.OpenAsync(ct);
                source.BackupDatabase(backup);
                await using var check = backup.CreateCommand();
                check.CommandText = "PRAGMA quick_check";
                if (!string.Equals((await check.ExecuteScalarAsync(ct))?.ToString(), "ok", StringComparison.OrdinalIgnoreCase))
                    throw new IOException("Database backup failed integrity quick_check.");
            }

            ct.ThrowIfCancellationRequested();
            File.Move(temporary, destination, overwrite: true);
            logger.LogInformation("Database backup complete: {Destination}", destination);
            return true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Database backup failed");
            return false;
        }
        finally
        {
            if (temporary != null && File.Exists(temporary)) File.Delete(temporary);
        }

        static string ResolvePath(FileSystemInfo item)
        {
            if (item.ResolveLinkTarget(returnFinalTarget: true) is { } target)
                return ResolvePath(target);
            var parent = item is DirectoryInfo directory ? directory.Parent : ((FileInfo)item).Directory;
            return parent == null ? item.FullName : Path.Combine(ResolvePath(parent), item.Name);
        }
    }

    public async Task<bool> MaintainAsync(CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(_dbPath) || !File.Exists(_dbPath))
            return true;

        if (!await CheckpointWalAsync(cancellationToken))
            _logger.LogWarning("WAL checkpoint failed during database maintenance");

        var health = await CheckHealthAsync(cancellationToken);
        if (health.IsHealthy)
        {
            _logger.LogDebug("Database maintenance: health check passed");
            return true;
        }

        _logger.LogError("Database health check failed during maintenance: {Message}. Stop Torrentarr and its workers before recovering the database offline.", health.Message);
        return false;
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

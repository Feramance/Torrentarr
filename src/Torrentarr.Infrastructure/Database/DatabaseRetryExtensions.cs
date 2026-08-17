using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Torrentarr.Infrastructure.Services;

namespace Torrentarr.Infrastructure.Database;

/// <summary>
/// qBitrr <c>with_database_retry</c> parity for transient SQLite errors.
/// </summary>
public static class DatabaseRetryExtensions
{
    public static async Task<int> SaveChangesWithRetryAsync(
        this DbContext context,
        ILogger? logger = null,
        DatabaseRestartCoordinator? restartCoordinator = null,
        int maxAttempts = 5,
        CancellationToken cancellationToken = default)
    {
        for (var attempt = 0; attempt < maxAttempts; attempt++)
        {
            try
            {
                var result = await context.SaveChangesAsync(cancellationToken);
                restartCoordinator?.RecordDatabaseSuccess();
                return result;
            }
            catch (Exception ex) when (IsRetriable(ex) && attempt < maxAttempts - 1)
            {
                restartCoordinator?.RecordDatabaseError();

                var delay = TimeSpan.FromMilliseconds(500 * Math.Pow(2, attempt));
                logger?.LogWarning(ex, "Database save retry {Attempt}/{Max}, waiting {Delay}ms",
                    attempt + 1, maxAttempts, delay.TotalMilliseconds);
                await Task.Delay(delay, cancellationToken);

                if (IsCorruption(ex) && context.Database.GetDbConnection() is SqliteConnection sqlite)
                {
                    try
                    {
                        await context.Database.CloseConnectionAsync();
                        await using var conn = new SqliteConnection(sqlite.ConnectionString);
                        await conn.OpenAsync(cancellationToken);
                        await using var cmd = conn.CreateCommand();
                        cmd.CommandText = "PRAGMA wal_checkpoint(TRUNCATE)";
                        await cmd.ExecuteNonQueryAsync(cancellationToken);
                    }
                    catch (Exception repairEx)
                    {
                        logger?.LogWarning(repairEx, "WAL checkpoint during DB retry failed");
                    }
                }
            }
            catch (Exception ex) when (IsRetriable(ex))
            {
                restartCoordinator?.RecordDatabaseError();
                throw;
            }
        }

        return await context.SaveChangesAsync(cancellationToken);
    }

    public static bool IsSqliteCorruption(Exception ex)
    {
        var current = ex;
        while (current != null)
        {
            var msg = current.Message;
            if (msg.Contains("disk image is malformed", StringComparison.OrdinalIgnoreCase)
                || msg.Contains("database disk image is malformed", StringComparison.OrdinalIgnoreCase)
                || msg.Contains("database corruption", StringComparison.OrdinalIgnoreCase)
                || msg.Contains("malformed", StringComparison.OrdinalIgnoreCase))
                return true;
            current = current.InnerException;
        }
        return false;
    }

    private static bool IsRetriable(Exception ex) =>
        ex is DbUpdateException or SqliteException
        && (ex.Message.Contains("database is locked", StringComparison.OrdinalIgnoreCase)
            || ex.Message.Contains("disk I/O", StringComparison.OrdinalIgnoreCase)
            || ex.Message.Contains("malformed", StringComparison.OrdinalIgnoreCase));

    private static bool IsCorruption(Exception ex) => IsSqliteCorruption(ex);
}

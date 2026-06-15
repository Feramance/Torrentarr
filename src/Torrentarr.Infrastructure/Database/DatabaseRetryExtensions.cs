using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Torrentarr.Infrastructure.Database;

/// <summary>
/// qBitrr <c>with_database_retry</c> parity for transient SQLite errors.
/// </summary>
public static class DatabaseRetryExtensions
{
    public static async Task<int> SaveChangesWithRetryAsync(
        this DbContext context,
        ILogger? logger = null,
        int maxAttempts = 5,
        CancellationToken cancellationToken = default)
    {
        for (var attempt = 0; attempt < maxAttempts; attempt++)
        {
            try
            {
                return await context.SaveChangesAsync(cancellationToken);
            }
            catch (Exception ex) when (IsRetriable(ex) && attempt < maxAttempts - 1)
            {
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
        }

        return await context.SaveChangesAsync(cancellationToken);
    }

    private static bool IsRetriable(Exception ex) =>
        ex is DbUpdateException or SqliteException
        && (ex.Message.Contains("database is locked", StringComparison.OrdinalIgnoreCase)
            || ex.Message.Contains("disk I/O", StringComparison.OrdinalIgnoreCase)
            || ex.Message.Contains("malformed", StringComparison.OrdinalIgnoreCase));

    private static bool IsCorruption(Exception ex) =>
        ex.Message.Contains("malformed", StringComparison.OrdinalIgnoreCase)
        || ex.Message.Contains("disk I/O", StringComparison.OrdinalIgnoreCase);
}

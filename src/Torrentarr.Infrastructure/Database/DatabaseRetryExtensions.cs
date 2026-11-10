using Microsoft.EntityFrameworkCore.Query;
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
    /// <summary>Bulk updates bypass SaveChanges, so retry transient SQLite writer contention here.</summary>
    public static async Task<int> ExecuteUpdateWithRetryAsync<T>(this IQueryable<T> query,
        Action<UpdateSettersBuilder<T>> setters,
        ILogger? logger = null, CancellationToken cancellationToken = default)
    {
        for (var attempt = 0; ; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try { return await query.ExecuteUpdateAsync(setters, cancellationToken); }
            catch (SqliteException ex) when (ex.SqliteErrorCode is 5 or 6 && attempt < 2)
            {
                logger?.LogWarning("SQLite writer busy; retrying bulk update (attempt {Attempt}/3)", attempt + 2);
                await Task.Delay(TimeSpan.FromMilliseconds(250 * (1 << attempt)), cancellationToken);
            }
        }
    }

    public static async Task<int> SaveChangesWithRetryAsync(
        this DbContext context,
        ILogger? logger = null,
        DatabaseRestartCoordinator? restartCoordinator = null,
        int maxAttempts = 5,
        CancellationToken cancellationToken = default)
    {
        // Capture only operational identifiers and state, never arbitrary entity values or payloads.
        var changes = logger?.IsEnabled(LogLevel.Debug) == true
            ? context.ChangeTracker.Entries()
                .Where(e => e.State is EntityState.Added or EntityState.Modified or EntityState.Deleted)
                .Select(e => (Entry: e, State: e.State)).ToArray()
            : [];

        async Task<int> SaveAsync()
        {
            var count = await context.SaveChangesAsync(cancellationToken);
            foreach (var (entry, state) in changes)
            {
                var identity = string.Join(", ", entry.Properties.Where(p => p.Metadata.Name is
                    "ArrInstance" or "ArrId" or "EntryId" or "Title" or "SeriesTitle" or "QueueId" or
                    "QbitInstance" or "Hash" or "Category" or "Monitored" or "HasFile" or "Reason" or
                    "Searched" or "Upgrade" or "IsRequest" or "Imported" or "QualityMet" or "CustomFormatMet" or
                    "SeasonNumber" or "EpisodeNumber" or "ArrSeriesId" or "AirDateUtc" or "ReleaseDate")
                    .Select(p => $"{p.Metadata.Name}={p.CurrentValue}"));
                logger?.LogDebug("Database saved {Operation}: {Entity} | {Identity}", state, entry.Metadata.ClrType.Name, identity);
            }
            logger?.LogDebug("Database save completed: {Count} entries saved (added={Added}, updated={Updated}, deleted={Deleted})",
                count, changes.Count(c => c.State == EntityState.Added), changes.Count(c => c.State == EntityState.Modified),
                changes.Count(c => c.State == EntityState.Deleted));
            return count;
        }

        for (var attempt = 0; attempt < maxAttempts; attempt++)
        {
            try
            {
                var result = await SaveAsync();
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

        return await SaveAsync();
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

    private static bool IsRetriable(Exception error)
    {
        for (Exception? current = error; current != null; current = current.InnerException)
            if (current is SqliteException sqlite && sqlite.SqliteErrorCode is 5 or 6
                || current is DbUpdateException or SqliteException &&
                (current.Message.Contains("database is locked", StringComparison.OrdinalIgnoreCase)
                 || current.Message.Contains("disk I/O", StringComparison.OrdinalIgnoreCase)
                 || current.Message.Contains("malformed", StringComparison.OrdinalIgnoreCase))) return true;
        return false;
    }

    private static bool IsCorruption(Exception ex) => IsSqliteCorruption(ex);
}

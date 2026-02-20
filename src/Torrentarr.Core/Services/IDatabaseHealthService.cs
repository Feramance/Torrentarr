namespace Torrentarr.Core.Services;

/// <summary>
/// Service for database health monitoring and maintenance
/// </summary>
public interface IDatabaseHealthService
{
    /// <summary>
    /// Check database integrity
    /// </summary>
    Task<DatabaseHealthResult> CheckHealthAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Force WAL checkpoint to flush changes to main database
    /// </summary>
    Task<bool> CheckpointWalAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Run VACUUM to reclaim space and optimize
    /// </summary>
    Task<bool> VacuumAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Attempt to repair a corrupted database
    /// </summary>
    Task<bool> RepairAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Get database statistics (size, WAL size, page count)
    /// </summary>
    Task<DatabaseStats> GetStatsAsync(CancellationToken cancellationToken = default);
}

public class DatabaseHealthResult
{
    public bool IsHealthy { get; set; }
    public string Message { get; set; } = "";
    public long SizeBytes { get; set; }
    public long WalSizeBytes { get; set; }
    public int PageCount { get; set; }
    public DateTime CheckedAt { get; set; } = DateTime.UtcNow;
}

public class DatabaseStats
{
    public long SizeBytes { get; set; }
    public long WalSizeBytes { get; set; }
    public int PageCount { get; set; }
    public int PageSize { get; set; }
    public long FreePages { get; set; }
    public string JournalMode { get; set; } = "";
    public string DatabasePath { get; set; } = "";
}

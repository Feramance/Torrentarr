namespace Torrentarr.Core.Services;

/// <summary>
/// Service for managing disk space and preventing download issues
/// </summary>
public interface IFreeSpaceService
{
    /// <summary>
    /// Check if there is enough free space for new downloads
    /// </summary>
    Task<bool> HasEnoughFreeSpaceAsync(long requiredBytes, CancellationToken cancellationToken = default);

    /// <summary>
    /// Get current free space statistics
    /// </summary>
    Task<FreeSpaceStats> GetFreeSpaceStatsAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Pause downloads if free space is below threshold
    /// </summary>
    Task<bool> PauseDownloadsIfLowSpaceAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Resume downloads if free space is adequate
    /// </summary>
    Task<bool> ResumeDownloadsIfSpaceAvailableAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Process torrents with per-torrent space calculation.
    /// Pauses torrents that would exceed free space threshold and manages tags.
    /// </summary>
    Task ProcessTorrentsForSpaceAsync(string category, CancellationToken cancellationToken = default);
}

public class FreeSpaceStats
{
    public long TotalBytes { get; set; }
    public long FreeBytes { get; set; }
    public long UsedBytes { get; set; }
    public double FreePercentage { get; set; }
    public bool BelowThreshold { get; set; }
    public long ThresholdBytes { get; set; }
    public string Path { get; set; } = "";
}

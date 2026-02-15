namespace Commandarr.Core.Services;

/// <summary>
/// Service for managing torrent seeding rules and Hit & Run protection
/// </summary>
public interface ISeedingService
{
    /// <summary>
    /// Check if a torrent meets seeding requirements and can be removed
    /// </summary>
    Task<bool> MeetsSeedingRequirementsAsync(string hash, string category, CancellationToken cancellationToken = default);

    /// <summary>
    /// Get seeding statistics for a torrent
    /// </summary>
    Task<SeedingStats> GetSeedingStatsAsync(string hash, CancellationToken cancellationToken = default);

    /// <summary>
    /// Check if torrent is protected by Hit & Run rules
    /// </summary>
    Task<bool> IsHitAndRunProtectedAsync(string hash, CancellationToken cancellationToken = default);

    /// <summary>
    /// Remove completed torrents that meet seeding requirements
    /// </summary>
    Task<RemovalResult> RemoveCompletedTorrentsAsync(string category, CancellationToken cancellationToken = default);
}

public class SeedingStats
{
    public string Hash { get; set; } = "";
    public double Ratio { get; set; }
    public long SeedingTimeSeconds { get; set; }
    public long UploadedBytes { get; set; }
    public long DownloadedBytes { get; set; }
    public DateTime CompletionTime { get; set; }
    public bool MeetsTimeRequirement { get; set; }
    public bool MeetsRatioRequirement { get; set; }
    public List<string> TrackerRequirements { get; set; } = new();
}

public class RemovalResult
{
    public int TorrentsChecked { get; set; }
    public int TorrentsRemoved { get; set; }
    public int TorrentsProtected { get; set; }
    public List<string> RemovedHashes { get; set; } = new();
    public List<string> Errors { get; set; } = new();
}

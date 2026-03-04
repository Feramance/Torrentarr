using Torrentarr.Core.Configuration;
using Torrentarr.Core.Models;

namespace Torrentarr.Core.Services;

/// <summary>
/// Service for managing torrent seeding rules and Hit &amp; Run protection.
/// Matches qBitrr's qbit_category_manager.py implementation exactly.
/// </summary>
public interface ISeedingService
{
    /// <summary>
    /// Check if a torrent meets seeding requirements and can be removed
    /// </summary>
    Task<bool> MeetsSeedingRequirementsAsync(string hash, string category, CancellationToken cancellationToken = default);

    /// <summary>
    /// Get seeding statistics for a torrent. Returns null if the torrent is not found.
    /// </summary>
    Task<SeedingStats?> GetSeedingStatsAsync(string hash, CancellationToken cancellationToken = default);

    /// <summary>
    /// Check if torrent is protected by Hit &amp; Run rules
    /// </summary>
    Task<bool> IsHitAndRunProtectedAsync(string hash, CancellationToken cancellationToken = default);

    /// <summary>
    /// Remove completed torrents that meet seeding requirements
    /// </summary>
    Task<RemovalResult> RemoveCompletedTorrentsAsync(string category, CancellationToken cancellationToken = default);

    /// <summary>
    /// Update seeding tags for all completed torrents in a category.
    /// Adds qBitrr-allowed_seeding tag when requirements are met.
    /// </summary>
    Task UpdateSeedingTagsAsync(string category, CancellationToken cancellationToken = default);

    /// <summary>
    /// Get the highest-priority matching tracker config for a torrent.
    /// Matches qBitrr's _get_tracker_config() exactly.
    /// </summary>
    Task<TrackerConfig?> GetTrackerConfigAsync(TorrentInfo torrent, CancellationToken cancellationToken = default);

    /// <summary>
    /// Check if the H&amp;R-enabled tracker reports the torrent as unregistered/dead.
    /// Matches qBitrr's _hnr_tracker_is_dead() exactly.
    /// </summary>
    Task<bool> IsTrackerDeadAsync(TorrentInfo torrent, TrackerConfig config, CancellationToken cancellationToken = default);

    /// <summary>
    /// Check if Hit and Run obligations are met for this torrent.
    /// Matches qBitrr's _hnr_safe_to_remove() exactly.
    /// </summary>
    Task<bool> IsHnRSafeToRemoveAsync(TorrentInfo torrent, TrackerConfig config, CancellationToken cancellationToken = default);

    /// <summary>
    /// Check if torrent meets removal conditions based on RemoveMode.
    /// Matches qBitrr's _should_remove_torrent() exactly.
    /// RemoveMode: -1=Never, 1=Ratio only, 2=Time only, 3=OR, 4=AND
    /// </summary>
    Task<bool> ShouldRemoveTorrentAsync(TorrentInfo torrent, CancellationToken cancellationToken = default);

    /// <summary>
    /// Check if HnR obligations allow deleting this torrent.
    /// Fetches tracker metadata and checks HnR. Returns true if deletion is allowed.
    /// Matches qBitrr's _hnr_allows_delete() exactly.
    /// </summary>
    Task<bool> HnrAllowsDeleteAsync(TorrentInfo torrent, string reason, CancellationToken cancellationToken = default);

    /// <summary>
    /// Apply seeding limits (ratio, time, rate limits) to a torrent.
    /// Matches qBitrr's _apply_seeding_limits() exactly.
    /// </summary>
    Task ApplySeedingLimitsAsync(TorrentInfo torrent, CancellationToken cancellationToken = default);

    /// <summary>
    /// Apply tracker actions (add/remove trackers, tags, super-seed) to a torrent.
    /// In qBitrr this runs for ALL torrents as a pre-step before the state machine.
    /// </summary>
    Task ApplyTrackerActionsForTorrentAsync(TorrentInfo torrent, CancellationToken cancellationToken = default);
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

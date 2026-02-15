using Commandarr.Core.Configuration;
using Commandarr.Core.Services;
using Commandarr.Infrastructure.Database;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Commandarr.Infrastructure.Services;

/// <summary>
/// Service for managing torrent seeding rules and Hit & Run protection
/// </summary>
public class SeedingService : ISeedingService
{
    private readonly ILogger<SeedingService> _logger;
    private readonly CommandarrDbContext _dbContext;
    private readonly CommandarrConfig _config;
    private readonly QBittorrentConnectionManager _qbitManager;

    public SeedingService(
        ILogger<SeedingService> logger,
        CommandarrDbContext dbContext,
        CommandarrConfig config,
        QBittorrentConnectionManager qbitManager)
    {
        _logger = logger;
        _dbContext = dbContext;
        _config = config;
        _qbitManager = qbitManager;
    }

    public async Task<bool> MeetsSeedingRequirementsAsync(string hash, string category, CancellationToken cancellationToken = default)
    {
        var stats = await GetSeedingStatsAsync(hash, cancellationToken);

        if (stats == null)
        {
            return false;
        }

        // Check category-specific seeding rules
        var categoryConfig = _config.Settings.CategorySeedingRules?.FirstOrDefault(r => r.Category == category);

        if (categoryConfig != null)
        {
            // Check minimum seeding time
            if (categoryConfig.MinimumSeedingTime > 0)
            {
                var seedingTime = TimeSpan.FromSeconds(stats.SeedingTimeSeconds);
                var requiredTime = TimeSpan.FromMinutes(categoryConfig.MinimumSeedingTime);

                if (seedingTime < requiredTime)
                {
                    _logger.LogDebug("Torrent {Hash} has not met minimum seeding time ({Current} < {Required})",
                        hash, seedingTime, requiredTime);
                    return false;
                }
            }

            // Check minimum ratio
            if (categoryConfig.MinimumRatio > 0 && stats.Ratio < categoryConfig.MinimumRatio)
            {
                _logger.LogDebug("Torrent {Hash} has not met minimum ratio ({Current} < {Required})",
                    hash, stats.Ratio, categoryConfig.MinimumRatio);
                return false;
            }
        }

        // Check tracker-specific rules
        foreach (var trackerReq in stats.TrackerRequirements)
        {
            _logger.LogDebug("Tracker requirement for {Hash}: {Requirement}", hash, trackerReq);
        }

        return stats.MeetsTimeRequirement && stats.MeetsRatioRequirement;
    }

    public async Task<SeedingStats> GetSeedingStatsAsync(string hash, CancellationToken cancellationToken = default)
    {
        var client = _qbitManager.GetDefaultClient();
        if (client == null)
        {
            throw new InvalidOperationException("No qBittorrent client available");
        }

        var torrents = await client.GetTorrentsAsync(ct: cancellationToken);
        var torrent = torrents.FirstOrDefault(t => t.Hash.Equals(hash, StringComparison.OrdinalIgnoreCase));

        if (torrent == null)
        {
            throw new InvalidOperationException($"Torrent {hash} not found");
        }

        var stats = new SeedingStats
        {
            Hash = hash,
            Ratio = torrent.Ratio,
            SeedingTimeSeconds = torrent.SeedingTime,
            UploadedBytes = torrent.Uploaded,
            DownloadedBytes = torrent.Downloaded,
            CompletionTime = DateTime.UtcNow.AddSeconds(-torrent.SeedingTime)
        };

        // Get category-specific rules
        var categoryConfig = _config.Settings.CategorySeedingRules?.FirstOrDefault(r => r.Category == torrent.Category);

        if (categoryConfig != null)
        {
            stats.MeetsTimeRequirement = categoryConfig.MinimumSeedingTime == 0 ||
                stats.SeedingTimeSeconds >= categoryConfig.MinimumSeedingTime * 60;

            stats.MeetsRatioRequirement = categoryConfig.MinimumRatio == 0 ||
                stats.Ratio >= categoryConfig.MinimumRatio;
        }
        else
        {
            // Default: no requirements
            stats.MeetsTimeRequirement = true;
            stats.MeetsRatioRequirement = true;
        }

        // Check for tracker-specific requirements
        var trackerConfigs = _config.Settings.TrackerRules?.Where(t =>
            torrent.Tracker != null && torrent.Tracker.Contains(t.TrackerUrl, StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (trackerConfigs != null && trackerConfigs.Any())
        {
            foreach (var tracker in trackerConfigs)
            {
                var meetsTrackerTime = tracker.MinimumSeedingTime == 0 ||
                    stats.SeedingTimeSeconds >= tracker.MinimumSeedingTime * 60;

                var meetsTrackerRatio = tracker.MinimumRatio == 0 ||
                    stats.Ratio >= tracker.MinimumRatio;

                if (!meetsTrackerTime || !meetsTrackerRatio)
                {
                    stats.MeetsTimeRequirement = false;
                    stats.MeetsRatioRequirement = meetsTrackerRatio;
                }

                stats.TrackerRequirements.Add(
                    $"{tracker.TrackerUrl}: Ratio {tracker.MinimumRatio}, Time {tracker.MinimumSeedingTime}min");
            }
        }

        return stats;
    }

    public async Task<bool> IsHitAndRunProtectedAsync(string hash, CancellationToken cancellationToken = default)
    {
        try
        {
            var client = _qbitManager.GetDefaultClient();
            if (client == null)
            {
                return false;
            }

            var torrents = await client.GetTorrentsAsync(ct: cancellationToken);
            var torrent = torrents.FirstOrDefault(t => t.Hash.Equals(hash, StringComparison.OrdinalIgnoreCase));

            if (torrent == null)
            {
                return false;
            }

            // Check if any tracker has Hit & Run protection rules
            var trackerConfigs = _config.Settings.TrackerRules?.Where(t =>
                torrent.Tracker != null && torrent.Tracker.Contains(t.TrackerUrl, StringComparison.OrdinalIgnoreCase))
                .ToList();

            if (trackerConfigs == null || !trackerConfigs.Any())
            {
                return false;
            }

            foreach (var tracker in trackerConfigs)
            {
                // If tracker has seeding requirements and they're not met, it's protected
                if (tracker.MinimumSeedingTime > 0 || tracker.MinimumRatio > 0)
                {
                    var seedingTimeMinutes = torrent.SeedingTime / 60;
                    var meetsTime = tracker.MinimumSeedingTime == 0 || seedingTimeMinutes >= tracker.MinimumSeedingTime;
                    var meetsRatio = tracker.MinimumRatio == 0 || torrent.Ratio >= tracker.MinimumRatio;

                    if (!meetsTime || !meetsRatio)
                    {
                        _logger.LogDebug("Torrent {Hash} is H&R protected by tracker {Tracker}", hash, tracker.TrackerUrl);
                        return true;
                    }
                }
            }

            return false;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error checking H&R protection for torrent {Hash}", hash);
            return true; // Err on the side of caution - protect the torrent
        }
    }

    public async Task<RemovalResult> RemoveCompletedTorrentsAsync(string category, CancellationToken cancellationToken = default)
    {
        var result = new RemovalResult();

        try
        {
            var client = _qbitManager.GetDefaultClient();
            if (client == null)
            {
                result.Errors.Add("No qBittorrent client available");
                return result;
            }

            // Get all completed torrents in this category
            var torrents = await client.GetTorrentsAsync(category, cancellationToken);
            var completedTorrents = torrents.Where(t =>
                t.State.Contains("up", StringComparison.OrdinalIgnoreCase) || // Uploading/seeding
                t.State.Contains("paused", StringComparison.OrdinalIgnoreCase) && t.Progress >= 1.0
            ).ToList();

            result.TorrentsChecked = completedTorrents.Count;

            foreach (var torrent in completedTorrents)
            {
                try
                {
                    // Check if torrent is H&R protected
                    var isProtected = await IsHitAndRunProtectedAsync(torrent.Hash, cancellationToken);
                    if (isProtected)
                    {
                        result.TorrentsProtected++;
                        _logger.LogDebug("Torrent {Name} is H&R protected, skipping removal", torrent.Name);
                        continue;
                    }

                    // Check if torrent meets seeding requirements
                    var meetsRequirements = await MeetsSeedingRequirementsAsync(torrent.Hash, category, cancellationToken);
                    if (meetsRequirements)
                    {
                        // Check if torrent was imported to Arr
                        var imported = await _dbContext.TorrentLibrary
                            .AnyAsync(t => t.Hash == torrent.Hash && t.Imported, cancellationToken);

                        if (imported)
                        {
                            // Safe to remove
                            var deleted = await client.DeleteTorrentsAsync(new List<string> { torrent.Hash }, deleteFiles: false, cancellationToken);
                            if (deleted)
                            {
                                result.TorrentsRemoved++;
                                result.RemovedHashes.Add(torrent.Hash);
                                _logger.LogInformation("Removed torrent {Name} (meets seeding requirements)", torrent.Name);
                            }
                        }
                        else
                        {
                            _logger.LogDebug("Torrent {Name} not imported yet, keeping", torrent.Name);
                        }
                    }
                    else
                    {
                        _logger.LogDebug("Torrent {Name} does not meet seeding requirements", torrent.Name);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error processing torrent {Hash} for removal", torrent.Hash);
                    result.Errors.Add($"{torrent.Name}: {ex.Message}");
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error removing completed torrents for category {Category}", category);
            result.Errors.Add(ex.Message);
        }

        return result;
    }
}

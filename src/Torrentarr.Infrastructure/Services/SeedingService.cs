using Torrentarr.Core.Configuration;
using Torrentarr.Core.Models;
using Torrentarr.Core.Services;
using Torrentarr.Infrastructure.ApiClients.QBittorrent;
using Torrentarr.Infrastructure.Database;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Torrentarr.Infrastructure.Services;

/// <summary>
/// Service for managing torrent seeding rules and Hit &amp; Run protection.
/// Matches qBitrr's qbit_category_manager.py implementation exactly.
/// </summary>
public class SeedingService : ISeedingService
{
    private const string AllowedSeedingTag = "qBitrr-allowed_seeding";
    private const string FreeSpacePausedTag = "qBitrr-free_space_paused";
    private const string IgnoredTag = "qBitrr-ignored";
    private const string HnrActiveTag = "qBitrr-hnr_active";

    private static readonly HashSet<string> DeadTrackerKeywords = new(StringComparer.OrdinalIgnoreCase)
    {
        "unregistered torrent",
        "torrent not registered",
        "info hash is not authorized",
        "torrent is not authorized",
        "not found",
        "torrent not found"
    };

    private readonly ILogger<SeedingService> _logger;
    private readonly TorrentarrDbContext _dbContext;
    private readonly TorrentarrConfig _config;
    private readonly QBittorrentConnectionManager _qbitManager;

    public SeedingService(
        ILogger<SeedingService> logger,
        TorrentarrDbContext dbContext,
        TorrentarrConfig config,
        QBittorrentConnectionManager qbitManager)
    {
        _logger = logger;
        _dbContext = dbContext;
        _config = config;
        _qbitManager = qbitManager;
    }

    /// <summary>Returns the seeding config for whichever qBit instance the torrent belongs to.</summary>
    private CategorySeedingConfig GetSeedingConfig(TorrentInfo torrent)
        => _config.QBitInstances.GetValueOrDefault(torrent.QBitInstanceName)?.CategorySeeding
           ?? new CategorySeedingConfig();

    /// <summary>Returns the tracker list for whichever qBit instance the torrent belongs to.</summary>
    private List<TrackerConfig> GetTrackerList(TorrentInfo torrent)
        => _config.QBitInstances.GetValueOrDefault(torrent.QBitInstanceName)?.Trackers
           ?? new List<TrackerConfig>();

    /// <summary>Returns the connected client for whichever qBit instance the torrent belongs to.</summary>
    private QBittorrentClient? GetClient(TorrentInfo torrent)
        => _qbitManager.GetClient(torrent.QBitInstanceName);

    public async Task<bool> MeetsSeedingRequirementsAsync(string hash, string category, CancellationToken cancellationToken = default)
    {
        var stats = await GetSeedingStatsAsync(hash, cancellationToken);

        if (stats == null)
        {
            return false;
        }

        var categoryConfig = _config.Settings.CategorySeedingRules?.FirstOrDefault(r => r.Category == category);

        if (categoryConfig != null)
        {
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

            if (categoryConfig.MinimumRatio > 0 && stats.Ratio < categoryConfig.MinimumRatio)
            {
                _logger.LogDebug("Torrent {Hash} has not met minimum ratio ({Current} < {Required})",
                    hash, stats.Ratio, categoryConfig.MinimumRatio);
                return false;
            }
        }

        foreach (var trackerReq in stats.TrackerRequirements)
        {
            _logger.LogDebug("Tracker requirement for {Hash}: {Requirement}", hash, trackerReq);
        }

        return stats.MeetsTimeRequirement && stats.MeetsRatioRequirement;
    }

    public async Task<SeedingStats> GetSeedingStatsAsync(string hash, CancellationToken cancellationToken = default)
    {
        // Search all qBit instances for this hash
        TorrentInfo? torrent = null;
        foreach (var (instanceName, client) in _qbitManager.GetAllClients())
        {
            var torrents = await client.GetTorrentsAsync(ct: cancellationToken);
            var found = torrents.FirstOrDefault(t => t.Hash.Equals(hash, StringComparison.OrdinalIgnoreCase));
            if (found != null)
            {
                found.QBitInstanceName = instanceName;
                torrent = found;
                break;
            }
        }

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
            stats.MeetsTimeRequirement = true;
            stats.MeetsRatioRequirement = true;
        }

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
            TorrentInfo? torrent = null;
            foreach (var (instanceName, client) in _qbitManager.GetAllClients())
            {
                var torrents = await client.GetTorrentsAsync(ct: cancellationToken);
                var found = torrents.FirstOrDefault(t => t.Hash.Equals(hash, StringComparison.OrdinalIgnoreCase));
                if (found != null) { found.QBitInstanceName = instanceName; torrent = found; break; }
            }

            if (torrent == null)
            {
                return false;
            }

            var trackerConfig = await GetTrackerConfigAsync(torrent, cancellationToken);
            var hnrConfig = trackerConfig != null ? ConvertToCategorySeeding(trackerConfig) : GetSeedingConfig(torrent);

            if (hnrConfig.HitAndRunMode != true)
            {
                return false;
            }

            if (trackerConfig != null && await IsTrackerDeadAsync(torrent, trackerConfig, cancellationToken))
            {
                _logger.LogDebug("H&R bypass: tracker reports torrent as unregistered/dead '{Name}'", torrent.Name);
                return false;
            }

            return !await IsHnRSafeToRemoveAsync(torrent, trackerConfig ?? new TrackerConfig
            {
                HitAndRunMode = hnrConfig.HitAndRunMode,
                MinSeedRatio = hnrConfig.MinSeedRatio,
                MinSeedingTime = hnrConfig.MinSeedingTimeDays,
                HitAndRunMinimumDownloadPercent = hnrConfig.HitAndRunMinimumDownloadPercent,
                HitAndRunPartialSeedRatio = hnrConfig.HitAndRunPartialSeedRatio,
                TrackerUpdateBuffer = hnrConfig.TrackerUpdateBuffer
            }, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error checking H&R protection for torrent {Hash}", hash);
            return true;
        }
    }

    /// <summary>
    /// Get the highest-priority matching tracker config for a torrent.
    /// Matches qBitrr's _get_tracker_config() exactly.
    /// Supports subdomain/apex matching (e.g., tracker.torrentleech.org matches torrentleech.org config).
    /// </summary>
    public async Task<TrackerConfig?> GetTrackerConfigAsync(TorrentInfo torrent, CancellationToken cancellationToken = default)
    {
        var trackers = GetTrackerList(torrent);
        if (!trackers.Any()) return null;

        var client = GetClient(torrent);
        if (client == null) return null;

        try
        {
            var torrentTrackers = await client.GetTorrentTrackersAsync(torrent.Hash, cancellationToken);
            var torrentHosts = torrentTrackers
                .Where(t => !string.IsNullOrEmpty(t.Url))
                .Select(t => ExtractTrackerHost(t.Url))
                .Where(h => !string.IsNullOrEmpty(h))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            TrackerConfig? best = null;
            var bestPriority = -1;

            foreach (var trackerCfg in trackers)
            {
                if (trackerCfg == null) continue;

                var uri = (trackerCfg.Uri ?? "").Trim().TrimEnd('/');
                var priority = trackerCfg.Priority;
                var cfgHost = ExtractTrackerHost(uri);

                if (string.IsNullOrEmpty(cfgHost)) continue;

                // Direct host match
                if (torrentHosts.Contains(cfgHost) && priority > bestPriority)
                {
                    best = trackerCfg;
                    bestPriority = priority;
                    continue;
                }

                // Subdomain/apex matching: tracker.torrentleech.org matches torrentleech.org config
                foreach (var torrentHost in torrentHosts)
                {
                    if (torrentHost.EndsWith("." + cfgHost, StringComparison.OrdinalIgnoreCase) && priority > bestPriority)
                    {
                        best = trackerCfg;
                        bestPriority = priority;
                        break;
                    }
                }
            }

            return best;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error getting tracker config for torrent {Hash}", torrent.Hash);
            return null;
        }
    }

    /// <summary>
    /// Check if the H&R-enabled tracker reports the torrent as unregistered/dead.
    /// Matches qBitrr's _hnr_tracker_is_dead() exactly.
    /// </summary>
    public async Task<bool> IsTrackerDeadAsync(TorrentInfo torrent, TrackerConfig config, CancellationToken cancellationToken = default)
    {
        var client = GetClient(torrent);
        if (client == null) return false;

        var uri = (config.Uri ?? "").Trim().TrimEnd('/');
        var cfgHost = ExtractTrackerHost(uri);

        if (string.IsNullOrEmpty(cfgHost))
        {
            return false;
        }

        try
        {
            var torrentTrackers = await client.GetTorrentTrackersAsync(torrent.Hash, cancellationToken);

            foreach (var tracker in torrentTrackers)
            {
                var trackerUrl = (tracker.Url ?? "").TrimEnd('/');
                if (ExtractTrackerHost(trackerUrl) != cfgHost)
                {
                    continue;
                }

                var messageText = (tracker.Msg ?? "").ToLowerInvariant();
                if (DeadTrackerKeywords.Any(keyword => messageText.Contains(keyword)))
                {
                    return true;
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error checking if tracker is dead for torrent {Hash}", torrent.Hash);
        }

        return false;
    }

    /// <summary>
    /// Check if Hit and Run obligations are met for this torrent.
    /// Matches qBitrr's _hnr_safe_to_remove() exactly.
    /// </summary>
    public async Task<bool> IsHnRSafeToRemoveAsync(TorrentInfo torrent, TrackerConfig config, CancellationToken cancellationToken = default)
    {
        if (config.HitAndRunMode != true)
        {
            return true;
        }

        var minRatio = config.MinSeedRatio ?? 1.0;
        var minTimeDays = config.MinSeedingTime ?? 0;
        var minTimeSeconds = minTimeDays * 86400;
        var minDownloadPercent = (config.HitAndRunMinimumDownloadPercent ?? 10) / 100.0;
        var partialRatio = config.HitAndRunPartialSeedRatio ?? 1.0;
        var bufferSeconds = config.TrackerUpdateBuffer ?? 0;

        var progress = torrent.Progress;
        var isPartial = progress < 1.0 && progress >= minDownloadPercent;
        var effectiveSeedingTime = torrent.SeedingTime - bufferSeconds;

        // Negligible download (<10% progress), no HnR obligation
        if (progress < minDownloadPercent)
        {
            return true;
        }

        // Partial download: ratio only check
        if (isPartial)
        {
            return torrent.Ratio >= partialRatio;
        }

        var ratioMet = minRatio > 0 && torrent.Ratio >= minRatio;
        var timeMet = minTimeSeconds > 0 && effectiveSeedingTime >= minTimeSeconds;

        if (minRatio > 0 && minTimeSeconds > 0)
        {
            return ratioMet || timeMet;
        }
        else if (minRatio > 0)
        {
            return ratioMet;
        }
        else if (minTimeSeconds > 0)
        {
            return timeMet;
        }

        return true;
    }

    /// <summary>
    /// Check if HnR obligations allow deleting this torrent.
    /// Fetches tracker metadata and checks HnR. Returns true if deletion is allowed.
    /// Matches qBitrr's _hnr_allows_delete() exactly.
    /// </summary>
    public async Task<bool> HnrAllowsDeleteAsync(TorrentInfo torrent, string reason, CancellationToken cancellationToken = default)
    {
        var trackers = GetTrackerList(torrent);
        var hasHnrTracker = trackers.Any(t => t.HitAndRunMode == true);
        
        if (!hasHnrTracker)
        {
            return true; // Fast path: no HnR on any tracker
        }

        var trackerConfig = await GetTrackerConfigAsync(torrent, cancellationToken);
        if (trackerConfig == null)
        {
            return true;
        }

        if (await IsTrackerDeadAsync(torrent, trackerConfig, cancellationToken))
        {
            return true;
        }

        if (await IsHnRSafeToRemoveAsync(torrent, trackerConfig, cancellationToken))
        {
            return true;
        }

        _logger.LogInformation(
            "HnR protection: blocking {Reason} of [{Name}] (ratio={Ratio:F2}, seeding={SeedingTime}s, progress={Progress:P0})",
            reason,
            torrent.Name,
            torrent.Ratio,
            torrent.SeedingTime,
            torrent.Progress);

        return false;
    }

    /// <summary>
    /// Check if torrent meets removal conditions based on RemoveMode.
    /// Matches qBitrr's _should_remove_torrent() and _should_leave_alone() logic.
    /// RemoveMode: -1=Never, 1=Ratio only, 2=Time only, 3=OR, 4=AND
    /// HnR protection now applies only to downloading torrents, not uploading.
    /// </summary>
    public async Task<bool> ShouldRemoveTorrentAsync(TorrentInfo torrent, CancellationToken cancellationToken = default)
    {
        var trackerConfig = await GetTrackerConfigAsync(torrent, cancellationToken);
        var seedingConfig = trackerConfig != null ? ConvertToCategorySeeding(trackerConfig) : GetSeedingConfig(torrent);

        var removeMode = seedingConfig.RemoveTorrent;

        if (removeMode == -1)
        {
            return false;
        }

        // Determine torrent state category
        var isUploading = IsUploadingState(torrent.State);
        var isDownloading = IsDownloadingState(torrent.State);

        var ratioLimit = seedingConfig.MaxUploadRatio;
        var timeLimit = seedingConfig.MaxSeedingTime;

        var ratioMet = ratioLimit > 0 && torrent.Ratio >= ratioLimit;
        var timeMet = timeLimit > 0 && torrent.SeedingTime >= timeLimit;

        // Only check removal conditions for uploading torrents
        var shouldRemove = false;
        if (isUploading)
        {
            shouldRemove = removeMode switch
            {
                1 => ratioMet,
                2 => timeMet,
                3 => ratioMet || timeMet,
                4 => ratioMet && timeMet,
                _ => false
            };
        }

        // HnR protection: only applies to downloading torrents
        if (isDownloading && shouldRemove && seedingConfig.HitAndRunMode)
        {
            if (trackerConfig != null)
            {
                if (await IsTrackerDeadAsync(torrent, trackerConfig, cancellationToken))
                {
                    _logger.LogDebug("H&R bypass: tracker reports torrent as unregistered/dead '{Name}'", torrent.Name);
                    return true;
                }

                if (!await IsHnRSafeToRemoveAsync(torrent, trackerConfig, cancellationToken))
                {
                    _logger.LogDebug("H&R protection: keeping downloading torrent '{Name}' (ratio={Ratio:F2}, seeding={SeedingTime}s)",
                        torrent.Name, torrent.Ratio, torrent.SeedingTime);
                    return false;
                }
            }
        }

        return shouldRemove;
    }

    /// <summary>
    /// Check if a torrent state indicates uploading/seeding.
    /// Matches qBitrr's is_uploading check.
    /// </summary>
    public static bool IsUploadingState(string state)
    {
        if (string.IsNullOrEmpty(state)) return false;
        var s = state.ToLowerInvariant();
        return s.Contains("uploading") ||
               s.Contains("stalledupload") ||
               s.Contains("queuedupload") ||
               s.Contains("pausedupload") ||
               s.Contains("forcedupload");
    }

    /// <summary>
    /// Check if a torrent state indicates downloading.
    /// Matches qBitrr's is_downloading check.
    /// </summary>
    public static bool IsDownloadingState(string state)
    {
        if (string.IsNullOrEmpty(state)) return false;
        var s = state.ToLowerInvariant();
        return s.Contains("downloading") ||
               s.Contains("stalleddownload") ||
               s.Contains("queueddownload") ||
               s.Contains("pauseddownload") ||
               s.Contains("forceddownload") ||
               s.Contains("metadata");
    }

    /// <summary>
    /// Check if a torrent state indicates stopped (not paused).
    /// </summary>
    public static bool IsStoppedState(string state)
    {
        if (string.IsNullOrEmpty(state)) return false;
        var s = state.ToLowerInvariant();
        return s == "stoppeddownload" ||
               s == "stoppedupload" ||
               s.Contains("stopped");
    }

    private CategorySeedingConfig ConvertToCategorySeeding(TrackerConfig tracker)
    {
        return new CategorySeedingConfig
        {
            MaxUploadRatio = tracker.MaxUploadRatio ?? -1,
            MaxSeedingTime = tracker.MaxSeedingTime ?? -1,
            RemoveTorrent = tracker.RemoveTorrent ?? -1,
            HitAndRunMode = tracker.HitAndRunMode ?? false,
            MinSeedRatio = tracker.MinSeedRatio ?? 1.0,
            MinSeedingTimeDays = tracker.MinSeedingTime ?? 0,
            DownloadRateLimitPerTorrent = tracker.DownloadRateLimit ?? -1,
            UploadRateLimitPerTorrent = tracker.UploadRateLimit ?? -1,
            TrackerUpdateBuffer = tracker.TrackerUpdateBuffer ?? 0
        };
    }

    /// <summary>
    /// Extract the host from a tracker URL for matching.
    /// Matches qBitrr's _extract_tracker_host() function.
    /// </summary>
    public static string ExtractTrackerHost(string url)
    {
        if (string.IsNullOrEmpty(url))
            return "";

        try
        {
            url = url.Trim();

            if (url.StartsWith("udp://", StringComparison.OrdinalIgnoreCase))
            {
                url = url.Substring(6);
            }
            else if (url.StartsWith("http://", StringComparison.OrdinalIgnoreCase))
            {
                url = url.Substring(7);
            }
            else if (url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            {
                url = url.Substring(8);
            }

            var slashIndex = url.IndexOf('/');
            if (slashIndex > 0)
            {
                url = url.Substring(0, slashIndex);
            }

            var colonIndex = url.IndexOf(':');
            if (colonIndex > 0)
            {
                url = url.Substring(0, colonIndex);
            }

            return url.ToLowerInvariant();
        }
        catch
        {
            return "";
        }
    }

    public async Task<RemovalResult> RemoveCompletedTorrentsAsync(string category, CancellationToken cancellationToken = default)
    {
        var result = new RemovalResult();

        var allClients = _qbitManager.GetAllClients();
        if (allClients.Count == 0)
        {
            result.Errors.Add("No qBittorrent clients available");
            return result;
        }

        // Gather completed torrents from all qBit instances for this category
        var completedByInstance = new List<(string instanceName, QBittorrentClient client, TorrentInfo torrent)>();
        foreach (var (instanceName, client) in allClients)
        {
            var torrents = await client.GetTorrentsAsync(category, cancellationToken);
            foreach (var t in torrents)
            {
                if (t.State.Contains("up", StringComparison.OrdinalIgnoreCase) ||
                    (t.State.Contains("paused", StringComparison.OrdinalIgnoreCase) && t.Progress >= 1.0))
                {
                    t.QBitInstanceName = instanceName;
                    completedByInstance.Add((instanceName, client, t));
                }
            }
        }

        result.TorrentsChecked = completedByInstance.Count;

        foreach (var (_, client, torrent) in completedByInstance)
        {
            try
            {
                var shouldRemove = await ShouldRemoveTorrentAsync(torrent, cancellationToken);
                if (!shouldRemove)
                {
                    _logger.LogDebug("Torrent {Name} does not meet removal criteria", torrent.Name);
                    continue;
                }

                var imported = await _dbContext.TorrentLibrary
                    .AnyAsync(t => t.Hash == torrent.Hash && t.Imported, cancellationToken);

                if (imported)
                {
                    var deleted = await client.DeleteTorrentsAsync(new List<string> { torrent.Hash }, deleteFiles: false, cancellationToken);
                    if (deleted)
                    {
                        result.TorrentsRemoved++;
                        result.RemovedHashes.Add(torrent.Hash);
                        _logger.LogInformation("Removed torrent {Name} (ratio: {Ratio:F2}, seeding time: {SeedingTime}s)",
                            torrent.Name, torrent.Ratio, torrent.SeedingTime);
                    }
                }
                else
                {
                    _logger.LogDebug("Torrent {Name} not imported yet, keeping", torrent.Name);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error processing torrent {Hash} for removal", torrent.Hash);
                result.Errors.Add($"{torrent.Name}: {ex.Message}");
            }
        }

        return result;
    }

    public async Task UpdateSeedingTagsAsync(string category, CancellationToken cancellationToken = default)
    {
        foreach (var (instanceName, client) in _qbitManager.GetAllClients())
        {
            try
            {
                await EnsureTagsExistAsync(client, cancellationToken);

                var torrents = await client.GetTorrentsAsync(category, cancellationToken);
                var completedTorrents = torrents.Where(t =>
                    t.Progress >= 1.0 &&
                    !HasTag(t, IgnoredTag) &&
                    !HasTag(t, FreeSpacePausedTag)
                ).ToList();

                foreach (var torrent in completedTorrents)
                {
                    torrent.QBitInstanceName = instanceName;
                    try
                    {
                        await UpdateSingleTorrentSeedingTagAsync(client, torrent, cancellationToken);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Error updating seeding tag for torrent {Hash}", torrent.Hash);
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error updating seeding tags for instance '{Instance}' category {Category}", instanceName, category);
            }
        }
    }

    /// <summary>
    /// Apply rate limits to a torrent.
    /// Note: qBitrr removed set_share_limits from tracker processing - limits are set per-torrent.
    /// </summary>
    public async Task ApplySeedingLimitsAsync(TorrentInfo torrent, CancellationToken cancellationToken = default)
    {
        var client = GetClient(torrent);
        if (client == null) return;

        var trackerConfig = await GetTrackerConfigAsync(torrent, cancellationToken);
        var config = trackerConfig != null ? ConvertToCategorySeeding(trackerConfig) : GetSeedingConfig(torrent);

        var dlLimit = config.DownloadRateLimitPerTorrent;
        if (dlLimit >= 0)
        {
            try
            {
                var limitBytes = dlLimit > 0 ? dlLimit * 1024 : -1;
                await client.SetDownloadLimitAsync(torrent.Hash, limitBytes, cancellationToken);
                _logger.LogDebug("Set download limit for '{Name}': {Limit} KB/s", torrent.Name, dlLimit);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to set download limit for '{Name}'", torrent.Name);
            }
        }

        var ulLimit = config.UploadRateLimitPerTorrent;
        if (ulLimit >= 0)
        {
            try
            {
                var limitBytes = ulLimit > 0 ? ulLimit * 1024 : -1;
                await client.SetUploadLimitAsync(torrent.Hash, limitBytes, cancellationToken);
                _logger.LogDebug("Set upload limit for '{Name}': {Limit} KB/s", torrent.Name, ulLimit);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to set upload limit for '{Name}'", torrent.Name);
            }
        }
    }

    private async Task UpdateSingleTorrentSeedingTagAsync(
        QBittorrentClient client,
        TorrentInfo torrent,
        CancellationToken cancellationToken)
    {
        var meetsRequirements = await MeetsSeedingRequirementsAsync(torrent.Hash, torrent.Category, cancellationToken);
        var hasAllowedSeedingTag = HasTag(torrent, AllowedSeedingTag);

        if (meetsRequirements && !hasAllowedSeedingTag)
        {
            await client.AddTagsAsync(
                new List<string> { torrent.Hash },
                new List<string> { AllowedSeedingTag },
                cancellationToken);

            _logger.LogDebug("Added {Tag} tag to torrent {Name}", AllowedSeedingTag, torrent.Name);
        }
        else if (!meetsRequirements && hasAllowedSeedingTag)
        {
            await client.RemoveTagsAsync(
                new List<string> { torrent.Hash },
                new List<string> { AllowedSeedingTag },
                cancellationToken);

            _logger.LogDebug("Removed {Tag} tag from torrent {Name}", AllowedSeedingTag, torrent.Name);
        }
    }

    private bool HasTag(TorrentInfo torrent, string tag)
    {
        if (string.IsNullOrEmpty(torrent.Tags))
            return false;

        var tags = torrent.Tags.Split(',', StringSplitOptions.RemoveEmptyEntries)
            .Select(t => t.Trim())
            .ToList();

        return tags.Contains(tag, StringComparer.OrdinalIgnoreCase);
    }

    private async Task EnsureTagsExistAsync(QBittorrentClient client, CancellationToken cancellationToken)
    {
        try
        {
            var existingTags = await client.GetTagsAsync(cancellationToken);
            var tagsToCreate = new List<string>();

            if (!existingTags.Contains(AllowedSeedingTag, StringComparer.OrdinalIgnoreCase))
                tagsToCreate.Add(AllowedSeedingTag);

            if (tagsToCreate.Count > 0)
            {
                await client.CreateTagsAsync(tagsToCreate, cancellationToken);
                _logger.LogDebug("Created tags: {Tags}", string.Join(", ", tagsToCreate));
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to ensure tags exist");
        }
    }
}

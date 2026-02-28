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

    /// <summary>
    /// §3.3: Returns the merged tracker list: qBit-level as base, Arr-level overrides on host collision.
    /// Matches qBitrr's _merge_trackers() — Arr-level wins when both define the same host.
    /// </summary>
    private List<TrackerConfig> GetTrackerList(TorrentInfo torrent)
    {
        var qbitTrackers = _config.QBitInstances.GetValueOrDefault(torrent.QBitInstanceName)?.Trackers
                           ?? new List<TrackerConfig>();

        // Find the Arr instance that manages this torrent's category
        var arrInstance = _config.ArrInstances.Values
            .FirstOrDefault(a => string.Equals(a.Category, torrent.Category, StringComparison.OrdinalIgnoreCase));
        var arrTrackers = arrInstance?.Torrent.Trackers ?? new List<TrackerConfig>();

        if (arrTrackers.Count == 0) return qbitTrackers;

        // Arr-level wins on normalized host collision
        var merged = qbitTrackers
            .ToDictionary(t => ExtractTrackerHost(t.Uri ?? "") ?? t.Uri ?? "", StringComparer.OrdinalIgnoreCase);
        foreach (var t in arrTrackers)
        {
            var host = ExtractTrackerHost(t.Uri ?? "") ?? t.Uri ?? "";
            merged[host] = t;
        }
        return merged.Values.ToList();
    }

    /// <summary>Returns the connected client for whichever qBit instance the torrent belongs to.</summary>
    private QBittorrentClient? GetClient(TorrentInfo torrent)
        => _qbitManager.GetClient(torrent.QBitInstanceName);

    public async Task<bool> MeetsSeedingRequirementsAsync(string hash, string category, CancellationToken cancellationToken = default)
    {
        _logger.LogTrace("Checking seeding requirements for hash {Hash} in category {Category}", hash, category);
        
        var stats = await GetSeedingStatsAsync(hash, cancellationToken);

        if (stats == null)
        {
            _logger.LogTrace("No seeding stats found for {Hash} - returning false", hash);
            return false;
        }

        _logger.LogTrace("Seeding stats for {Hash}: Ratio={Ratio}, SeedingTime={Time}s, MeetsTime={MeetsTime}, MeetsRatio={MeetsRatio}",
            hash, stats.Ratio, stats.SeedingTimeSeconds, stats.MeetsTimeRequirement, stats.MeetsRatioRequirement);

        var categoryConfig = _config.Settings.CategorySeedingRules?.FirstOrDefault(r => r.Category == category);
        _logger.LogTrace("Category config for {Category}: {Config}", category, categoryConfig != null ? "found" : "not found");

        if (categoryConfig != null)
        {
            if (categoryConfig.MinimumSeedingTime > 0)
            {
                var seedingTime = TimeSpan.FromSeconds(stats.SeedingTimeSeconds);
                var requiredTime = TimeSpan.FromMinutes(categoryConfig.MinimumSeedingTime);

                _logger.LogTrace("Checking minimum seeding time: {Current} vs required {Required}", seedingTime, requiredTime);
                
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

        var result = stats.MeetsTimeRequirement && stats.MeetsRatioRequirement;
        _logger.LogTrace("Seeding requirements result for {Hash}: {Result}", hash, result);
        
        return result;
    }

    public async Task<SeedingStats?> GetSeedingStatsAsync(string hash, CancellationToken cancellationToken = default)
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
            return null;
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

                if (!meetsTrackerTime)
                    stats.MeetsTimeRequirement = false;
                if (!meetsTrackerRatio)
                    stats.MeetsRatioRequirement = false;

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
                MinSeedingTimeDays = hnrConfig.MinSeedingTimeDays,
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
        var minTimeDays = config.MinSeedingTimeDays ?? 0;
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
            MinSeedingTimeDays = tracker.MinSeedingTimeDays ?? 0,
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
        _logger.LogTrace("H&R Check: Processing {Count} completed torrents in category {Category}", completedByInstance.Count, category);

        foreach (var (instanceName, client, torrent) in completedByInstance)
        {
            try
            {
                _logger.LogTrace("H&R Check: [{Name}] | Ratio[{Ratio:F2}] | SeedingTime[{SeedingTime}s] | State[{State}] | Hash[{Hash}]",
                    torrent.Name, torrent.Ratio, torrent.SeedingTime, torrent.State, torrent.Hash);

                var shouldRemove = await ShouldRemoveTorrentAsync(torrent, cancellationToken);
                if (!shouldRemove)
                {
                    _logger.LogTrace("H&R: Keeping torrent [{Name}] - does not meet removal criteria | Ratio[{Ratio:F2}] | SeedingTime[{SeedingTime}s]",
                        torrent.Name, torrent.Ratio, torrent.SeedingTime);
                    result.TorrentsProtected++;
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
                        _logger.LogInformation("H&R Remove: [{Name}] | Reason[Removal criteria met] | Ratio[{Ratio:F2}] | SeedingTime[{SeedingTime}s] | Hash[{Hash}]",
                            torrent.Name, torrent.Ratio, torrent.SeedingTime, torrent.Hash);
                    }
                }
                else
                {
                    _logger.LogTrace("H&R: Keeping torrent [{Name}] - not imported yet | Hash[{Hash}]", torrent.Name, torrent.Hash);
                    result.TorrentsProtected++;
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[{Instance}] Error processing torrent {Hash} for removal", instanceName, torrent.Hash);
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
                if (!_config.Settings.Tagless)
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

                // §3.1 / §3.2: Tracker actions + message scanning — run on all torrents
                foreach (var torrent in torrents)
                {
                    torrent.QBitInstanceName = instanceName;
                    try
                    {
                        await ApplyTrackerActionsAsync(client, torrent, cancellationToken);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Error applying tracker actions for {Hash}", torrent.Hash);
                    }
                    try
                    {
                        await ProcessTrackerMessagesAsync(client, torrent, cancellationToken);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Error processing tracker messages for {Hash}", torrent.Hash);
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
                _logger.LogTrace("Set download limit for '{Name}': {Limit} KB/s", torrent.Name, dlLimit);
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
                _logger.LogTrace("Set upload limit for '{Name}': {Limit} KB/s", torrent.Name, ulLimit);
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
            if (_config.Settings.Tagless)
            {
                await _dbContext.TorrentLibrary
                    .Where(t => t.Hash == torrent.Hash)
                    .ExecuteUpdateAsync(s => s.SetProperty(t => t.AllowedSeeding, true), cancellationToken);
            }
            else
            {
                await client.AddTagsAsync(
                    new List<string> { torrent.Hash },
                    new List<string> { AllowedSeedingTag },
                    cancellationToken);
            }
            _logger.LogTrace("AllowedSeeding set for torrent {Name} (Tagless={Tagless})", torrent.Name, _config.Settings.Tagless);
        }
        else if (!meetsRequirements && hasAllowedSeedingTag)
        {
            if (_config.Settings.Tagless)
            {
                await _dbContext.TorrentLibrary
                    .Where(t => t.Hash == torrent.Hash)
                    .ExecuteUpdateAsync(s => s.SetProperty(t => t.AllowedSeeding, false), cancellationToken);
            }
            else
            {
                await client.RemoveTagsAsync(
                    new List<string> { torrent.Hash },
                    new List<string> { AllowedSeedingTag },
                    cancellationToken);
            }
            _logger.LogTrace("AllowedSeeding cleared for torrent {Name} (Tagless={Tagless})", torrent.Name, _config.Settings.Tagless);
        }
    }

    private bool HasTag(TorrentInfo torrent, string tag)
    {
        // §1.6 Tagless mode: map tag names to TorrentLibrary DB columns
        if (_config.Settings.Tagless)
        {
            var dbEntry = _dbContext.TorrentLibrary.AsNoTracking()
                .FirstOrDefault(t => t.Hash == torrent.Hash);
            if (dbEntry == null) return false;
            return tag switch
            {
                AllowedSeedingTag => dbEntry.AllowedSeeding,
                FreeSpacePausedTag => dbEntry.FreeSpacePaused,
                _ => false
            };
        }

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
                _logger.LogTrace("Created tags: {Tags}", string.Join(", ", tagsToCreate));
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to ensure tags exist");
        }
    }

    /// <summary>
    /// §3.1: Apply per-tracker-config actions (RemoveIfExists, AddTrackerIfMissing, AddTags).
    /// Iterates the merged tracker list and applies each action for the trackers that match/don't match.
    /// </summary>
    private async Task ApplyTrackerActionsAsync(
        QBittorrentClient client,
        TorrentInfo torrent,
        CancellationToken ct)
    {
        var trackers = GetTrackerList(torrent);
        var actionTrackers = trackers.Where(t =>
            t.RemoveIfExists || t.AddTrackerIfMissing || t.AddTags.Count > 0 || t.SuperSeedMode.HasValue).ToList();
        if (actionTrackers.Count == 0) return;

        List<TorrentTracker> torrentTrackers;
        try { torrentTrackers = await client.GetTorrentTrackersAsync(torrent.Hash, ct); }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "ApplyTrackerActions: failed to get trackers for {Hash}", torrent.Hash);
            return;
        }

        var existingTagSet = (torrent.Tags ?? "")
            .Split(',', StringSplitOptions.RemoveEmptyEntries)
            .Select(t => t.Trim())
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var tagsToAdd = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var cfg in actionTrackers)
        {
            var cfgHost = ExtractTrackerHost(cfg.Uri ?? "");
            if (string.IsNullOrEmpty(cfgHost)) continue;

            // Find torrent tracker entries that match this config's host (direct or subdomain)
            var matchingTrackers = torrentTrackers
                .Where(t =>
                {
                    var h = ExtractTrackerHost(t.Url ?? "");
                    return string.Equals(h, cfgHost, StringComparison.OrdinalIgnoreCase)
                        || h.EndsWith("." + cfgHost, StringComparison.OrdinalIgnoreCase);
                })
                .ToList();

            var isPresent = matchingTrackers.Count > 0;

            // RemoveIfExists: torrent has this tracker → remove it
            if (cfg.RemoveIfExists && isPresent)
            {
                var urlsToRemove = matchingTrackers.Select(t => t.Url ?? "").Where(u => u.Length > 0).ToList();
                if (urlsToRemove.Count > 0)
                {
                    _logger.LogDebug("RemoveIfExists: removing tracker host {Host} from [{Name}]", cfgHost, torrent.Name);
                    await client.RemoveTrackersAsync(torrent.Hash, urlsToRemove, ct);
                    isPresent = false; // tracker was removed; AddTrackerIfMissing should not re-add
                }
            }

            // AddTrackerIfMissing: torrent lacks this tracker → inject it
            if (cfg.AddTrackerIfMissing && !isPresent && !string.IsNullOrEmpty(cfg.Uri))
            {
                _logger.LogDebug("AddTrackerIfMissing: adding tracker {Uri} to [{Name}]", cfg.Uri, torrent.Name);
                await client.AddTrackersAsync(torrent.Hash, new List<string> { cfg.Uri }, ct);
            }

            // AddTags: active tracker match → queue user-defined tags
            if (cfg.AddTags.Count > 0 && isPresent)
            {
                foreach (var tag in cfg.AddTags)
                    tagsToAdd.Add(tag);
            }

            // §3.5: SuperSeedMode — set or clear super-seed mode when active tracker matches
            if (cfg.SuperSeedMode.HasValue && isPresent)
            {
                _logger.LogDebug("SuperSeedMode={Mode}: applying to [{Name}]", cfg.SuperSeedMode.Value, torrent.Name);
                await client.SetSuperSeedingAsync(torrent.Hash, cfg.SuperSeedMode.Value, ct);
            }
        }

        // Apply new tags (only add, never remove — qBitrr pattern)
        var newTags = tagsToAdd.Where(t => !existingTagSet.Contains(t)).ToList();
        if (newTags.Count > 0)
        {
            _logger.LogDebug("AddTags: adding {Tags} to [{Name}]", string.Join(", ", newTags), torrent.Name);
            await client.AddTagsAsync(new List<string> { torrent.Hash }, newTags, ct);
        }
    }

    /// <summary>
    /// §3.2: Check each tracker's status message against RemoveTrackerWithMessage list.
    /// If matched: remove the tracker, or delete the torrent if RemoveDeadTrackers=true.
    /// Checks both Arr-level SeedingMode and TorrentConfig lists.
    /// </summary>
    private async Task ProcessTrackerMessagesAsync(
        QBittorrentClient client,
        TorrentInfo torrent,
        CancellationToken ct)
    {
        // Determine effective RemoveTrackerWithMessage and RemoveDeadTrackers from Arr config
        var arrCfg = _config.ArrInstances.Values
            .FirstOrDefault(a => string.Equals(a.Category, torrent.Category, StringComparison.OrdinalIgnoreCase));

        List<string> keywords;
        bool removeDead;

        if (arrCfg != null)
        {
            // Prefer SeedingMode list (has defaults); fall back to Torrent-level list
            var seedingModeKws = arrCfg.Torrent.SeedingMode?.RemoveTrackerWithMessage;
            var torrentKws = arrCfg.Torrent.RemoveTrackerWithMessage;
            keywords = seedingModeKws?.Count > 0 ? seedingModeKws : torrentKws;
            removeDead = arrCfg.Torrent.SeedingMode?.RemoveDeadTrackers ?? arrCfg.Torrent.RemoveDeadTrackers;
        }
        else
        {
            // No Arr config — nothing to do
            return;
        }

        if (keywords.Count == 0) return;

        var trackers = await client.GetTorrentTrackersAsync(torrent.Hash, ct);

        foreach (var tracker in trackers)
        {
            var msg = tracker.Msg ?? "";
            if (string.IsNullOrEmpty(msg)) continue;

            var matched = keywords.FirstOrDefault(kw =>
                msg.Contains(kw, StringComparison.OrdinalIgnoreCase));

            if (matched == null) continue;

            if (removeDead)
            {
                _logger.LogWarning(
                    "RemoveTrackerWithMessage+RemoveDeadTrackers: deleting torrent [{Name}] — tracker {Url} reported \"{Msg}\"",
                    torrent.Name, tracker.Url, msg);
                await client.DeleteTorrentsAsync(new List<string> { torrent.Hash }, deleteFiles: false, ct);
                return; // torrent deleted; stop processing trackers
            }
            else
            {
                _logger.LogDebug(
                    "RemoveTrackerWithMessage: removing tracker {Url} from [{Name}] — message: \"{Msg}\"",
                    tracker.Url, torrent.Name, msg);
                await client.RemoveTrackersAsync(torrent.Hash, new List<string> { tracker.Url }, ct);
            }
        }
    }
}

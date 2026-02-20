using Torrentarr.Core.Configuration;
using Torrentarr.Core.Models;
using Torrentarr.Core.Services;
using Torrentarr.Infrastructure.ApiClients.QBittorrent;
using Microsoft.Extensions.Logging;

namespace Torrentarr.Infrastructure.Services;

/// <summary>
/// Service for managing disk space and preventing download issues.
/// Checks ALL qBit instances and handles torrents by added date across all instances at once.
/// Each qBit instance's seeding/download-path settings are applied only to that instance's torrents.
/// </summary>
public class FreeSpaceService : IFreeSpaceService
{
    private const string FreeSpacePausedTag = "qBitrr-free_space_paused";
    private const string AllowedSeedingTag = "qBitrr-allowed_seeding";

    private readonly ILogger<FreeSpaceService> _logger;
    private readonly TorrentarrConfig _config;
    private readonly QBittorrentConnectionManager _qbitManager;
    private long _currentFreeSpace;
    private long _minFreeSpaceBytes;

    public FreeSpaceService(
        ILogger<FreeSpaceService> logger,
        TorrentarrConfig config,
        QBittorrentConnectionManager qbitManager)
    {
        _logger = logger;
        _config = config;
        _qbitManager = qbitManager;
        _minFreeSpaceBytes = (long)(_config.Settings.FreeSpaceThresholdGB ?? 10) * 1024L * 1024L * 1024L;
    }

    public async Task<bool> HasEnoughFreeSpaceAsync(long requiredBytes, CancellationToken cancellationToken = default)
    {
        var stats = await GetFreeSpaceStatsAsync(cancellationToken);

        if (stats.FreeBytes < requiredBytes)
        {
            _logger.LogWarning("Insufficient free space: {Free} bytes available, {Required} bytes required",
                stats.FreeBytes, requiredBytes);
            return false;
        }

        return true;
    }

    /// <summary>
    /// Returns stats for the most constrained drive across all qBit instances.
    /// </summary>
    public async Task<FreeSpaceStats> GetFreeSpaceStatsAsync(CancellationToken cancellationToken = default)
    {
        var thresholdGB = _config.Settings.FreeSpaceThresholdGB ?? 10;
        var thresholdBytes = (long)thresholdGB * 1024 * 1024 * 1024;
        FreeSpaceStats? mostConstrained = null;

        foreach (var (instanceName, qbitConfig) in _config.QBitInstances)
        {
            if (qbitConfig.Disabled) continue;

            var savePath = qbitConfig.DownloadPath ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            var stats = new FreeSpaceStats { Path = savePath };

            DriveInfo? drive = null;
            if (OperatingSystem.IsWindows())
            {
                var driveLetter = Path.GetPathRoot(savePath);
                if (!string.IsNullOrEmpty(driveLetter))
                    drive = DriveInfo.GetDrives().FirstOrDefault(d => d.Name == driveLetter);
            }
            else
            {
                drive = DriveInfo.GetDrives()
                    .Where(d => d.IsReady && d.DriveType == DriveType.Fixed)
                    .OrderByDescending(d => d.Name.Length)
                    .FirstOrDefault(d => savePath.StartsWith(d.Name));
            }

            if (drive != null && drive.IsReady)
            {
                stats.TotalBytes = drive.TotalSize;
                stats.FreeBytes = drive.AvailableFreeSpace;
                stats.UsedBytes = stats.TotalBytes - stats.FreeBytes;
                stats.FreePercentage = (double)stats.FreeBytes / stats.TotalBytes * 100;
                stats.ThresholdBytes = thresholdBytes;
                stats.BelowThreshold = stats.FreeBytes < thresholdBytes;

                _logger.LogDebug("[{Instance}] Free space: {Free}GB / {Total}GB ({Percent:F1}%)",
                    instanceName,
                    stats.FreeBytes / 1024.0 / 1024.0 / 1024.0,
                    stats.TotalBytes / 1024.0 / 1024.0 / 1024.0,
                    stats.FreePercentage);

                // Track the most constrained (lowest free bytes)
                if (mostConstrained == null || stats.FreeBytes < mostConstrained.FreeBytes)
                    mostConstrained = stats;
            }
            else
            {
                _logger.LogWarning("[{Instance}] Unable to determine drive info for path: {Path}", instanceName, savePath);
            }
        }

        await Task.CompletedTask;
        return mostConstrained ?? new FreeSpaceStats { ThresholdBytes = thresholdBytes };
    }

    public async Task<bool> PauseDownloadsIfLowSpaceAsync(CancellationToken cancellationToken = default)
    {
        var stats = await GetFreeSpaceStatsAsync(cancellationToken);
        if (!stats.BelowThreshold) return false;

        var paused = false;
        foreach (var (instanceName, client) in _qbitManager.GetAllClients())
        {
            try
            {
                var torrents = await client.GetTorrentsAsync(ct: cancellationToken);
                var downloading = torrents.Where(t =>
                    t.State.Contains("downloading", StringComparison.OrdinalIgnoreCase) ||
                    t.State.Contains("stalledDL", StringComparison.OrdinalIgnoreCase)
                ).ToList();

                foreach (var torrent in downloading)
                {
                    try
                    {
                        await client.PauseTorrentAsync(torrent.Hash, cancellationToken);
                        _logger.LogInformation("[{Instance}] Paused torrent due to low space: {Name}", instanceName, torrent.Name);
                        paused = true;
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "[{Instance}] Failed to pause torrent {Hash}", instanceName, torrent.Hash);
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[{Instance}] Error pausing downloads due to low space", instanceName);
            }
        }

        return paused;
    }

    public async Task<bool> ResumeDownloadsIfSpaceAvailableAsync(CancellationToken cancellationToken = default)
    {
        var stats = await GetFreeSpaceStatsAsync(cancellationToken);
        if (stats.BelowThreshold) return false;

        var resumed = false;
        foreach (var (instanceName, client) in _qbitManager.GetAllClients())
        {
            try
            {
                var torrents = await client.GetTorrentsAsync(ct: cancellationToken);
                var paused = torrents.Where(t =>
                    t.State.Contains("pausedDL", StringComparison.OrdinalIgnoreCase)
                ).ToList();

                foreach (var torrent in paused)
                {
                    try
                    {
                        await client.ResumeTorrentAsync(torrent.Hash, cancellationToken);
                        _logger.LogInformation("[{Instance}] Resumed torrent: {Name}", instanceName, torrent.Name);
                        resumed = true;
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "[{Instance}] Failed to resume torrent {Hash}", instanceName, torrent.Hash);
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[{Instance}] Error resuming downloads", instanceName);
            }
        }

        return resumed;
    }

    /// <summary>
    /// Process torrents from ALL qBit instances for the given category, sorted by added date globally.
    /// Each torrent uses its own qBit instance's client for pause/resume/tag operations.
    /// </summary>
    public async Task ProcessTorrentsForSpaceAsync(string category, CancellationToken cancellationToken = default)
    {
        // Gather torrents from all qBit instances, stamping instance name
        var allTorrents = new List<(string instanceName, QBittorrentClient client, TorrentInfo torrent)>();
        foreach (var (instanceName, client) in _qbitManager.GetAllClients())
        {
            try
            {
                await EnsureTagsExistAsync(client, cancellationToken);
                var torrents = await client.GetTorrentsAsync(category, cancellationToken);
                foreach (var t in torrents)
                {
                    t.QBitInstanceName = instanceName;
                    allTorrents.Add((instanceName, client, t));
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[{Instance}] Error fetching torrents for space processing", instanceName);
            }
        }

        if (allTorrents.Count == 0) return;

        // Get current free space (most constrained drive across all instances)
        var stats = await GetFreeSpaceStatsAsync(cancellationToken);
        _currentFreeSpace = stats.FreeBytes - _minFreeSpaceBytes;

        _logger.LogDebug(
            "Processing torrents for space across {Count} qBit instance(s) | Available: {Available} | Threshold: {Threshold} | Usable: {Usable}",
            _qbitManager.GetAllClients().Count,
            FormatBytes(stats.FreeBytes),
            FormatBytes(_minFreeSpaceBytes),
            FormatBytes(_currentFreeSpace));

        // Sort globally by added date — older torrents get priority to keep downloading
        var sorted = allTorrents.OrderBy(x => x.torrent.AddedOn).ToList();

        foreach (var (instanceName, client, torrent) in sorted)
        {
            try
            {
                await ProcessSingleTorrentSpaceAsync(client, torrent, cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[{Instance}] Error processing torrent {Hash} for space", instanceName, torrent.Hash);
            }
        }
    }

    private async Task ProcessSingleTorrentSpaceAsync(
        QBittorrentClient client,
        TorrentInfo torrent,
        CancellationToken cancellationToken)
    {
        var isDownloading = IsDownloadingState(torrent.State);
        var isPausedDownload = torrent.State.Contains("pausedDL", StringComparison.OrdinalIgnoreCase);
        var hasFreeSpaceTag = HasTag(torrent, FreeSpacePausedTag);

        if (isDownloading || (isPausedDownload && hasFreeSpaceTag))
        {
            var freeSpaceTest = _currentFreeSpace - torrent.AmountLeft;

            _logger.LogTrace(
                "Evaluating torrent: {Name} | Current space: {Current} | Space after: {After} | Remaining: {Remaining}",
                torrent.Name,
                FormatBytes(_currentFreeSpace + _minFreeSpaceBytes),
                FormatBytes(freeSpaceTest + _minFreeSpaceBytes),
                FormatBytes(torrent.AmountLeft));

            if (!isPausedDownload && freeSpaceTest < 0)
            {
                _logger.LogInformation(
                    "Pausing download (insufficient space) | Torrent: {Name} | Available: {Available} | Needed: {Needed} | Deficit: {Deficit}",
                    torrent.Name,
                    FormatBytes(_currentFreeSpace + _minFreeSpaceBytes),
                    FormatBytes(torrent.AmountLeft),
                    FormatBytes(-freeSpaceTest));

                await client.AddTagsAsync(new List<string> { torrent.Hash }, new List<string> { FreeSpacePausedTag }, cancellationToken);
                await client.RemoveTagsAsync(new List<string> { torrent.Hash }, new List<string> { AllowedSeedingTag }, cancellationToken);
                await client.PauseTorrentAsync(torrent.Hash, cancellationToken);
            }
            else if (isPausedDownload && freeSpaceTest < 0)
            {
                _logger.LogInformation(
                    "Keeping paused (insufficient space) | Torrent: {Name} | Available: {Available} | Needed: {Needed} | Deficit: {Deficit}",
                    torrent.Name,
                    FormatBytes(_currentFreeSpace + _minFreeSpaceBytes),
                    FormatBytes(torrent.AmountLeft),
                    FormatBytes(-freeSpaceTest));

                await client.AddTagsAsync(new List<string> { torrent.Hash }, new List<string> { FreeSpacePausedTag }, cancellationToken);
                await client.RemoveTagsAsync(new List<string> { torrent.Hash }, new List<string> { AllowedSeedingTag }, cancellationToken);
            }
            else if (!isPausedDownload && freeSpaceTest >= 0)
            {
                _logger.LogDebug(
                    "Continuing download (sufficient space) | Torrent: {Name} | Available: {Available} | Space after: {After}",
                    torrent.Name,
                    FormatBytes(_currentFreeSpace + _minFreeSpaceBytes),
                    FormatBytes(freeSpaceTest + _minFreeSpaceBytes));

                _currentFreeSpace = freeSpaceTest;
                await client.RemoveTagsAsync(new List<string> { torrent.Hash }, new List<string> { FreeSpacePausedTag }, cancellationToken);
            }
            else if (isPausedDownload && freeSpaceTest >= 0)
            {
                _logger.LogInformation(
                    "Resuming download (space available) | Torrent: {Name} | Available: {Available} | Space after: {After}",
                    torrent.Name,
                    FormatBytes(_currentFreeSpace + _minFreeSpaceBytes),
                    FormatBytes(freeSpaceTest + _minFreeSpaceBytes));

                _currentFreeSpace = freeSpaceTest;
                await client.RemoveTagsAsync(new List<string> { torrent.Hash }, new List<string> { FreeSpacePausedTag }, cancellationToken);
                await client.ResumeTorrentAsync(torrent.Hash, cancellationToken);
            }
        }
        else if (!isDownloading && hasFreeSpaceTag)
        {
            _logger.LogInformation(
                "Torrent completed, removing free space tag | Torrent: {Name} | Available: {Available}",
                torrent.Name,
                FormatBytes(_currentFreeSpace + _minFreeSpaceBytes));

            await client.RemoveTagsAsync(new List<string> { torrent.Hash }, new List<string> { FreeSpacePausedTag }, cancellationToken);
        }
    }

    private bool IsDownloadingState(string state)
    {
        return state.Contains("downloading", StringComparison.OrdinalIgnoreCase) ||
               state.Contains("stalledDL", StringComparison.OrdinalIgnoreCase) ||
               state.Contains("metaDL", StringComparison.OrdinalIgnoreCase);
    }

    private bool HasTag(TorrentInfo torrent, string tag)
    {
        if (string.IsNullOrEmpty(torrent.Tags)) return false;
        return torrent.Tags.Split(',', StringSplitOptions.RemoveEmptyEntries)
            .Select(t => t.Trim())
            .Contains(tag, StringComparer.OrdinalIgnoreCase);
    }

    private async Task EnsureTagsExistAsync(QBittorrentClient client, CancellationToken cancellationToken)
    {
        try
        {
            var existingTags = await client.GetTagsAsync(cancellationToken);
            var tagsToCreate = new List<string>();

            if (!existingTags.Contains(FreeSpacePausedTag, StringComparer.OrdinalIgnoreCase))
                tagsToCreate.Add(FreeSpacePausedTag);
            if (!existingTags.Contains(AllowedSeedingTag, StringComparer.OrdinalIgnoreCase))
                tagsToCreate.Add(AllowedSeedingTag);

            if (tagsToCreate.Count > 0)
                await client.CreateTagsAsync(tagsToCreate, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to ensure tags exist");
        }
    }

    private static string FormatBytes(long bytes)
    {
        string[] sizes = { "B", "KB", "MB", "GB", "TB" };
        int order = 0;
        double size = bytes;
        while (size >= 1024 && order < sizes.Length - 1) { order++; size /= 1024; }
        return $"{size:0.##} {sizes[order]}";
    }
}

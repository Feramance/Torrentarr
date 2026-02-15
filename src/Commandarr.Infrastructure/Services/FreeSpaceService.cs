using Commandarr.Core.Configuration;
using Commandarr.Core.Services;
using Microsoft.Extensions.Logging;

namespace Commandarr.Infrastructure.Services;

/// <summary>
/// Service for managing disk space and preventing download issues
/// </summary>
public class FreeSpaceService : IFreeSpaceService
{
    private readonly ILogger<FreeSpaceService> _logger;
    private readonly CommandarrConfig _config;
    private readonly QBittorrentConnectionManager _qbitManager;

    public FreeSpaceService(
        ILogger<FreeSpaceService> logger,
        CommandarrConfig config,
        QBittorrentConnectionManager qbitManager)
    {
        _logger = logger;
        _config = config;
        _qbitManager = qbitManager;
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

    public async Task<FreeSpaceStats> GetFreeSpaceStatsAsync(CancellationToken cancellationToken = default)
    {
        var stats = new FreeSpaceStats();

        try
        {
            var client = _qbitManager.GetDefaultClient();
            if (client == null)
            {
                _logger.LogWarning("No qBittorrent client available");
                return stats;
            }

            // Get save path from qBittorrent
            var savePath = _config.QBit.DownloadPath ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            stats.Path = savePath;

            // Get drive info
            DriveInfo? drive = null;
            if (OperatingSystem.IsWindows())
            {
                // On Windows, extract drive letter
                var driveLetter = Path.GetPathRoot(savePath);
                if (!string.IsNullOrEmpty(driveLetter))
                {
                    drive = DriveInfo.GetDrives().FirstOrDefault(d => d.Name == driveLetter);
                }
            }
            else
            {
                // On Linux/Mac, find the drive that contains the path
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

                // Get threshold from config (default 10GB)
                var thresholdGB = _config.Settings.FreeSpaceThresholdGB ?? 10;
                stats.ThresholdBytes = (long)thresholdGB * 1024 * 1024 * 1024;
                stats.BelowThreshold = stats.FreeBytes < stats.ThresholdBytes;

                _logger.LogDebug("Free space: {Free}GB / {Total}GB ({Percent:F1}%)",
                    stats.FreeBytes / 1024.0 / 1024.0 / 1024.0,
                    stats.TotalBytes / 1024.0 / 1024.0 / 1024.0,
                    stats.FreePercentage);
            }
            else
            {
                _logger.LogWarning("Unable to determine drive info for path: {Path}", savePath);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting free space stats");
        }

        await Task.CompletedTask;
        return stats;
    }

    public async Task<bool> PauseDownloadsIfLowSpaceAsync(CancellationToken cancellationToken = default)
    {
        var stats = await GetFreeSpaceStatsAsync(cancellationToken);

        if (!stats.BelowThreshold)
        {
            return false;
        }

        try
        {
            var client = _qbitManager.GetDefaultClient();
            if (client == null)
            {
                return false;
            }

            // Get all downloading torrents
            var torrents = await client.GetTorrentsAsync(ct: cancellationToken);
            var downloading = torrents.Where(t =>
                t.State.Contains("downloading", StringComparison.OrdinalIgnoreCase) ||
                t.State.Contains("stalledDL", StringComparison.OrdinalIgnoreCase)
            ).ToList();

            if (downloading.Count == 0)
            {
                return false;
            }

            _logger.LogWarning("Low disk space detected ({Free}GB free, threshold {Threshold}GB). Pausing {Count} downloads",
                stats.FreeBytes / 1024.0 / 1024.0 / 1024.0,
                stats.ThresholdBytes / 1024.0 / 1024.0 / 1024.0,
                downloading.Count);

            foreach (var torrent in downloading)
            {
                try
                {
                    await client.PauseTorrentAsync(torrent.Hash, cancellationToken);
                    _logger.LogInformation("Paused torrent due to low space: {Name}", torrent.Name);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Failed to pause torrent {Hash}", torrent.Hash);
                }
            }

            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error pausing downloads due to low space");
            return false;
        }
    }

    public async Task<bool> ResumeDownloadsIfSpaceAvailableAsync(CancellationToken cancellationToken = default)
    {
        var stats = await GetFreeSpaceStatsAsync(cancellationToken);

        if (stats.BelowThreshold)
        {
            return false;
        }

        try
        {
            var client = _qbitManager.GetDefaultClient();
            if (client == null)
            {
                return false;
            }

            // Get all paused torrents
            var torrents = await client.GetTorrentsAsync(ct: cancellationToken);
            var paused = torrents.Where(t =>
                t.State.Contains("pausedDL", StringComparison.OrdinalIgnoreCase)
            ).ToList();

            if (paused.Count == 0)
            {
                return false;
            }

            _logger.LogInformation("Sufficient disk space available ({Free}GB free). Resuming {Count} downloads",
                stats.FreeBytes / 1024.0 / 1024.0 / 1024.0,
                paused.Count);

            foreach (var torrent in paused)
            {
                try
                {
                    await client.ResumeTorrentAsync(torrent.Hash, cancellationToken);
                    _logger.LogInformation("Resumed torrent: {Name}", torrent.Name);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Failed to resume torrent {Hash}", torrent.Hash);
                }
            }

            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error resuming downloads");
            return false;
        }
    }
}

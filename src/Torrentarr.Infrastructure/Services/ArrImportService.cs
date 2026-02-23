using Torrentarr.Core.Configuration;
using Torrentarr.Core.Services;
using Torrentarr.Infrastructure.ApiClients.Arr;
using Torrentarr.Infrastructure.ApiClients.QBittorrent;
using Microsoft.Extensions.Logging;

namespace Torrentarr.Infrastructure.Services;

public class ArrImportService : IArrImportService
{
    private readonly ILogger<ArrImportService> _logger;
    private readonly TorrentarrConfig _config;
    private readonly QBittorrentConnectionManager _qbitManager;

    public ArrImportService(
        ILogger<ArrImportService> logger,
        TorrentarrConfig config,
        QBittorrentConnectionManager qbitManager)
    {
        _logger = logger;
        _config = config;
        _qbitManager = qbitManager;
    }

    public async Task<ImportResult> TriggerImportAsync(
        string hash,
        string contentPath,
        string category,
        CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("Triggering import for hash {Hash} in category {Category}", hash, category);

        // Find the Arr instance configuration for this category
        var arrInstance = _config.ArrInstances.Values.FirstOrDefault(i =>
            i.Category.Equals(category, StringComparison.OrdinalIgnoreCase));

        if (arrInstance == null)
        {
            _logger.LogWarning("No Arr instance configured for category {Category}", category);
            return new ImportResult
            {
                Success = false,
                Message = $"No Arr instance configured for category {category}"
            };
        }

        _logger.LogDebug("Found Arr instance: {Type} at {URI}", arrInstance.Type, arrInstance.URI);

        try
        {
            return arrInstance.Type.ToLower() switch
            {
                "radarr" => await TriggerRadarrImportAsync(arrInstance, hash, contentPath, cancellationToken),
                "sonarr" => await TriggerSonarrImportAsync(arrInstance, hash, contentPath, cancellationToken),
                "lidarr" => await TriggerLidarrImportAsync(arrInstance, hash, contentPath, cancellationToken),
                _ => new ImportResult
                {
                    Success = false,
                    Message = $"Unknown Arr type: {arrInstance.Type}"
                }
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error triggering import for hash {Hash}", hash);
            return new ImportResult
            {
                Success = false,
                Message = $"Error: {ex.Message}"
            };
        }
    }

    public async Task<bool> IsImportedAsync(string hash, CancellationToken cancellationToken = default)
    {
        // Check all Arr instances to see if they have this download in their queue
        foreach (var arrInstance in _config.ArrInstances.Values)
        {
            try
            {
                var hasInQueue = arrInstance.Type.ToLower() switch
                {
                    "radarr" => await CheckRadarrQueueAsync(arrInstance, hash, cancellationToken),
                    "sonarr" => await CheckSonarrQueueAsync(arrInstance, hash, cancellationToken),
                    "lidarr" => await CheckLidarrQueueAsync(arrInstance, hash, cancellationToken),
                    _ => false
                };

                if (hasInQueue)
                {
                    return false; // Still in queue, not yet imported
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error checking queue for instance {Category}", arrInstance.Category);
            }
        }

        // Not in any queue means it's been imported
        return true;
    }

    private async Task<ImportResult> TriggerRadarrImportAsync(
        ArrInstanceConfig config,
        string hash,
        string contentPath,
        CancellationToken cancellationToken)
    {
        var client = new RadarrClient(config.URI, config.APIKey);

        var importMode = _config.Settings.ImportMode ?? "Auto";
        var response = await client.TriggerDownloadedMoviesScanAsync(
            contentPath, hash, importMode, cancellationToken);

        if (response != null)
        {
            _logger.LogInformation("Triggered Radarr import command {CommandId} for {Path}",
                response.Id, contentPath);

            return new ImportResult
            {
                Success = true,
                Message = $"Radarr import command {response.Id} queued",
                CommandId = response.Id
            };
        }

        return new ImportResult
        {
            Success = false,
            Message = "Failed to trigger Radarr import"
        };
    }

    private async Task<ImportResult> TriggerSonarrImportAsync(
        ArrInstanceConfig config,
        string hash,
        string contentPath,
        CancellationToken cancellationToken)
    {
        var client = new SonarrClient(config.URI, config.APIKey);

        var importMode = _config.Settings.ImportMode ?? "Auto";
        var response = await client.TriggerDownloadedEpisodesScanAsync(
            contentPath, hash, importMode, cancellationToken);

        if (response != null)
        {
            _logger.LogInformation("Triggered Sonarr import command {CommandId} for {Path}",
                response.Id, contentPath);

            return new ImportResult
            {
                Success = true,
                Message = $"Sonarr import command {response.Id} queued",
                CommandId = response.Id
            };
        }

        return new ImportResult
        {
            Success = false,
            Message = "Failed to trigger Sonarr import"
        };
    }

    private async Task<ImportResult> TriggerLidarrImportAsync(
        ArrInstanceConfig config,
        string hash,
        string contentPath,
        CancellationToken cancellationToken)
    {
        var client = new LidarrClient(config.URI, config.APIKey);

        var importMode = _config.Settings.ImportMode ?? "Auto";
        var response = await client.TriggerDownloadedAlbumsScanAsync(
            contentPath, hash, importMode, cancellationToken);

        if (response != null)
        {
            _logger.LogInformation("Triggered Lidarr import command {CommandId} for {Path}",
                response.Id, contentPath);

            return new ImportResult
            {
                Success = true,
                Message = $"Lidarr import command {response.Id} queued",
                CommandId = response.Id
            };
        }

        return new ImportResult
        {
            Success = false,
            Message = "Failed to trigger Lidarr import"
        };
    }

    private async Task<bool> CheckRadarrQueueAsync(
        ArrInstanceConfig config,
        string hash,
        CancellationToken cancellationToken)
    {
        var client = new RadarrClient(config.URI, config.APIKey);
        var queue = await client.GetQueueAsync(ct: cancellationToken);

        return queue.Records.Any(r =>
            r.DownloadId != null &&
            r.DownloadId.Equals(hash, StringComparison.OrdinalIgnoreCase));
    }

    private async Task<bool> CheckSonarrQueueAsync(
        ArrInstanceConfig config,
        string hash,
        CancellationToken cancellationToken)
    {
        var client = new SonarrClient(config.URI, config.APIKey);
        var queue = await client.GetQueueAsync(ct: cancellationToken);

        return queue.Records.Any(r =>
            r.DownloadId != null &&
            r.DownloadId.Equals(hash, StringComparison.OrdinalIgnoreCase));
    }

    private async Task<bool> CheckLidarrQueueAsync(
        ArrInstanceConfig config,
        string hash,
        CancellationToken cancellationToken)
    {
        var client = new LidarrClient(config.URI, config.APIKey);
        var queue = await client.GetQueueAsync(ct: cancellationToken);

        return queue.Records.Any(r =>
            r.DownloadId != null &&
            r.DownloadId.Equals(hash, StringComparison.OrdinalIgnoreCase));
    }

    public async Task MarkAsImportedAsync(string hash, IEnumerable<string> tags, CancellationToken cancellationToken = default)
    {
        var tagList = tags.ToList();
        if (tagList.Count == 0)
        {
            tagList.Add("qbitrr-imported");
        }

        _logger.LogInformation("Adding import tags to torrent {Hash}: {Tags}", hash, string.Join(", ", tagList));

        foreach (var (instanceName, _) in _config.QBitInstances)
        {
            try
            {
                var client = _qbitManager.GetClient(instanceName);
                if (client == null) continue;

                await client.CreateTagsAsync(tagList, cancellationToken);
                var success = await client.AddTagsAsync(new List<string> { hash }, tagList, cancellationToken);
                
                if (success)
                {
                    _logger.LogDebug("Successfully added tags to {Hash} in {Instance}", hash, instanceName);
                    return;
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to add tags to {Hash} in {Instance}", hash, instanceName);
            }
        }

        _logger.LogWarning("Could not add import tags to torrent {Hash} - no qBittorrent client available", hash);
    }
}

using Torrentarr.Core.Configuration;
using Torrentarr.Core.Interfaces;
using Torrentarr.Core.Services;
using Torrentarr.Infrastructure.ApiClients.Arr;
using Torrentarr.Infrastructure.ApiClients.QBittorrent;
using Torrentarr.Infrastructure.Database;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Torrentarr.Infrastructure.Services;

public class ArrImportService : IArrImportService
{
    private readonly ILogger<ArrImportService> _logger;
    private readonly TorrentarrConfig _config;
    private readonly ITorrentClientRegistry _qbitManager;
    private readonly TorrentarrDbContext _dbContext;
    private readonly ISearchExecutor _searchExecutor;
    private readonly IOperationalIssueReporter? _issues;

    public ArrImportService(
        ILogger<ArrImportService> logger,
        TorrentarrConfig config,
        ITorrentClientRegistry qbitManager,
        TorrentarrDbContext dbContext,
        ISearchExecutor? searchExecutor = null, IOperationalIssueReporter? issues = null)
    {
        _logger = logger;
        _issues = issues;
        _config = config;
        _qbitManager = qbitManager;
        _dbContext = dbContext;
        var coordinator = new DatabaseRestartCoordinator();
        _searchExecutor = searchExecutor ?? new SearchExecutor(NullLogger<SearchExecutor>.Instance, config, dbContext,
            new QualityProfileSwitcherService(NullLogger<QualityProfileSwitcherService>.Instance, dbContext, coordinator, issues), coordinator, issues);
    }

    private OperationalIssueReport ImportIssue(string operation, string category, string hash) => new(
        "import." + operation, "automation", "Arr " + operation + " could not complete",
        "Import or replacement progress could not be confirmed; dependent work is held back.",
        "Check Arr connectivity, its queue, category ownership and application logs.",
        ArrInstance: CategoryOwnershipHelper.FindArrSectionForCategory(_config, category) ?? "", SubjectType: "torrent", SubjectId: hash.ToUpperInvariant(), Target: "logs");

    private async Task ObserveAsync(string operation, string category, string hash, bool success, CancellationToken ct)
    {
        if (_issues == null) return;
        var issue = ImportIssue(operation, category, hash);
        if (success) await _issues.ResolveAsync(issue, ct); else await _issues.ReportAsync(issue, ct);
    }

    public async Task<ImportResult> TriggerImportAsync(
        string hash,
        string contentPath,
        string category,
        CancellationToken cancellationToken = default)
    {
        _logger.LogTrace("Starting import trigger for hash {Hash} in category {Category}", hash, category);
        _logger.LogInformation("Triggering import for hash {Hash} in category {Category}", hash, category);

        // Find the Arr instance configuration for this category
        _logger.LogTrace("Looking up Arr instance for category {Category}", category);
        var arrInstance = _config.ArrInstances.Values.FirstOrDefault(i =>
            i.Category.Equals(category, StringComparison.OrdinalIgnoreCase));

        if (arrInstance == null)
        {
            _logger.LogWarning("No Arr instance configured for category {Category}", category);
            _logger.LogTrace("Import aborted - no Arr instance found for category {Category}", category);
            return new ImportResult
            {
                Success = false,
                Message = $"No Arr instance configured for category {category}"
            };
        }

        _logger.LogTrace("Found Arr instance: Type={Type}, URI={URI}, Category={Category}",
            arrInstance.Type, arrInstance.URI, arrInstance.Category);
        _logger.LogDebug("Found Arr instance: {Type} at {URI}", arrInstance.Type, arrInstance.URI);

        try
        {
            _logger.LogTrace("Routing import to {Type} handler", arrInstance.Type);
            var result = arrInstance.Type.ToLower() switch
            {
                "radarr" => await TriggerRadarrImportAsync(arrInstance, hash, contentPath, cancellationToken),
                "sonarr" => await TriggerSonarrImportAsync(arrInstance, hash, contentPath, cancellationToken),
                "lidarr" => await TriggerLidarrImportAsync(arrInstance, hash, contentPath, cancellationToken),
                "readarr" => await TriggerReadarrImportAsync(arrInstance, hash, contentPath, cancellationToken),
                _ => new ImportResult
                {
                    Success = false,
                    Message = $"Unknown Arr type: {arrInstance.Type}"
                }
            };

            _logger.LogTrace("Import result for {Hash}: Success={Success}, Message={Message}",
                hash, result.Success, result.Message);
            await ObserveAsync("scan", category, hash, result.Success, cancellationToken);
            return result;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            await ObserveAsync("scan", category, hash, false, cancellationToken);
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
        _logger.LogTrace("Checking if hash {Hash} has been imported", hash);

        // Check all Arr instances to see if they have this download in their queue
        var instancesChecked = 0;
        var hadCheckErrors = false;
        foreach (var arrInstance in _config.ArrInstances.Values)
        {
            instancesChecked++;
            _logger.LogTrace("Checking Arr instance {Name} ({Type}) for hash {Hash}",
                arrInstance.Category, arrInstance.Type, hash);

            try
            {
                var hasInQueue = arrInstance.Type.ToLower() switch
                {
                    "radarr" => await CheckRadarrQueueAsync(arrInstance, hash, cancellationToken),
                    "sonarr" => await CheckSonarrQueueAsync(arrInstance, hash, cancellationToken),
                    "lidarr" => await CheckLidarrQueueAsync(arrInstance, hash, cancellationToken),
                    "readarr" => await CheckReadarrQueueAsync(arrInstance, hash, cancellationToken),
                    _ => false
                };

                await ObserveAsync("queue", arrInstance.Category, hash, true, cancellationToken);
                if (hasInQueue)
                {
                    _logger.LogTrace("Hash {Hash} found in queue for {Instance}, not yet imported", hash, arrInstance.Category);
                    return false; // Still in queue, not yet imported
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch (Exception ex)
            {
                await ObserveAsync("queue", arrInstance.Category, hash, false, cancellationToken);
                hadCheckErrors = true;
                _logger.LogError(ex, "Error checking queue for instance {Category}", arrInstance.Category);
            }
        }

        if (hadCheckErrors)
        {
            _logger.LogWarning(
                "Could not confirm import status for hash {Hash} after checking {Count} Arr instance(s); treating as not yet imported",
                hash, instancesChecked);
            return false;
        }

        _logger.LogTrace("Checked {Count} Arr instances, hash {Hash} not in any queue", instancesChecked, hash);
        // Not in any queue means it's been imported
        return true;
    }

    private async Task<ImportResult> TriggerRadarrImportAsync(
        ArrInstanceConfig config,
        string hash,
        string contentPath,
        CancellationToken cancellationToken)
    {
        var client = new RadarrClient(config.URI, config.APIKey, config.SkipTLSVerify);

        var importMode = ResolveImportMode(config, _config.Settings);
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
        var client = new SonarrClient(config.URI, config.APIKey, config.SkipTLSVerify);

        var importMode = ResolveImportMode(config, _config.Settings);
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
        var client = new LidarrClient(config.URI, config.APIKey, config.SkipTLSVerify);

        var importMode = ResolveImportMode(config, _config.Settings);
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

    private async Task<ImportResult> TriggerReadarrImportAsync(
        ArrInstanceConfig config,
        string hash,
        string contentPath,
        CancellationToken cancellationToken)
    {
        var client = new ReadarrClient(config.URI, config.APIKey, config.SkipTLSVerify);

        var importMode = ResolveImportMode(config, _config.Settings);
        var response = await client.TriggerDownloadedBooksScanAsync(
            contentPath, hash, importMode, cancellationToken);

        if (response != null)
        {
            _logger.LogInformation("Triggered Readarr import command {CommandId} for {Path}",
                response.Id, contentPath);

            return new ImportResult
            {
                Success = true,
                Message = $"Readarr import command {response.Id} queued",
                CommandId = response.Id
            };
        }

        return new ImportResult
        {
            Success = false,
            Message = "Failed to trigger Readarr import"
        };
    }

    private async Task<bool> CheckRadarrQueueAsync(
        ArrInstanceConfig config,
        string hash,
        CancellationToken cancellationToken)
    {
        var client = new RadarrClient(config.URI, config.APIKey, config.SkipTLSVerify);
        var queue = await client.GetAllQueueAsync(ct: cancellationToken);

        return queue.Any(r =>
            r.DownloadId != null &&
            r.DownloadId.Equals(hash, StringComparison.OrdinalIgnoreCase));
    }

    private async Task<bool> CheckSonarrQueueAsync(
        ArrInstanceConfig config,
        string hash,
        CancellationToken cancellationToken)
    {
        var client = new SonarrClient(config.URI, config.APIKey, config.SkipTLSVerify);
        var queue = await client.GetAllQueueAsync(ct: cancellationToken);

        return queue.Any(r =>
            r.DownloadId != null &&
            r.DownloadId.Equals(hash, StringComparison.OrdinalIgnoreCase));
    }

    private async Task<bool> CheckLidarrQueueAsync(
        ArrInstanceConfig config,
        string hash,
        CancellationToken cancellationToken)
    {
        var client = new LidarrClient(config.URI, config.APIKey, config.SkipTLSVerify);
        var queue = await client.GetAllQueueAsync(ct: cancellationToken);

        return queue.Any(r =>
            r.DownloadId != null &&
            r.DownloadId.Equals(hash, StringComparison.OrdinalIgnoreCase));
    }

    private async Task<bool> CheckReadarrQueueAsync(
        ArrInstanceConfig config,
        string hash,
        CancellationToken cancellationToken)
    {
        var client = new ReadarrClient(config.URI, config.APIKey, config.SkipTLSVerify);
        var queue = await client.GetAllQueueAsync(ct: cancellationToken);

        return queue.Any(r =>
            r.DownloadId != null &&
            r.DownloadId.Equals(hash, StringComparison.OrdinalIgnoreCase));
    }

    public async Task MarkAsImportedAsync(string instanceId, string hash, IEnumerable<string> tags, CancellationToken cancellationToken = default)
    {
        var client = _qbitManager.GetClient(instanceId);
        if (client == null || !client.Capabilities.Tags) return;
        var tagList = tags.ToList();
        if (tagList.Count == 0) tagList.Add("qbitrr-imported");
        if (!await client.CreateTagsAsync(tagList, cancellationToken)
            || !await client.AddTagsAsync([hash], tagList, cancellationToken))
            _logger.LogWarning("Could not add import tags to torrent {Hash} on {Instance}", hash, instanceId);
    }

    /// <summary>
    /// Check if a torrent's custom format score is unmet.
    /// Matches qBitrr's custom_format_unmet_check (arss.py:6255-6324).
    /// </summary>
    public async Task<bool> IsCustomFormatUnmetAsync(string hash, string category, CancellationToken cancellationToken = default)
    {
        var arrInstance = _config.ArrInstances.Values.FirstOrDefault(i =>
            i.Category.Equals(category, StringComparison.OrdinalIgnoreCase));

        if (arrInstance == null || !arrInstance.Search.CustomFormatUnmetSearch)
            return false;

        try
        {
            var downloadId = hash.ToUpperInvariant();

            return arrInstance.Type.ToLower() switch
            {
                "radarr" => await CheckRadarrCfUnmetAsync(arrInstance, downloadId, cancellationToken),
                "sonarr" => await CheckSonarrCfUnmetAsync(arrInstance, downloadId, cancellationToken),
                "lidarr" => await CheckLidarrCfUnmetAsync(arrInstance, downloadId, cancellationToken),
                "readarr" => await CheckReadarrCfUnmetAsync(arrInstance, downloadId, cancellationToken),
                _ => false
            };
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error checking custom format for torrent {Hash}", hash);
            return false;
        }
    }

    private async Task<bool> CheckRadarrCfUnmetAsync(ArrInstanceConfig config, string downloadId, CancellationToken ct)
    {
        var client = new RadarrClient(config.URI, config.APIKey, config.SkipTLSVerify);
        var queue = await client.GetAllQueueAsync(ct: ct);
        var record = queue.FirstOrDefault(r =>
            string.Equals(r.DownloadId, downloadId, StringComparison.OrdinalIgnoreCase));

        if (record?.CustomFormatScore == null || record.MovieId == null)
            return false;

        var arrName = _config.ArrInstances.FirstOrDefault(kv =>
            kv.Value == config).Key ?? "";

        var modelEntry = await _dbContext.Movies.AsNoTracking()
            .FirstOrDefaultAsync(m => m.ArrId == record.MovieId && m.ArrInstance == arrName, ct);

        if (modelEntry == null)
            return false;

        if (modelEntry.MovieFileId != 0)
        {
            var cfUnmet = record.CustomFormatScore < (modelEntry.CustomFormatScore ?? 0);
            if (config.Search.ForceMinimumCustomFormat)
                cfUnmet = cfUnmet && record.CustomFormatScore < (modelEntry.MinCustomFormatScore ?? 0);
            return cfUnmet;
        }

        return false;
    }

    private async Task<bool> CheckSonarrCfUnmetAsync(ArrInstanceConfig config, string downloadId, CancellationToken ct)
    {
        var client = new SonarrClient(config.URI, config.APIKey, config.SkipTLSVerify);
        var queue = await client.GetAllQueueAsync(ct: ct);
        var record = queue.FirstOrDefault(r =>
            string.Equals(r.DownloadId, downloadId, StringComparison.OrdinalIgnoreCase));

        if (record?.CustomFormatScore == null)
            return false;

        var arrName = _config.ArrInstances.FirstOrDefault(kv =>
            kv.Value == config).Key ?? "";

        var isSeriesSearch = config.Search.SearchBySeries.Equals("series", StringComparison.OrdinalIgnoreCase);

        if (isSeriesSearch)
        {
            if (record.SeriesId == null) return false;

            var seriesEntry = await _dbContext.Series.AsNoTracking()
                .FirstOrDefaultAsync(s => s.ArrId == record.SeriesId && s.ArrInstance == arrName, ct);

            if (seriesEntry == null) return false;

            if (config.Search.ForceMinimumCustomFormat)
                return record.CustomFormatScore < (seriesEntry.MinCustomFormatScore ?? 0);

            return false;
        }
        else
        {
            var episodes = await _dbContext.Episodes.AsNoTracking()
                .Where(e => record.EpisodeIds.Contains(e.ArrId) && e.ArrInstance == arrName && e.EpisodeFileId > 0)
                .ToListAsync(ct);
            return episodes.Any(e => record.CustomFormatScore < (e.CustomFormatScore ?? 0)
                && (!config.Search.ForceMinimumCustomFormat || record.CustomFormatScore < (e.MinCustomFormatScore ?? 0)));
        }
    }

    private async Task<bool> CheckLidarrCfUnmetAsync(ArrInstanceConfig config, string downloadId, CancellationToken ct)
    {
        var client = new LidarrClient(config.URI, config.APIKey, config.SkipTLSVerify);
        var queue = await client.GetAllQueueAsync(ct: ct);
        var record = queue.FirstOrDefault(r =>
            string.Equals(r.DownloadId, downloadId, StringComparison.OrdinalIgnoreCase));

        if (record?.CustomFormatScore == null || record.AlbumId == null)
            return false;

        var arrName = _config.ArrInstances.FirstOrDefault(kv =>
            kv.Value == config).Key ?? "";

        var modelEntry = await _dbContext.Albums.AsNoTracking()
            .FirstOrDefaultAsync(a => a.ArrId == record.AlbumId && a.ArrInstance == arrName, ct);

        if (modelEntry == null)
            return false;

        var cfUnmet = record.CustomFormatScore < (modelEntry.CustomFormatScore ?? 0);
        if (config.Search.ForceMinimumCustomFormat)
            cfUnmet = cfUnmet && record.CustomFormatScore < (modelEntry.MinCustomFormatScore ?? 0);
        return cfUnmet;
    }

    /// <summary>
    /// Blocklist a torrent in the Arr queue (removeFromClient=false, blocklist=true).
    /// When <see cref="ArrInstanceConfig.ReSearch"/> is true, also fire Arr *Search for the media.
    /// </summary>
    public async Task<bool> BlocklistAndReSearchAsync(string hash, string category, CancellationToken cancellationToken = default)
    {
        var arrInstance = _config.ArrInstances.Values.FirstOrDefault(i =>
            i.Category.Equals(category, StringComparison.OrdinalIgnoreCase));

        if (arrInstance == null)
        {
            _logger.LogWarning("No Arr instance for category {Category} — cannot blocklist", category);
            return false;
        }

        try
        {
            var downloadId = hash.ToUpperInvariant();
            var instanceName = _config.ArrInstances.First(kv => kv.Value == arrInstance).Key;
            if (arrInstance.ReSearch && await _dbContext.SonarrSearchRetries.AnyAsync(r => r.ArrInstance == instanceName && r.DownloadId == downloadId, cancellationToken))
            {
                await SonarrRetryQueue.ExecuteAsync(_dbContext, _searchExecutor, instanceName, _logger, cancellationToken, arrInstance, _issues);
                return !await _dbContext.SonarrSearchRetries.AnyAsync(r => r.ArrInstance == instanceName && r.DownloadId == downloadId, cancellationToken);
            }

            var success = arrInstance.Type.ToLower() switch
            {
                "radarr" => await BlocklistRadarrAsync(arrInstance, downloadId, cancellationToken),
                "sonarr" => await BlocklistSonarrAsync(arrInstance, downloadId, cancellationToken),
                "lidarr" => await BlocklistLidarrAsync(arrInstance, downloadId, cancellationToken),
                "readarr" => await BlocklistReadarrAsync(arrInstance, downloadId, cancellationToken),
                _ => false
            };
            if (!arrInstance.ReSearch) await ObserveAsync("replacement", category, hash, success, cancellationToken);
            return success;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            await ObserveAsync("replacement", category, hash, false, cancellationToken);
            _logger.LogError(ex, "Error blocklisting torrent {Hash} in {Category}", hash, category);
            return false;
        }
    }

    private async Task<bool> BlocklistRadarrAsync(ArrInstanceConfig config, string downloadId, CancellationToken ct)
    {
        var client = new RadarrClient(config.URI, config.APIKey, config.SkipTLSVerify);
        var queue = await client.GetAllQueueAsync(ct: ct);
        var record = queue.FirstOrDefault(r =>
            string.Equals(r.DownloadId, downloadId, StringComparison.OrdinalIgnoreCase));

        if (record == null) return false;

        if (!config.ReSearch) return await client.DeleteFromQueueAsync(record.Id, removeFromClient: false, blocklist: true, ct, skipRedownload: true);
        List<SearchCandidate> candidates = record.MovieId is int id ? [new SearchCandidate { ArrId = id, Type = "Movie", Reason = "Retry" }] : [];
        var instanceName = _config.ArrInstances.First(kv => kv.Value == config).Key;
        return await SonarrRetryQueue.StageAndExecuteAsync(_dbContext, _searchExecutor, instanceName, downloadId,
            candidates, config, _logger, _issues, ct);
    }

    private async Task<bool> BlocklistSonarrAsync(ArrInstanceConfig config, string downloadId, CancellationToken ct)
    {
        var client = new SonarrClient(config.URI, config.APIKey, config.SkipTLSVerify);
        var queue = await client.GetAllQueueAsync(ct: ct);
        var record = queue.FirstOrDefault(r =>
            string.Equals(r.DownloadId, downloadId, StringComparison.OrdinalIgnoreCase));

        if (record == null) return false;

        if (!config.ReSearch) return await client.DeleteFromQueueAsync(record.Id, removeFromClient: false, blocklist: true, ct, skipRedownload: true);
        List<SearchCandidate> candidates = record.EpisodeIds.Distinct().Select(id => new SearchCandidate { ArrId = id, SeriesId = record.SeriesId, Type = "Episode", Reason = "Retry" }).ToList();
        if (candidates.Count == 0 && record.SeriesId.HasValue && config.Search.SearchBySeries.Equals("series", StringComparison.OrdinalIgnoreCase))
            candidates.Add(new SearchCandidate { SeriesId = record.SeriesId, Type = "Episode", Reason = "Retry" });
        var instanceName = _config.ArrInstances.First(kv => kv.Value == config).Key;
        return await SonarrRetryQueue.StageAndExecuteAsync(_dbContext, _searchExecutor, instanceName, downloadId,
            candidates, config, _logger, _issues, ct);
    }

    private async Task<bool> BlocklistLidarrAsync(ArrInstanceConfig config, string downloadId, CancellationToken ct)
    {
        var client = new LidarrClient(config.URI, config.APIKey, config.SkipTLSVerify);
        var queue = await client.GetAllQueueAsync(ct: ct);
        var record = queue.FirstOrDefault(r =>
            string.Equals(r.DownloadId, downloadId, StringComparison.OrdinalIgnoreCase));

        if (record == null) return false;

        if (!config.ReSearch) return await client.DeleteFromQueueAsync(record.Id, removeFromClient: false, blocklist: true, ct, skipRedownload: true);
        List<SearchCandidate> candidates = record.AlbumId is int id ? [new SearchCandidate { ArrId = id, Type = "Album", Reason = "Retry" }] : [];
        var instanceName = _config.ArrInstances.First(kv => kv.Value == config).Key;
        return await SonarrRetryQueue.StageAndExecuteAsync(_dbContext, _searchExecutor, instanceName, downloadId,
            candidates, config, _logger, _issues, ct);
    }

    private async Task<bool> BlocklistReadarrAsync(ArrInstanceConfig config, string downloadId, CancellationToken ct)
    {
        var client = new ReadarrClient(config.URI, config.APIKey, config.SkipTLSVerify);
        var queue = await client.GetAllQueueAsync(ct: ct);
        var record = queue.FirstOrDefault(r =>
            string.Equals(r.DownloadId, downloadId, StringComparison.OrdinalIgnoreCase));

        if (record == null) return false;

        if (!config.ReSearch) return await client.DeleteFromQueueAsync(record.Id, removeFromClient: false, blocklist: true, ct, skipRedownload: true);
        List<SearchCandidate> candidates = record.BookId is int book ? [new SearchCandidate { ArrId = book, Type = "Book", Reason = "Retry" }]
            : record.AuthorId is int author ? [new SearchCandidate { ArrId = author, AuthorId = author, Type = "Author", Reason = "Retry" }] : [];
        var instanceName = _config.ArrInstances.First(kv => kv.Value == config).Key;
        return await SonarrRetryQueue.StageAndExecuteAsync(_dbContext, _searchExecutor, instanceName, downloadId,
            candidates, config, _logger, _issues, ct);
    }

    internal static (string Kind, int Id)? ResolveReadarrReSearch(int? bookId, int? authorId)
    {
        if (bookId is int book)
            return ("book", book);
        if (authorId is int author)
            return ("author", author);
        return null;
    }

    private async Task<bool> CheckReadarrCfUnmetAsync(ArrInstanceConfig config, string downloadId, CancellationToken ct)
    {
        var client = new ReadarrClient(config.URI, config.APIKey, config.SkipTLSVerify);
        var queue = await client.GetAllQueueAsync(ct: ct);
        var record = queue.FirstOrDefault(r =>
            string.Equals(r.DownloadId, downloadId, StringComparison.OrdinalIgnoreCase));

        if (record?.CustomFormatScore == null || record.BookId == null)
            return false;

        var arrName = _config.ArrInstances.FirstOrDefault(kv =>
            kv.Value == config).Key ?? "";

        var modelEntry = await _dbContext.Books.AsNoTracking()
            .FirstOrDefaultAsync(b => b.ArrId == record.BookId && b.ArrInstance == arrName, ct);

        if (modelEntry == null)
            return false;

        var cfUnmet = record.CustomFormatScore < (modelEntry.CustomFormatScore ?? 0);
        if (config.Search.ForceMinimumCustomFormat)
            cfUnmet = cfUnmet && record.CustomFormatScore < (modelEntry.MinCustomFormatScore ?? 0);
        return cfUnmet;
    }

    /// <summary>
    /// Per-Arr <c>ImportMode</c> wins; otherwise <see cref="SettingsConfig.ImportMode"/>; otherwise Auto.
    /// Null/blank means the per-Arr TOML key was omitted — use Settings, not Auto.
    /// </summary>
    internal static string ResolveImportMode(ArrInstanceConfig instance, SettingsConfig settings)
    {
        var perArr = instance.ImportMode?.Trim();
        if (!string.IsNullOrEmpty(perArr))
            return perArr;

        var global = settings.ImportMode?.Trim();
        if (!string.IsNullOrEmpty(global))
            return global;

        return "Auto";
    }
}

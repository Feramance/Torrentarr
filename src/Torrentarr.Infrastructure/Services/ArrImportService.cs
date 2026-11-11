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

    private async Task ObserveAsync(string operation, string category, string hash, bool success, CancellationToken ct, string? evidence = null)
    {
        if (_issues == null) return;
        var issue = ImportIssue(operation, category, hash);
        if (success) await _issues.ResolveAsync(issue, ct); else await _issues.ReportAsync(issue.WithEvidence($"Operation={operation}; category={category}; torrent={hash}; {evidence ?? "Arr operation returned false without additional response details."}"), ct);
    }

    public async Task<ImportResult> TriggerImportAsync(
        string hash,
        string contentPath,
        string category,
        CancellationToken cancellationToken = default)
    {
        using var importScope = _logger.BeginScope(new Dictionary<string, object> { ["Operation"] = "Import", ["TorrentHash"] = hash, ["TorrentCategory"] = category });
        var started = System.Diagnostics.Stopwatch.GetTimestamp();
        _logger.LogInformation("Starting import trigger for hash {Hash} in category {Category}", hash, category);

        // Find the Arr instance configuration for this category
        _logger.LogTrace("Looking up Arr instance for category {Category}", category);
        var target = _config.ArrInstances.FirstOrDefault(p =>
            p.Value.Category.Equals(category, StringComparison.OrdinalIgnoreCase));
        var arrInstance = target.Value;

        if (arrInstance == null)
        {
            _logger.LogWarning("No Arr instance configured for category {Category}", category);
            _logger.LogInformation("Import aborted - no Arr instance found for category {Category}", category);
            return new ImportResult
            {
                Success = false,
                Message = $"No Arr instance configured for category {category}"
            };
        }

        _logger.LogInformation("Found Arr instance: Type={Type}, URI={URI}, Category={Category}",
            arrInstance.Type, DiagnosticText.Safe(arrInstance.URI), arrInstance.Category);
        using var instanceScope = _logger.BeginScope(new Dictionary<string, object> { ["ProcessInstance"] = target.Key });
        _logger.LogTrace("Import request: path={Path}; configuredMode={ConfiguredMode}; globalMode={GlobalMode}; type={Type}", contentPath, arrInstance.ImportMode, _config.Settings.ImportMode, arrInstance.Type);

        try
        {
            _logger.LogInformation("Routing import to {Type} handler", arrInstance.Type);
            using var activity = ArrActivityTracking.Begin(_dbContext, _logger, target.Key, arrInstance, "import", hash);
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

            _logger.LogInformation("Import scan submission result for {Hash}: accepted={Success}; message={Message}; elapsed={ElapsedMs:F1}ms; media import completion requires a later Arr observation",
                hash, result.Success, result.Message, System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds);
            await ObserveAsync("scan", category, hash, result.Success, cancellationToken, result.Message);
            return result;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            await ObserveAsync("scan", category, hash, false, cancellationToken, DiagnosticText.Error(ex));
            _logger.LogError(ex, "Error triggering import for hash {Hash}", hash);
            return new ImportResult
            {
                Success = false,
                Message = $"Error: {ex.Message}"
            };
        }
    }

    public async Task<bool> IsImportedAsync(string hash, string category, DateTimeOffset submittedAt,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var section = CategoryOwnershipHelper.FindArrSectionForCategory(_config, category);
        if (section == null || !_config.ArrInstances.TryGetValue(section, out var arr)) return false;

        try
        {
            var type = arr.Type.ToLowerInvariant();
            var evidence = new Torrentarr.Infrastructure.Database.Models.ArrActivityOperation
            {
                Type = type,
                DownloadId = hash,
                SubmittedAt = submittedAt.ToUnixTimeSeconds()
            };
            var eventType = type is "lidarr" or "readarr" ? 8 : 3;
            var since = DateTimeOffset.FromUnixTimeSeconds(evidence.SubmittedAt);
            var history = await ArrActivityCollector.ReadAsync(arr,
                $"history/since?date={Uri.EscapeDataString(since.ToString("O"))}&eventType={eventType}", cancellationToken);
            await ObserveAsync("history", category, hash, true, cancellationToken);
            return history.Any(item => ArrActivityCollector.PositiveImport(item, evidence));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            await ObserveAsync("history", category, hash, false, cancellationToken, DiagnosticText.Error(ex));
            _logger.LogWarning("Could not verify Arr import history for {Category} and {Hash}", category, hash);
            return false;
        }
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
        using var scope = _logger.BeginScope(new Dictionary<string, object>
        {
            ["Operation"] = "CustomFormatCheck",
            ["TorrentHash"] = hash,
            ["TorrentCategory"] = category
        });
        var target = _config.ArrInstances.FirstOrDefault(p =>
            p.Value.Category.Equals(category, StringComparison.OrdinalIgnoreCase));
        var arrInstance = target.Value;

        if (arrInstance == null || !arrInstance.Search.CustomFormatUnmetSearch)
        {
            _logger.LogDebug("Custom format check skipped: instanceFound={Found}; enabled={Enabled}", arrInstance != null, arrInstance?.Search.CustomFormatUnmetSearch);
            return false;
        }

        try
        {
            var downloadId = hash.ToUpperInvariant();

            var unmet = arrInstance.Type.ToLower() switch
            {
                "radarr" => await CheckRadarrCfUnmetAsync(arrInstance, downloadId, cancellationToken),
                "sonarr" => await CheckSonarrCfUnmetAsync(arrInstance, downloadId, cancellationToken),
                "lidarr" => await CheckLidarrCfUnmetAsync(arrInstance, downloadId, cancellationToken),
                "readarr" => await CheckReadarrCfUnmetAsync(arrInstance, downloadId, cancellationToken),
                _ => false
            };
            _logger.LogDebug("Custom format decision: type={Type}; unmet={Unmet}; forceMinimum={ForceMinimum}; searchBySeries={SearchBySeries}",
                arrInstance.Type, unmet, arrInstance.Search.ForceMinimumCustomFormat, arrInstance.Search.SearchBySeries);
            return unmet;
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
        {
            _logger.LogDebug("Custom format check skipped: matchingQueueRow={Found}; queueScore={Score}; movieId={MediaId}", record != null, record?.CustomFormatScore, record?.MovieId);
            return false;
        }

        var arrName = _config.ArrInstances.FirstOrDefault(kv =>
            kv.Value == config).Key ?? "";

        var modelEntry = await _dbContext.Movies.AsNoTracking()
            .FirstOrDefaultAsync(m => m.ArrId == record.MovieId && m.ArrInstance == arrName, ct);

        if (modelEntry == null)
        {
            _logger.LogDebug("Custom format check skipped: no synchronized catalog row for matching queue item");
            return false;
        }

        if (modelEntry.MovieFileId != 0)
        {
            var cfUnmet = record.CustomFormatScore < (modelEntry.CustomFormatScore ?? 0);
            if (config.Search.ForceMinimumCustomFormat)
                cfUnmet = cfUnmet && record.CustomFormatScore < (modelEntry.MinCustomFormatScore ?? 0);
            _logger.LogTrace("Custom format comparison: queued={Queued}; existing={Existing}; minimum={Minimum}; fileId={FileId}; unmet={Unmet}",
                record.CustomFormatScore, modelEntry.CustomFormatScore ?? 0, modelEntry.MinCustomFormatScore ?? 0, modelEntry.MovieFileId, cfUnmet);
            return cfUnmet;
        }

        _logger.LogDebug("Custom format check skipped: movie has no existing file; movieId={MediaId}", record.MovieId);
        return false;
    }

    private async Task<bool> CheckSonarrCfUnmetAsync(ArrInstanceConfig config, string downloadId, CancellationToken ct)
    {
        var client = new SonarrClient(config.URI, config.APIKey, config.SkipTLSVerify);
        var queue = await client.GetAllQueueAsync(ct: ct);
        var records = queue.Where(r =>
            string.Equals(r.DownloadId, downloadId, StringComparison.OrdinalIgnoreCase) &&
            r.CustomFormatScore.HasValue).ToList();

        if (records.Count == 0)
        {
            _logger.LogDebug("Custom format check skipped: no matching queue rows with a score");
            return false;
        }
        foreach (var record in records)
            _logger.LogTrace("Custom format queue input: score={Score}; seriesId={SeriesId}; episodeIds={EpisodeIds}", record.CustomFormatScore, record.SeriesId, string.Join(",", record.EpisodeIds));

        var arrName = _config.ArrInstances.FirstOrDefault(kv =>
            kv.Value == config).Key ?? "";

        if (config.Search.SearchBySeries.Equals("series", StringComparison.OrdinalIgnoreCase))
        {
            if (!config.Search.ForceMinimumCustomFormat)
            {
                _logger.LogDebug("Custom format check skipped: series search requires ForceMinimumCustomFormat");
                return false;
            }

            var seriesIds = records.Where(r => r.SeriesId.HasValue)
                .Select(r => r.SeriesId!.Value).Distinct().ToList();
            var series = await _dbContext.Series.AsNoTracking()
                .Where(s => s.ArrInstance == arrName && seriesIds.Contains(s.ArrId))
                .ToListAsync(ct);
            foreach (var row in series)
                _logger.LogTrace("Custom format catalog input: seriesId={MediaId}; minimum={Minimum}", row.ArrId, row.MinCustomFormatScore ?? 0);
            return records.Any(r => r.SeriesId.HasValue &&
                series.Any(s => s.ArrId == r.SeriesId.Value &&
                    r.CustomFormatScore!.Value < (s.MinCustomFormatScore ?? 0)));
        }

        var episodeIds = records.SelectMany(r => r.EpisodeIds).Distinct().ToList();
        var episodes = await _dbContext.Episodes.AsNoTracking()
            .Where(e => episodeIds.Contains(e.ArrId) && e.ArrInstance == arrName && e.EpisodeFileId > 0)
            .ToListAsync(ct);
        foreach (var row in episodes)
            _logger.LogTrace("Custom format catalog input: episodeId={MediaId}; existing={Existing}; minimum={Minimum}; fileId={FileId}", row.ArrId, row.CustomFormatScore ?? 0, row.MinCustomFormatScore ?? 0, row.EpisodeFileId);
        return records.Any(r => episodes.Any(e => r.EpisodeIds.Contains(e.ArrId) &&
            r.CustomFormatScore!.Value < (e.CustomFormatScore ?? 0) &&
            (!config.Search.ForceMinimumCustomFormat ||
                r.CustomFormatScore.Value < (e.MinCustomFormatScore ?? 0))));
    }

    private async Task<bool> CheckLidarrCfUnmetAsync(ArrInstanceConfig config, string downloadId, CancellationToken ct)
    {
        var client = new LidarrClient(config.URI, config.APIKey, config.SkipTLSVerify);
        var queue = await client.GetAllQueueAsync(ct: ct);
        var record = queue.FirstOrDefault(r =>
            string.Equals(r.DownloadId, downloadId, StringComparison.OrdinalIgnoreCase));

        if (record?.CustomFormatScore == null || record.AlbumId == null)
        {
            _logger.LogDebug("Custom format check skipped: matchingQueueRow={Found}; queueScore={Score}; albumId={MediaId}", record != null, record?.CustomFormatScore, record?.AlbumId);
            return false;
        }

        var arrName = _config.ArrInstances.FirstOrDefault(kv =>
            kv.Value == config).Key ?? "";

        var modelEntry = await _dbContext.Albums.AsNoTracking()
            .FirstOrDefaultAsync(a => a.ArrId == record.AlbumId && a.ArrInstance == arrName, ct);

        if (modelEntry == null)
        {
            _logger.LogDebug("Custom format check skipped: no synchronized catalog row for matching queue item");
            return false;
        }

        var cfUnmet = record.CustomFormatScore < (modelEntry.CustomFormatScore ?? 0);
        if (config.Search.ForceMinimumCustomFormat)
            cfUnmet = cfUnmet && record.CustomFormatScore < (modelEntry.MinCustomFormatScore ?? 0);
        _logger.LogTrace("Custom format comparison: queued={Queued}; existing={Existing}; minimum={Minimum}; unmet={Unmet}",
            record.CustomFormatScore, modelEntry.CustomFormatScore ?? 0, modelEntry.MinCustomFormatScore ?? 0, cfUnmet);
        return cfUnmet;
    }

    /// <summary>
    /// Blocklist a torrent in the Arr queue (removeFromClient=false, blocklist=true).
    /// When <see cref="ArrInstanceConfig.ReSearch"/> is true, also fire Arr *Search for the media.
    /// </summary>
    public async Task<bool> BlocklistAndReSearchAsync(string hash, string category, CancellationToken cancellationToken = default)
    {
        using var scope = _logger.BeginScope(new Dictionary<string, object>
        {
            ["Operation"] = "BlocklistReplacement",
            ["TorrentHash"] = hash,
            ["TorrentCategory"] = category
        });
        var target = _config.ArrInstances.FirstOrDefault(p =>
            p.Value.Category.Equals(category, StringComparison.OrdinalIgnoreCase));
        var arrInstance = target.Value;

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
                _logger.LogDebug("Resuming persisted replacement intent for {Instance}; queue removal will follow the stored phase", instanceName);
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
            _logger.LogInformation("Blocklist/replacement result: type={Type}; reSearch={ReSearch}; completed={Completed}; removeFromClient=false", arrInstance.Type, arrInstance.ReSearch, success);
            return success;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            await ObserveAsync("replacement", category, hash, false, cancellationToken, DiagnosticText.Error(ex));
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

        if (record == null)
        {
            _logger.LogDebug("Blocklist skipped: no matching Arr queue row for download {Hash}", downloadId);
            return false;
        }
        _logger.LogDebug("Blocklist queue match: queueId={QueueId}; reSearch={ReSearch}; removeFromClient=false", record.Id, config.ReSearch);

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
        var records = queue.Where(r =>
            string.Equals(r.DownloadId, downloadId, StringComparison.OrdinalIgnoreCase)).ToList();

        if (records.Count == 0)
        {
            _logger.LogDebug("Blocklist skipped: no matching Sonarr queue rows for download {Hash}", downloadId);
            return false;
        }
        _logger.LogDebug("Blocklist queue match: rows={Count}; queueIds={QueueIds}; reSearch={ReSearch}; removeFromClient=false", records.Count, string.Join(",", records.Select(r => r.Id)), config.ReSearch);

        if (!config.ReSearch)
        {
            var succeeded = true;
            foreach (var id in records.Select(r => r.Id).Distinct())
                if (!await client.DeleteFromQueueAsync(id, removeFromClient: false, blocklist: true, ct, skipRedownload: true))
                    succeeded = false;
            return succeeded;
        }

        // A download may produce several queue rows, all removed by ConfirmBlocklistAsync.
        // Stage every affected episode before deleting any of them.
        var candidates = records
            .SelectMany(r => r.EpisodeIds.Where(id => id > 0).Select(id => new SearchCandidate
            {
                ArrId = id,
                SeriesId = r.SeriesId,
                Type = "Episode",
                Reason = "Retry"
            }))
            .DistinctBy(c => c.ArrId)
            .ToList();

        var unidentified = records.Where(r => !r.EpisodeIds.Any(id => id > 0)).ToList();
        if (unidentified.Count > 0)
        {
            // The retry planner deduplicates on ArrId; multiple ArrId=0 series
            // fallbacks would collapse and lose replacement intent.
            var seriesIds = unidentified.Where(r => r.SeriesId is > 0)
                .Select(r => r.SeriesId!.Value).Distinct().ToList();
            if (!config.Search.SearchBySeries.Equals("series", StringComparison.OrdinalIgnoreCase) ||
                seriesIds.Count != 1 || unidentified.Any(r => r.SeriesId is not > 0))
                throw new IOException("Cannot preserve replacement intent for all Sonarr queue rows; blocklisting was skipped.");

            candidates.Add(new SearchCandidate
            {
                SeriesId = seriesIds[0],
                Type = "Episode",
                Reason = "Retry"
            });
        }

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

        if (record == null)
        {
            _logger.LogDebug("Blocklist skipped: no matching Arr queue row for download {Hash}", downloadId);
            return false;
        }
        _logger.LogDebug("Blocklist queue match: queueId={QueueId}; reSearch={ReSearch}; removeFromClient=false", record.Id, config.ReSearch);

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

        if (record == null)
        {
            _logger.LogDebug("Blocklist skipped: no matching Arr queue row for download {Hash}", downloadId);
            return false;
        }
        _logger.LogDebug("Blocklist queue match: queueId={QueueId}; reSearch={ReSearch}; removeFromClient=false", record.Id, config.ReSearch);

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
        {
            _logger.LogDebug("Custom format check skipped: matchingQueueRow={Found}; queueScore={Score}; bookId={MediaId}", record != null, record?.CustomFormatScore, record?.BookId);
            return false;
        }

        var arrName = _config.ArrInstances.FirstOrDefault(kv =>
            kv.Value == config).Key ?? "";

        var modelEntry = await _dbContext.Books.AsNoTracking()
            .FirstOrDefaultAsync(b => b.ArrId == record.BookId && b.ArrInstance == arrName, ct);

        if (modelEntry == null)
        {
            _logger.LogDebug("Custom format check skipped: no synchronized book catalog row");
            return false;
        }

        var cfUnmet = record.CustomFormatScore < (modelEntry.CustomFormatScore ?? 0);
        if (config.Search.ForceMinimumCustomFormat)
            cfUnmet = cfUnmet && record.CustomFormatScore < (modelEntry.MinCustomFormatScore ?? 0);
        _logger.LogTrace("Custom format comparison: queued={Queued}; existing={Existing}; minimum={Minimum}; unmet={Unmet}",
            record.CustomFormatScore, modelEntry.CustomFormatScore ?? 0, modelEntry.MinCustomFormatScore ?? 0, cfUnmet);
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

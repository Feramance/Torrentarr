using Torrentarr.Core.Configuration;
using Torrentarr.Core.Services;
using Torrentarr.Infrastructure.ApiClients.Arr;
using Torrentarr.Infrastructure.Database;
using Torrentarr.Infrastructure.Database.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Torrentarr.Infrastructure.Services;

public class SearchExecutor : ISearchExecutor
{
    private readonly ILogger<SearchExecutor> _logger;
    private readonly TorrentarrConfig _config;
    private readonly TorrentarrDbContext _db;
    private readonly QualityProfileSwitcherService _profileSwitcher;

    private static readonly HashSet<string> ActiveCommandStatuses = new(StringComparer.OrdinalIgnoreCase)
    {
        "queued", "started", "running"
    };

    private static readonly HashSet<string> SearchCommandNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "MoviesSearch", "MovieSearch", "EpisodeSearch", "SeasonSearch", "SeriesSearch",
        "AlbumSearch", "ArtistSearch", "MissingMoviesSearch", "MissingEpisodesSearch",
        "CutoffUnmetMoviesSearch", "CutoffUnmetEpisodesSearch"
    };

    public SearchExecutor(
        ILogger<SearchExecutor> logger,
        TorrentarrConfig config,
        TorrentarrDbContext db,
        QualityProfileSwitcherService profileSwitcher)
    {
        _logger = logger;
        _config = config;
        _db = db;
        _profileSwitcher = profileSwitcher;
    }

    public async Task<SearchResult> ExecuteSearchesAsync(
        string instanceName,
        IEnumerable<SearchCandidate> candidates,
        CancellationToken cancellationToken = default)
    {
        var result = new SearchResult();

        if (!_config.ArrInstances.TryGetValue(instanceName, out var arrConfig))
        {
            _logger.LogWarning("SearchExecutor: no instance named {Name}", instanceName);
            return result;
        }

        var searchLoopDelay = _config.Settings.SearchLoopDelay > 0
            ? _config.Settings.SearchLoopDelay
            : 30;

        var searchLimit = arrConfig.Search.SearchLimit > 0 ? arrConfig.Search.SearchLimit : 5;

        // SearchInReverse=false (default) → oldest first (ASC); SearchInReverse=true → newest first (DESC)
        var candidatesList = arrConfig.Search.SearchInReverse
            ? candidates
                .OrderBy(c => c.Priority)
                .ThenByDescending(c => c.IsTodaysRelease)
                .ThenByDescending(c => c.Year ?? 0)
                .ToList()
            : candidates
                .OrderBy(c => c.Priority)
                .ThenByDescending(c => c.IsTodaysRelease)
                .ThenBy(c => c.Year ?? 0)
                .ToList();

        if (candidatesList.Count == 0)
        {
            _logger.LogTrace("SearchExecutor: no candidates for {Name}", instanceName);
            return result;
        }

        _logger.LogInformation("SearchExecutor: {Count} candidates for {Name}, limit={Limit}, delay={Delay}s",
            candidatesList.Count, instanceName, searchLimit, searchLoopDelay);

        // §1.2: UseTempForMissing — switch quality profiles before searching
        if (arrConfig.Search.UseTempForMissing && arrConfig.Search.QualityProfileMappings.Count > 0)
        {
            try
            {
                await _profileSwitcher.SwitchToTempProfilesAsync(instanceName, arrConfig, candidatesList, cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "SearchExecutor: profile switch failed for {Name}, continuing anyway", instanceName);
            }
        }

        // §2.11: SearchBySeries — pre-count episodes per series for "smart" mode
        var searchBySeries = (arrConfig.Search.SearchBySeries ?? "smart").Trim('"').ToLowerInvariant();
        var seriesEpisodeCount = candidatesList
            .Where(c => c.SeriesId.HasValue)
            .GroupBy(c => c.SeriesId!.Value)
            .ToDictionary(g => g.Key, g => g.Count());
        // Track which series have already had a SeriesSearch triggered this pass
        var searchedSeriesIds = new HashSet<int>();

        var firstSearch = true;

        foreach (var candidate in candidatesList)
        {
            if (cancellationToken.IsCancellationRequested)
                break;

            // §2.11: For Sonarr, if we already triggered a SeriesSearch for this series, skip episode-level
            if (candidate.SeriesId.HasValue && searchedSeriesIds.Contains(candidate.SeriesId.Value))
            {
                // Mark all episodes from this series as searched (series search covers them)
                await MarkAsSearchedAsync(arrConfig, candidate, cancellationToken);
                result.SearchedIds.Add(candidate.ArrId);
                continue;
            }

            var activeCommands = await GetActiveCommandCountAsync(instanceName, cancellationToken);
            if (!CanSearch(activeCommands, searchLimit))
            {
                _logger.LogTrace("SearchExecutor: command limit reached ({Active}/{Limit}), pausing searches",
                    activeCommands, searchLimit);
                break;
            }

            if (!firstSearch)
            {
                await Task.Delay(TimeSpan.FromSeconds(searchLoopDelay), cancellationToken);
            }
            firstSearch = false;

            try
            {
                // §2.11: Determine whether to use SeriesSearch or EpisodeSearch for Sonarr
                var useSeriesSearch = false;
                if (arrConfig.Type.Equals("sonarr", StringComparison.OrdinalIgnoreCase) && candidate.SeriesId.HasValue)
                {
                    var count = seriesEpisodeCount.GetValueOrDefault(candidate.SeriesId.Value, 1);
                    useSeriesSearch = searchBySeries == "true" ||
                                      (searchBySeries == "smart" && count > 1);
                }

                // Log candidate being searched (qBitrr format) - BEFORE attempting trigger
                var reasonText = string.IsNullOrEmpty(candidate.Reason) ? "" : $"[{candidate.Reason?.Trim('"').Trim()}]";
                var typeText = (candidate.Type ?? "").Trim('"').Trim();

                switch (typeText.ToLowerInvariant())
                {
                    case "movie":
                        _logger.LogInformation(
                            "Searching for: {Title:l} ({Year}) [id={ArrId}|movie]{Reason:l}",
                            candidate.Title, candidate.Year ?? 0, candidate.ArrId, reasonText);
                        break;
                    case "episode":
                        if (useSeriesSearch)
                            _logger.LogInformation(
                                "Searching for: {Title:l} [id={SeriesId}|series]{Reason:l}",
                                candidate.Title, candidate.SeriesId, reasonText);
                        else
                            _logger.LogInformation(
                                "Searching for: {Title:l} | S{SeasonNumber:E2}E{EpisodeNumber:E3} | [id={ArrId}|episode]{Reason:l}",
                                candidate.Title, candidate.SeasonNumber ?? 0, candidate.EpisodeNumber ?? 0, candidate.ArrId, reasonText);
                        break;
                    case "album":
                        _logger.LogInformation(
                            "Searching for: {ArtistName:l} [id={AlbumId}|album]{Reason:l}",
                            candidate.Title, candidate.AlbumId ?? 0, reasonText);
                        break;
                    default:
                        _logger.LogInformation("Searching for: {Title:l} [id={ArrId}|{Type:l}]{Reason:l}",
                            candidate.Title, candidate.ArrId, typeText, reasonText);
                        break;
                }

                var triggered = await TriggerSearchForCandidateAsync(arrConfig, candidate, useSeriesSearch, cancellationToken);
                if (triggered)
                {
                    result.SearchesTriggered++;
                    result.SearchedIds.Add(candidate.ArrId);
                    await MarkAsSearchedAsync(arrConfig, candidate, cancellationToken);
                    if (useSeriesSearch && candidate.SeriesId.HasValue)
                        searchedSeriesIds.Add(candidate.SeriesId.Value);
                }
                else
                {
                    _logger.LogTrace("SearchExecutor: search not triggered for {Title}", candidate.Title);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "SearchExecutor: error searching {Title}", candidate.Title);
                result.Errors.Add($"Failed to search {candidate.Title}: {ex.Message}");
            }
        }

        return result;
    }

    public async Task<int> GetActiveCommandCountAsync(string instanceName, CancellationToken cancellationToken = default)
    {
        if (!_config.ArrInstances.TryGetValue(instanceName, out var arrConfig))
            return 0;

        try
        {
            List<CommandStatus> commands = arrConfig.Type.ToLowerInvariant() switch
            {
                "radarr" => await new RadarrClient(arrConfig.URI, arrConfig.APIKey).GetCommandsAsync(cancellationToken),
                "sonarr" => await new SonarrClient(arrConfig.URI, arrConfig.APIKey).GetCommandsAsync(cancellationToken),
                "lidarr" => await new LidarrClient(arrConfig.URI, arrConfig.APIKey).GetCommandsAsync(cancellationToken),
                _ => new List<CommandStatus>()
            };

            var activeSearchCommands = commands.Count(c =>
                ActiveCommandStatuses.Contains(c.Status) &&
                SearchCommandNames.Contains(c.Name));

            return activeSearchCommands;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "SearchExecutor: failed to get commands for {Name}", instanceName);
            return 0;
        }
    }

    public bool CanSearch(int activeCommandCount, int searchLimit)
    {
        return activeCommandCount < searchLimit;
    }

    private async Task<bool> TriggerSearchForCandidateAsync(
        ArrInstanceConfig arrConfig,
        SearchCandidate candidate,
        bool useSeriesSearch,
        CancellationToken cancellationToken)
    {
        try
        {
            switch (arrConfig.Type.ToLowerInvariant())
            {
                case "radarr":
                    var radarrClient = new RadarrClient(arrConfig.URI, arrConfig.APIKey);
                    return await radarrClient.SearchMovieAsync(candidate.ArrId, cancellationToken);

                case "sonarr":
                    var sonarrClient = new SonarrClient(arrConfig.URI, arrConfig.APIKey);
                    // §2.11: SearchBySeries — use SeriesSearch when useSeriesSearch=true and SeriesId is available
                    if (useSeriesSearch && candidate.SeriesId.HasValue)
                        return await sonarrClient.SearchSeriesAsync(candidate.SeriesId.Value, cancellationToken);
                    return await sonarrClient.SearchEpisodeAsync(new List<int> { candidate.ArrId }, cancellationToken);

                case "lidarr":
                    var lidarrClient = new LidarrClient(arrConfig.URI, arrConfig.APIKey);
                    return await lidarrClient.SearchAlbumAsync(new List<int> { candidate.ArrId }, cancellationToken);

                default:
                    return false;
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "SearchExecutor: error triggering search for {Title}: {Message}", candidate.Title, ex.Message);
            return false;
        }
    }

    private async Task MarkAsSearchedAsync(
        ArrInstanceConfig arrConfig,
        SearchCandidate candidate,
        CancellationToken cancellationToken)
    {
        try
        {
            switch (arrConfig.Type.ToLowerInvariant())
            {
                case "radarr":
                    var movie = await _db.Movies
                        .FirstOrDefaultAsync(m => m.ArrId == candidate.ArrId, cancellationToken);
                    if (movie != null)
                    {
                        movie.Searched = true;
                        await _db.SaveChangesAsync(cancellationToken);
                    }
                    break;

                case "sonarr":
                    var episode = await _db.Episodes
                        .FirstOrDefaultAsync(e => e.ArrId == candidate.ArrId, cancellationToken);
                    if (episode != null)
                    {
                        episode.Searched = true;
                        await _db.SaveChangesAsync(cancellationToken);
                    }
                    break;

                case "lidarr":
                    var album = await _db.Albums
                        .FirstOrDefaultAsync(a => a.ArrId == candidate.ArrId, cancellationToken);
                    if (album != null)
                    {
                        album.Searched = true;
                        await _db.SaveChangesAsync(cancellationToken);
                    }
                    break;
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "SearchExecutor: failed to mark {Title} as searched", candidate.Title);
        }
    }
}

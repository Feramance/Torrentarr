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
        TorrentarrDbContext db)
    {
        _logger = logger;
        _config = config;
        _db = db;
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

        var candidatesList = candidates
            .OrderBy(c => c.Priority)
            .ThenByDescending(c => c.IsTodaysRelease)
            .ThenByDescending(c => c.Year ?? 0)
            .ToList();

        if (candidatesList.Count == 0)
        {
            _logger.LogDebug("SearchExecutor: no candidates for {Name}", instanceName);
            return result;
        }

        _logger.LogInformation("SearchExecutor: {Count} candidates for {Name}, limit={Limit}, delay={Delay}s",
            candidatesList.Count, instanceName, searchLimit, searchLoopDelay);

        var firstSearch = true;

        foreach (var candidate in candidatesList)
        {
            if (cancellationToken.IsCancellationRequested)
                break;

            var activeCommands = await GetActiveCommandCountAsync(instanceName, cancellationToken);
            if (!CanSearch(activeCommands, searchLimit))
            {
                _logger.LogDebug("SearchExecutor: command limit reached ({Active}/{Limit}), pausing searches",
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
                var triggered = await TriggerSearchForCandidateAsync(arrConfig, candidate, cancellationToken);
                if (triggered)
                {
                    result.SearchesTriggered++;
                    result.SearchedIds.Add(candidate.ArrId);
                    _logger.LogInformation("SearchExecutor: searched {Title} (Reason: {Reason})",
                        candidate.Title, candidate.Reason);

                    await MarkAsSearchedAsync(arrConfig, candidate, cancellationToken);
                }
                else
                {
                    _logger.LogDebug("SearchExecutor: search not triggered for {Title}", candidate.Title);
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
            _logger.LogError(ex, "SearchExecutor: error triggering search for {Title}", candidate.Title);
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

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
    private readonly DatabaseRestartCoordinator _restartCoordinator;
    private readonly IOperationalIssueReporter? _issues;

    // Cached per-instance Arr clients — created once, reused across calls
    private readonly Dictionary<string, object> _clientCache = new(StringComparer.OrdinalIgnoreCase);

    private static readonly HashSet<string> ActiveCommandStatuses = new(StringComparer.OrdinalIgnoreCase)
    {
        "queued", "started", "running"
    };

    private static readonly HashSet<string> SearchCommandNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "MoviesSearch", "MovieSearch", "EpisodeSearch", "SeasonSearch", "SeriesSearch",
        "AlbumSearch", "ArtistSearch", "BookSearch", "AuthorSearch", "MissingMoviesSearch", "MissingEpisodesSearch",
        "CutoffUnmetMoviesSearch", "CutoffUnmetEpisodesSearch"
    };

    public SearchExecutor(
        ILogger<SearchExecutor> logger,
        TorrentarrConfig config,
        TorrentarrDbContext db,
        QualityProfileSwitcherService profileSwitcher,
        DatabaseRestartCoordinator restartCoordinator, IOperationalIssueReporter? issues = null)
    {
        _logger = logger;
        _config = config;
        _db = db;
        _profileSwitcher = profileSwitcher;
        _restartCoordinator = restartCoordinator;
        _issues = issues;
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
            result.LoopCompleted = true;
            return result;
        }

        _logger.LogInformation("SearchExecutor: {Count} candidates for {Name}, limit={Limit}, delay={Delay}s",
            candidatesList.Count, instanceName, searchLimit, searchLoopDelay);

        var isSonarr = arrConfig.Type.Equals("sonarr", StringComparison.OrdinalIgnoreCase);
        var plans = candidatesList.Select(c => new SonarrSearchPlan(c, "", [c], 1, 1)).ToList();
        if (isSonarr)
        {
            var inventory = await _db.Episodes.AsNoTracking().Where(e => e.ArrInstance == instanceName).ToListAsync(cancellationToken);
            candidatesList = await ResolveSonarrRetriesAsync(instanceName, arrConfig, candidatesList, inventory, result, cancellationToken);
            plans = SonarrSearchScope.Plan(candidatesList, inventory,
                await _db.Series.AsNoTracking().Where(e => e.ArrInstance == instanceName).ToListAsync(cancellationToken),
                arrConfig.Search, DateTime.UtcNow);
            plans = await RestrictSonarrSeriesSearchesAsync(instanceName, arrConfig, plans, inventory, cancellationToken);
        }

        var firstSearch = true;
        var drained = result.Errors.Count == 0;
        var submissionStopped = false;

        foreach (var plan in plans)
        {
            var candidate = plan.Trigger;
            if (cancellationToken.IsCancellationRequested)
            {
                submissionStopped = true;
                drained = false;
                break;
            }

            var activeCommands = await GetActiveCommandCountAsync(instanceName, cancellationToken);
            if (!CanSearch(activeCommands, searchLimit))
            {
                submissionStopped = true;
                _logger.LogTrace("SearchExecutor: command limit reached ({Active}/{Limit}), pausing searches",
                    activeCommands, searchLimit);
                drained = false;
                break;
            }

            if (!firstSearch)
            {
                await Task.Delay(TimeSpan.FromSeconds(searchLoopDelay), cancellationToken);
            }
            firstSearch = false;

            try
            {
                // Recheck capacity after sleeping, before changing any external profile.
                if (!CanSearch(await GetActiveCommandCountAsync(instanceName, cancellationToken), searchLimit))
                { drained = false; submissionStopped = true; break; }
                if (!await _profileSwitcher.SwitchToTempProfilesAsync(instanceName, arrConfig, plan.Covered, cancellationToken))
                {
                    drained = false; submissionStopped = true;
                    result.Errors.Add($"Search skipped for {candidate.Title}: temporary profile preparation failed.");
                    continue;
                }
                var useSeriesSearch = plan.Command == "SeriesSearch";
                if (isSonarr)
                    _logger.LogInformation("Sonarr {Command}: series={SeriesId}, season={Season}, eligible={Eligible}/{Total}",
                        plan.Command, candidate.SeriesId, candidate.SeasonNumber, plan.EligibleCount, plan.TotalCount);

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
                                "Searching for: {Title:l} | S{SeasonNumber:D2}E{EpisodeNumber:D3} | [id={ArrId}|episode]{Reason:l}",
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

                var triggered = isSonarr
                    ? await TriggerSonarrSearchAsync(instanceName, arrConfig, plan, cancellationToken)
                    : await TriggerSearchForCandidateAsync(instanceName, arrConfig, candidate, false, cancellationToken);
                if (triggered)
                {
                    if (_issues != null) await _issues.ResolveAsync(SearchIssue(instanceName, "submission", candidate.ArrId.ToString()), cancellationToken);
                    result.SearchesTriggered++;
                    foreach (var covered in plan.Covered)
                    {
                        if (covered.ArrId <= 0) continue;
                        await MarkAsSearchedAsync(instanceName, arrConfig, covered, cancellationToken);
                        result.SearchedIds.Add(covered.ArrId);
                    }
                }
                else
                {
                    submissionStopped = true;
                    drained = false;
                    result.Errors.Add("Arr rejected a search submission.");
                    if (_issues != null) await _issues.ReportAsync(SearchIssue(instanceName, "submission", candidate.ArrId.ToString()), cancellationToken);
                    _logger.LogTrace("SearchExecutor: search not triggered for {Title}", candidate.Title);
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch (Exception ex)
            {
                submissionStopped = true;
                drained = false;
                _logger.LogError(ex, "SearchExecutor: error searching {Title}", candidate.Title);
                result.Errors.Add($"Failed to search {candidate.Title}: {ex.Message}");
            }
        }

        result.ItemsSearched = result.SearchedIds.Count;
        result.LoopCompleted = drained;
        result.OnlyRetryResolutionFailed = !drained && !submissionStopped;
        return result;
    }

    private async Task<List<SearchCandidate>> ResolveSonarrRetriesAsync(string instanceName, ArrInstanceConfig config,
        List<SearchCandidate> requested, List<EpisodeFilesModel> inventory, SearchResult searchResult, CancellationToken ct)
    {
        var knownIds = inventory.Select(e => e.ArrId).ToHashSet();
        var result = requested.Where(c => c.Reason != "Retry" || knownIds.Contains(c.ArrId)).ToList();
        foreach (var group in requested.Where(c => c.Reason == "Retry" && !knownIds.Contains(c.ArrId)).GroupBy(c => c.SeriesId))
        {
            if (group.Key is not > 0)
            {
                _logger.LogWarning("Skipping Sonarr retry without episode/series metadata; cannot verify specials permission");
                searchResult.Errors.Add("Sonarr replacement metadata is incomplete; the retry is retained.");
                continue;
            }
            try
            {
                var episodes = await ((SonarrClient)GetOrCreateClient(instanceName, config)).GetEpisodesAsync(group.Key.Value, ct);
                var retryIds = group.Select(c => c.ArrId).ToHashSet();
                foreach (var episode in episodes.Where(e => e.Id > 0))
                {
                    if (knownIds.Add(episode.Id))
                        inventory.Add(new EpisodeFilesModel
                        {
                            ArrId = episode.Id,
                            ArrInstance = instanceName,
                            ArrSeriesId = group.Key.Value,
                            SeasonNumber = episode.SeasonNumber,
                            EpisodeNumber = episode.EpisodeNumber,
                            Monitored = episode.Monitored,
                            AirDateUtc = episode.AirDateUtc,
                            HasFile = episode.HasFile,
                            Searched = true,
                            Upgrade = true
                        });
                    if (retryIds.Contains(0) || retryIds.Contains(episode.Id))
                        result.Add(new SearchCandidate
                        {
                            ArrId = episode.Id,
                            SeriesId = group.Key,
                            SeasonNumber = episode.SeasonNumber,
                            Type = "Episode",
                            Reason = "Retry"
                        });
                }
                if (!retryIds.Contains(0) && retryIds.Any(id => !knownIds.Contains(id)))
                {
                    searchResult.Errors.Add($"Unresolved Sonarr retry IDs for series {group.Key}");
                    _logger.LogWarning("Skipping unresolved Sonarr retry IDs for series {SeriesId}", group.Key);
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex)
            {
                searchResult.Errors.Add($"Cannot resolve Sonarr retry metadata for series {group.Key}: {ex.Message}");
                _logger.LogWarning(ex, "Cannot resolve Sonarr retry metadata for series {SeriesId}; leaving retry unsubmitted", group.Key);
            }
        }
        var resolved = result.DistinctBy(c => c.ArrId).ToList();
        searchResult.RetryCandidates = resolved.Where(c => c.Reason == "Retry")
            .Concat(requested.Where(c => c.Reason == "Retry" &&
                !resolved.Any(r => r.ArrId == c.ArrId || (c.ArrId == 0 && r.SeriesId == c.SeriesId))))
            .DistinctBy(c => c.ArrId).ToList();
        return resolved;
    }

    internal virtual Task<List<SonarrSeries>> GetSonarrSeriesAsync(string instanceName, ArrInstanceConfig config, CancellationToken ct) =>
        ((SonarrClient)GetOrCreateClient(instanceName, config)).GetSeriesAsync(ct);

    private async Task<List<SonarrSearchPlan>> RestrictSonarrSeriesSearchesAsync(string instanceName, ArrInstanceConfig config,
        List<SonarrSearchPlan> plans, List<EpisodeFilesModel> inventory, CancellationToken ct)
    {
        var allowedSpecials = SonarrSearchScope.SpecialSeries(inventory, config.Search);
        var metadataBySeries = new Dictionary<int, SonarrSeries>();
        if (plans.Any(p => p.Command == "SeriesSearch"))
        {
            try
            {
                metadataBySeries = (await GetSonarrSeriesAsync(instanceName, config, ct))
                    .DistinctBy(s => s.Id).ToDictionary(s => s.Id);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex) { _logger.LogWarning(ex, "Cannot verify Sonarr series scope; using explicit episodes"); }
        }
        var result = new List<SonarrSearchPlan>();
        foreach (var plan in plans)
        {
            if (plan.Command != "SeriesSearch")
            {
                result.Add(plan);
                continue;
            }
            var metadata = metadataBySeries.GetValueOrDefault(plan.Trigger.SeriesId!.Value);
            var specialsAllowed = allowedSpecials.Contains(plan.Trigger.SeriesId!.Value);
            if (metadata?.Seasons is not { } seasons)
            {
                result.AddRange(plan.Covered.Select(c => new SonarrSearchPlan(c, "EpisodeSearch", [c], 1, 1)));
                continue;
            }
            if (((specialsAllowed && config.Search.Unmonitored) || !seasons.Any(s => s.SeasonNumber == 0 && s.Monitored)) &&
                plan.Covered.All(c => seasons.Any(s => s.Monitored && s.SeasonNumber == c.SeasonNumber)))
            {
                result.Add(plan);
                continue;
            }

            // SeriesSearch has no exclude-specials parameter, so restrict its scope with season commands.
            // Keep only seasons with covered episodes so partial retries can advance past completed seasons.
            var seasonNumbers = plan.Covered.Where(c => c.SeasonNumber.HasValue).Select(c => c.SeasonNumber!.Value);
            foreach (var season in seasonNumbers.Distinct().Where(n => n != 0 || (specialsAllowed && config.Search.Unmonitored)))
            {
                var covered = plan.Covered.Where(c => c.SeasonNumber == season).ToList();
                var trigger = new SearchCandidate
                {
                    SeriesId = plan.Trigger.SeriesId,
                    SeasonNumber = season,
                    Type = "Episode",
                    Title = plan.Trigger.Title
                };
                result.Add(new SonarrSearchPlan(trigger, "SeasonSearch", covered, covered.Count,
                    inventory.Count(e => e.ArrSeriesId == trigger.SeriesId && e.SeasonNumber == season && e.Monitored == true && e.AirDateUtc <= DateTime.UtcNow)));
            }
            result.AddRange(plan.Covered.Where(c => !c.SeasonNumber.HasValue ||
                (c.SeasonNumber == 0 && specialsAllowed && !config.Search.Unmonitored))
                .Select(c => new SonarrSearchPlan(c, "EpisodeSearch", [c], 1, 1)));
        }
        return result;
    }

    internal virtual async Task<bool> TriggerSonarrSearchAsync(string instanceName, ArrInstanceConfig config,
        SonarrSearchPlan plan, CancellationToken ct)
    {
        if (plan.Command == "SeasonSearch")
            return await ((SonarrClient)GetOrCreateClient(instanceName, config))
                .SearchSeasonAsync(plan.Trigger.SeriesId!.Value, plan.Trigger.SeasonNumber!.Value, ct);
        if (plan.Command == "EpisodeSearch" && plan.Covered.Count > 1)
            return await ((SonarrClient)GetOrCreateClient(instanceName, config))
                .SearchEpisodeAsync(plan.Covered.Select(c => c.ArrId).ToList(), ct);
        return await TriggerSearchForCandidateAsync(instanceName, config, plan.Trigger, plan.Command == "SeriesSearch", ct);
    }

    protected virtual object GetOrCreateClient(string instanceName, ArrInstanceConfig arrConfig)
    {
        if (_clientCache.TryGetValue(instanceName, out var cached))
            return cached;
        object client = arrConfig.Type.ToLowerInvariant() switch
        {
            "radarr" => new RadarrClient(arrConfig.URI, arrConfig.APIKey, arrConfig.SkipTLSVerify),
            "sonarr" => new SonarrClient(arrConfig.URI, arrConfig.APIKey, arrConfig.SkipTLSVerify),
            "lidarr" => new LidarrClient(arrConfig.URI, arrConfig.APIKey, arrConfig.SkipTLSVerify),
            "readarr" => new ReadarrClient(arrConfig.URI, arrConfig.APIKey, arrConfig.SkipTLSVerify),
            _ => new object()
        };
        _clientCache[instanceName] = client;
        return client;
    }

    public virtual async Task<int> GetActiveCommandCountAsync(string instanceName, CancellationToken cancellationToken = default)
    {
        if (!_config.ArrInstances.TryGetValue(instanceName, out var arrConfig))
            return 0;

        try
        {
            var rawClient = GetOrCreateClient(instanceName, arrConfig);
            List<CommandStatus> commands = rawClient switch
            {
                RadarrClient r => await r.GetCommandsAsync(cancellationToken),
                SonarrClient s => await s.GetCommandsAsync(cancellationToken),
                LidarrClient l => await l.GetCommandsAsync(cancellationToken),
                ReadarrClient b => await b.GetCommandsAsync(cancellationToken),
                _ => new List<CommandStatus>()
            };

            var activeSearchCommands = commands.Count(c =>
                ActiveCommandStatuses.Contains(c.Status) &&
                SearchCommandNames.Contains(c.Name));

            if (_issues != null) await _issues.ResolveAsync(SearchIssue(instanceName, "capacity"), cancellationToken);
            return activeSearchCommands;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "SearchExecutor: failed to get commands for {Name}", instanceName);
            if (_issues != null) await _issues.ReportAsync(SearchIssue(instanceName, "capacity"), cancellationToken);
            throw;
        }
    }

    private static OperationalIssueReport SearchIssue(string instance, string operation, string id = "") => new(
        "search." + operation, "automation", operation == "capacity" ? "Search capacity could not be verified" : "Arr rejected a search",
        "Search submission is held back; affected candidates remain unsearched.", "Check Arr connectivity, command access and Logs before retrying.",
        ArrInstance: instance, SubjectType: "search", SubjectId: id);

    public bool CanSearch(int activeCommandCount, int searchLimit)
    {
        return activeCommandCount < searchLimit;
    }

    protected virtual async Task<bool> TriggerSearchForCandidateAsync(
        string instanceName,
        ArrInstanceConfig arrConfig,
        SearchCandidate candidate,
        bool useSeriesSearch,
        CancellationToken cancellationToken)
    {
        try
        {
            var rawClient = GetOrCreateClient(instanceName, arrConfig);

            switch (rawClient)
            {
                case RadarrClient radarrClient:
                    return await radarrClient.SearchMovieAsync(candidate.ArrId, cancellationToken);

                case SonarrClient sonarrClient:
                    // §2.11: SearchBySeries — use SeriesSearch when useSeriesSearch=true and SeriesId is available
                    if (useSeriesSearch && candidate.SeriesId.HasValue)
                        return await sonarrClient.SearchSeriesAsync(candidate.SeriesId.Value, cancellationToken);
                    return await sonarrClient.SearchEpisodeAsync(new List<int> { candidate.ArrId }, cancellationToken);

                case LidarrClient lidarrClient:
                    return await lidarrClient.SearchAlbumAsync(new List<int> { candidate.ArrId }, cancellationToken);

                case ReadarrClient readarrClient when candidate.Type.Equals("Author", StringComparison.OrdinalIgnoreCase):
                    return await readarrClient.SearchAuthorAsync(candidate.AuthorId ?? candidate.ArrId, cancellationToken);
                case ReadarrClient readarrClient:
                    return await readarrClient.SearchBookAsync(new List<int> { candidate.ArrId }, cancellationToken);

                default:
                    return false;
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            _logger.LogError(ex, "SearchExecutor: error triggering search for {Title}: {Message}", candidate.Title, ex.Message);
            return false;
        }
    }

    protected virtual async Task MarkAsSearchedAsync(
        string instanceName,
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
                        .FirstOrDefaultAsync(m => m.ArrId == candidate.ArrId && m.ArrInstance == instanceName, cancellationToken);
                    if (movie != null)
                    {
                        movie.Searched = true;
                        movie.Upgrade = true;
                        await _db.SaveChangesWithRetryAsync(_logger, _restartCoordinator, cancellationToken: cancellationToken);
                    }
                    break;

                case "sonarr":
                    var episode = await _db.Episodes
                        .FirstOrDefaultAsync(e => e.ArrId == candidate.ArrId && e.ArrInstance == instanceName, cancellationToken);
                    if (episode != null)
                    {
                        episode.Searched = true;
                        episode.Upgrade = true;
                        await _db.SaveChangesWithRetryAsync(_logger, _restartCoordinator, cancellationToken: cancellationToken);
                    }
                    break;

                case "lidarr":
                    var album = await _db.Albums
                        .FirstOrDefaultAsync(a => a.ArrId == candidate.ArrId && a.ArrInstance == instanceName, cancellationToken);
                    if (album != null)
                    {
                        album.Searched = true;
                        album.Upgrade = true;
                        await _db.SaveChangesWithRetryAsync(_logger, _restartCoordinator, cancellationToken: cancellationToken);
                    }
                    break;

                case "readarr" when candidate.Type.Equals("Author", StringComparison.OrdinalIgnoreCase):
                    break; // An author replacement must not mark a book with the same numeric ID searched.
                case "readarr":
                    var book = await _db.Books
                        .FirstOrDefaultAsync(b => b.ArrId == candidate.ArrId && b.ArrInstance == instanceName, cancellationToken);
                    if (book != null)
                    {
                        book.Searched = true;
                        book.Upgrade = true;
                        await _db.SaveChangesWithRetryAsync(_logger, _restartCoordinator, cancellationToken: cancellationToken);
                    }
                    break;
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "SearchExecutor: failed to mark {Title} as searched", candidate.Title);
        }
    }
}

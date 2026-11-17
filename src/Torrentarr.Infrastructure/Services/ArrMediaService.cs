using Torrentarr.Core.Configuration;
using Torrentarr.Core.Services;
using Torrentarr.Infrastructure.Database;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Torrentarr.Infrastructure.Services;

public class ArrMediaService : IArrMediaService
{
    private readonly ILogger<ArrMediaService> _logger;
    private readonly TorrentarrDbContext _dbContext;
    private readonly TorrentarrConfig _config;
    private readonly ISearchExecutor _searchExecutor;
    private readonly ArrSyncService _syncService;
    private readonly SearchYearCursor _yearCursor;

    private static readonly Dictionary<string, int> ReasonPriority = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Missing"] = 1,
        ["CustomFormat"] = 2,
        ["Quality"] = 3,
        ["Upgrade"] = 4,
        ["None"] = 99,
        ["NotAvailable"] = 99
    };

    public ArrMediaService(
        ILogger<ArrMediaService> logger,
        TorrentarrDbContext dbContext,
        TorrentarrConfig config,
        ISearchExecutor searchExecutor,
        ArrSyncService syncService,
        SearchYearCursor? yearCursor = null)
    {
        _logger = logger;
        _dbContext = dbContext;
        _config = config;
        _searchExecutor = searchExecutor;
        _syncService = syncService;
        _yearCursor = yearCursor ?? new SearchYearCursor();
    }

    public Task<SearchResult> SearchMissingMediaAsync(string category, CancellationToken cancellationToken = default)
        => SearchMissingMediaAsync(category, null, cancellationToken);

    public async Task<SearchResult> SearchMissingMediaAsync(string category, DateTimeOffset? deadline, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (deadline is { } end && end - DateTimeOffset.UtcNow <= TimeSpan.FromMinutes(1))
            return new SearchResult { BudgetExhausted = true };
        _logger.LogInformation("Starting missing media search for category {Category}", category);

        var arrInstance = _config.ArrInstances.Values.FirstOrDefault(i =>
            i.Category.Equals(category, StringComparison.OrdinalIgnoreCase));

        if (arrInstance == null)
        {
            _logger.LogWarning("No Arr instance found for category {Category}", category);
            _logger.LogInformation("Search aborted - no Arr instance for {Category}", category);
            return new SearchResult();
        }

        var instanceName = _config.ArrInstances.First(kvp => kvp.Value == arrInstance).Key;
        _logger.LogInformation("Found Arr instance {Name} for category {Category}", instanceName, category);

        if (!arrInstance.Search.SearchMissing)
        {
            _logger.LogInformation("SearchMissing disabled for {Category}", category);
            _logger.LogInformation("Search skipped - SearchMissing disabled");
            return new SearchResult();
        }

        _logger.LogInformation("Searching for missing media in {Category}", category);

        _logger.LogTrace("Getting search candidates for {Name}", instanceName);
        var candidates = await GetSearchCandidatesAsync(instanceName, arrInstance, cancellationToken);
        candidates = await ApplySearchByYearAsync(instanceName, arrInstance, candidates, cancellationToken);
        _logger.LogInformation("Found {Count} search candidates", candidates.Count);

        _logger.LogTrace("Executing searches for {Count} candidates", candidates.Count);
        var result = await (deadline.HasValue
            ? _searchExecutor.ExecuteSearchesAsync(instanceName, candidates, deadline, cancellationToken)
            : _searchExecutor.ExecuteSearchesAsync(instanceName, candidates, cancellationToken));
        _logger.LogInformation("Search complete - triggered {Count} searches", result.SearchesTriggered);

        return result;
    }

    public Task<SearchResult> SearchQualityUpgradesAsync(string category, CancellationToken cancellationToken = default)
        => SearchQualityUpgradesAsync(category, null, cancellationToken);

    public async Task<SearchResult> SearchQualityUpgradesAsync(string category, DateTimeOffset? deadline, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (deadline is { } end && end - DateTimeOffset.UtcNow <= TimeSpan.FromMinutes(1))
            return new SearchResult { BudgetExhausted = true };
        _logger.LogInformation("Starting quality upgrade search for category {Category}", category);

        var arrInstance = _config.ArrInstances.Values.FirstOrDefault(i =>
            i.Category.Equals(category, StringComparison.OrdinalIgnoreCase));

        if (arrInstance == null)
        {
            _logger.LogInformation("No Arr instance found for category {Category}", category);
            _logger.LogInformation("Search aborted - no Arr instance for {Category}", category);
            return new SearchResult();
        }

        var instanceName = _config.ArrInstances.First(kvp => kvp.Value == arrInstance).Key;
        _logger.LogInformation("Found Arr instance {Name} for category {Category}", instanceName, category);

        if (!arrInstance.Search.DoUpgradeSearch && !arrInstance.Search.CustomFormatUnmetSearch && !arrInstance.Search.QualityUnmetSearch)
        {
            _logger.LogInformation("Quality upgrade search disabled for {Category}", category);
            _logger.LogInformation("Search skipped - all upgrade searches disabled");
            return new SearchResult();
        }

        _logger.LogInformation("Searching for quality upgrades in {Category}", category);

        var candidates = await GetUpgradeCandidatesAsync(instanceName, arrInstance, cancellationToken);
        candidates = await ApplySearchByYearAsync(instanceName, arrInstance, candidates, cancellationToken);

        return await (deadline.HasValue
            ? _searchExecutor.ExecuteSearchesAsync(instanceName, candidates, deadline, cancellationToken)
            : _searchExecutor.ExecuteSearchesAsync(instanceName, candidates, cancellationToken));
    }

    public async Task<List<WantedMedia>> GetWantedMediaAsync(string category, CancellationToken cancellationToken = default)
    {
        var wanted = new List<WantedMedia>();

        var arrInstance = _config.ArrInstances.Values.FirstOrDefault(i =>
            i.Category.Equals(category, StringComparison.OrdinalIgnoreCase));

        if (arrInstance == null)
            return wanted;

        var instanceName = _config.ArrInstances.First(kvp => kvp.Value == arrInstance).Key;

        try
        {
            switch (arrInstance.Type.ToLowerInvariant())
            {
                case "radarr":
                    var movies = await _dbContext.Movies
                        .Where(m => m.ArrInstance == instanceName && m.Monitored && !m.HasFile && !m.Searched)
                        .ToListAsync(cancellationToken);
                    wanted.AddRange(movies.Select(m => new WantedMedia
                    {
                        Id = m.ArrId,
                        ArrId = m.ArrId,
                        Title = m.Title,
                        Year = m.Year,
                        Monitored = m.Monitored
                    }));
                    break;

                case "sonarr":
                    var episodes = await _dbContext.Episodes
                        .Where(e => e.ArrInstance == instanceName && e.Monitored == true && !e.HasFile && !e.Searched)
                        .ToListAsync(cancellationToken);
                    wanted.AddRange(episodes.Select(e => new WantedMedia
                    {
                        Id = e.ArrId,
                        ArrId = e.ArrId,
                        Title = $"{e.SeriesTitle} S{e.SeasonNumber:00}E{e.EpisodeNumber:00}",
                        SeriesId = e.ArrSeriesId,
                        SeasonNumber = e.SeasonNumber,
                        EpisodeNumber = e.EpisodeNumber,
                        Monitored = e.Monitored ?? false
                    }));
                    break;

                case "lidarr":
                    var albums = await _dbContext.Albums
                        .Where(a => a.ArrInstance == instanceName && a.Monitored && !a.HasFile && !a.Searched)
                        .ToListAsync(cancellationToken);
                    wanted.AddRange(albums.Select(a => new WantedMedia
                    {
                        Id = a.ArrId,
                        ArrId = a.ArrId,
                        Title = $"{a.ArtistTitle} - {a.Title}",
                        ArtistId = a.ArrArtistId,
                        Monitored = a.Monitored
                    }));
                    break;

                case "readarr":
                    var books = await _dbContext.Books
                        .Where(b => b.ArrInstance == instanceName && b.Monitored && !b.HasFile && !b.Searched)
                        .ToListAsync(cancellationToken);
                    wanted.AddRange(books.Select(b => new WantedMedia
                    {
                        Id = b.ArrId,
                        ArrId = b.ArrId,
                        Title = $"{b.AuthorTitle} - {b.Title}",
                        AuthorId = b.ArrAuthorId,
                        Year = b.ReleaseDate?.Year ?? 0,
                        Monitored = b.Monitored
                    }));
                    break;
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting wanted media for category {Category}", category);
        }

        return wanted;
    }

    public async Task<QualityUpgradeResult> GetCustomFormatUnmetMediaAsync(string category, CancellationToken cancellationToken = default)
    {
        var result = new QualityUpgradeResult();

        var arrInstance = _config.ArrInstances.Values.FirstOrDefault(i =>
            i.Category.Equals(category, StringComparison.OrdinalIgnoreCase));

        if (arrInstance == null)
            return result;

        var instanceName = _config.ArrInstances.First(kvp => kvp.Value == arrInstance).Key;

        try
        {
            switch (arrInstance.Type.ToLowerInvariant())
            {
                case "radarr":
                    var movies = await _dbContext.Movies
                        .Where(m => m.ArrInstance == instanceName && m.Monitored && m.HasFile && !m.CustomFormatMet)
                        .ToListAsync(cancellationToken);
                    foreach (var movie in movies)
                    {
                        result.UnmetMedia.Add(new CustomFormatUnmetItem
                        {
                            Id = movie.ArrId,
                            Title = movie.Title,
                            Type = "Movie",
                            CurrentCustomFormatScore = movie.CustomFormatScore ?? 0,
                            MinCustomFormatScore = movie.MinCustomFormatScore ?? 0,
                            QualityProfileId = movie.QualityProfileId ?? 0,
                            QualityProfileName = movie.QualityProfileName ?? ""
                        });
                    }
                    break;

                case "sonarr":
                    var episodes = await _dbContext.Episodes
                        .Where(e => e.ArrInstance == instanceName && e.Monitored == true && e.HasFile && !e.CustomFormatMet)
                        .ToListAsync(cancellationToken);
                    foreach (var ep in episodes)
                    {
                        result.UnmetMedia.Add(new CustomFormatUnmetItem
                        {
                            Id = ep.ArrId,
                            Title = $"{ep.SeriesTitle} S{ep.SeasonNumber:00}E{ep.EpisodeNumber:00}",
                            Type = "Episode",
                            CurrentCustomFormatScore = ep.CustomFormatScore ?? 0,
                            MinCustomFormatScore = ep.MinCustomFormatScore ?? 0,
                            QualityProfileId = ep.QualityProfileId ?? 0,
                            QualityProfileName = ep.QualityProfileName ?? "",
                            SeasonNumber = ep.SeasonNumber,
                            EpisodeNumber = ep.EpisodeNumber
                        });
                    }
                    break;

                case "lidarr":
                    var albums = await _dbContext.Albums
                        .Where(a => a.ArrInstance == instanceName && a.Monitored && a.HasFile && !a.CustomFormatMet)
                        .ToListAsync(cancellationToken);
                    foreach (var album in albums)
                    {
                        result.UnmetMedia.Add(new CustomFormatUnmetItem
                        {
                            Id = album.ArrId,
                            Title = $"{album.ArtistTitle} - {album.Title}",
                            Type = "Album",
                            CurrentCustomFormatScore = album.CustomFormatScore ?? 0,
                            MinCustomFormatScore = album.MinCustomFormatScore ?? 0,
                            QualityProfileId = album.QualityProfileId ?? 0,
                            QualityProfileName = album.QualityProfileName ?? "",
                            ArtistId = album.ArrArtistId
                        });
                    }
                    break;

                case "readarr":
                    var books = await _dbContext.Books
                        .Where(b => b.ArrInstance == instanceName && b.Monitored && b.HasFile && !b.CustomFormatMet)
                        .ToListAsync(cancellationToken);
                    foreach (var book in books)
                    {
                        result.UnmetMedia.Add(new CustomFormatUnmetItem
                        {
                            Id = book.ArrId,
                            Title = $"{book.AuthorTitle} - {book.Title}",
                            Type = "Book",
                            CurrentCustomFormatScore = book.CustomFormatScore ?? 0,
                            MinCustomFormatScore = book.MinCustomFormatScore ?? 0,
                            QualityProfileId = book.QualityProfileId ?? 0,
                            QualityProfileName = book.QualityProfileName ?? "",
                            AuthorId = book.ArrAuthorId
                        });
                    }
                    break;
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting CF unmet media for category {Category}", category);
        }

        return result;
    }

    private async Task<List<SearchCandidate>> GetSearchCandidatesAsync(
        string instanceName,
        ArrInstanceConfig arrConfig,
        CancellationToken cancellationToken)
    {
        var candidates = new List<SearchCandidate>();
        var searchConfig = arrConfig.Search;

        try
        {
            switch (arrConfig.Type.ToLowerInvariant())
            {
                case "radarr":
                    // §2.10: When Unmonitored=true, include unmonitored items
                    var movies = await _dbContext.Movies.AsNoTracking()
                            .Where(m => m.ArrInstance == instanceName).ToListAsync(cancellationToken);

                    foreach (var movie in movies)
                    {
                        if ((!searchConfig.Unmonitored && movie.Monitored != true) || movie.Searched)
                        {
                            _logger.LogDebug("Search skipped for {Title} [id={ArrId}, instance={Instance}]: monitored={Monitored}, searched={Searched}", movie.Title, movie.ArrId, instanceName, movie.Monitored, movie.Searched);
                            continue;
                        }
                        var priority = GetReasonPriority(movie.Reason, searchConfig);
                        _logger.LogInformation("Search eligibility for {Title} [id={ArrId}, instance={Instance}]: eligible={Eligible}, reason={Reason}, missingSearch={Missing}, qualitySearch={Quality}, customFormatSearch={CustomFormat}, upgradeSearch={Upgrade}", movie.Title, movie.ArrId, instanceName, priority < 99, movie.Reason, searchConfig.SearchMissing, searchConfig.QualityUnmetSearch, searchConfig.CustomFormatUnmetSearch, searchConfig.DoUpgradeSearch);

                        if (priority >= 99) continue;

                        candidates.Add(new SearchCandidate
                        {
                            ArrId = movie.ArrId,
                            Title = movie.Title,
                            Type = "Movie",
                            Reason = movie.Reason ?? "Missing",
                            Priority = priority,
                            Year = movie.Year
                        });
                    }
                    break;

                case "sonarr":
                    var episodes = await _dbContext.Episodes.AsNoTracking()
                        .Where(e => e.ArrInstance == instanceName).ToListAsync(cancellationToken);
                    candidates.AddRange(SonarrSearchScope.Candidates(episodes, searchConfig, DateTime.UtcNow, logger: _logger));
                    break;

                case "lidarr":
                    // §2.10: When Unmonitored=true, include unmonitored items
                    var albums = await _dbContext.Albums.AsNoTracking()
                            .Where(m => m.ArrInstance == instanceName).ToListAsync(cancellationToken);

                    foreach (var album in albums)
                    {
                        if ((!searchConfig.Unmonitored && album.Monitored != true) || album.Searched)
                        {
                            _logger.LogDebug("Search skipped for {Title} [id={ArrId}, instance={Instance}]: monitored={Monitored}, searched={Searched}", album.Title, album.ArrId, instanceName, album.Monitored, album.Searched);
                            continue;
                        }
                        var priority = GetReasonPriority(album.Reason, searchConfig);
                        _logger.LogInformation("Search eligibility for {Title} [id={ArrId}, instance={Instance}]: eligible={Eligible}, reason={Reason}, missingSearch={Missing}, qualitySearch={Quality}, customFormatSearch={CustomFormat}, upgradeSearch={Upgrade}", album.Title, album.ArrId, instanceName, priority < 99, album.Reason, searchConfig.SearchMissing, searchConfig.QualityUnmetSearch, searchConfig.CustomFormatUnmetSearch, searchConfig.DoUpgradeSearch);
                        if (priority >= 99) continue;

                        candidates.Add(new SearchCandidate
                        {
                            ArrId = album.ArrId,
                            Title = $"{album.ArtistTitle} - {album.Title}",
                            Type = "Album",
                            Reason = album.Reason ?? "Missing",
                            Priority = priority,
                            ArtistId = album.ArrArtistId,
                            AlbumId = album.ArrId,
                            Year = album.ReleaseDate?.Year
                        });
                    }
                    break;

                case "readarr":
                    var books = await _dbContext.Books.AsNoTracking()
                            .Where(m => m.ArrInstance == instanceName).ToListAsync(cancellationToken);

                    foreach (var book in books)
                    {
                        if ((!searchConfig.Unmonitored && book.Monitored != true) || book.Searched)
                        {
                            _logger.LogDebug("Search skipped for {Title} [id={ArrId}, instance={Instance}]: monitored={Monitored}, searched={Searched}", book.Title, book.ArrId, instanceName, book.Monitored, book.Searched);
                            continue;
                        }
                        var priority = GetReasonPriority(book.Reason, searchConfig);
                        _logger.LogInformation("Search eligibility for {Title} [id={ArrId}, instance={Instance}]: eligible={Eligible}, reason={Reason}, missingSearch={Missing}, qualitySearch={Quality}, customFormatSearch={CustomFormat}, upgradeSearch={Upgrade}", book.Title, book.ArrId, instanceName, priority < 99, book.Reason, searchConfig.SearchMissing, searchConfig.QualityUnmetSearch, searchConfig.CustomFormatUnmetSearch, searchConfig.DoUpgradeSearch);
                        if (priority >= 99) continue;

                        candidates.Add(new SearchCandidate
                        {
                            ArrId = book.ArrId,
                            Title = $"{book.AuthorTitle} - {book.Title}",
                            Type = "Book",
                            Reason = book.Reason ?? "Missing",
                            Priority = priority,
                            AuthorId = book.ArrAuthorId,
                            BookId = book.ArrId,
                            Year = book.ReleaseDate?.Year
                        });
                    }
                    break;
            }

        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Search candidate loop failed for {Instance}", instanceName);
            throw;
        }

        return candidates;
    }

    private async Task<List<SearchCandidate>> GetUpgradeCandidatesAsync(
        string instanceName,
        ArrInstanceConfig arrConfig,
        CancellationToken cancellationToken)
    {
        var candidates = new List<SearchCandidate>();
        var searchConfig = arrConfig.Search;

        switch (arrConfig.Type.ToLowerInvariant())
        {
            case "radarr":
                var movies = await _dbContext.Movies.AsNoTracking()
                    .Where(m => m.ArrInstance == instanceName).ToListAsync(cancellationToken);

                foreach (var movie in movies)
                {
                    if (movie.Monitored != true || !movie.HasFile || (searchConfig.DoUpgradeSearch ? movie.Upgrade : movie.Searched))
                    {
                        _logger.LogDebug("Upgrade search skipped for {Title} [id={ArrId}, instance={Instance}]: monitored={Monitored}, hasFile={HasFile}, upgrade={Upgrade}, searched={Searched}", movie.Title, movie.ArrId, instanceName, movie.Monitored, movie.HasFile, movie.Upgrade, movie.Searched);
                        continue;
                    }
                    var priority = GetUpgradePriority(movie.QualityMet, movie.CustomFormatMet, searchConfig);
                    _logger.LogInformation("Search eligibility for {Title} [id={ArrId}, instance={Instance}]: eligible={Eligible}, reason={Reason}, missingSearch={Missing}, qualitySearch={Quality}, customFormatSearch={CustomFormat}, upgradeSearch={Upgrade}", movie.Title, movie.ArrId, instanceName, priority < 99, movie.Reason, searchConfig.SearchMissing, searchConfig.QualityUnmetSearch, searchConfig.CustomFormatUnmetSearch, searchConfig.DoUpgradeSearch);
                    if (priority >= 99) continue;

                    var reason = DetermineUpgradeReason(movie.QualityMet, movie.CustomFormatMet, searchConfig);

                    candidates.Add(new SearchCandidate
                    {
                        ArrId = movie.ArrId,
                        Title = movie.Title,
                        Type = "Movie",
                        Reason = reason,
                        Priority = priority,
                        Year = movie.Year
                    });
                }
                break;

            case "sonarr":
                var episodes = await _dbContext.Episodes.AsNoTracking()
                    .Where(e => e.ArrInstance == instanceName).ToListAsync(cancellationToken);
                candidates.AddRange(SonarrSearchScope.Candidates(episodes, searchConfig, DateTime.UtcNow, upgradesOnly: true, logger: _logger));
                break;

            case "lidarr":
                var albums = await _dbContext.Albums.AsNoTracking()
                    .Where(m => m.ArrInstance == instanceName).ToListAsync(cancellationToken);

                foreach (var album in albums)
                {
                    if (album.Monitored != true || !album.HasFile || (searchConfig.DoUpgradeSearch ? album.Upgrade : album.Searched))
                    {
                        _logger.LogDebug("Upgrade search skipped for {Title} [id={ArrId}, instance={Instance}]: monitored={Monitored}, hasFile={HasFile}, upgrade={Upgrade}, searched={Searched}", album.Title, album.ArrId, instanceName, album.Monitored, album.HasFile, album.Upgrade, album.Searched);
                        continue;
                    }
                    var priority = GetUpgradePriority(album.QualityMet, album.CustomFormatMet, searchConfig);
                    _logger.LogInformation("Search eligibility for {Title} [id={ArrId}, instance={Instance}]: eligible={Eligible}, reason={Reason}, missingSearch={Missing}, qualitySearch={Quality}, customFormatSearch={CustomFormat}, upgradeSearch={Upgrade}", album.Title, album.ArrId, instanceName, priority < 99, album.Reason, searchConfig.SearchMissing, searchConfig.QualityUnmetSearch, searchConfig.CustomFormatUnmetSearch, searchConfig.DoUpgradeSearch);
                    if (priority >= 99) continue;

                    var reason = DetermineUpgradeReason(album.QualityMet, album.CustomFormatMet, searchConfig);

                    candidates.Add(new SearchCandidate
                    {
                        ArrId = album.ArrId,
                        Title = $"{album.ArtistTitle} - {album.Title}",
                        Type = "Album",
                        Reason = reason,
                        Priority = priority,
                        ArtistId = album.ArrArtistId,
                        AlbumId = album.ArrId,
                        Year = album.ReleaseDate?.Year
                    });
                }
                break;

            case "readarr":
                var books = await _dbContext.Books.AsNoTracking()
                    .Where(m => m.ArrInstance == instanceName).ToListAsync(cancellationToken);

                foreach (var book in books)
                {
                    if (book.Monitored != true || !book.HasFile || (searchConfig.DoUpgradeSearch ? book.Upgrade : book.Searched))
                    {
                        _logger.LogDebug("Upgrade search skipped for {Title} [id={ArrId}, instance={Instance}]: monitored={Monitored}, hasFile={HasFile}, upgrade={Upgrade}, searched={Searched}", book.Title, book.ArrId, instanceName, book.Monitored, book.HasFile, book.Upgrade, book.Searched);
                        continue;
                    }
                    var priority = GetUpgradePriority(book.QualityMet, book.CustomFormatMet, searchConfig);
                    _logger.LogInformation("Search eligibility for {Title} [id={ArrId}, instance={Instance}]: eligible={Eligible}, reason={Reason}, missingSearch={Missing}, qualitySearch={Quality}, customFormatSearch={CustomFormat}, upgradeSearch={Upgrade}", book.Title, book.ArrId, instanceName, priority < 99, book.Reason, searchConfig.SearchMissing, searchConfig.QualityUnmetSearch, searchConfig.CustomFormatUnmetSearch, searchConfig.DoUpgradeSearch);
                    if (priority >= 99) continue;

                    var reason = DetermineUpgradeReason(book.QualityMet, book.CustomFormatMet, searchConfig);

                    candidates.Add(new SearchCandidate
                    {
                        ArrId = book.ArrId,
                        Title = $"{book.AuthorTitle} - {book.Title}",
                        Type = "Book",
                        Reason = reason,
                        Priority = priority,
                        AuthorId = book.ArrAuthorId,
                        BookId = book.ArrId,
                        Year = book.ReleaseDate?.Year
                    });
                }
                break;
        }

        return candidates;
    }

    private int GetReasonPriority(string? reason, SearchConfig searchConfig)
    {
        if (string.IsNullOrEmpty(reason))
            return ReasonPriority["Missing"];

        if (!ReasonPriority.TryGetValue(reason, out var priority))
            return 99;

        if (reason == "CustomFormat" && !searchConfig.CustomFormatUnmetSearch)
            return 99;

        if (reason == "Quality" && !searchConfig.QualityUnmetSearch)
            return 99;

        if (reason == "Upgrade" && !searchConfig.DoUpgradeSearch)
            return 99;

        return priority;
    }

    private int GetUpgradePriority(bool qualityMet, bool customFormatMet, SearchConfig searchConfig)
    {
        if (searchConfig.DoUpgradeSearch)
            return ReasonPriority["Upgrade"];

        if (!customFormatMet && searchConfig.CustomFormatUnmetSearch)
            return ReasonPriority["CustomFormat"];

        if (!qualityMet && searchConfig.QualityUnmetSearch)
            return ReasonPriority["Quality"];

        return 99;
    }

    private string DetermineUpgradeReason(bool qualityMet, bool customFormatMet, SearchConfig searchConfig)
    {
        if (searchConfig.DoUpgradeSearch)
            return "Upgrade";

        if (!customFormatMet && searchConfig.CustomFormatUnmetSearch)
            return "CustomFormat";

        if (!qualityMet && searchConfig.QualityUnmetSearch)
            return "Quality";

        return "None";
    }

    internal async Task<List<SearchCandidate>> ApplySearchByYearAsync(
        string instanceName,
        ArrInstanceConfig arrConfig,
        List<SearchCandidate> candidates,
        CancellationToken cancellationToken)
    {
        if (!SearchYearCursor.ShouldFilter(arrConfig) || candidates.Count == 0)
            return candidates;

        var years = await CollectYearsAsync(instanceName, arrConfig, cancellationToken);
        var currentYear = _yearCursor.CurrentYear(instanceName, arrConfig, years);
        if (currentYear is null)
        {
            _logger.LogInformation("SearchByYear enabled for {Instance} but no years available; skipping year filter", instanceName);
            return candidates;
        }

        _logger.LogInformation("SearchByYear: {Instance} current year {Year}", instanceName, currentYear);
        foreach (var candidate in candidates)
            _logger.LogInformation("Search year decision for {Title} [id={ArrId}, instance={Instance}]: year={Year}, current={CurrentYear}, todaysRelease={Today}, eligible={Eligible}",
                candidate.Title, candidate.ArrId, instanceName, candidate.Year, currentYear, candidate.IsTodaysRelease, candidate.IsTodaysRelease || candidate.Year == currentYear);
        return candidates
            .Where(c => c.IsTodaysRelease || c.Year == currentYear)
            .ToList();
    }

    internal async Task<List<int>> CollectYearsAsync(
        string instanceName,
        ArrInstanceConfig arrConfig,
        CancellationToken cancellationToken)
    {
        var nowYear = DateTime.UtcNow.Year;
        var years = new HashSet<int>();

        switch (arrConfig.Type.ToLowerInvariant())
        {
            case "radarr":
                var movieYears = await _dbContext.Movies.AsNoTracking()
                    .Where(m => m.ArrInstance == instanceName && m.Monitored && m.Year > 0 && m.Year <= nowYear)
                    .Select(m => m.Year)
                    .Distinct()
                    .ToListAsync(cancellationToken);
                years.UnionWith(movieYears);
                break;

            case "sonarr":
                var episodeQuery = _dbContext.Episodes.AsNoTracking()
                    .Where(e => e.ArrInstance == instanceName && e.Monitored == true && e.AirDateUtc != null);
                if (!arrConfig.Search.AlsoSearchSpecials)
                    episodeQuery = episodeQuery.Where(e => e.SeasonNumber != 0);
                var episodeYears = await episodeQuery
                    .Select(e => e.AirDateUtc!.Value.Year)
                    .Distinct()
                    .ToListAsync(cancellationToken);
                years.UnionWith(episodeYears.Where(y => y > 0 && y <= nowYear));
                break;

            case "readarr":
                var bookYears = await _dbContext.Books.AsNoTracking()
                    .Where(b => b.ArrInstance == instanceName && b.Monitored && b.ReleaseDate != null)
                    .Select(b => b.ReleaseDate!.Value.Year)
                    .Distinct()
                    .ToListAsync(cancellationToken);
                years.UnionWith(bookYears.Where(y => y > 0 && y <= nowYear));
                break;
        }

        return SearchYearCursor.OrderYears(years, arrConfig.Search.SearchInReverse);
    }
}

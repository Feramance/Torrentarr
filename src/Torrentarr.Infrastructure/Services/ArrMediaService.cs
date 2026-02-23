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

    private static readonly Dictionary<string, int> ReasonPriority = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Missing"] = 1,
        ["CustomFormat"] = 2,
        ["Quality"] = 3,
        ["Upgrade"] = 4,
        ["None"] = 99
    };

    public ArrMediaService(
        ILogger<ArrMediaService> logger,
        TorrentarrDbContext dbContext,
        TorrentarrConfig config,
        ISearchExecutor searchExecutor,
        ArrSyncService syncService)
    {
        _logger = logger;
        _dbContext = dbContext;
        _config = config;
        _searchExecutor = searchExecutor;
        _syncService = syncService;
    }

    public async Task<SearchResult> SearchMissingMediaAsync(string category, CancellationToken cancellationToken = default)
    {
        var arrInstance = _config.ArrInstances.Values.FirstOrDefault(i =>
            i.Category.Equals(category, StringComparison.OrdinalIgnoreCase));

        if (arrInstance == null)
        {
            _logger.LogWarning("No Arr instance found for category {Category}", category);
            return new SearchResult();
        }

        var instanceName = _config.ArrInstances.First(kvp => kvp.Value == arrInstance).Key;

        if (!arrInstance.Search.SearchMissing)
        {
            _logger.LogDebug("SearchMissing disabled for {Category}", category);
            return new SearchResult();
        }

        if (_config.Settings.SearchLoopDelay == 0 || _config.Settings.SearchLoopDelay == -1)
        {
            _logger.LogDebug("SearchLoopDelay disabled for {Category}", category);
            return new SearchResult();
        }

        _logger.LogInformation("Searching for missing media in {Category}", category);

        await _syncService.SyncAsync(instanceName, cancellationToken);
        await _syncService.SyncSearchMetadataAsync(instanceName, cancellationToken);

        var candidates = await GetSearchCandidatesAsync(instanceName, arrInstance, cancellationToken);

        return await _searchExecutor.ExecuteSearchesAsync(instanceName, candidates, cancellationToken);
    }

    public async Task<SearchResult> SearchQualityUpgradesAsync(string category, CancellationToken cancellationToken = default)
    {
        var arrInstance = _config.ArrInstances.Values.FirstOrDefault(i =>
            i.Category.Equals(category, StringComparison.OrdinalIgnoreCase));

        if (arrInstance == null)
        {
            _logger.LogDebug("No Arr instance found for category {Category}", category);
            return new SearchResult();
        }

        var instanceName = _config.ArrInstances.First(kvp => kvp.Value == arrInstance).Key;

        if (!arrInstance.Search.DoUpgradeSearch && !arrInstance.Search.CustomFormatUnmetSearch && !arrInstance.Search.QualityUnmetSearch)
        {
            _logger.LogDebug("Quality upgrade search disabled for {Category}", category);
            return new SearchResult();
        }

        if (_config.Settings.SearchLoopDelay == 0 || _config.Settings.SearchLoopDelay == -1)
        {
            _logger.LogDebug("SearchLoopDelay disabled for {Category}", category);
            return new SearchResult();
        }

        _logger.LogInformation("Searching for quality upgrades in {Category}", category);

        await _syncService.SyncAsync(instanceName, cancellationToken);
        await _syncService.SyncSearchMetadataAsync(instanceName, cancellationToken);

        var candidates = await GetUpgradeCandidatesAsync(instanceName, arrInstance, cancellationToken);

        return await _searchExecutor.ExecuteSearchesAsync(instanceName, candidates, cancellationToken);
    }

    public async Task<bool> IsQualityUpgradeAsync(int arrId, string quality, CancellationToken cancellationToken = default)
    {
        await Task.CompletedTask;
        return false;
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

        var today = DateTime.UtcNow.Date;

        switch (arrConfig.Type.ToLowerInvariant())
        {
            case "radarr":
                var movies = await _dbContext.Movies
                    .Where(m => m.ArrInstance == instanceName && m.Monitored && !m.Searched)
                    .ToListAsync(cancellationToken);

                foreach (var movie in movies)
                {
                    var priority = GetReasonPriority(movie.Reason, searchConfig);
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
                var episodes = await _dbContext.Episodes
                    .Where(e => e.ArrInstance == instanceName && e.Monitored == true && !e.Searched)
                    .ToListAsync(cancellationToken);

                foreach (var ep in episodes)
                {
                    if (!searchConfig.AlsoSearchSpecials && ep.SeasonNumber == 0)
                        continue;

                    var priority = GetReasonPriority(ep.Reason, searchConfig);
                    if (priority >= 99) continue;

                    var isTodaysRelease = searchConfig.PrioritizeTodaysReleases &&
                        ep.AirDateUtc.HasValue &&
                        ep.AirDateUtc.Value.Date == today;

                    candidates.Add(new SearchCandidate
                    {
                        ArrId = ep.ArrId,
                        Title = $"{ep.SeriesTitle} S{ep.SeasonNumber:00}E{ep.EpisodeNumber:00}",
                        Type = "Episode",
                        Reason = ep.Reason ?? "Missing",
                        Priority = priority,
                        SeriesId = ep.ArrSeriesId,
                        SeasonNumber = ep.SeasonNumber,
                        EpisodeNumber = ep.EpisodeNumber,
                        AirDate = ep.AirDateUtc,
                        IsTodaysRelease = isTodaysRelease
                    });
                }
                break;

            case "lidarr":
                var albums = await _dbContext.Albums
                    .Where(a => a.ArrInstance == instanceName && a.Monitored && !a.Searched)
                    .ToListAsync(cancellationToken);

                foreach (var album in albums)
                {
                    var priority = GetReasonPriority(album.Reason, searchConfig);
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
                var movies = await _dbContext.Movies
                    .Where(m => m.ArrInstance == instanceName && m.Monitored && m.HasFile && !m.Searched)
                    .ToListAsync(cancellationToken);

                foreach (var movie in movies)
                {
                    var priority = GetUpgradePriority(movie.QualityMet, movie.CustomFormatMet, searchConfig);
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
                var episodes = await _dbContext.Episodes
                    .Where(e => e.ArrInstance == instanceName && e.Monitored == true && e.HasFile && !e.Searched)
                    .ToListAsync(cancellationToken);

                foreach (var ep in episodes)
                {
                    var priority = GetUpgradePriority(ep.QualityMet, ep.CustomFormatMet, searchConfig);
                    if (priority >= 99) continue;

                    var reason = DetermineUpgradeReason(ep.QualityMet, ep.CustomFormatMet, searchConfig);

                    candidates.Add(new SearchCandidate
                    {
                        ArrId = ep.ArrId,
                        Title = $"{ep.SeriesTitle} S{ep.SeasonNumber:00}E{ep.EpisodeNumber:00}",
                        Type = "Episode",
                        Reason = reason,
                        Priority = priority,
                        SeriesId = ep.ArrSeriesId,
                        SeasonNumber = ep.SeasonNumber,
                        EpisodeNumber = ep.EpisodeNumber
                    });
                }
                break;

            case "lidarr":
                var albums = await _dbContext.Albums
                    .Where(a => a.ArrInstance == instanceName && a.Monitored && a.HasFile && !a.Searched)
                    .ToListAsync(cancellationToken);

                foreach (var album in albums)
                {
                    var priority = GetUpgradePriority(album.QualityMet, album.CustomFormatMet, searchConfig);
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
                        Year = album.ReleaseDate?.Year
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
}

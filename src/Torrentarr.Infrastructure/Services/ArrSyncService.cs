using Torrentarr.Core.Configuration;
using Torrentarr.Infrastructure.ApiClients.Arr;
using Torrentarr.Infrastructure.Database;
using Torrentarr.Infrastructure.Database.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Torrentarr.Infrastructure.Services;

/// <summary>
/// Syncs media data from Radarr/Sonarr/Lidarr APIs into the local SQLite database.
/// Called once at worker startup and periodically during the worker loop.
/// 
/// Three sync phases:
/// 1. SyncAsync() - Basic media sync (titles, monitored status, etc.)
/// 2. SyncSearchMetadataAsync() - Quality profiles, custom format scores, search eligibility
/// 3. SyncQueueAsync() - Download queue data for torrent tracking
/// </summary>
public class ArrSyncService
{
    private readonly ILogger<ArrSyncService> _logger;
    private readonly TorrentarrConfig _config;
    private readonly TorrentarrDbContext _db;

    public ArrSyncService(
        ILogger<ArrSyncService> logger,
        TorrentarrConfig config,
        TorrentarrDbContext db)
    {
        _logger = logger;
        _config = config;
        _db = db;
    }

    public async Task SyncAsync(string instanceName, CancellationToken ct = default)
    {
        if (!_config.ArrInstances.TryGetValue(instanceName, out var arrConfig))
        {
            _logger.LogWarning("ArrSyncService: no instance named {Name}", instanceName);
            return;
        }

        if (string.IsNullOrEmpty(arrConfig.URI) || arrConfig.URI == "CHANGE_ME")
        {
            _logger.LogDebug("ArrSyncService: skipping unconfigured instance {Name}", instanceName);
            return;
        }

        _logger.LogDebug("ArrSyncService: syncing {Type} instance {Name}", arrConfig.Type, instanceName);

        try
        {
            switch (arrConfig.Type.ToLowerInvariant())
            {
                case "radarr":
                    await SyncRadarrAsync(instanceName, arrConfig, ct);
                    break;
                case "sonarr":
                    await SyncSonarrAsync(instanceName, arrConfig, ct);
                    break;
                case "lidarr":
                    await SyncLidarrAsync(instanceName, arrConfig, ct);
                    break;
                default:
                    _logger.LogWarning("ArrSyncService: unknown type {Type} for {Name}", arrConfig.Type, instanceName);
                    break;
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "ArrSyncService: error syncing {Type} instance {Name}", arrConfig.Type, instanceName);
        }
    }

    /// <summary>
    /// Sync search-relevant metadata: quality profiles, custom format scores, and search eligibility.
    /// Populates: CustomFormatScore, MinCustomFormatScore, CustomFormatMet, QualityMet, Reason, Searched
    /// </summary>
    public async Task SyncSearchMetadataAsync(string instanceName, CancellationToken ct = default)
    {
        if (!_config.ArrInstances.TryGetValue(instanceName, out var arrConfig))
            return;

        if (string.IsNullOrEmpty(arrConfig.URI) || arrConfig.URI == "CHANGE_ME")
            return;

        var searchConfig = arrConfig.Search;
        bool needsMetadata = searchConfig.SearchMissing ||
                            searchConfig.DoUpgradeSearch ||
                            searchConfig.QualityUnmetSearch ||
                            searchConfig.CustomFormatUnmetSearch;

        if (!needsMetadata)
            return;

        _logger.LogDebug("ArrSyncService: syncing search metadata for {Name}", instanceName);

        try
        {
            switch (arrConfig.Type.ToLowerInvariant())
            {
                case "radarr":
                    await SyncRadarrSearchMetadataAsync(instanceName, arrConfig, ct);
                    break;
                case "sonarr":
                    await SyncSonarrSearchMetadataAsync(instanceName, arrConfig, ct);
                    break;
                case "lidarr":
                    await SyncLidarrSearchMetadataAsync(instanceName, arrConfig, ct);
                    break;
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "ArrSyncService: error syncing search metadata for {Name}", instanceName);
        }
    }

    /// <summary>
    /// Sync download queue data from Arr APIs into queue tables.
    /// Stores both Arr queue info and matches with qBittorrent torrent data.
    /// </summary>
    public async Task SyncQueueAsync(string instanceName, CancellationToken ct = default)
    {
        if (!_config.ArrInstances.TryGetValue(instanceName, out var arrConfig))
            return;

        if (string.IsNullOrEmpty(arrConfig.URI) || arrConfig.URI == "CHANGE_ME")
            return;

        _logger.LogDebug("ArrSyncService: syncing queue for {Name}", instanceName);

        try
        {
            switch (arrConfig.Type.ToLowerInvariant())
            {
                case "radarr":
                    await SyncRadarrQueueAsync(instanceName, arrConfig, ct);
                    break;
                case "sonarr":
                    await SyncSonarrQueueAsync(instanceName, arrConfig, ct);
                    break;
                case "lidarr":
                    await SyncLidarrQueueAsync(instanceName, arrConfig, ct);
                    break;
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "ArrSyncService: error syncing queue for {Name}", instanceName);
        }
    }

    // ── Radarr ──────────────────────────────────────────────────────────────

    private async Task SyncRadarrAsync(string instanceName, ArrInstanceConfig cfg, CancellationToken ct)
    {
        var client = new RadarrClient(cfg.URI, cfg.APIKey);

        List<RadarrMovie> movies;
        try { movies = await client.GetMoviesAsync(ct); }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "ArrSyncService: Radarr {Name} unreachable", instanceName);
            return;
        }

        var dbMovies = await _db.Movies
            .Where(m => m.ArrInstance == instanceName)
            .ToDictionaryAsync(m => m.TmdbId, ct);

        var apiTmdbIds = new HashSet<int>();

        foreach (var movie in movies)
        {
            apiTmdbIds.Add(movie.TmdbId);

            if (dbMovies.TryGetValue(movie.TmdbId, out var existing))
            {
                existing.Title = movie.Title;
                existing.Monitored = movie.Monitored;
                existing.Year = movie.Year;
                existing.MovieFileId = movie.MovieFile?.Id ?? 0;
                existing.QualityProfileId = movie.QualityProfileId;
                existing.ArrId = movie.Id;
                existing.HasFile = movie.HasFile;
                _db.Movies.Update(existing);
            }
            else
            {
                _db.Movies.Add(new MoviesFilesModel
                {
                    ArrInstance = instanceName,
                    TmdbId = movie.TmdbId,
                    Title = movie.Title,
                    Monitored = movie.Monitored,
                    Year = movie.Year,
                    MovieFileId = movie.MovieFile?.Id ?? 0,
                    QualityProfileId = movie.QualityProfileId,
                    ArrId = movie.Id,
                    HasFile = movie.HasFile
                });
            }
        }

        var toDelete = dbMovies.Values.Where(m => !apiTmdbIds.Contains(m.TmdbId)).ToList();
        if (toDelete.Count > 0)
            _db.Movies.RemoveRange(toDelete);

        await _db.SaveChangesAsync(ct);
        _logger.LogDebug("ArrSyncService: Radarr {Name} synced {Count} movies", instanceName, movies.Count);
    }

    private async Task SyncRadarrSearchMetadataAsync(string instanceName, ArrInstanceConfig cfg, CancellationToken ct)
    {
        var client = new RadarrClient(cfg.URI, cfg.APIKey);
        var searchConfig = cfg.Search;

        var profiles = await client.GetQualityProfilesAsync(ct);
        var profileDict = profiles.ToDictionary(p => p.Id);

        var dbMovies = await _db.Movies
            .Where(m => m.ArrInstance == instanceName && m.Monitored)
            .ToListAsync(ct);

        foreach (var movie in dbMovies)
        {
            if (movie.ArrId <= 0)
                continue;

            var profileId = movie.QualityProfileId ?? 0;
            if (profileId <= 0 || !profileDict.TryGetValue(profileId, out var profile))
                continue;

            var minCfScore = profile.MinCustomFormatScore ?? 0;
            movie.MinCustomFormatScore = minCfScore;

            if (movie.HasFile && movie.MovieFileId > 0)
            {
                try
                {
                    var movieFile = await client.GetMovieFileAsync(movie.MovieFileId, ct);
                    if (movieFile != null)
                    {
                        movie.CustomFormatScore = movieFile.CustomFormatScore ?? 0;
                        movie.QualityMet = !movieFile.QualityCutoffNotMet;
                        movie.CustomFormatMet = movie.CustomFormatScore >= minCfScore;
                    }
                    else
                    {
                        movie.CustomFormatScore = 0;
                        movie.QualityMet = true;
                        movie.CustomFormatMet = true;
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "ArrSyncService: failed to get movie file {Id}", movie.MovieFileId);
                    movie.CustomFormatScore = 0;
                    movie.QualityMet = true;
                    movie.CustomFormatMet = true;
                }
            }
            else
            {
                movie.CustomFormatScore = 0;
                movie.QualityMet = true;
                movie.CustomFormatMet = true;
            }

            movie.Reason = DetermineReason(
                hasFile: movie.HasFile,
                qualityMet: movie.QualityMet,
                customFormatMet: movie.CustomFormatMet,
                searchConfig: searchConfig);

            movie.Searched = DetermineSearched(movie.HasFile, movie.QualityMet, movie.CustomFormatMet, searchConfig);
        }

        await _db.SaveChangesAsync(ct);
        _logger.LogDebug("ArrSyncService: Radarr {Name} synced search metadata for {Count} movies",
            instanceName, dbMovies.Count);
    }

    private async Task SyncRadarrQueueAsync(string instanceName, ArrInstanceConfig cfg, CancellationToken ct)
    {
        var client = new RadarrClient(cfg.URI, cfg.APIKey);

        var queueResponse = await client.GetQueueAsync(ct: ct);
        var queueItems = queueResponse.Records;

        var dbQueue = await _db.MovieQueue
            .Where(q => q.ArrInstance == instanceName)
            .ToDictionaryAsync(q => q.QueueId ?? 0, ct);

        var apiQueueIds = new HashSet<int>();

        foreach (var item in queueItems)
        {
            if (item.Id <= 0) continue;
            apiQueueIds.Add(item.Id);

            if (dbQueue.TryGetValue(item.Id, out var existing))
            {
                UpdateMovieQueueFromApi(existing, item);
                _db.MovieQueue.Update(existing);
            }
            else
            {
                var newQueue = new MovieQueueModel
                {
                    ArrInstance = instanceName,
                    QueueId = item.Id
                };
                UpdateMovieQueueFromApi(newQueue, item);
                _db.MovieQueue.Add(newQueue);
            }
        }

        var toDelete = dbQueue.Values.Where(q => !apiQueueIds.Contains(q.QueueId ?? 0)).ToList();
        if (toDelete.Count > 0)
            _db.MovieQueue.RemoveRange(toDelete);

        await _db.SaveChangesAsync(ct);
        _logger.LogDebug("ArrSyncService: Radarr {Name} synced {Count} queue items", instanceName, queueItems.Count);
    }

    // ── Sonarr ──────────────────────────────────────────────────────────────

    private async Task SyncSonarrAsync(string instanceName, ArrInstanceConfig cfg, CancellationToken ct)
    {
        var client = new SonarrClient(cfg.URI, cfg.APIKey);

        List<SonarrSeries> seriesList;
        try { seriesList = await client.GetSeriesAsync(ct); }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "ArrSyncService: Sonarr {Name} unreachable", instanceName);
            return;
        }

        var dbSeries = await _db.Series
            .Where(s => s.ArrInstance == instanceName)
            .ToDictionaryAsync(s => s.Title ?? "", ct);

        var apiTitles = new HashSet<string>();
        var entityBySonarrId = new Dictionary<int, SeriesFilesModel>();

        foreach (var series in seriesList)
        {
            apiTitles.Add(series.Title);

            if (dbSeries.TryGetValue(series.Title, out var existing))
            {
                existing.Monitored = series.Monitored;
                existing.TvdbId = series.TvdbId;
                existing.QualityProfileId = series.QualityProfileId;
                existing.ArrId = series.Id;
                _db.Series.Update(existing);
                entityBySonarrId[series.Id] = existing;
            }
            else
            {
                var newSeries = new SeriesFilesModel
                {
                    ArrInstance = instanceName,
                    Title = series.Title,
                    TvdbId = series.TvdbId,
                    Monitored = series.Monitored,
                    QualityProfileId = series.QualityProfileId,
                    ArrId = series.Id
                };
                _db.Series.Add(newSeries);
                entityBySonarrId[series.Id] = newSeries;
            }
        }

        var seriesToDelete = dbSeries.Values
            .Where(s => !apiTitles.Contains(s.Title ?? ""))
            .ToList();
        if (seriesToDelete.Count > 0)
        {
            var deleteIds = seriesToDelete.Select(s => s.EntryId).ToList();
            var orphanedEps = await _db.Episodes
                .Where(e => e.ArrInstance == instanceName && deleteIds.Contains(e.SeriesId))
                .ToListAsync(ct);
            _db.Episodes.RemoveRange(orphanedEps);
            _db.Series.RemoveRange(seriesToDelete);
        }

        await _db.SaveChangesAsync(ct);

        foreach (var (sonarrId, seriesEntity) in entityBySonarrId)
        {
            List<SonarrEpisode> episodes;
            try { episodes = await client.GetEpisodesAsync(sonarrId, ct); }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "ArrSyncService: failed to get episodes for series {Id}", sonarrId);
                continue;
            }

            var existingEps = await _db.Episodes
                .Where(e => e.SeriesId == seriesEntity.EntryId)
                .ToListAsync(ct);
            _db.Episodes.RemoveRange(existingEps);

            foreach (var ep in episodes)
            {
                _db.Episodes.Add(new EpisodeFilesModel
                {
                    ArrInstance = instanceName,
                    SeriesId = seriesEntity.EntryId,
                    SeriesTitle = seriesEntity.Title,
                    Title = ep.Title,
                    EpisodeNumber = ep.EpisodeNumber,
                    SeasonNumber = ep.SeasonNumber,
                    EpisodeFileId = ep.EpisodeFileId > 0 ? ep.EpisodeFileId : null,
                    AirDateUtc = ep.AirDateUtc,
                    Monitored = ep.Monitored,
                    AbsoluteEpisodeNumber = ep.AbsoluteEpisodeNumber,
                    SceneAbsoluteEpisodeNumber = ep.SceneAbsoluteEpisodeNumber,
                    ArrId = ep.Id,
                    ArrSeriesId = sonarrId,
                    HasFile = ep.HasFile
                });
            }

            await _db.SaveChangesAsync(ct);
        }

        _logger.LogDebug("ArrSyncService: Sonarr {Name} synced {Count} series", instanceName, seriesList.Count);
    }

    private async Task SyncSonarrSearchMetadataAsync(string instanceName, ArrInstanceConfig cfg, CancellationToken ct)
    {
        var client = new SonarrClient(cfg.URI, cfg.APIKey);
        var searchConfig = cfg.Search;

        var profiles = await client.GetQualityProfilesAsync(ct);
        var profileDict = profiles.ToDictionary(p => p.Id);

        var seriesList = await _db.Series
            .Where(s => s.ArrInstance == instanceName && s.Monitored == true)
            .ToListAsync(ct);

        var seriesIds = seriesList.Select(s => s.ArrId).Where(id => id > 0).ToHashSet();
        var allEpisodes = await _db.Episodes
            .Where(e => e.ArrInstance == instanceName && e.Monitored == true)
            .ToListAsync(ct);

        foreach (var episode in allEpisodes)
        {
            if (episode.ArrId <= 0)
                continue;

            int profileId = 0;
            var series = seriesList.FirstOrDefault(s => s.EntryId == episode.SeriesId);
            if (series != null)
                profileId = series.QualityProfileId ?? 0;

            if (profileId <= 0 || !profileDict.TryGetValue(profileId, out var profile))
                continue;

            var minCfScore = profile.MinCustomFormatScore ?? 0;
            episode.MinCustomFormatScore = minCfScore;

            if (episode.HasFile && episode.EpisodeFileId > 0)
            {
                try
                {
                    var episodeFile = await client.GetEpisodeFileAsync(episode.EpisodeFileId.Value, ct);
                    if (episodeFile != null)
                    {
                        episode.CustomFormatScore = episodeFile.CustomFormatScore ?? 0;
                        episode.QualityMet = !episodeFile.QualityCutoffNotMet;
                        episode.CustomFormatMet = episode.CustomFormatScore >= minCfScore;
                    }
                    else
                    {
                        episode.CustomFormatScore = 0;
                        episode.QualityMet = true;
                        episode.CustomFormatMet = true;
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "ArrSyncService: failed to get episode file {Id}", episode.EpisodeFileId);
                    episode.CustomFormatScore = 0;
                    episode.QualityMet = true;
                    episode.CustomFormatMet = true;
                }
            }
            else
            {
                episode.CustomFormatScore = 0;
                episode.QualityMet = true;
                episode.CustomFormatMet = true;
            }

            episode.Reason = DetermineReason(
                hasFile: episode.HasFile,
                qualityMet: episode.QualityMet,
                customFormatMet: episode.CustomFormatMet,
                searchConfig: searchConfig);

            episode.Searched = DetermineSearched(episode.HasFile, episode.QualityMet, episode.CustomFormatMet, searchConfig);
        }

        await _db.SaveChangesAsync(ct);
        _logger.LogDebug("ArrSyncService: Sonarr {Name} synced search metadata for {Count} episodes",
            instanceName, allEpisodes.Count);
    }

    private async Task SyncSonarrQueueAsync(string instanceName, ArrInstanceConfig cfg, CancellationToken ct)
    {
        var client = new SonarrClient(cfg.URI, cfg.APIKey);

        var queueResponse = await client.GetQueueAsync(ct: ct);
        var queueItems = queueResponse.Records;

        var dbQueue = await _db.EpisodeQueue
            .Where(q => q.ArrInstance == instanceName)
            .ToDictionaryAsync(q => q.QueueId ?? 0, ct);

        var apiQueueIds = new HashSet<int>();

        foreach (var item in queueItems)
        {
            if (item.Id <= 0) continue;
            apiQueueIds.Add(item.Id);

            if (dbQueue.TryGetValue(item.Id, out var existing))
            {
                UpdateEpisodeQueueFromApi(existing, item);
                _db.EpisodeQueue.Update(existing);
            }
            else
            {
                var newQueue = new EpisodeQueueModel
                {
                    ArrInstance = instanceName,
                    QueueId = item.Id
                };
                UpdateEpisodeQueueFromApi(newQueue, item);
                _db.EpisodeQueue.Add(newQueue);
            }
        }

        var toDelete = dbQueue.Values.Where(q => !apiQueueIds.Contains(q.QueueId ?? 0)).ToList();
        if (toDelete.Count > 0)
            _db.EpisodeQueue.RemoveRange(toDelete);

        await _db.SaveChangesAsync(ct);
        _logger.LogDebug("ArrSyncService: Sonarr {Name} synced {Count} queue items", instanceName, queueItems.Count);
    }

    // ── Ombi / Overseerr request marking ────────────────────────────────────

    /// <summary>
    /// Fetches approved requests from Ombi/Overseerr and marks matching DB entries as IsRequest=true.
    /// Lidarr is not supported by either service.
    /// </summary>
    public async Task MarkRequestsAsync(string instanceName, CancellationToken ct = default)
    {
        if (!_config.ArrInstances.TryGetValue(instanceName, out var arrConfig))
            return;

        if (arrConfig.Type.Equals("lidarr", StringComparison.OrdinalIgnoreCase))
            return; // Ombi/Overseerr do not support Lidarr

        var ombi = arrConfig.Search.Ombi;
        var overseerr = arrConfig.Search.Overseerr;
        bool useOmbi = ombi?.SearchOmbiRequests == true
            && !string.IsNullOrEmpty(ombi.OmbiURI) && ombi.OmbiURI != "CHANGE_ME"
            && !string.IsNullOrEmpty(ombi.OmbiAPIKey) && ombi.OmbiAPIKey != "CHANGE_ME";
        bool useOverseerr = overseerr?.SearchOverseerrRequests == true
            && !string.IsNullOrEmpty(overseerr.OverseerrURI) && overseerr.OverseerrURI != "CHANGE_ME"
            && !string.IsNullOrEmpty(overseerr.OverseerrAPIKey) && overseerr.OverseerrAPIKey != "CHANGE_ME";

        if (!useOmbi && !useOverseerr)
            return;

        var requestTmdbIds = new HashSet<int>();
        var requestTvdbIds = new HashSet<int>();

        using var http = new System.Net.Http.HttpClient();
        http.Timeout = TimeSpan.FromSeconds(10);

        // ── Ombi ────────────────────────────────────────────────────────────
        if (useOmbi)
        {
            try
            {
                var endpoint = arrConfig.Type.Equals("radarr", StringComparison.OrdinalIgnoreCase)
                    ? "/api/v1/Request/movie"
                    : "/api/v1/Request/tvlite";
                var req = new System.Net.Http.HttpRequestMessage(
                    System.Net.Http.HttpMethod.Get,
                    $"{ombi!.OmbiURI.TrimEnd('/')}{endpoint}");
                req.Headers.Add("ApiKey", ombi.OmbiAPIKey);
                var resp = await http.SendAsync(req, ct);
                if (resp.IsSuccessStatusCode)
                {
                    var json = await resp.Content.ReadAsStringAsync(ct);
                    var requests = Newtonsoft.Json.JsonConvert.DeserializeObject<List<Newtonsoft.Json.Linq.JObject>>(json);
                    if (requests != null)
                    {
                        foreach (var entry in requests)
                        {
                            if (ombi.ApprovedOnly && entry["denied"]?.ToObject<bool?>() == true)
                                continue;
                            if (arrConfig.Type.Equals("radarr", StringComparison.OrdinalIgnoreCase))
                            {
                                var tmdbId = entry["theMovieDbId"]?.ToObject<int?>();
                                if (tmdbId.HasValue && tmdbId.Value > 0)
                                    requestTmdbIds.Add(tmdbId.Value);
                            }
                            else
                            {
                                var tvdbId = entry["tvDbId"]?.ToObject<int?>();
                                if (tvdbId.HasValue && tvdbId.Value > 0)
                                    requestTvdbIds.Add(tvdbId.Value);
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "ArrSyncService: failed to fetch Ombi requests for {Name}", instanceName);
            }
        }

        // ── Overseerr ───────────────────────────────────────────────────────
        if (useOverseerr)
        {
            try
            {
                var mediaType = arrConfig.Type.Equals("radarr", StringComparison.OrdinalIgnoreCase) ? "movie" : "tv";
                var filter = overseerr!.ApprovedOnly ? "approved" : "unavailable";
                var skip = 0;
                const int take = 100;
                while (true)
                {
                    var req = new System.Net.Http.HttpRequestMessage(
                        System.Net.Http.HttpMethod.Get,
                        $"{overseerr.OverseerrURI.TrimEnd('/')}/api/v1/request?take={take}&skip={skip}&sort=added&filter={filter}");
                    req.Headers.Add("X-Api-Key", overseerr.OverseerrAPIKey);
                    var resp = await http.SendAsync(req, ct);
                    if (!resp.IsSuccessStatusCode) break;

                    var json = await resp.Content.ReadAsStringAsync(ct);
                    // Overseerr response shape varies by version: bare array, {results:[]}, or {data:[]}
                    List<Newtonsoft.Json.Linq.JObject>? results = null;
                    var token = Newtonsoft.Json.JsonConvert.DeserializeObject<Newtonsoft.Json.Linq.JToken>(json);
                    if (token is Newtonsoft.Json.Linq.JArray jarr)
                        results = jarr.OfType<Newtonsoft.Json.Linq.JObject>().ToList();
                    else if (token is Newtonsoft.Json.Linq.JObject jobj)
                        results = (jobj["results"] ?? jobj["data"])?.ToObject<List<Newtonsoft.Json.Linq.JObject>>();
                    if (results == null || results.Count == 0) break;

                    foreach (var entry in results)
                    {
                        if (entry["type"]?.ToObject<string>() != mediaType)
                            continue;
                        bool is4k = entry["is4k"]?.ToObject<bool?>() ?? false;
                        if (is4k != overseerr.Is4K)
                            continue;
                        var media = entry["media"] as Newtonsoft.Json.Linq.JObject;
                        if (media == null) continue;

                        if (mediaType == "movie")
                        {
                            var tmdbId = media["tmdbId"]?.ToObject<int?>();
                            if (tmdbId.HasValue && tmdbId.Value > 0)
                                requestTmdbIds.Add(tmdbId.Value);
                        }
                        else
                        {
                            var tvdbId = media["tvdbId"]?.ToObject<int?>();
                            if (tvdbId.HasValue && tvdbId.Value > 0)
                                requestTvdbIds.Add(tvdbId.Value);
                        }
                    }

                    if (results.Count < take) break;
                    skip += take;
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "ArrSyncService: failed to fetch Overseerr requests for {Name}", instanceName);
            }
        }

        // ── Update DB ───────────────────────────────────────────────────────
        try
        {
            if (arrConfig.Type.Equals("radarr", StringComparison.OrdinalIgnoreCase))
            {
                var allMovies = await _db.Movies
                    .Where(m => m.ArrInstance == instanceName)
                    .ToListAsync(ct);
                foreach (var movie in allMovies)
                    movie.IsRequest = requestTmdbIds.Contains(movie.TmdbId);
                await _db.SaveChangesAsync(ct);
                _logger.LogDebug("ArrSyncService: marked {Count} movies as requests for {Name}",
                    requestTmdbIds.Count, instanceName);
            }
            else if (arrConfig.Type.Equals("sonarr", StringComparison.OrdinalIgnoreCase))
            {
                var requestSeriesIds = await _db.Series
                    .Where(s => s.ArrInstance == instanceName && requestTvdbIds.Contains(s.TvdbId))
                    .Select(s => s.EntryId)
                    .ToHashSetAsync(ct);
                var allEps = await _db.Episodes
                    .Where(e => e.ArrInstance == instanceName)
                    .ToListAsync(ct);
                foreach (var ep in allEps)
                    ep.IsRequest = requestSeriesIds.Contains(ep.SeriesId);
                await _db.SaveChangesAsync(ct);
                _logger.LogDebug("ArrSyncService: marked episodes for {Count} requested series for {Name}",
                    requestSeriesIds.Count, instanceName);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "ArrSyncService: failed to update IsRequest flags for {Name}", instanceName);
        }
    }

    // ── Lidarr ──────────────────────────────────────────────────────────────

    private async Task SyncLidarrAsync(string instanceName, ArrInstanceConfig cfg, CancellationToken ct)
    {
        var client = new LidarrClient(cfg.URI, cfg.APIKey);

        // Fetch artists
        List<LidarrArtist> artists;
        try { artists = await client.GetArtistsAsync(ct); }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "ArrSyncService: Lidarr {Name} unreachable", instanceName);
            return;
        }

        // Upsert artists keyed by ArtistName
        var dbArtists = await _db.Artists
            .Where(a => a.ArrInstance == instanceName)
            .ToDictionaryAsync(a => a.Title ?? "", ct);

        var apiArtistNames = new HashSet<string>();
        foreach (var artist in artists)
        {
            apiArtistNames.Add(artist.ArtistName);
            if (dbArtists.TryGetValue(artist.ArtistName, out var existing))
            {
                existing.Monitored = artist.Monitored;
                existing.QualityProfileId = artist.QualityProfileId;
                _db.Artists.Update(existing);
            }
            else
            {
                _db.Artists.Add(new ArtistFilesModel
                {
                    ArrInstance = instanceName,
                    Title = artist.ArtistName,
                    Monitored = artist.Monitored,
                    QualityProfileId = artist.QualityProfileId
                });
            }
        }
        var artistsToDelete = dbArtists.Values
            .Where(a => !apiArtistNames.Contains(a.Title ?? ""))
            .ToList();
        if (artistsToDelete.Count > 0)
            _db.Artists.RemoveRange(artistsToDelete);

        await _db.SaveChangesAsync(ct);

        // Fetch all albums at once
        List<LidarrAlbum> albums;
        try { albums = await client.GetAlbumsAsync(ct: ct); }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "ArrSyncService: Lidarr {Name} failed to fetch albums", instanceName);
            return;
        }

        // Build artist name lookup from what we fetched
        var artistNameById = artists.ToDictionary(a => a.Id, a => a.ArtistName);

        // Upsert albums keyed by ForeignAlbumId
        var dbAlbums = await _db.Albums
            .Where(a => a.ArrInstance == instanceName)
            .ToDictionaryAsync(a => a.ForeignAlbumId, ct);

        var apiForeignIds = new HashSet<string>();
        // Track Lidarr album ID → EF entity for track sync
        var albumEntityByLidarrId = new Dictionary<int, AlbumFilesModel>();

        foreach (var album in albums)
        {
            apiForeignIds.Add(album.ForeignAlbumId);
            artistNameById.TryGetValue(album.ArtistId, out var artistName);

            if (dbAlbums.TryGetValue(album.ForeignAlbumId, out var existing))
            {
                existing.Title = album.Title;
                existing.Monitored = album.Monitored;
                existing.ReleaseDate = album.ReleaseDate;
                existing.ArtistId = album.ArtistId;
                existing.ArtistTitle = artistName;
                _db.Albums.Update(existing);
                albumEntityByLidarrId[album.Id] = existing;
            }
            else
            {
                var newAlbum = new AlbumFilesModel
                {
                    ArrInstance = instanceName,
                    Title = album.Title,
                    ForeignAlbumId = album.ForeignAlbumId,
                    Monitored = album.Monitored,
                    ReleaseDate = album.ReleaseDate,
                    ArtistId = album.ArtistId,
                    ArtistTitle = artistName
                };
                _db.Albums.Add(newAlbum);
                albumEntityByLidarrId[album.Id] = newAlbum;
            }
        }

        var albumsToDelete = dbAlbums.Values
            .Where(a => !apiForeignIds.Contains(a.ForeignAlbumId))
            .ToList();
        if (albumsToDelete.Count > 0)
        {
            var deleteIds = albumsToDelete.Select(a => a.EntryId).ToList();
            var orphanedTracks = await _db.Tracks
                .Where(t => t.ArrInstance == instanceName && deleteIds.Contains(t.AlbumId))
                .ToListAsync(ct);
            _db.Tracks.RemoveRange(orphanedTracks);
            _db.Albums.RemoveRange(albumsToDelete);
        }

        // Save so EF Core assigns EntryId to new album rows
        await _db.SaveChangesAsync(ct);

        // Fetch all tracks at once and group by Lidarr album ID
        List<Track> allTracks;
        try { allTracks = await client.GetTracksAsync(ct: ct); }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "ArrSyncService: Lidarr {Name} failed to fetch tracks", instanceName);
            return;
        }

        // Clear all existing tracks for this instance then re-insert
        var existingTracks = await _db.Tracks
            .Where(t => t.ArrInstance == instanceName)
            .ToListAsync(ct);
        _db.Tracks.RemoveRange(existingTracks);
        await _db.SaveChangesAsync(ct);

        foreach (var track in allTracks)
        {
            if (!albumEntityByLidarrId.TryGetValue(track.AlbumId, out var albumEntity))
                continue;

            _db.Tracks.Add(new TrackFilesModel
            {
                ArrInstance = instanceName,
                AlbumId = albumEntity.EntryId,
                TrackNumber = track.TrackNumber,
                Title = track.Title,
                Duration = track.Duration,
                HasFile = track.HasFile,
                TrackFileId = track.TrackFileId,
                Monitored = track.Monitored
            });
        }

        await _db.SaveChangesAsync(ct);
        _logger.LogDebug("ArrSyncService: Lidarr {Name} synced {Artists} artists, {Albums} albums, {Tracks} tracks",
            instanceName, artists.Count, albums.Count, allTracks.Count);
    }

    private async Task SyncLidarrSearchMetadataAsync(string instanceName, ArrInstanceConfig cfg, CancellationToken ct)
    {
        var client = new LidarrClient(cfg.URI, cfg.APIKey);
        var searchConfig = cfg.Search;

        var profiles = await client.GetQualityProfilesAsync(ct);
        var profileDict = profiles.ToDictionary(p => p.Id);

        var dbAlbums = await _db.Albums
            .Where(a => a.ArrInstance == instanceName && a.Monitored)
            .ToListAsync(ct);

        foreach (var album in dbAlbums)
        {
            if (album.ArrId <= 0)
                continue;

            int profileId = album.QualityProfileId ?? 0;
            if (profileId <= 0 || !profileDict.TryGetValue(profileId, out var profile))
                continue;

            var minCfScore = profile.MinCustomFormatScore ?? 0;
            album.MinCustomFormatScore = minCfScore;

            if (album.HasFile)
            {
                try
                {
                    var tracks = await client.GetTracksAsync(album.ArrId, ct);
                    var tracksWithFiles = tracks.Where(t => t.HasFile).ToList();

                    if (tracksWithFiles.Any())
                    {
                        var avgScore = 0;
                        foreach (var track in tracksWithFiles)
                        {
                            if (track.TrackFileId.HasValue && track.TrackFileId.Value > 0)
                            {
                                var trackFile = await client.GetTrackFileAsync(track.TrackFileId.Value, ct);
                                if (trackFile?.CustomFormatScore.HasValue == true)
                                    avgScore += trackFile.CustomFormatScore.Value;
                            }
                        }
                        album.CustomFormatScore = avgScore / tracksWithFiles.Count;
                        album.CustomFormatMet = album.CustomFormatScore >= minCfScore;

                        album.QualityMet = CalculateLidarrQualityMet(profile, tracksWithFiles);
                    }
                    else
                    {
                        album.CustomFormatScore = 0;
                        album.QualityMet = true;
                        album.CustomFormatMet = true;
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "ArrSyncService: failed to get track files for album {Id}", album.ArrId);
                    album.CustomFormatScore = 0;
                    album.QualityMet = true;
                    album.CustomFormatMet = true;
                }
            }
            else
            {
                album.CustomFormatScore = 0;
                album.QualityMet = true;
                album.CustomFormatMet = true;
            }

            album.Reason = DetermineReason(
                hasFile: album.HasFile,
                qualityMet: album.QualityMet,
                customFormatMet: album.CustomFormatMet,
                searchConfig: searchConfig);

            album.Searched = DetermineSearched(album.HasFile, album.QualityMet, album.CustomFormatMet, searchConfig);
        }

        await _db.SaveChangesAsync(ct);
        _logger.LogDebug("ArrSyncService: Lidarr {Name} synced search metadata for {Count} albums",
            instanceName, dbAlbums.Count);
    }

    private async Task SyncLidarrQueueAsync(string instanceName, ArrInstanceConfig cfg, CancellationToken ct)
    {
        var client = new LidarrClient(cfg.URI, cfg.APIKey);

        var queueResponse = await client.GetQueueAsync(ct: ct);
        var queueItems = queueResponse.Records;

        var dbQueue = await _db.AlbumQueue
            .Where(q => q.ArrInstance == instanceName)
            .ToDictionaryAsync(q => q.QueueId ?? 0, ct);

        var apiQueueIds = new HashSet<int>();

        foreach (var item in queueItems)
        {
            if (item.Id <= 0) continue;
            apiQueueIds.Add(item.Id);

            if (dbQueue.TryGetValue(item.Id, out var existing))
            {
                UpdateAlbumQueueFromLidarrApi(existing, item);
                _db.AlbumQueue.Update(existing);
            }
            else
            {
                var newQueue = new AlbumQueueModel
                {
                    ArrInstance = instanceName,
                    QueueId = item.Id
                };
                UpdateAlbumQueueFromLidarrApi(newQueue, item);
                _db.AlbumQueue.Add(newQueue);
            }
        }

        var toDelete = dbQueue.Values.Where(q => !apiQueueIds.Contains(q.QueueId ?? 0)).ToList();
        if (toDelete.Count > 0)
            _db.AlbumQueue.RemoveRange(toDelete);

        await _db.SaveChangesAsync(ct);
        _logger.LogDebug("ArrSyncService: Lidarr {Name} synced {Count} queue items", instanceName, queueItems.Count);
    }

    // ── Helper Methods ────────────────────────────────────────────────────────

    private static string DetermineReason(bool hasFile, bool qualityMet, bool customFormatMet, SearchConfig searchConfig)
    {
        if (!hasFile)
            return "Missing";

        if (searchConfig.CustomFormatUnmetSearch && !customFormatMet)
            return "CustomFormat";

        if (searchConfig.QualityUnmetSearch && !qualityMet)
            return "Quality";

        if (searchConfig.DoUpgradeSearch)
            return "Upgrade";

        return "None";
    }

    private static bool DetermineSearched(bool hasFile, bool qualityMet, bool customFormatMet, SearchConfig searchConfig)
    {
        if (!hasFile)
            return false;

        if (!qualityMet || !customFormatMet)
            return false;

        return true;
    }

    private static bool CalculateLidarrQualityMet(QualityProfile profile, List<Track> tracksWithFiles)
    {
        var cutoffId = profile.Cutoff;
        if (!cutoffId.HasValue || cutoffId.Value <= 0)
            return true;

        return true;
    }

    private static void UpdateMovieQueueFromApi(MovieQueueModel queue, QueueItem item)
    {
        queue.QueueId = item.Id;
        queue.MovieId = item.MovieId;
        queue.DownloadId = item.DownloadId;
        queue.Title = item.Title;
        queue.Status = item.Status;
        queue.TrackedDownloadStatus = item.TrackedDownloadStatus;
        queue.TrackedDownloadState = item.TrackedDownloadState;
        queue.CustomFormatScore = item.CustomFormatScore;
        queue.Quality = item.Quality?.QualityDefinition?.Name;
        queue.Size = item.Size;
        queue.TimeLeft = item.TimeLeft;
        queue.EstimatedCompletionTime = item.EstimatedCompletionTime;
        queue.Added = item.Added;
    }

    private static void UpdateEpisodeQueueFromApi(EpisodeQueueModel queue, SonarrQueueItem item)
    {
        queue.QueueId = item.Id;
        queue.SeriesId = item.SeriesId;
        queue.EpisodeId = item.EpisodeId;
        queue.SeasonNumber = item.SeasonNumber;
        queue.EpisodeNumber = item.EpisodeNumber;
        queue.DownloadId = item.DownloadId;
        queue.Title = item.Title;
        queue.SeriesTitle = item.Title;
        queue.Status = item.Status;
        queue.TrackedDownloadStatus = item.TrackedDownloadStatus;
        queue.TrackedDownloadState = item.TrackedDownloadState;
        queue.CustomFormatScore = item.CustomFormatScore;
        queue.Quality = item.Quality?.QualityDefinition?.Name;
        queue.Size = item.Size;
        queue.TimeLeft = item.TimeLeft;
        queue.EstimatedCompletionTime = item.EstimatedCompletionTime;
        queue.Added = item.Added;
    }

    private static void UpdateAlbumQueueFromApi(AlbumQueueModel queue, QueueItem item)
    {
        queue.QueueId = item.Id;
        queue.AlbumId = item.MovieId;
        queue.DownloadId = item.DownloadId;
        queue.Title = item.Title;
        queue.Status = item.Status;
        queue.TrackedDownloadStatus = item.TrackedDownloadStatus;
        queue.TrackedDownloadState = item.TrackedDownloadState;
        queue.CustomFormatScore = item.CustomFormatScore;
        queue.Quality = item.Quality?.QualityDefinition?.Name;
        queue.Size = item.Size;
        queue.TimeLeft = item.TimeLeft;
        queue.EstimatedCompletionTime = item.EstimatedCompletionTime;
        queue.Added = item.Added;
    }

    private static void UpdateAlbumQueueFromLidarrApi(AlbumQueueModel queue, LidarrQueueItem item)
    {
        queue.QueueId = item.Id;
        queue.AlbumId = item.AlbumId;
        queue.DownloadId = item.DownloadId;
        queue.Title = item.Title;
        queue.Status = item.Status;
        queue.CustomFormatScore = item.CustomFormatScore;
    }
}

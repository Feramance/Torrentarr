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
    // Shared static HttpClient — reused across calls to avoid socket exhaustion from per-call instantiation
    private static readonly System.Net.Http.HttpClient _sharedHttpClient = new()
    {
        Timeout = TimeSpan.FromSeconds(10)
    };

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
        _logger.LogTrace("[{Instance}] Starting sync for Arr instance {Name}", instanceName, instanceName);
        
        if (!_config.ArrInstances.TryGetValue(instanceName, out var arrConfig))
        {
            _logger.LogWarning("[{Instance}] ArrSyncService: no instance named {Name}", instanceName, instanceName);
            _logger.LogTrace("[{Instance}] Sync aborted - instance {Name} not found in config", instanceName, instanceName);
            return;
        }

        if (string.IsNullOrEmpty(arrConfig.URI) || arrConfig.URI == "CHANGE_ME")
        {
            _logger.LogDebug("[{Instance}] ArrSyncService: skipping unconfigured instance {Name}", instanceName, instanceName);
            _logger.LogTrace("[{Instance}] Sync skipped - instance {Name} not configured", instanceName, instanceName);
            return;
        }

        _logger.LogTrace("[{Instance}] Syncing Arr instance {Name} of type {Type}", instanceName, instanceName, arrConfig.Type);
        _logger.LogDebug("[{Instance}] ArrSyncService: syncing {Type} instance {Name}", instanceName, arrConfig.Type, instanceName);

        try
        {
            switch (arrConfig.Type.ToLowerInvariant())
            {
                case "radarr":
                    _logger.LogTrace("[{Instance}] Routing to Radarr sync handler", instanceName);
                    await SyncRadarrAsync(instanceName, arrConfig, ct);
                    break;
                case "sonarr":
                    _logger.LogTrace("[{Instance}] Routing to Sonarr sync handler", instanceName);
                    await SyncSonarrAsync(instanceName, arrConfig, ct);
                    break;
                case "lidarr":
                    _logger.LogTrace("[{Instance}] Routing to Lidarr sync handler", instanceName);
                    await SyncLidarrAsync(instanceName, arrConfig, ct);
                    break;
                default:
                    _logger.LogWarning("[{Instance}] ArrSyncService: unknown type {Type} for {Name}", instanceName, arrConfig.Type, instanceName);
                    break;
            }
            
            _logger.LogTrace("[{Instance}] Sync completed for {Name}", instanceName, instanceName);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[{Instance}] ArrSyncService: error syncing {Type} instance {Name}", instanceName, arrConfig.Type, instanceName);
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
        var searchConfig = cfg.Search;

        _logger.LogInformation("Started updating database");

        List<RadarrMovie> movies;
        try { movies = await client.GetMoviesAsync(ct); }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[{Instance}] ArrSyncService: Radarr {Name} unreachable", instanceName, instanceName);
            return;
        }

        var profiles = await client.GetQualityProfilesAsync(ct);
        var profileDict = profiles.ToDictionary(p => p.Id);

        var dbMovies = await _db.Movies
            .Where(m => m.ArrInstance == instanceName)
            .ToDictionaryAsync(m => m.TmdbId, ct);

        var apiTmdbIds = new HashSet<int>();
        var added = 0;
        var updated = 0;

        foreach (var movie in movies)
        {
            apiTmdbIds.Add(movie.TmdbId);

            profileDict.TryGetValue(movie.QualityProfileId, out var profile);
            var minCfScore = profile?.MinCustomFormatScore ?? 0;
            var cfScore = movie.MovieFile?.CustomFormatScore ?? 0;
            var qualityMet = !movie.HasFile || !(movie.MovieFile?.QualityCutoffNotMet ?? false);
            var customFormatMet = !movie.HasFile || cfScore >= minCfScore;
            var isAvailable = MinimumAvailabilityCheck(movie.MinimumAvailability, movie.InCinemas, movie.DigitalRelease, movie.PhysicalRelease, movie.Year, movie.Title, _logger);
            var reason = DetermineReasonWithAvailability(movie.HasFile, qualityMet, customFormatMet, isAvailable, searchConfig);
            var searched = DetermineSearched(movie.HasFile, qualityMet, customFormatMet, searchConfig);

            if (dbMovies.TryGetValue(movie.TmdbId, out var existing))
            {
                existing.Title = movie.Title;
                existing.Monitored = movie.Monitored;
                existing.Year = movie.Year;
                existing.MovieFileId = movie.MovieFile?.Id ?? 0;
                existing.QualityProfileId = movie.QualityProfileId;
                existing.ArrId = movie.Id;
                existing.HasFile = movie.HasFile;
                existing.InCinemas = movie.InCinemas;
                existing.DigitalRelease = movie.DigitalRelease;
                existing.PhysicalRelease = movie.PhysicalRelease;
                existing.MinimumAvailability = movie.MinimumAvailability;
                existing.CustomFormatScore = cfScore;
                existing.QualityMet = qualityMet;
                existing.MinCustomFormatScore = minCfScore;
                existing.CustomFormatMet = customFormatMet;
                existing.Reason = reason;
                existing.Searched = searched;
                _db.Movies.Update(existing);
                updated++;
                _logger.LogTrace("DB Update: Movie {Title} (TmdbId: {TmdbId}) updated in database (quality: {Quality}, file: {FileId})", movie.Title, movie.TmdbId, movie.QualityProfileId, movie.MovieFile?.Id ?? 0);
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
                    HasFile = movie.HasFile,
                    InCinemas = movie.InCinemas,
                    DigitalRelease = movie.DigitalRelease,
                    PhysicalRelease = movie.PhysicalRelease,
                    MinimumAvailability = movie.MinimumAvailability,
                    CustomFormatScore = cfScore,
                    QualityMet = qualityMet,
                    MinCustomFormatScore = minCfScore,
                    CustomFormatMet = customFormatMet,
                    Reason = reason,
                    Searched = searched
                });
                added++;
                _logger.LogTrace("DB Insert: Movie {Title} (TmdbId: {TmdbId}) added to database (new)", movie.Title, movie.TmdbId);
            }
        }

        var toDelete = dbMovies.Values.Where(m => !apiTmdbIds.Contains(m.TmdbId)).ToList();
        foreach (var movie in toDelete)
        {
            _logger.LogTrace("DB Delete: Movie {Title} (TmdbId: {TmdbId}) removed from database", movie.Title, movie.TmdbId);
        }
        if (toDelete.Count > 0)
            _db.Movies.RemoveRange(toDelete);

        await _db.SaveChangesAsync(ct);
        _logger.LogDebug("[{Instance}] ArrSyncService: Radarr {Name} synced {Count} movies - Added: {Added}, Updated: {Updated}, Deleted: {Deleted}", instanceName, instanceName, movies.Count, added, updated, toDelete.Count);
        _logger.LogTrace("[{Instance}] Finished updating database for Radarr instance {Name}", instanceName, instanceName);
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

        // §1.7: Scan for ArrErrorCodesToBlocklist matches
        await ScanQueueForBlocklistAsync(
            queueItems.Select(i => (i.Id, i.DownloadId, i.TrackedDownloadStatus, i.TrackedDownloadState, i.StatusMessages)),
            cfg,
            (id, token) => client.DeleteFromQueueAsync(id, removeFromClient: true, blocklist: true, ct: token),
            ct);
    }

    // ── Sonarr ──────────────────────────────────────────────────────────────

    private async Task SyncSonarrAsync(string instanceName, ArrInstanceConfig cfg, CancellationToken ct)
    {
        var client = new SonarrClient(cfg.URI, cfg.APIKey);
        var searchConfig = cfg.Search;

        _logger.LogInformation("Started updating database");

        List<SonarrSeries> seriesList;
        try { seriesList = await client.GetSeriesAsync(ct); }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[{Instance}] ArrSyncService: Sonarr {Name} unreachable", instanceName, instanceName);
            return;
        }

        var profiles = await client.GetQualityProfilesAsync(ct);
        var profileDict = profiles.ToDictionary(p => p.Id);
        var seriesProfileById = seriesList.ToDictionary(s => s.Id, s => s.QualityProfileId);

        var dbSeries = await _db.Series
            .Where(s => s.ArrInstance == instanceName)
            .ToDictionaryAsync(s => s.Title ?? "", ct);

        var apiTitles = new HashSet<string>();
        var entityBySonarrId = new Dictionary<int, SeriesFilesModel>();
        var seriesAdded = 0;
        var seriesUpdated = 0;

        foreach (var series in seriesList)
        {
            ct.ThrowIfCancellationRequested();
            apiTitles.Add(series.Title);

            if (dbSeries.TryGetValue(series.Title, out var existing))
            {
                existing.Monitored = series.Monitored;
                existing.TvdbId = series.TvdbId;
                existing.QualityProfileId = series.QualityProfileId;
                existing.ArrId = series.Id;
                _db.Series.Update(existing);
                entityBySonarrId[series.Id] = existing;
                seriesUpdated++;
                _logger.LogTrace("DB Update: Series {Title} (TvdbId: {TvdbId}) updated in database", series.Title, series.TvdbId);
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
                seriesAdded++;
                _logger.LogTrace("DB Insert: Series {Title} (TvdbId: {TvdbId}) added to database (new)", series.Title, series.TvdbId);
            }
        }

        var seriesToDelete = dbSeries.Values
            .Where(s => !apiTitles.Contains(s.Title ?? ""))
            .ToList();
        foreach (var series in seriesToDelete)
        {
            _logger.LogTrace("DB Delete: Series {Title} removed from database", series.Title);
        }
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

        var episodesAdded = 0;

        foreach (var (sonarrId, seriesEntity) in entityBySonarrId)
        {
            ct.ThrowIfCancellationRequested();
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

            var seriesProfileId = seriesProfileById.GetValueOrDefault(sonarrId);
            profileDict.TryGetValue(seriesProfileId, out var seriesProfile);
            var minCfScore = seriesProfile?.MinCustomFormatScore ?? 0;

            foreach (var ep in episodes)
            {
                var cfScore = ep.EpisodeFile?.CustomFormatScore ?? 0;
                var qualityMet = !ep.HasFile || !(ep.EpisodeFile?.QualityCutoffNotMet ?? false);
                var customFormatMet = !ep.HasFile || cfScore >= minCfScore;
                var isAvailable = CheckEpisodeAvailability(ep.AirDateUtc, ep.Title ?? "Unknown", _logger);
                var reason = DetermineReasonWithAvailability(ep.HasFile, qualityMet, customFormatMet, isAvailable, searchConfig);
                var searched = DetermineSearched(ep.HasFile, qualityMet, customFormatMet, searchConfig);

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
                    HasFile = ep.HasFile,
                    CustomFormatScore = cfScore,
                    QualityMet = qualityMet,
                    MinCustomFormatScore = minCfScore,
                    CustomFormatMet = customFormatMet,
                    Reason = reason,
                    Searched = searched
                });
                episodesAdded++;
                _logger.LogTrace("DB Insert: Episode {SeriesTitle} S{SeasonNumber:E} E{EpisodeNumber} added to database (new)",
                    seriesEntity.Title, ep.SeasonNumber, ep.EpisodeNumber);
            }

            await _db.SaveChangesAsync(ct);
        }

        _logger.LogDebug("[{Instance}] ArrSyncService: Sonarr {Name} synced {SeriesCount} series - Series Added: {SeriesAdded}, Updated: {SeriesUpdated}, Deleted: {SeriesDeleted}, Episodes Added: {EpisodesAdded}",
            instanceName, instanceName, seriesList.Count, seriesAdded, seriesUpdated, seriesToDelete.Count, episodesAdded);
        _logger.LogTrace("[{Instance}] Finished updating database for Sonarr instance {Name}", instanceName, instanceName);
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

        // §1.7: Scan for ArrErrorCodesToBlocklist matches
        await ScanQueueForBlocklistAsync(
            queueItems.Select(i => (i.Id, i.DownloadId, i.TrackedDownloadStatus, i.TrackedDownloadState, i.StatusMessages)),
            cfg,
            (id, token) => client.DeleteFromQueueAsync(id, removeFromClient: true, blocklist: true, ct: token),
            ct);
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

        var http = _sharedHttpClient;

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
        var searchConfig = cfg.Search;

        _logger.LogInformation("Started updating database");

        var qualityProfiles = await client.GetQualityProfilesAsync(ct);
        var profileDict = qualityProfiles.ToDictionary(p => p.Id);

        // Fetch artists
        List<LidarrArtist> artists;
        try { artists = await client.GetArtistsAsync(ct); }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[{Instance}] ArrSyncService: Lidarr {Name} unreachable", instanceName, instanceName);
            return;
        }

        var artistProfileById = artists.ToDictionary(a => a.Id, a => a.QualityProfileId);

        // Upsert artists keyed by ArtistName
        var dbArtists = await _db.Artists
            .Where(a => a.ArrInstance == instanceName)
            .ToDictionaryAsync(a => a.Title ?? "", ct);

        var apiArtistNames = new HashSet<string>();
        var artistsAdded = 0;
        var artistsUpdated = 0;

        foreach (var artist in artists)
        {
            ct.ThrowIfCancellationRequested();
            apiArtistNames.Add(artist.ArtistName);
            if (dbArtists.TryGetValue(artist.ArtistName, out var existing))
            {
                existing.Monitored = artist.Monitored;
                existing.QualityProfileId = artist.QualityProfileId;
                _db.Artists.Update(existing);
                artistsUpdated++;
                _logger.LogTrace("DB Update: Artist {Title} updated in database", artist.ArtistName);
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
                artistsAdded++;
                _logger.LogTrace("DB Insert: Artist {Title} added to database (new)", artist.ArtistName);
            }
        }
        var artistsToDelete = dbArtists.Values
            .Where(a => !apiArtistNames.Contains(a.Title ?? ""))
            .ToList();
        foreach (var artist in artistsToDelete)
        {
            _logger.LogTrace("DB Delete: Artist {Title} removed from database", artist.Title);
        }
        if (artistsToDelete.Count > 0)
            _db.Artists.RemoveRange(artistsToDelete);

        await _db.SaveChangesAsync(ct);

        // Fetch all albums at once
        List<LidarrAlbum> albums;
        try { albums = await client.GetAlbumsAsync(ct: ct); }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[{Instance}] ArrSyncService: Lidarr {Name} failed to fetch albums", instanceName, instanceName);
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
        var albumsAdded = 0;
        var albumsUpdated = 0;

        foreach (var album in albums)
        {
            ct.ThrowIfCancellationRequested();
            apiForeignIds.Add(album.ForeignAlbumId);
            artistNameById.TryGetValue(album.ArtistId, out var artistName);
            var albumProfileId = album.QualityProfileId
                ?? (artistProfileById.TryGetValue(album.ArtistId, out var ap) ? ap : 0);

            if (dbAlbums.TryGetValue(album.ForeignAlbumId, out var existing))
            {
                existing.Title = album.Title;
                existing.Monitored = album.Monitored;
                existing.ReleaseDate = album.ReleaseDate;
                existing.ArtistId = album.ArtistId;
                existing.ArtistTitle = artistName;
                existing.QualityProfileId = albumProfileId;
                existing.ArrId = album.Id;
                existing.HasFile = album.Statistics?.TrackFileCount > 0;
                _db.Albums.Update(existing);
                albumEntityByLidarrId[album.Id] = existing;
                albumsUpdated++;
                _logger.LogTrace("DB Update: Album {Title} ({Artist}) updated in database", album.Title, artistName);
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
                    ArtistTitle = artistName,
                    QualityProfileId = albumProfileId,
                    ArrId = album.Id,
                    HasFile = album.Statistics?.TrackFileCount > 0
                };
                _db.Albums.Add(newAlbum);
                albumEntityByLidarrId[album.Id] = newAlbum;
                albumsAdded++;
                _logger.LogTrace("DB Insert: Album {Title} ({Artist}) added to database (new)", album.Title, artistName);
            }
        }

        var albumsToDelete = dbAlbums.Values
            .Where(a => !apiForeignIds.Contains(a.ForeignAlbumId))
            .ToList();
        foreach (var album in albumsToDelete)
        {
            _logger.LogTrace("DB Delete: Album {Title} removed from database", album.Title);
        }
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

        // Compute search metadata for each album using bulk track files (one API call per album)
        foreach (var (lidarrAlbumId, albumEntity) in albumEntityByLidarrId)
        {
            artistProfileById.TryGetValue(albumEntity.ArtistId, out var artistProfileId);
            profileDict.TryGetValue(artistProfileId, out var profile);
            var minCfScore = profile?.MinCustomFormatScore ?? 0;
            albumEntity.MinCustomFormatScore = minCfScore;

            if (albumEntity.HasFile)
            {
                try
                {
                    var trackFiles = await client.GetTrackFilesByAlbumAsync(lidarrAlbumId, ct);
                    if (trackFiles.Count > 0)
                    {
                        albumEntity.CustomFormatScore = trackFiles.Sum(tf => tf.CustomFormatScore ?? 0) / trackFiles.Count;
                        albumEntity.QualityMet = true;
                        albumEntity.CustomFormatMet = albumEntity.CustomFormatScore >= minCfScore;
                    }
                    else
                    {
                        albumEntity.CustomFormatScore = 0;
                        albumEntity.QualityMet = true;
                        albumEntity.CustomFormatMet = true;
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "ArrSyncService: failed to get track files for album {Id}", lidarrAlbumId);
                    albumEntity.CustomFormatScore = 0;
                    albumEntity.QualityMet = true;
                    albumEntity.CustomFormatMet = true;
                }
            }
            else
            {
                albumEntity.CustomFormatScore = 0;
                albumEntity.QualityMet = true;
                albumEntity.CustomFormatMet = true;
            }

            var isAvailable = CheckAlbumAvailability(albumEntity.ReleaseDate, albumEntity.Title ?? "Unknown", _logger);
            albumEntity.Reason = DetermineReasonWithAvailability(albumEntity.HasFile, albumEntity.QualityMet, albumEntity.CustomFormatMet, isAvailable, searchConfig);
            albumEntity.Searched = DetermineSearched(albumEntity.HasFile, albumEntity.QualityMet, albumEntity.CustomFormatMet, searchConfig);
        }
        await _db.SaveChangesAsync(ct);

        // Fetch all tracks at once and group by Lidarr album ID
        List<Track> allTracks;
        try { allTracks = await client.GetTracksAsync(ct: ct); }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[{Instance}] ArrSyncService: Lidarr {Name} failed to fetch tracks", instanceName, instanceName);
            return;
        }

        // Clear all existing tracks for this instance then re-insert
        var existingTracks = await _db.Tracks
            .Where(t => t.ArrInstance == instanceName)
            .ToListAsync(ct);
        _db.Tracks.RemoveRange(existingTracks);
        await _db.SaveChangesAsync(ct);

        var tracksAdded = 0;
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
            tracksAdded++;
            _logger.LogTrace("DB Insert: Track {Title} added to database (new)", track.Title);
        }

        await _db.SaveChangesAsync(ct);
        _logger.LogDebug("[{Instance}] ArrSyncService: Lidarr {Name} synced - Artists: Added: {ArtistsAdded}, Updated: {ArtistsUpdated}, Deleted: {ArtistsDeleted} | Albums: Added: {AlbumsAdded}, Updated: {AlbumsUpdated}, Deleted: {AlbumsDeleted} | Tracks Added: {TracksAdded}",
            instanceName, instanceName, artistsAdded, artistsUpdated, artistsToDelete.Count, albumsAdded, albumsUpdated, albumsToDelete.Count, tracksAdded);
        _logger.LogTrace("Finished updating database for Lidarr instance {Name}", instanceName);
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

        // §1.7: Scan for ArrErrorCodesToBlocklist matches
        await ScanQueueForBlocklistAsync(
            queueItems.Select(i => (i.Id, i.DownloadId, i.TrackedDownloadStatus, i.TrackedDownloadState, i.StatusMessages)),
            cfg,
            (id, token) => client.DeleteFromQueueAsync(id, removeFromClient: true, blocklist: true, ct: token),
            ct);
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

    private static string DetermineReasonWithAvailability(bool hasFile, bool qualityMet, bool customFormatMet, bool isAvailable, SearchConfig searchConfig)
    {
        if (!isAvailable)
            return "NotAvailable";

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

    private static bool MinimumAvailabilityCheck(string? minimumAvailability, DateTime? inCinemas, DateTime? digitalRelease, DateTime? physicalRelease, int year, string title, ILogger? logger = null)
    {
        var now = DateTime.UtcNow;

        // Case 1: Year > now.year or Year == 0 → Skip
        if (year > now.Year || year == 0)
        {
            logger?.LogTrace("Skipping 1 {Title} - Minimum Availability: {MinAvail}, Dates Cinema:{Cinemas}, Digital:{Digital}, Physical:{Physical}",
                title, minimumAvailability, inCinemas, digitalRelease, physicalRelease);
            return false;
        }

        // Case 2: Year < now.year - 1 → Grab (old movie over 1 year old)
        if (year < now.Year - 1)
        {
            logger?.LogTrace("Grabbing 2 {Title} - Minimum Availability: {MinAvail}, Dates Cinema:{Cinemas}, Digital:{Digital}, Physical:{Physical}",
                title, minimumAvailability, inCinemas, digitalRelease, physicalRelease);
            return true;
        }

        // Case 3: No dates + "released" → Grab
        if (inCinemas == null && digitalRelease == null && physicalRelease == null && minimumAvailability == "released")
        {
            logger?.LogTrace("Grabbing 3 {Title} - Minimum Availability: {MinAvail}, Dates Cinema:{Cinemas}, Digital:{Digital}, Physical:{Physical}",
                title, minimumAvailability, inCinemas, digitalRelease, physicalRelease);
            return true;
        }

        // Case 4: Both dates + "released" + any passed → Grab
        if (digitalRelease != null && physicalRelease != null && minimumAvailability == "released")
        {
            if (digitalRelease <= now || physicalRelease <= now)
            {
                logger?.LogTrace("Grabbing 4 {Title} - Minimum Availability: {MinAvail}, Dates Cinema:{Cinemas}, Digital:{Digital}, Physical:{Physical}",
                    title, minimumAvailability, inCinemas, digitalRelease, physicalRelease);
                return true;
            }
            else
            {
                logger?.LogTrace("Skipping 5 {Title} - Minimum Availability: {MinAvail}, Dates Cinema:{Cinemas}, Digital:{Digital}, Physical:{Physical}",
                    title, minimumAvailability, inCinemas, digitalRelease, physicalRelease);
                return false;
            }
        }

        // Case 6-7: Digital only + "released"
        if ((digitalRelease != null || physicalRelease != null) && minimumAvailability == "released")
        {
            if (digitalRelease != null)
            {
                if (digitalRelease <= now)
                {
                    logger?.LogTrace("Grabbing 6 {Title} - Minimum Availability: {MinAvail}, Dates Cinema:{Cinemas}, Digital:{Digital}, Physical:{Physical}",
                        title, minimumAvailability, inCinemas, digitalRelease, physicalRelease);
                    return true;
                }
                else
                {
                    logger?.LogTrace("Skipping 7 {Title} - Minimum Availability: {MinAvail}, Dates Cinema:{Cinemas}, Digital:{Digital}, Physical:{Physical}",
                        title, minimumAvailability, inCinemas, digitalRelease, physicalRelease);
                    return false;
                }
            }
            else if (physicalRelease != null)
            {
                if (physicalRelease <= now)
                {
                    logger?.LogTrace("Grabbing 8 {Title} - Minimum Availability: {MinAvail}, Dates Cinema:{Cinemas}, Digital:{Digital}, Physical:{Physical}",
                        title, minimumAvailability, inCinemas, digitalRelease, physicalRelease);
                    return true;
                }
                else
                {
                    logger?.LogTrace("Skipping 9 {Title} - Minimum Availability: {MinAvail}, Dates Cinema:{Cinemas}, Digital:{Digital}, Physical:{Physical}",
                        title, minimumAvailability, inCinemas, digitalRelease, physicalRelease);
                    return false;
                }
            }
        }

        // Case 10: No dates + "inCinemas" → Grab
        if (inCinemas == null && digitalRelease == null && physicalRelease == null && minimumAvailability == "inCinemas")
        {
            logger?.LogTrace("Grabbing 10 {Title} - Minimum Availability: {MinAvail}, Dates Cinema:{Cinemas}, Digital:{Digital}, Physical:{Physical}",
                title, minimumAvailability, inCinemas, digitalRelease, physicalRelease);
            return true;
        }

        // Case 11-12: inCinemas + "inCinemas"
        if (inCinemas != null && minimumAvailability == "inCinemas")
        {
            if (inCinemas <= now)
            {
                logger?.LogTrace("Grabbing 11 {Title} - Minimum Availability: {MinAvail}, Dates Cinema:{Cinemas}, Digital:{Digital}, Physical:{Physical}",
                    title, minimumAvailability, inCinemas, digitalRelease, physicalRelease);
                return true;
            }
            else
            {
                logger?.LogTrace("Skipping 12 {Title} - Minimum Availability: {MinAvail}, Dates Cinema:{Cinemas}, Digital:{Digital}, Physical:{Physical}",
                    title, minimumAvailability, inCinemas, digitalRelease, physicalRelease);
                return false;
            }
        }

        // Case 13-17: No inCinemas + "inCinemas" + other dates
        if (inCinemas == null && minimumAvailability == "inCinemas")
        {
            if (digitalRelease != null)
            {
                if (digitalRelease <= now)
                {
                    logger?.LogTrace("Grabbing 13 {Title} - Minimum Availability: {MinAvail}, Dates Cinema:{Cinemas}, Digital:{Digital}, Physical:{Physical}",
                        title, minimumAvailability, inCinemas, digitalRelease, physicalRelease);
                    return true;
                }
                else
                {
                    logger?.LogTrace("Skipping 14 {Title} - Minimum Availability: {MinAvail}, Dates Cinema:{Cinemas}, Digital:{Digital}, Physical:{Physical}",
                        title, minimumAvailability, inCinemas, digitalRelease, physicalRelease);
                    return false;
                }
            }
            else if (physicalRelease != null)
            {
                if (physicalRelease <= now)
                {
                    logger?.LogTrace("Grabbing 15 {Title} - Minimum Availability: {MinAvail}, Dates Cinema:{Cinemas}, Digital:{Digital}, Physical:{Physical}",
                        title, minimumAvailability, inCinemas, digitalRelease, physicalRelease);
                    return true;
                }
                else
                {
                    logger?.LogTrace("Skipping 16 {Title} - Minimum Availability: {MinAvail}, Dates Cinema:{Cinemas}, Digital:{Digital}, Physical:{Physical}",
                        title, minimumAvailability, inCinemas, digitalRelease, physicalRelease);
                    return false;
                }
            }
            else
            {
                // Case 17: No inCinemas + no dates + "inCinemas" → Skip
                logger?.LogTrace("Skipping 17 {Title} - Minimum Availability: {MinAvail}, Dates Cinema:{Cinemas}, Digital:{Digital}, Physical:{Physical}",
                    title, minimumAvailability, inCinemas, digitalRelease, physicalRelease);
                return false;
            }
        }

        // Case 18: "announced" → Grab
        if (minimumAvailability == "announced")
        {
            logger?.LogTrace("Grabbing 18 {Title} - Minimum Availability: {MinAvail}, Dates Cinema:{Cinemas}, Digital:{Digital}, Physical:{Physical}",
                title, minimumAvailability, inCinemas, digitalRelease, physicalRelease);
            return true;
        }

        // Case 19: Default → Skip
        logger?.LogTrace("Skipping 19 {Title} - Minimum Availability: {MinAvail}, Dates Cinema:{Cinemas}, Digital:{Digital}, Physical:{Physical}",
            title, minimumAvailability, inCinemas, digitalRelease, physicalRelease);
        return false;
    }

    private bool CheckEpisodeAvailability(DateTime? airDateUtc, string episodeTitle, ILogger? logger = null)
    {
        var now = DateTime.UtcNow;

        if (airDateUtc == null)
        {
            logger?.LogTrace("Episode {Title} - No air date, available for search", episodeTitle);
            return true;
        }

        if (airDateUtc > now)
        {
            logger?.LogTrace("Episode {Title} - Not aired yet (AirDate: {AirDate}), skipping", episodeTitle, airDateUtc);
            return false;
        }

        logger?.LogTrace("Episode {Title} - Available (aired on {AirDate})", episodeTitle, airDateUtc);
        return true;
    }

    private bool CheckAlbumAvailability(DateTime? releaseDate, string albumTitle, ILogger? logger = null)
    {
        var now = DateTime.UtcNow;

        if (releaseDate == null)
        {
            logger?.LogTrace("Album {Title} - No release date, available for search", albumTitle);
            return true;
        }

        if (releaseDate > now)
        {
            logger?.LogTrace("Album {Title} - Not released yet (ReleaseDate: {ReleaseDate}), skipping", albumTitle, releaseDate);
            return false;
        }

        logger?.LogTrace("Album {Title} - Available (released on {ReleaseDate})", albumTitle, releaseDate);
        return true;
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
        queue.TrackedDownloadStatus = item.TrackedDownloadStatus;
        queue.TrackedDownloadState = item.TrackedDownloadState;
        queue.CustomFormatScore = item.CustomFormatScore;
    }

    /// <summary>
    /// §1.7: Scan freshly-synced queue items for entries matching ArrErrorCodesToBlocklist
    /// and blocklist+delete them from the Arr queue (removeFromClient=true also removes the
    /// torrent from qBittorrent via Arr's queue deletion API).
    /// </summary>
    private async Task ScanQueueForBlocklistAsync(
        IEnumerable<(int Id, string? DownloadId, string? TrackedDownloadStatus, string? TrackedDownloadState, List<StatusMessage>? StatusMessages)> items,
        ArrInstanceConfig cfg,
        Func<int, CancellationToken, Task<bool>> deleteFromQueue,
        CancellationToken ct)
    {
        if (cfg.ArrErrorCodesToBlocklist.Count == 0) return;

        foreach (var (id, downloadId, status, state, messages) in items)
        {
            if (!string.Equals(status, "warning", StringComparison.OrdinalIgnoreCase)) continue;
            if (!string.Equals(state, "importPending", StringComparison.OrdinalIgnoreCase)) continue;

            var allMessages = messages?.SelectMany(m => m.Messages ?? Enumerable.Empty<string>())
                              ?? Enumerable.Empty<string>();

            var matchedCode = allMessages.FirstOrDefault(msg =>
                cfg.ArrErrorCodesToBlocklist.Any(code =>
                    msg.Contains(code, StringComparison.OrdinalIgnoreCase)));

            if (matchedCode == null) continue;

            _logger.LogWarning(
                "ArrErrorCodesToBlocklist: blocklisting queue item {Id} (hash: {DownloadId}) — matched: \"{Error}\"",
                id, downloadId, matchedCode);

            await deleteFromQueue(id, ct);
        }
    }
}

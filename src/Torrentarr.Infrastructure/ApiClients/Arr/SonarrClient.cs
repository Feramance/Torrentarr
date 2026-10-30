using Newtonsoft.Json;
using RestSharp;

namespace Torrentarr.Infrastructure.ApiClients.Arr;

/// <summary>
/// Sonarr API client using RestSharp
/// </summary>
public class SonarrClient
{
    private readonly RestClient _client;
    private readonly string _apiKey;
    private readonly string _baseUrl;
    private readonly bool _skipTlsVerify;

    public SonarrClient(string baseUrl, string apiKey, bool skipTlsVerify = false)
    {
        _apiKey = apiKey;
        _baseUrl = baseUrl;
        _skipTlsVerify = skipTlsVerify;
        _client = new RestClient(Torrentarr.Infrastructure.Http.TlsSkipHelper.CreateRestOptions(baseUrl, skipTlsVerify));
    }

    /// <summary>
    /// Get all series
    /// </summary>
    public async Task<List<SonarrSeries>> GetSeriesAsync(CancellationToken ct = default)
    {
        var request = await CreateRequestAsync("series", Method.Get, ct);
        AddApiKeyHeader(request);

        var response = await ArrClientResponse.ExecuteAsync(_client, request, ct);
        ArrClientResponse.EnsureSuccess(response, $"GET /{request.Resource}");

        if (string.IsNullOrEmpty(response.Content))
            return new List<SonarrSeries>();

        return JsonConvert.DeserializeObject<List<SonarrSeries>>(response.Content) ?? new List<SonarrSeries>();
    }

    /// <summary>
    /// Get system status/info
    /// </summary>
    public async Task<SystemInfo> GetSystemInfoAsync(CancellationToken ct = default)
    {
        var request = await CreateRequestAsync("system/status", Method.Get, ct);
        AddApiKeyHeader(request);
        var response = await ArrClientResponse.ExecuteAsync(_client, request, ct);
        ArrClientResponse.EnsureSuccess(response, $"GET /{request.Resource}");
        return ArrClientResponse.DeserializeOrDefault<SystemInfo>(response.Content);
    }

    /// <summary>
    /// Get series by ID
    /// </summary>
    public async Task<SonarrSeries?> GetSeriesAsync(int seriesId, CancellationToken ct = default)
    {
        var request = await CreateRequestAsync($"series/{seriesId}", Method.Get, ct);
        AddApiKeyHeader(request);

        var response = await ArrClientResponse.ExecuteAsync(_client, request, ct);

        if (response.IsSuccessful && !string.IsNullOrEmpty(response.Content))
        {
            return JsonConvert.DeserializeObject<SonarrSeries>(response.Content);
        }

        return null;
    }

    /// <summary>
    /// Get episodes for a series
    /// </summary>
    public async Task<List<SonarrEpisode>> GetEpisodesAsync(int seriesId, CancellationToken ct = default)
    {
        var request = await CreateRequestAsync("episode", Method.Get, ct);
        AddApiKeyHeader(request);
        request.AddQueryParameter("seriesId", seriesId.ToString());
        request.AddQueryParameter(request.Resource.Contains("/v5/") ? "includeSubresources" : "includeEpisodeFile",
            request.Resource.Contains("/v5/") ? "episodeFile" : "true");

        var response = await ArrClientResponse.ExecuteAsync(_client, request, ct);
        ArrClientResponse.EnsureSuccess(response, $"GET /{request.Resource}");

        if (string.IsNullOrEmpty(response.Content))
            return new List<SonarrEpisode>();

        return JsonConvert.DeserializeObject<List<SonarrEpisode>>(response.Content) ?? new List<SonarrEpisode>();
    }

    /// <summary>
    /// Trigger series search
    /// </summary>
    public async Task<bool> SearchSeriesAsync(int seriesId, CancellationToken ct = default)
    {
        var request = await CreateRequestAsync("command", Method.Post, ct);
        AddApiKeyHeader(request);

        var command = new
        {
            name = "SeriesSearch",
            seriesId
        };

        request.AddJsonBody(command);

        var response = await ArrClientResponse.ExecuteAsync(_client, request, ct);
        return response.IsSuccessful;
    }

    /// <summary>Trigger a season search using Sonarr's release selection.</summary>
    public async Task<bool> SearchSeasonAsync(int seriesId, int seasonNumber, CancellationToken ct = default)
    {
        var request = await CreateRequestAsync("command", Method.Post, ct);
        AddApiKeyHeader(request);
        request.AddJsonBody(new { name = "SeasonSearch", seriesId, seasonNumber });
        var response = await ArrClientResponse.ExecuteAsync(_client, request, ct);
        return response.IsSuccessful;
    }

    /// <summary>
    /// Trigger episode search
    /// </summary>
    public async Task<bool> SearchEpisodeAsync(List<int> episodeIds, CancellationToken ct = default)
    {
        var request = await CreateRequestAsync("command", Method.Post, ct);
        AddApiKeyHeader(request);

        var command = new
        {
            name = "EpisodeSearch",
            episodeIds
        };

        request.AddJsonBody(command);

        var response = await ArrClientResponse.ExecuteAsync(_client, request, ct);
        return response.IsSuccessful;
    }

    /// <summary>
    /// Update series
    /// </summary>
    public async Task<SonarrSeries?> UpdateSeriesAsync(SonarrSeries series, CancellationToken ct = default)
    {
        var request = await CreateRequestAsync($"series/{series.Id}", Method.Put, ct);
        AddApiKeyHeader(request);

        request.AddJsonBody(series);

        var response = await ArrClientResponse.ExecuteAsync(_client, request, ct);

        if (response.IsSuccessful && !string.IsNullOrEmpty(response.Content))
        {
            return JsonConvert.DeserializeObject<SonarrSeries>(response.Content);
        }

        return null;
    }

    /// <summary>
    /// Get wanted/missing episodes
    /// </summary>
    public async Task<WantedEpisodeResponse> GetWantedAsync(int page = 1, int pageSize = 50, CancellationToken ct = default)
    {
        var request = await CreateRequestAsync("wanted/missing", Method.Get, ct);
        AddApiKeyHeader(request);

        request.AddQueryParameter("page", page.ToString());
        request.AddQueryParameter("pageSize", pageSize.ToString());
        request.AddQueryParameter("sortKey", "airDateUtc");
        request.AddQueryParameter("sortDirection", "descending");

        var response = await ArrClientResponse.ExecuteAsync(_client, request, ct);

        if (response.IsSuccessful && !string.IsNullOrEmpty(response.Content))
        {
            return JsonConvert.DeserializeObject<WantedEpisodeResponse>(response.Content) ?? new WantedEpisodeResponse();
        }

        return new WantedEpisodeResponse();
    }

    /// <summary>
    /// Get download queue
    /// </summary>
    public async Task<SonarrQueueResponse> GetQueueAsync(int page = 1, int pageSize = 1000, CancellationToken ct = default)
    {
        var request = await CreateRequestAsync("queue", Method.Get, ct);
        AddApiKeyHeader(request);

        request.AddQueryParameter("page", page.ToString());
        request.AddQueryParameter("pageSize", pageSize.ToString());
        request.AddQueryParameter("includeUnknownSeriesItems", "true");

        var response = await ArrClientResponse.ExecuteAsync(_client, request, ct);
        ArrClientResponse.EnsureSuccess(response, $"GET /{request.Resource}");

        if (string.IsNullOrEmpty(response.Content))
            return new SonarrQueueResponse();

        var queue = JsonConvert.DeserializeObject<SonarrQueueResponse>(response.Content) ?? new SonarrQueueResponse();
        foreach (var item in queue.Records) item.NormalizeEpisodeIds();
        return queue;
    }

    /// <summary>
    /// Trigger manual import scan for downloaded episodes
    /// </summary>
    public Task<List<SonarrQueueItem>> GetAllQueueAsync(CancellationToken ct = default)
        => ArrQueuePaging.ReadAllAsync(async (page, size) =>
        {
            var response = await GetQueueAsync(page, size, ct);
            return (response.Page, response.PageSize, response.TotalRecords, response.Records);
        }, item => item.Id, ct);

    public async Task<CommandResponse?> TriggerDownloadedEpisodesScanAsync(
        string path,
        string downloadClientId,
        string importMode = "Auto",
        CancellationToken ct = default)
    {
        var request = await CreateRequestAsync("command", Method.Post, ct);
        AddApiKeyHeader(request);

        var command = new
        {
            name = "DownloadedEpisodesScan",
            path = path,
            downloadClientId = downloadClientId.ToUpper(),
            importMode = importMode
        };

        request.AddJsonBody(command);

        var response = await ArrClientResponse.ExecuteAsync(_client, request, ct);

        if (response.IsSuccessful && !string.IsNullOrEmpty(response.Content))
        {
            return JsonConvert.DeserializeObject<CommandResponse>(response.Content);
        }

        return null;
    }

    /// <summary>
    /// Get command status
    /// </summary>
    public async Task<List<CommandStatus>> GetCommandsAsync(CancellationToken ct = default)
    {
        var request = await CreateRequestAsync("command", Method.Get, ct);
        AddApiKeyHeader(request);

        var response = await ArrClientResponse.ExecuteAsync(_client, request, ct);

        ArrClientResponse.EnsureSuccess(response, "GET commands");
        if (string.IsNullOrWhiteSpace(response.Content)) throw new IOException("Arr returned no command inventory.");
        return JsonConvert.DeserializeObject<List<CommandStatus>>(response.Content)
            ?? throw new IOException("Arr returned an invalid command inventory.");
    }

    /// <summary>
    /// Get episode file by ID
    /// </summary>
    public async Task<EpisodeFile?> GetEpisodeFileAsync(int episodeFileId, CancellationToken ct = default)
    {
        var request = await CreateRequestAsync($"episodefile/{episodeFileId}", Method.Get, ct);
        AddApiKeyHeader(request);

        var response = await ArrClientResponse.ExecuteAsync(_client, request, ct);

        if (response.IsSuccessful && !string.IsNullOrEmpty(response.Content))
        {
            return JsonConvert.DeserializeObject<EpisodeFile>(response.Content);
        }

        return null;
    }

    /// <summary>
    /// Delete item from queue
    /// </summary>
    public async Task<bool> DeleteFromQueueAsync(int id, bool removeFromClient = true, bool blocklist = false,
        CancellationToken ct = default, bool skipRedownload = false)
    {
        var request = await CreateRequestAsync($"queue/{id}", Method.Delete, ct);
        AddApiKeyHeader(request);

        request.AddQueryParameter("removeFromClient", removeFromClient.ToString().ToLowerInvariant());
        request.AddQueryParameter("blocklist", blocklist.ToString().ToLowerInvariant());
        request.AddQueryParameter("skipRedownload", skipRedownload.ToString().ToLowerInvariant());
        // Sonarr 5.0.0.2569 binds booleans; 5.0.0.2608 binds enums and ignores the booleans.
        if (request.Resource.Contains("/v5/"))
        {
            request.AddQueryParameter("removalMethod", removeFromClient ? "removeFromClient" : "ignore");
            var blocklistMethod = skipRedownload ? "blocklistOnly" : "blocklistAndSearch";
            request.AddQueryParameter("blocklistMethod", blocklist ? blocklistMethod : "doNotBlocklist");
        }

        var response = await ArrClientResponse.ExecuteAsync(_client, request, ct);
        return response.IsSuccessful;
    }

    /// <summary>
    /// Trigger RSS sync
    /// </summary>
    public async Task<CommandResponse?> RssSyncAsync(CancellationToken ct = default)
    {
        var request = await CreateRequestAsync("command", Method.Post, ct);
        AddApiKeyHeader(request);

        var command = new { name = "RssSync" };
        request.AddJsonBody(command);

        var response = await ArrClientResponse.ExecuteAsync(_client, request, ct);

        if (response.IsSuccessful && !string.IsNullOrEmpty(response.Content))
        {
            return JsonConvert.DeserializeObject<CommandResponse>(response.Content);
        }

        return null;
    }

    /// <summary>
    /// Refresh monitored downloads
    /// </summary>
    public async Task<CommandResponse?> RefreshMonitoredDownloadsAsync(CancellationToken ct = default)
    {
        var request = await CreateRequestAsync("command", Method.Post, ct);
        AddApiKeyHeader(request);

        var command = new { name = "RefreshMonitoredDownloads" };
        request.AddJsonBody(command);

        var response = await ArrClientResponse.ExecuteAsync(_client, request, ct);

        if (response.IsSuccessful && !string.IsNullOrEmpty(response.Content))
        {
            return JsonConvert.DeserializeObject<CommandResponse>(response.Content);
        }

        return null;
    }

    /// <summary>
    /// Update series quality profile, preserving unmapped fields via a raw JSON round-trip.
    /// </summary>
    public async Task<bool> UpdateSeriesQualityProfileAsync(int seriesId, int qualityProfileId, CancellationToken ct = default, int? expectedProfileId = null)
    {
        var request = await CreateRequestAsync($"series/{seriesId}", Method.Get, ct);
        return await ArrQualityProfilePut.UpdateAsync(_client, _apiKey, request.Resource, qualityProfileId, ct, expectedProfileId);
    }

    /// <summary>
    /// Get quality profiles
    /// </summary>
    public async Task<List<QualityProfile>> GetQualityProfilesAsync(CancellationToken ct = default)
    {
        var request = await CreateRequestAsync("qualityprofile", Method.Get, ct);
        AddApiKeyHeader(request);

        var response = await ArrClientResponse.ExecuteAsync(_client, request, ct);

        if (response.IsSuccessful && !string.IsNullOrEmpty(response.Content))
        {
            return JsonConvert.DeserializeObject<List<QualityProfile>>(response.Content) ?? new List<QualityProfile>();
        }

        return new List<QualityProfile>();
    }

    public async Task<List<EpisodeFile>> GetEpisodeFilesAsync(int seriesId, CancellationToken ct = default)
    {
        var request = await CreateRequestAsync("episodefile", Method.Get, ct);
        AddApiKeyHeader(request);
        request.AddQueryParameter("seriesId", seriesId.ToString());

        var response = await ArrClientResponse.ExecuteAsync(_client, request, ct);

        if (response.IsSuccessful && !string.IsNullOrEmpty(response.Content))
        {
            return JsonConvert.DeserializeObject<List<EpisodeFile>>(response.Content) ?? new List<EpisodeFile>();
        }

        return new List<EpisodeFile>();
    }

    public async Task<List<SonarrEpisode>> GetEpisodesWithFilesAsync(int seriesId, CancellationToken ct = default)
    {
        var episodes = await GetEpisodesAsync(seriesId, ct);
        return episodes;
    }

    /// <summary>§6.7: Trigger a full library rescan (RescanSeries command)</summary>
    public async Task<bool> RescanAsync(CancellationToken ct = default)
    {
        var request = await CreateRequestAsync("command", Method.Post, ct);
        AddApiKeyHeader(request);
        request.AddJsonBody(new { name = "RescanSeries" });
        var response = await ArrClientResponse.ExecuteAsync(_client, request, ct);
        return response.IsSuccessful;
    }

    private async Task<RestRequest> CreateRequestAsync(string path, Method method, CancellationToken ct)
    {
        var info = await SonarrApiDiscovery.ResolveAsync(_baseUrl, _apiKey, _skipTlsVerify, ct);
        return new RestRequest($"api/{info.ApiVersion}/{path}", method);
    }

    private void AddApiKeyHeader(RestRequest request)
    {
        request.AddHeader("X-Api-Key", _apiKey);
    }
}

/// <summary>
/// Sonarr series model
/// </summary>
public class SonarrSeries
{
    [JsonProperty("id")]
    public int Id { get; set; }

    [JsonProperty("title")]
    public string Title { get; set; } = "";

    [JsonProperty("monitored")]
    public bool Monitored { get; set; }

    [JsonProperty("qualityProfileId")]
    public int QualityProfileId { get; set; }

    [JsonProperty("path")]
    public string Path { get; set; } = "";

    [JsonProperty("tvdbId")]
    public int TvdbId { get; set; }

    [JsonProperty("statistics")]
    public SeriesStatistics? Statistics { get; set; }

    [JsonProperty("seasons")]
    public List<Season>? Seasons { get; set; }
}

public class Season
{
    [JsonProperty("seasonNumber")]
    public int SeasonNumber { get; set; }

    [JsonProperty("monitored")]
    public bool Monitored { get; set; }
}

public class SeriesStatistics
{
    [JsonProperty("episodeFileCount")]
    public int EpisodeFileCount { get; set; }

    [JsonProperty("episodeCount")]
    public int EpisodeCount { get; set; }

    [JsonProperty("totalEpisodeCount")]
    public int TotalEpisodeCount { get; set; }

    [JsonProperty("sizeOnDisk")]
    public long SizeOnDisk { get; set; }
}

/// <summary>
/// Sonarr episode model
/// </summary>
public class SonarrEpisode
{
    [JsonProperty("id")]
    public int Id { get; set; }

    [JsonProperty("seriesId")]
    public int SeriesId { get; set; }

    [JsonProperty("episodeFileId")]
    public int EpisodeFileId { get; set; }

    [JsonProperty("seasonNumber")]
    public int SeasonNumber { get; set; }

    [JsonProperty("episodeNumber")]
    public int EpisodeNumber { get; set; }

    [JsonProperty("title")]
    public string Title { get; set; } = "";

    [JsonProperty("airDate")]
    public string? AirDate { get; set; }

    [JsonProperty("airDateUtc")]
    public DateTime? AirDateUtc { get; set; }

    [JsonProperty("hasFile")]
    public bool HasFile { get; set; }

    [JsonProperty("monitored")]
    public bool Monitored { get; set; }

    [JsonProperty("absoluteEpisodeNumber")]
    public int? AbsoluteEpisodeNumber { get; set; }

    [JsonProperty("sceneAbsoluteEpisodeNumber")]
    public int? SceneAbsoluteEpisodeNumber { get; set; }

    [JsonProperty("episodeFile")]
    public EpisodeFile? EpisodeFile { get; set; }

    [JsonProperty("qualityProfileId")]
    public int? QualityProfileId { get; set; }
}

public class WantedEpisodeResponse
{
    [JsonProperty("page")]
    public int Page { get; set; }

    [JsonProperty("pageSize")]
    public int PageSize { get; set; }

    [JsonProperty("totalRecords")]
    public int TotalRecords { get; set; }

    [JsonProperty("records")]
    public List<SonarrEpisode> Records { get; set; } = new();
}

public class SonarrQueueResponse
{
    [JsonProperty("page")]
    public int Page { get; set; }

    [JsonProperty("pageSize")]
    public int PageSize { get; set; }

    [JsonProperty("totalRecords")]
    public int TotalRecords { get; set; }

    [JsonProperty("records")]
    public List<SonarrQueueItem> Records { get; set; } = new();
}

public class SonarrQueueItem
{
    [JsonProperty("id")]
    public int Id { get; set; }

    [JsonProperty("seriesId")]
    public int? SeriesId { get; set; }

    [JsonProperty("episodeIds")]
    public List<int> EpisodeIds { get; set; } = new();

    [JsonProperty("seasonNumbers")]
    public List<int> SeasonNumbers { get; set; } = new();

    internal void NormalizeEpisodeIds()
    {
        EpisodeIds ??= new();
        SeasonNumbers ??= new();
        if (EpisodeId is int id && EpisodeIds.Count == 0) EpisodeIds.Add(id);
        if (SeasonNumber is int season && SeasonNumbers.Count == 0) SeasonNumbers.Add(season);
        EpisodeId = EpisodeIds.Count == 1 ? EpisodeIds[0] : null;
        SeasonNumber = SeasonNumbers.Count == 1 ? SeasonNumbers[0] : null;
    }

    [JsonProperty("episodeId")]
    public int? EpisodeId { get; set; }

    [JsonProperty("downloadId")]
    public string? DownloadId { get; set; }

    [JsonProperty("title")]
    public string Title { get; set; } = "";

    [JsonProperty("status")]
    public string Status { get; set; } = "";

    [JsonProperty("customFormatScore")]
    public int? CustomFormatScore { get; set; }

    [JsonProperty("quality")]
    public Quality? Quality { get; set; }

    [JsonProperty("trackedDownloadStatus")]
    public string? TrackedDownloadStatus { get; set; }

    [JsonProperty("trackedDownloadState")]
    public string? TrackedDownloadState { get; set; }

    [JsonProperty("size")]
    public long? Size { get; set; }

    [JsonProperty("sizeleft")]
    public long? SizeLeft { get; set; }

    [JsonProperty("timeleft")]
    public string? TimeLeft { get; set; }

    [JsonProperty("estimatedCompletionTime")]
    public DateTime? EstimatedCompletionTime { get; set; }

    [JsonProperty("added")]
    public DateTime? Added { get; set; }

    [JsonProperty("statusMessages")]
    public List<StatusMessage>? StatusMessages { get; set; }

    [JsonProperty("downloadClient")]
    public string? DownloadClient { get; set; }

    [JsonProperty("seasonNumber")]
    public int? SeasonNumber { get; set; }

    [JsonProperty("episodeNumber")]
    public int? EpisodeNumber { get; set; }

    [JsonProperty("outputPath")]
    public string? OutputPath { get; set; }
}

/// <summary>
/// Episode file model
/// </summary>
public class EpisodeFile
{
    [JsonProperty("id")]
    public int Id { get; set; }

    [JsonProperty("seriesId")]
    public int SeriesId { get; set; }

    [JsonProperty("seasonNumber")]
    public int SeasonNumber { get; set; }

    [JsonProperty("relativePath")]
    public string RelativePath { get; set; } = "";

    [JsonProperty("path")]
    public string Path { get; set; } = "";

    [JsonProperty("size")]
    public long Size { get; set; }

    [JsonProperty("quality")]
    public Quality Quality { get; set; } = new();

    [JsonProperty("customFormats")]
    public List<CustomFormat> CustomFormats { get; set; } = new();

    [JsonProperty("customFormatScore")]
    public int? CustomFormatScore { get; set; }

    [JsonProperty("qualityCutoffNotMet")]
    public bool QualityCutoffNotMet { get; set; }

    [JsonProperty("episodeFileId")]
    public int? EpisodeFileId { get; set; }

    [JsonProperty("dateAdded")]
    public DateTime? DateAdded { get; set; }

    [JsonProperty("releaseGroup")]
    public string? ReleaseGroup { get; set; }
}

// Note: CommandResponse and CommandStatus are shared from RadarrClient.cs

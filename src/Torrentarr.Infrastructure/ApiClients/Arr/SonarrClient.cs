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

    public SonarrClient(string baseUrl, string apiKey)
    {
        _apiKey = apiKey;

        var options = new RestClientOptions(baseUrl.TrimEnd('/'))
        {
            Timeout = TimeSpan.FromSeconds(30)
        };

        _client = new RestClient(options);
    }

    /// <summary>
    /// Get all series
    /// </summary>
    public async Task<List<SonarrSeries>> GetSeriesAsync(CancellationToken ct = default)
    {
        var request = new RestRequest("/api/v3/series", Method.Get);
        AddApiKeyHeader(request);

        var response = await _client.ExecuteAsync(request, ct);

        if (response.IsSuccessful && !string.IsNullOrEmpty(response.Content))
        {
            return JsonConvert.DeserializeObject<List<SonarrSeries>>(response.Content) ?? new List<SonarrSeries>();
        }

        return new List<SonarrSeries>();
    }

    /// <summary>
    /// Get system status/info
    /// </summary>
    public async Task<SystemInfo> GetSystemInfoAsync(CancellationToken ct = default)
    {
        var request = new RestRequest("/api/v3/system/status", Method.Get);
        AddApiKeyHeader(request);

        var response = await _client.ExecuteAsync(request, ct);

        if (response.IsSuccessful && !string.IsNullOrEmpty(response.Content))
        {
            return JsonConvert.DeserializeObject<SystemInfo>(response.Content) ?? new SystemInfo();
        }

        return new SystemInfo();
    }

    /// <summary>
    /// Get series by ID
    /// </summary>
    public async Task<SonarrSeries?> GetSeriesAsync(int seriesId, CancellationToken ct = default)
    {
        var request = new RestRequest($"/api/v3/series/{seriesId}", Method.Get);
        AddApiKeyHeader(request);

        var response = await _client.ExecuteAsync(request, ct);

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
        var request = new RestRequest("/api/v3/episode", Method.Get);
        AddApiKeyHeader(request);
        request.AddQueryParameter("seriesId", seriesId.ToString());
        request.AddQueryParameter("includeEpisodeFile", "true");

        var response = await _client.ExecuteAsync(request, ct);

        if (response.IsSuccessful && !string.IsNullOrEmpty(response.Content))
        {
            return JsonConvert.DeserializeObject<List<SonarrEpisode>>(response.Content) ?? new List<SonarrEpisode>();
        }

        return new List<SonarrEpisode>();
    }

    /// <summary>
    /// Trigger series search
    /// </summary>
    public async Task<bool> SearchSeriesAsync(int seriesId, CancellationToken ct = default)
    {
        var request = new RestRequest("/api/v3/command", Method.Post);
        AddApiKeyHeader(request);

        var command = new
        {
            name = "SeriesSearch",
            seriesId
        };

        request.AddJsonBody(command);

        var response = await _client.ExecuteAsync(request, ct);
        return response.IsSuccessful;
    }

    /// <summary>
    /// Trigger episode search
    /// </summary>
    public async Task<bool> SearchEpisodeAsync(List<int> episodeIds, CancellationToken ct = default)
    {
        var request = new RestRequest("/api/v3/command", Method.Post);
        AddApiKeyHeader(request);

        var command = new
        {
            name = "EpisodeSearch",
            episodeIds
        };

        request.AddJsonBody(command);

        var response = await _client.ExecuteAsync(request, ct);
        return response.IsSuccessful;
    }

    /// <summary>
    /// Update series
    /// </summary>
    public async Task<SonarrSeries?> UpdateSeriesAsync(SonarrSeries series, CancellationToken ct = default)
    {
        var request = new RestRequest($"/api/v3/series/{series.Id}", Method.Put);
        AddApiKeyHeader(request);

        request.AddJsonBody(series);

        var response = await _client.ExecuteAsync(request, ct);

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
        var request = new RestRequest("/api/v3/wanted/missing", Method.Get);
        AddApiKeyHeader(request);

        request.AddQueryParameter("page", page.ToString());
        request.AddQueryParameter("pageSize", pageSize.ToString());
        request.AddQueryParameter("sortKey", "airDateUtc");
        request.AddQueryParameter("sortDirection", "descending");

        var response = await _client.ExecuteAsync(request, ct);

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
        var request = new RestRequest("/api/v3/queue", Method.Get);
        AddApiKeyHeader(request);

        request.AddQueryParameter("page", page.ToString());
        request.AddQueryParameter("pageSize", pageSize.ToString());

        var response = await _client.ExecuteAsync(request, ct);

        if (response.IsSuccessful && !string.IsNullOrEmpty(response.Content))
        {
            return JsonConvert.DeserializeObject<SonarrQueueResponse>(response.Content) ?? new SonarrQueueResponse();
        }

        return new SonarrQueueResponse();
    }

    /// <summary>
    /// Trigger manual import scan for downloaded episodes
    /// </summary>
    public async Task<CommandResponse?> TriggerDownloadedEpisodesScanAsync(
        string path,
        string downloadClientId,
        string importMode = "Auto",
        CancellationToken ct = default)
    {
        var request = new RestRequest("/api/v3/command", Method.Post);
        AddApiKeyHeader(request);

        var command = new
        {
            name = "DownloadedEpisodesScan",
            path = path,
            downloadClientId = downloadClientId.ToUpper(),
            importMode = importMode
        };

        request.AddJsonBody(command);

        var response = await _client.ExecuteAsync(request, ct);

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
        var request = new RestRequest("/api/v3/command", Method.Get);
        AddApiKeyHeader(request);

        var response = await _client.ExecuteAsync(request, ct);

        if (response.IsSuccessful && !string.IsNullOrEmpty(response.Content))
        {
            return JsonConvert.DeserializeObject<List<CommandStatus>>(response.Content) ?? new List<CommandStatus>();
        }

        return new List<CommandStatus>();
    }

    /// <summary>
    /// Get episode file by ID
    /// </summary>
    public async Task<EpisodeFile?> GetEpisodeFileAsync(int episodeFileId, CancellationToken ct = default)
    {
        var request = new RestRequest($"/api/v3/episodefile/{episodeFileId}", Method.Get);
        AddApiKeyHeader(request);

        var response = await _client.ExecuteAsync(request, ct);

        if (response.IsSuccessful && !string.IsNullOrEmpty(response.Content))
        {
            return JsonConvert.DeserializeObject<EpisodeFile>(response.Content);
        }

        return null;
    }

    /// <summary>
    /// Delete item from queue
    /// </summary>
    public async Task<bool> DeleteFromQueueAsync(int id, bool removeFromClient = true, bool blocklist = false, CancellationToken ct = default)
    {
        var request = new RestRequest($"/api/v3/queue/{id}", Method.Delete);
        AddApiKeyHeader(request);

        request.AddQueryParameter("removeFromClient", removeFromClient.ToString().ToLower());
        request.AddQueryParameter("blocklist", blocklist.ToString().ToLower());

        var response = await _client.ExecuteAsync(request, ct);
        return response.IsSuccessful;
    }

    /// <summary>
    /// Trigger RSS sync
    /// </summary>
    public async Task<CommandResponse?> RssSyncAsync(CancellationToken ct = default)
    {
        var request = new RestRequest("/api/v3/command", Method.Post);
        AddApiKeyHeader(request);

        var command = new { name = "RssSync" };
        request.AddJsonBody(command);

        var response = await _client.ExecuteAsync(request, ct);

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
        var request = new RestRequest("/api/v3/command", Method.Post);
        AddApiKeyHeader(request);

        var command = new { name = "RefreshMonitoredDownloads" };
        request.AddJsonBody(command);

        var response = await _client.ExecuteAsync(request, ct);

        if (response.IsSuccessful && !string.IsNullOrEmpty(response.Content))
        {
            return JsonConvert.DeserializeObject<CommandResponse>(response.Content);
        }

        return null;
    }

    /// <summary>
    /// Update series quality profile
    /// </summary>
    public async Task<bool> UpdateSeriesQualityProfileAsync(int seriesId, int qualityProfileId, CancellationToken ct = default)
    {
        var series = await GetSeriesAsync(seriesId, ct);
        if (series == null) return false;

        series.QualityProfileId = qualityProfileId;
        var updated = await UpdateSeriesAsync(series, ct);
        return updated != null;
    }

    /// <summary>
    /// Get quality profiles
    /// </summary>
    public async Task<List<QualityProfile>> GetQualityProfilesAsync(CancellationToken ct = default)
    {
        var request = new RestRequest("/api/v3/qualityprofile", Method.Get);
        AddApiKeyHeader(request);

        var response = await _client.ExecuteAsync(request, ct);

        if (response.IsSuccessful && !string.IsNullOrEmpty(response.Content))
        {
            return JsonConvert.DeserializeObject<List<QualityProfile>>(response.Content) ?? new List<QualityProfile>();
        }

        return new List<QualityProfile>();
    }

    public async Task<List<EpisodeFile>> GetEpisodeFilesAsync(int seriesId, CancellationToken ct = default)
    {
        var request = new RestRequest("/api/v3/episodefile", Method.Get);
        AddApiKeyHeader(request);
        request.AddQueryParameter("seriesId", seriesId.ToString());

        var response = await _client.ExecuteAsync(request, ct);

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
        var request = new RestRequest("/api/v3/command", Method.Post);
        AddApiKeyHeader(request);
        request.AddJsonBody(new { name = "RescanSeries" });
        var response = await _client.ExecuteAsync(request, ct);
        return response.IsSuccessful;
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

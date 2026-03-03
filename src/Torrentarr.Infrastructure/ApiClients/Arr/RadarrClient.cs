using Newtonsoft.Json;
using RestSharp;

namespace Torrentarr.Infrastructure.ApiClients.Arr;

/// <summary>
/// Radarr API client using RestSharp
/// </summary>
public class RadarrClient
{
    private readonly RestClient _client;
    private readonly string _apiKey;

    public RadarrClient(string baseUrl, string apiKey)
    {
        _apiKey = apiKey;

        var options = new RestClientOptions(baseUrl.TrimEnd('/'))
        {
            Timeout = TimeSpan.FromSeconds(30)
        };

        _client = new RestClient(options);
    }

    /// <summary>
    /// Get all movies
    /// </summary>
    public async Task<List<RadarrMovie>> GetMoviesAsync(CancellationToken ct = default)
    {
        var request = new RestRequest("/api/v3/movie", Method.Get);
        AddApiKeyHeader(request);

        var response = await _client.ExecuteAsync(request, ct);

        if (response.IsSuccessful && !string.IsNullOrEmpty(response.Content))
        {
            return JsonConvert.DeserializeObject<List<RadarrMovie>>(response.Content) ?? new List<RadarrMovie>();
        }

        return new List<RadarrMovie>();
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
    /// Get movie by ID
    /// </summary>
    public async Task<RadarrMovie?> GetMovieAsync(int movieId, CancellationToken ct = default)
    {
        var request = new RestRequest($"/api/v3/movie/{movieId}", Method.Get);
        AddApiKeyHeader(request);

        var response = await _client.ExecuteAsync(request, ct);

        if (response.IsSuccessful && !string.IsNullOrEmpty(response.Content))
        {
            return JsonConvert.DeserializeObject<RadarrMovie>(response.Content);
        }

        return null;
    }

    /// <summary>
    /// Trigger movie search
    /// </summary>
    public async Task<bool> SearchMovieAsync(int movieId, CancellationToken ct = default)
    {
        var request = new RestRequest("/api/v3/command", Method.Post);
        AddApiKeyHeader(request);

        var command = new
        {
            name = "MoviesSearch",
            movieIds = new[] { movieId }
        };

        request.AddJsonBody(command);

        var response = await _client.ExecuteAsync(request, ct);
        return response.IsSuccessful;
    }

    /// <summary>
    /// Update movie
    /// </summary>
    public async Task<RadarrMovie?> UpdateMovieAsync(RadarrMovie movie, CancellationToken ct = default)
    {
        var request = new RestRequest($"/api/v3/movie/{movie.Id}", Method.Put);
        AddApiKeyHeader(request);

        request.AddJsonBody(movie);

        var response = await _client.ExecuteAsync(request, ct);

        if (response.IsSuccessful && !string.IsNullOrEmpty(response.Content))
        {
            return JsonConvert.DeserializeObject<RadarrMovie>(response.Content);
        }

        return null;
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

    /// <summary>
    /// Get wanted/missing movies
    /// </summary>
    public async Task<WantedResponse> GetWantedAsync(int page = 1, int pageSize = 50, CancellationToken ct = default)
    {
        var request = new RestRequest("/api/v3/wanted/missing", Method.Get);
        AddApiKeyHeader(request);

        request.AddQueryParameter("page", page.ToString());
        request.AddQueryParameter("pageSize", pageSize.ToString());
        request.AddQueryParameter("sortKey", "title");
        request.AddQueryParameter("sortDirection", "ascending");

        var response = await _client.ExecuteAsync(request, ct);

        if (response.IsSuccessful && !string.IsNullOrEmpty(response.Content))
        {
            return JsonConvert.DeserializeObject<WantedResponse>(response.Content) ?? new WantedResponse();
        }

        return new WantedResponse();
    }

    /// <summary>
    /// Get download queue
    /// </summary>
    public async Task<QueueResponse> GetQueueAsync(int page = 1, int pageSize = 1000, CancellationToken ct = default)
    {
        var request = new RestRequest("/api/v3/queue", Method.Get);
        AddApiKeyHeader(request);

        request.AddQueryParameter("page", page.ToString());
        request.AddQueryParameter("pageSize", pageSize.ToString());

        var response = await _client.ExecuteAsync(request, ct);

        if (response.IsSuccessful && !string.IsNullOrEmpty(response.Content))
        {
            return JsonConvert.DeserializeObject<QueueResponse>(response.Content) ?? new QueueResponse();
        }

        return new QueueResponse();
    }

    /// <summary>
    /// Trigger manual import scan for downloaded movie
    /// </summary>
    public async Task<CommandResponse?> TriggerDownloadedMoviesScanAsync(
        string path,
        string downloadClientId,
        string importMode = "Auto",
        CancellationToken ct = default)
    {
        var request = new RestRequest("/api/v3/command", Method.Post);
        AddApiKeyHeader(request);

        var command = new
        {
            name = "DownloadedMoviesScan",
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
    /// Get movie file by ID
    /// </summary>
    public async Task<MovieFile?> GetMovieFileAsync(int movieFileId, CancellationToken ct = default)
    {
        var request = new RestRequest($"/api/v3/moviefile/{movieFileId}", Method.Get);
        AddApiKeyHeader(request);

        var response = await _client.ExecuteAsync(request, ct);

        if (response.IsSuccessful && !string.IsNullOrEmpty(response.Content))
        {
            return JsonConvert.DeserializeObject<MovieFile>(response.Content);
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
    /// Update movie quality profile
    /// </summary>
    public async Task<bool> UpdateMovieQualityProfileAsync(int movieId, int qualityProfileId, CancellationToken ct = default)
    {
        var movie = await GetMovieAsync(movieId, ct);
        if (movie == null) return false;

        movie.QualityProfileId = qualityProfileId;
        var updated = await UpdateMovieAsync(movie, ct);
        return updated != null;
    }

    /// <summary>
    /// Get custom formats
    /// </summary>
    public async Task<List<CustomFormat>> GetCustomFormatsAsync(CancellationToken ct = default)
    {
        var request = new RestRequest("/api/v3/customformat", Method.Get);
        AddApiKeyHeader(request);

        var response = await _client.ExecuteAsync(request, ct);

        if (response.IsSuccessful && !string.IsNullOrEmpty(response.Content))
        {
            return JsonConvert.DeserializeObject<List<CustomFormat>>(response.Content) ?? new List<CustomFormat>();
        }

        return new List<CustomFormat>();
    }

    public async Task<List<MovieFile>> GetMovieFilesAsync(int movieId, CancellationToken ct = default)
    {
        var request = new RestRequest("/api/v3/moviefile", Method.Get);
        AddApiKeyHeader(request);
        request.AddQueryParameter("movieId", movieId.ToString());

        var response = await _client.ExecuteAsync(request, ct);

        if (response.IsSuccessful && !string.IsNullOrEmpty(response.Content))
        {
            return JsonConvert.DeserializeObject<List<MovieFile>>(response.Content) ?? new List<MovieFile>();
        }

        return new List<MovieFile>();
    }

    public async Task<List<RadarrMovie>> GetMoviesWithFilesAsync(CancellationToken ct = default)
    {
        var request = new RestRequest("/api/v3/movie", Method.Get);
        AddApiKeyHeader(request);
        request.AddQueryParameter("includeMovieImages", "false");

        var response = await _client.ExecuteAsync(request, ct);

        if (response.IsSuccessful && !string.IsNullOrEmpty(response.Content))
        {
            return JsonConvert.DeserializeObject<List<RadarrMovie>>(response.Content) ?? new List<RadarrMovie>();
        }

        return new List<RadarrMovie>();
    }

    /// <summary>§6.7: Trigger a full library rescan (RescanMovie command)</summary>
    public async Task<bool> RescanAsync(CancellationToken ct = default)
    {
        var request = new RestRequest("/api/v3/command", Method.Post);
        AddApiKeyHeader(request);
        request.AddJsonBody(new { name = "RescanMovie" });
        var response = await _client.ExecuteAsync(request, ct);
        return response.IsSuccessful;
    }

    private void AddApiKeyHeader(RestRequest request)
    {
        request.AddHeader("X-Api-Key", _apiKey);
    }
}

/// <summary>
/// Radarr movie model
/// </summary>
public class RadarrMovie
{
    [JsonProperty("id")]
    public int Id { get; set; }

    [JsonProperty("title")]
    public string Title { get; set; } = "";

    [JsonProperty("year")]
    public int Year { get; set; }

    [JsonProperty("tmdbId")]
    public int TmdbId { get; set; }

    [JsonProperty("monitored")]
    public bool Monitored { get; set; }

    [JsonProperty("hasFile")]
    public bool HasFile { get; set; }

    [JsonProperty("qualityProfileId")]
    public int QualityProfileId { get; set; }

    [JsonProperty("movieFile")]
    public MovieFile? MovieFile { get; set; }

    [JsonProperty("path")]
    public string Path { get; set; } = "";

    [JsonProperty("movieFileId")]
    public int MovieFileId { get; set; }

    [JsonProperty("inCinemas")]
    public DateTime? InCinemas { get; set; }

    [JsonProperty("digitalRelease")]
    public DateTime? DigitalRelease { get; set; }

    [JsonProperty("physicalRelease")]
    public DateTime? PhysicalRelease { get; set; }

    [JsonProperty("minimumAvailability")]
    public string? MinimumAvailability { get; set; }
}

public class MovieFile
{
    [JsonProperty("id")]
    public int Id { get; set; }

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

    [JsonProperty("movieId")]
    public int? MovieId { get; set; }

    [JsonProperty("dateAdded")]
    public DateTime? DateAdded { get; set; }

    [JsonProperty("releaseGroup")]
    public string? ReleaseGroup { get; set; }
}

public class Quality
{
    [JsonProperty("quality")]
    public QualityDefinition QualityDefinition { get; set; } = new();
}

public class QualityDefinition
{
    [JsonProperty("id")]
    public int Id { get; set; }

    [JsonProperty("name")]
    public string Name { get; set; } = "";
}

public class CustomFormat
{
    [JsonProperty("id")]
    public int Id { get; set; }

    [JsonProperty("name")]
    public string Name { get; set; } = "";
}

public class QualityProfile
{
    [JsonProperty("id")]
    public int Id { get; set; }

    [JsonProperty("name")]
    public string Name { get; set; } = "";

    [JsonProperty("minCustomFormatScore")]
    public int? MinCustomFormatScore { get; set; }

    [JsonProperty("cutoff")]
    public int? Cutoff { get; set; }

    [JsonProperty("items")]
    public List<QualityProfileItem>? Items { get; set; }
}

public class QualityProfileItem
{
    [JsonProperty("quality")]
    public QualityDefinition? Quality { get; set; }

    [JsonProperty("allowed")]
    public bool Allowed { get; set; }
}

public class WantedResponse
{
    [JsonProperty("page")]
    public int Page { get; set; }

    [JsonProperty("pageSize")]
    public int PageSize { get; set; }

    [JsonProperty("totalRecords")]
    public int TotalRecords { get; set; }

    [JsonProperty("records")]
    public List<RadarrMovie> Records { get; set; } = new();
}

public class QueueResponse
{
    [JsonProperty("page")]
    public int Page { get; set; }

    [JsonProperty("pageSize")]
    public int PageSize { get; set; }

    [JsonProperty("totalRecords")]
    public int TotalRecords { get; set; }

    [JsonProperty("records")]
    public List<QueueItem> Records { get; set; } = new();
}

public class QueueItem
{
    [JsonProperty("id")]
    public int Id { get; set; }

    [JsonProperty("movieId")]
    public int? MovieId { get; set; }

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

    [JsonProperty("downloadClientHasPostImportCategory")]
    public bool? DownloadClientHasPostImportCategory { get; set; }
}

public class StatusMessage
{
    [JsonProperty("title")]
    public string? Title { get; set; }

    [JsonProperty("messages")]
    public List<string>? Messages { get; set; }
}

public class CommandResponse
{
    [JsonProperty("id")]
    public int Id { get; set; }

    [JsonProperty("name")]
    public string Name { get; set; } = "";

    [JsonProperty("commandName")]
    public string CommandName { get; set; } = "";

    [JsonProperty("status")]
    public string Status { get; set; } = "";

    [JsonProperty("queued")]
    public DateTime Queued { get; set; }

    [JsonProperty("started")]
    public DateTime? Started { get; set; }

    [JsonProperty("ended")]
    public DateTime? Ended { get; set; }
}

public class CommandStatus
{
    [JsonProperty("id")]
    public int Id { get; set; }

    [JsonProperty("name")]
    public string Name { get; set; } = "";

    [JsonProperty("status")]
    public string Status { get; set; } = "";
}

public class SystemInfo
{
    [JsonProperty("version")]
    public string? Version { get; set; }

    [JsonProperty("branch")]
    public string? Branch { get; set; }

    [JsonProperty("appName")]
    public string? AppName { get; set; }

    [JsonProperty("instanceName")]
    public string? InstanceName { get; set; }
}

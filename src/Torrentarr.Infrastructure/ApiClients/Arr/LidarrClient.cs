using Newtonsoft.Json;
using RestSharp;

namespace Torrentarr.Infrastructure.ApiClients.Arr;

/// <summary>
/// Lidarr API client using RestSharp
/// </summary>
public class LidarrClient
{
    private readonly RestClient _client;
    private readonly string _apiKey;

    public LidarrClient(string baseUrl, string apiKey)
    {
        _apiKey = apiKey;

        var options = new RestClientOptions(baseUrl.TrimEnd('/'))
        {
            Timeout = TimeSpan.FromSeconds(30)
        };

        _client = new RestClient(options);
    }

    /// <summary>
    /// Get all artists
    /// </summary>
    public async Task<List<LidarrArtist>> GetArtistsAsync(CancellationToken ct = default)
    {
        var request = new RestRequest("/api/v1/artist", Method.Get);
        AddApiKeyHeader(request);

        var response = await _client.ExecuteAsync(request, ct);

        if (response.IsSuccessful && !string.IsNullOrEmpty(response.Content))
        {
            return JsonConvert.DeserializeObject<List<LidarrArtist>>(response.Content) ?? new List<LidarrArtist>();
        }

        return new List<LidarrArtist>();
    }

    /// <summary>
    /// Get system status/info
    /// </summary>
    public async Task<SystemInfo> GetSystemInfoAsync(CancellationToken ct = default)
    {
        var request = new RestRequest("/api/v1/system/status", Method.Get);
        AddApiKeyHeader(request);

        var response = await _client.ExecuteAsync(request, ct);

        if (response.IsSuccessful && !string.IsNullOrEmpty(response.Content))
        {
            return JsonConvert.DeserializeObject<SystemInfo>(response.Content) ?? new SystemInfo();
        }

        return new SystemInfo();
    }

    /// <summary>
    /// Get artist by ID
    /// </summary>
    public async Task<LidarrArtist?> GetArtistAsync(int artistId, CancellationToken ct = default)
    {
        var request = new RestRequest($"/api/v1/artist/{artistId}", Method.Get);
        AddApiKeyHeader(request);

        var response = await _client.ExecuteAsync(request, ct);

        if (response.IsSuccessful && !string.IsNullOrEmpty(response.Content))
        {
            return JsonConvert.DeserializeObject<LidarrArtist>(response.Content);
        }

        return null;
    }

    /// <summary>
    /// Get albums for an artist
    /// </summary>
    public async Task<List<LidarrAlbum>> GetAlbumsAsync(int? artistId = null, CancellationToken ct = default)
    {
        var request = new RestRequest("/api/v1/album", Method.Get);
        AddApiKeyHeader(request);

        if (artistId.HasValue)
            request.AddQueryParameter("artistId", artistId.Value.ToString());

        var response = await _client.ExecuteAsync(request, ct);

        if (response.IsSuccessful && !string.IsNullOrEmpty(response.Content))
        {
            return JsonConvert.DeserializeObject<List<LidarrAlbum>>(response.Content) ?? new List<LidarrAlbum>();
        }

        return new List<LidarrAlbum>();
    }

    /// <summary>
    /// Trigger artist search
    /// </summary>
    public async Task<bool> SearchArtistAsync(int artistId, CancellationToken ct = default)
    {
        var request = new RestRequest("/api/v1/command", Method.Post);
        AddApiKeyHeader(request);

        var command = new
        {
            name = "ArtistSearch",
            artistId
        };

        request.AddJsonBody(command);

        var response = await _client.ExecuteAsync(request, ct);
        return response.IsSuccessful;
    }

    /// <summary>
    /// Trigger album search
    /// </summary>
    public async Task<bool> SearchAlbumAsync(List<int> albumIds, CancellationToken ct = default)
    {
        var request = new RestRequest("/api/v1/command", Method.Post);
        AddApiKeyHeader(request);

        var command = new
        {
            name = "AlbumSearch",
            albumIds
        };

        request.AddJsonBody(command);

        var response = await _client.ExecuteAsync(request, ct);
        return response.IsSuccessful;
    }

    /// <summary>
    /// Update artist
    /// </summary>
    public async Task<LidarrArtist?> UpdateArtistAsync(LidarrArtist artist, CancellationToken ct = default)
    {
        var request = new RestRequest($"/api/v1/artist/{artist.Id}", Method.Put);
        AddApiKeyHeader(request);

        request.AddJsonBody(artist);

        var response = await _client.ExecuteAsync(request, ct);

        if (response.IsSuccessful && !string.IsNullOrEmpty(response.Content))
        {
            return JsonConvert.DeserializeObject<LidarrArtist>(response.Content);
        }

        return null;
    }

    /// <summary>
    /// Update an artist's quality profile (§1.2 UseTempForMissing).
    /// Fetches the artist, swaps qualityProfileId, then PUTs it back.
    /// </summary>
    public async Task<bool> UpdateArtistQualityProfileAsync(int artistId, int qualityProfileId, CancellationToken ct = default)
    {
        var artist = await GetArtistAsync(artistId, ct);
        if (artist == null)
            return false;

        artist.QualityProfileId = qualityProfileId;
        var updated = await UpdateArtistAsync(artist, ct);
        return updated != null;
    }

    /// <summary>
    /// Get wanted/missing albums
    /// </summary>
    public async Task<WantedAlbumResponse> GetWantedAsync(int page = 1, int pageSize = 50, CancellationToken ct = default)
    {
        var request = new RestRequest("/api/v1/wanted/missing", Method.Get);
        AddApiKeyHeader(request);

        request.AddQueryParameter("page", page.ToString());
        request.AddQueryParameter("pageSize", pageSize.ToString());

        var response = await _client.ExecuteAsync(request, ct);

        if (response.IsSuccessful && !string.IsNullOrEmpty(response.Content))
        {
            return JsonConvert.DeserializeObject<WantedAlbumResponse>(response.Content) ?? new WantedAlbumResponse();
        }

        return new WantedAlbumResponse();
    }

    /// <summary>
    /// Get download queue
    /// </summary>
    public async Task<LidarrQueueResponse> GetQueueAsync(int page = 1, int pageSize = 1000, CancellationToken ct = default)
    {
        var request = new RestRequest("/api/v1/queue", Method.Get);
        AddApiKeyHeader(request);

        request.AddQueryParameter("page", page.ToString());
        request.AddQueryParameter("pageSize", pageSize.ToString());

        var response = await _client.ExecuteAsync(request, ct);

        if (response.IsSuccessful && !string.IsNullOrEmpty(response.Content))
        {
            return JsonConvert.DeserializeObject<LidarrQueueResponse>(response.Content) ?? new LidarrQueueResponse();
        }

        return new LidarrQueueResponse();
    }

    /// <summary>
    /// Trigger manual import scan for downloaded albums
    /// </summary>
    public async Task<CommandResponse?> TriggerDownloadedAlbumsScanAsync(
        string path,
        string downloadClientId,
        string importMode = "Auto",
        CancellationToken ct = default)
    {
        var request = new RestRequest("/api/v1/command", Method.Post);
        AddApiKeyHeader(request);

        var command = new
        {
            name = "DownloadedAlbumsScan",
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
        var request = new RestRequest("/api/v1/command", Method.Get);
        AddApiKeyHeader(request);

        var response = await _client.ExecuteAsync(request, ct);

        if (response.IsSuccessful && !string.IsNullOrEmpty(response.Content))
        {
            return JsonConvert.DeserializeObject<List<CommandStatus>>(response.Content) ?? new List<CommandStatus>();
        }

        return new List<CommandStatus>();
    }

    /// <summary>
    /// Get track file by ID
    /// </summary>
    public async Task<TrackFile?> GetTrackFileAsync(int trackFileId, CancellationToken ct = default)
    {
        var request = new RestRequest($"/api/v1/trackfile/{trackFileId}", Method.Get);
        AddApiKeyHeader(request);

        var response = await _client.ExecuteAsync(request, ct);

        if (response.IsSuccessful && !string.IsNullOrEmpty(response.Content))
        {
            return JsonConvert.DeserializeObject<TrackFile>(response.Content);
        }

        return null;
    }

    /// <summary>
    /// Get tracks for an album
    /// </summary>
    public async Task<List<Track>> GetTracksAsync(int? albumId = null, CancellationToken ct = default)
    {
        var request = new RestRequest("/api/v1/track", Method.Get);
        AddApiKeyHeader(request);

        if (albumId.HasValue)
            request.AddQueryParameter("albumId", albumId.Value.ToString());

        var response = await _client.ExecuteAsync(request, ct);

        if (response.IsSuccessful && !string.IsNullOrEmpty(response.Content))
        {
            return JsonConvert.DeserializeObject<List<Track>>(response.Content) ?? new List<Track>();
        }

        return new List<Track>();
    }

    /// <summary>
    /// Delete item from queue
    /// </summary>
    public async Task<bool> DeleteFromQueueAsync(int id, bool removeFromClient = true, bool blocklist = false, CancellationToken ct = default)
    {
        var request = new RestRequest($"/api/v1/queue/{id}", Method.Delete);
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
        var request = new RestRequest("/api/v1/command", Method.Post);
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
        var request = new RestRequest("/api/v1/command", Method.Post);
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
    /// Get quality profiles
    /// </summary>
    public async Task<List<QualityProfile>> GetQualityProfilesAsync(CancellationToken ct = default)
    {
        var request = new RestRequest("/api/v1/qualityprofile", Method.Get);
        AddApiKeyHeader(request);

        var response = await _client.ExecuteAsync(request, ct);

        if (response.IsSuccessful && !string.IsNullOrEmpty(response.Content))
        {
            return JsonConvert.DeserializeObject<List<QualityProfile>>(response.Content) ?? new List<QualityProfile>();
        }

        return new List<QualityProfile>();
    }

    public async Task<List<TrackFile>> GetTrackFilesByAlbumAsync(int albumId, CancellationToken ct = default)
    {
        var request = new RestRequest("/api/v1/trackfile", Method.Get);
        AddApiKeyHeader(request);
        request.AddQueryParameter("albumId", albumId.ToString());

        var response = await _client.ExecuteAsync(request, ct);

        if (response.IsSuccessful && !string.IsNullOrEmpty(response.Content))
        {
            return JsonConvert.DeserializeObject<List<TrackFile>>(response.Content) ?? new List<TrackFile>();
        }

        return new List<TrackFile>();
    }

    /// <summary>§6.7: Trigger a full library rescan (RescanArtist command)</summary>
    public async Task<bool> RescanAsync(CancellationToken ct = default)
    {
        var request = new RestRequest("/api/v1/command", Method.Post);
        AddApiKeyHeader(request);
        request.AddJsonBody(new { name = "RescanArtist" });
        var response = await _client.ExecuteAsync(request, ct);
        return response.IsSuccessful;
    }

    private void AddApiKeyHeader(RestRequest request)
    {
        request.AddHeader("X-Api-Key", _apiKey);
    }
}

/// <summary>
/// Lidarr artist model
/// </summary>
public class LidarrArtist
{
    [JsonProperty("id")]
    public int Id { get; set; }

    [JsonProperty("artistName")]
    public string ArtistName { get; set; } = "";

    [JsonProperty("foreignArtistId")]
    public string ForeignArtistId { get; set; } = "";

    [JsonProperty("monitored")]
    public bool Monitored { get; set; }

    [JsonProperty("qualityProfileId")]
    public int QualityProfileId { get; set; }

    [JsonProperty("path")]
    public string Path { get; set; } = "";

    [JsonProperty("statistics")]
    public ArtistStatistics? Statistics { get; set; }
}

public class ArtistStatistics
{
    [JsonProperty("albumCount")]
    public int AlbumCount { get; set; }

    [JsonProperty("trackFileCount")]
    public int TrackFileCount { get; set; }

    [JsonProperty("trackCount")]
    public int TrackCount { get; set; }

    [JsonProperty("totalTrackCount")]
    public int TotalTrackCount { get; set; }

    [JsonProperty("sizeOnDisk")]
    public long SizeOnDisk { get; set; }
}

/// <summary>
/// Lidarr album model
/// </summary>
public class LidarrAlbum
{
    [JsonProperty("id")]
    public int Id { get; set; }

    [JsonProperty("title")]
    public string Title { get; set; } = "";

    [JsonProperty("foreignAlbumId")]
    public string ForeignAlbumId { get; set; } = "";

    [JsonProperty("artistId")]
    public int ArtistId { get; set; }

    [JsonProperty("monitored")]
    public bool Monitored { get; set; }

    [JsonProperty("releaseDate")]
    public DateTime? ReleaseDate { get; set; }

    [JsonProperty("statistics")]
    public AlbumStatistics? Statistics { get; set; }

    [JsonProperty("qualityProfileId")]
    public int? QualityProfileId { get; set; }

    [JsonProperty("albumType")]
    public string? AlbumType { get; set; }
}

public class AlbumStatistics
{
    [JsonProperty("trackFileCount")]
    public int TrackFileCount { get; set; }

    [JsonProperty("trackCount")]
    public int TrackCount { get; set; }

    [JsonProperty("totalTrackCount")]
    public int TotalTrackCount { get; set; }

    [JsonProperty("sizeOnDisk")]
    public long SizeOnDisk { get; set; }

    [JsonProperty("percentOfTracks")]
    public double PercentOfTracks { get; set; }
}

public class WantedAlbumResponse
{
    [JsonProperty("page")]
    public int Page { get; set; }

    [JsonProperty("pageSize")]
    public int PageSize { get; set; }

    [JsonProperty("totalRecords")]
    public int TotalRecords { get; set; }

    [JsonProperty("records")]
    public List<LidarrAlbum> Records { get; set; } = new();
}

public class LidarrQueueResponse
{
    [JsonProperty("page")]
    public int Page { get; set; }

    [JsonProperty("pageSize")]
    public int PageSize { get; set; }

    [JsonProperty("totalRecords")]
    public int TotalRecords { get; set; }

    [JsonProperty("records")]
    public List<LidarrQueueItem> Records { get; set; } = new();
}

public class LidarrQueueItem
{
    [JsonProperty("id")]
    public int Id { get; set; }

    [JsonProperty("albumId")]
    public int? AlbumId { get; set; }

    [JsonProperty("downloadId")]
    public string? DownloadId { get; set; }

    [JsonProperty("title")]
    public string Title { get; set; } = "";

    [JsonProperty("status")]
    public string Status { get; set; } = "";

    [JsonProperty("customFormatScore")]
    public int? CustomFormatScore { get; set; }

    [JsonProperty("trackedDownloadStatus")]
    public string? TrackedDownloadStatus { get; set; }

    [JsonProperty("trackedDownloadState")]
    public string? TrackedDownloadState { get; set; }

    [JsonProperty("statusMessages")]
    public List<StatusMessage>? StatusMessages { get; set; }
}

/// <summary>
/// Track file model
/// </summary>
public class TrackFile
{
    [JsonProperty("id")]
    public int Id { get; set; }

    [JsonProperty("albumId")]
    public int AlbumId { get; set; }

    [JsonProperty("artistId")]
    public int ArtistId { get; set; }

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
}

/// <summary>
/// Track model
/// </summary>
public class Track
{
    [JsonProperty("id")]
    public int Id { get; set; }

    [JsonProperty("albumId")]
    public int AlbumId { get; set; }

    [JsonProperty("artistId")]
    public int ArtistId { get; set; }

    [JsonProperty("trackNumber")]
    public int TrackNumber { get; set; }

    [JsonProperty("title")]
    public string Title { get; set; } = "";

    [JsonProperty("duration")]
    public int? Duration { get; set; }

    [JsonProperty("hasFile")]
    public bool HasFile { get; set; }

    [JsonProperty("trackFileId")]
    public int? TrackFileId { get; set; }

    [JsonProperty("monitored")]
    public bool Monitored { get; set; }
}

// Note: CommandResponse and CommandStatus are shared from RadarrClient.cs

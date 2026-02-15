using Newtonsoft.Json;
using RestSharp;

namespace Commandarr.Infrastructure.ApiClients.Arr;

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

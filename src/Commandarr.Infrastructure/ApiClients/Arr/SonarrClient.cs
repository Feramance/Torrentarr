using Newtonsoft.Json;
using RestSharp;

namespace Commandarr.Infrastructure.ApiClients.Arr;

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

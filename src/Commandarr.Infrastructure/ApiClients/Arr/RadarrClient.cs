using Newtonsoft.Json;
using RestSharp;

namespace Commandarr.Infrastructure.ApiClients.Arr;

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
}

public class MovieFile
{
    [JsonProperty("id")]
    public int Id { get; set; }

    [JsonProperty("relativePath")]
    public string RelativePath { get; set; } = "";

    [JsonProperty("quality")]
    public Quality Quality { get; set; } = new();

    [JsonProperty("customFormats")]
    public List<CustomFormat> CustomFormats { get; set; } = new();
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

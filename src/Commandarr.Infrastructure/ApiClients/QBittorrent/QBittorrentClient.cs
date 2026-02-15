using Newtonsoft.Json;
using RestSharp;
using RestSharp.Authenticators;

namespace Commandarr.Infrastructure.ApiClients.QBittorrent;

/// <summary>
/// qBittorrent WebUI API client using RestSharp
/// </summary>
public class QBittorrentClient
{
    private readonly RestClient _client;
    private readonly string _host;
    private readonly int _port;
    private readonly string _username;
    private readonly string _password;
    private string? _cookie;

    public QBittorrentClient(string host, int port, string username, string password)
    {
        _host = host;
        _port = port;
        _username = username;
        _password = password;

        var options = new RestClientOptions($"http://{host}:{port}")
        {
            Timeout = TimeSpan.FromSeconds(30)
        };

        _client = new RestClient(options);
    }

    /// <summary>
    /// Authenticate with qBittorrent
    /// </summary>
    public async Task<bool> LoginAsync(CancellationToken ct = default)
    {
        var request = new RestRequest("/api/v2/auth/login", Method.Post);
        request.AddParameter("username", _username);
        request.AddParameter("password", _password);

        var response = await _client.ExecuteAsync(request, ct);

        if (response.IsSuccessful && response.Content == "Ok.")
        {
            // Extract cookie from response
            var cookie = response.Cookies?.FirstOrDefault(c => c.Name == "SID");
            if (cookie != null)
            {
                _cookie = $"{cookie.Name}={cookie.Value}";
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Get application version
    /// </summary>
    public async Task<string> GetVersionAsync(CancellationToken ct = default)
    {
        var request = new RestRequest("/api/v2/app/version", Method.Get);
        AddAuthCookie(request);

        var response = await _client.ExecuteAsync(request, ct);
        return response.Content ?? "";
    }

    /// <summary>
    /// Get all torrents
    /// </summary>
    public async Task<List<TorrentInfo>> GetTorrentsAsync(string? category = null, CancellationToken ct = default)
    {
        var request = new RestRequest("/api/v2/torrents/info", Method.Get);
        AddAuthCookie(request);

        if (!string.IsNullOrEmpty(category))
            request.AddQueryParameter("category", category);

        var response = await _client.ExecuteAsync(request, ct);

        if (response.IsSuccessful && !string.IsNullOrEmpty(response.Content))
        {
            return JsonConvert.DeserializeObject<List<TorrentInfo>>(response.Content) ?? new List<TorrentInfo>();
        }

        return new List<TorrentInfo>();
    }

    /// <summary>
    /// Add torrent from URL
    /// </summary>
    public async Task<bool> AddTorrentAsync(string url, string? category = null, string? savePath = null, CancellationToken ct = default)
    {
        var request = new RestRequest("/api/v2/torrents/add", Method.Post);
        AddAuthCookie(request);

        request.AddParameter("urls", url);
        if (!string.IsNullOrEmpty(category))
            request.AddParameter("category", category);
        if (!string.IsNullOrEmpty(savePath))
            request.AddParameter("savepath", savePath);

        var response = await _client.ExecuteAsync(request, ct);
        return response.IsSuccessful && response.Content == "Ok.";
    }

    /// <summary>
    /// Delete torrents
    /// </summary>
    public async Task<bool> DeleteTorrentsAsync(List<string> hashes, bool deleteFiles = false, CancellationToken ct = default)
    {
        var request = new RestRequest("/api/v2/torrents/delete", Method.Post);
        AddAuthCookie(request);

        request.AddParameter("hashes", string.Join("|", hashes));
        request.AddParameter("deleteFiles", deleteFiles.ToString().ToLower());

        var response = await _client.ExecuteAsync(request, ct);
        return response.IsSuccessful;
    }

    /// <summary>
    /// Pause torrents
    /// </summary>
    public async Task<bool> PauseTorrentsAsync(List<string> hashes, CancellationToken ct = default)
    {
        var request = new RestRequest("/api/v2/torrents/pause", Method.Post);
        AddAuthCookie(request);

        request.AddParameter("hashes", string.Join("|", hashes));

        var response = await _client.ExecuteAsync(request, ct);
        return response.IsSuccessful;
    }

    /// <summary>
    /// Resume torrents
    /// </summary>
    public async Task<bool> ResumeTorrentsAsync(List<string> hashes, CancellationToken ct = default)
    {
        var request = new RestRequest("/api/v2/torrents/resume", Method.Post);
        AddAuthCookie(request);

        request.AddParameter("hashes", string.Join("|", hashes));

        var response = await _client.ExecuteAsync(request, ct);
        return response.IsSuccessful;
    }

    /// <summary>
    /// Set torrent category
    /// </summary>
    public async Task<bool> SetCategoryAsync(List<string> hashes, string category, CancellationToken ct = default)
    {
        var request = new RestRequest("/api/v2/torrents/setCategory", Method.Post);
        AddAuthCookie(request);

        request.AddParameter("hashes", string.Join("|", hashes));
        request.AddParameter("category", category);

        var response = await _client.ExecuteAsync(request, ct);
        return response.IsSuccessful;
    }

    /// <summary>
    /// Get all categories
    /// </summary>
    public async Task<Dictionary<string, CategoryInfo>> GetCategoriesAsync(CancellationToken ct = default)
    {
        var request = new RestRequest("/api/v2/torrents/categories", Method.Get);
        AddAuthCookie(request);

        var response = await _client.ExecuteAsync(request, ct);

        if (response.IsSuccessful && !string.IsNullOrEmpty(response.Content))
        {
            return JsonConvert.DeserializeObject<Dictionary<string, CategoryInfo>>(response.Content)
                   ?? new Dictionary<string, CategoryInfo>();
        }

        return new Dictionary<string, CategoryInfo>();
    }

    private void AddAuthCookie(RestRequest request)
    {
        if (!string.IsNullOrEmpty(_cookie))
        {
            request.AddHeader("Cookie", _cookie);
        }
    }
}

/// <summary>
/// Torrent information from qBittorrent API
/// </summary>
public class TorrentInfo
{
    [JsonProperty("hash")]
    public string Hash { get; set; } = "";

    [JsonProperty("name")]
    public string Name { get; set; } = "";

    [JsonProperty("size")]
    public long Size { get; set; }

    [JsonProperty("progress")]
    public double Progress { get; set; }

    [JsonProperty("state")]
    public string State { get; set; } = "";

    [JsonProperty("category")]
    public string Category { get; set; } = "";

    [JsonProperty("ratio")]
    public double Ratio { get; set; }

    [JsonProperty("seeding_time")]
    public long SeedingTime { get; set; }

    [JsonProperty("added_on")]
    public long AddedOn { get; set; }

    [JsonProperty("completion_on")]
    public long CompletionOn { get; set; }

    [JsonProperty("save_path")]
    public string SavePath { get; set; } = "";

    [JsonProperty("tracker")]
    public string Tracker { get; set; } = "";
}

/// <summary>
/// Category information from qBittorrent API
/// </summary>
public class CategoryInfo
{
    [JsonProperty("name")]
    public string Name { get; set; } = "";

    [JsonProperty("savePath")]
    public string SavePath { get; set; } = "";
}

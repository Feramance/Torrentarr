using Torrentarr.Core.Models;
using Newtonsoft.Json;
using RestSharp;
using RestSharp.Authenticators;

namespace Torrentarr.Infrastructure.ApiClients.QBittorrent;

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

        // Host may already include a protocol prefix (e.g. "http://192.168.0.240")
        // Avoid double-prefix like "http://http://..."
        string baseUrl;
        if (host.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
            host.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            baseUrl = $"{host}:{port}";
        else
            baseUrl = $"http://{host}:{port}";

        var options = new RestClientOptions(baseUrl)
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
    public async Task<List<TorrentInfo>> GetTorrentsAsync(string? category = null, string? sort = null, CancellationToken cancellationToken = default)
    {
        var request = new RestRequest("/api/v2/torrents/info", Method.Get);
        AddAuthCookie(request);

        if (!string.IsNullOrEmpty(category))
            request.AddQueryParameter("category", category);
        if (!string.IsNullOrEmpty(sort))
            request.AddQueryParameter("sort", sort);

        var response = await _client.ExecuteAsync(request, cancellationToken);

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
    /// Pause a single torrent
    /// </summary>
    public async Task<bool> PauseTorrentAsync(string hash, CancellationToken ct = default)
    {
        return await PauseTorrentsAsync(new List<string> { hash }, ct);
    }

    /// <summary>
    /// Resume a single torrent
    /// </summary>
    public async Task<bool> ResumeTorrentAsync(string hash, CancellationToken ct = default)
    {
        return await ResumeTorrentsAsync(new List<string> { hash }, ct);
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

    /// <summary>
    /// Add tags to torrents
    /// </summary>
    public async Task<bool> AddTagsAsync(List<string> hashes, List<string> tags, CancellationToken ct = default)
    {
        var request = new RestRequest("api/v2/torrents/addTags", Method.Post);
        AddAuthCookie(request);

        request.AddParameter("hashes", string.Join("|", hashes));
        request.AddParameter("tags", string.Join(",", tags));

        var response = await _client.ExecuteAsync(request, ct);
        return response.IsSuccessful;
    }

    /// <summary>
    /// Remove tags from torrents
    /// </summary>
    public async Task<bool> RemoveTagsAsync(List<string> hashes, List<string> tags, CancellationToken ct = default)
    {
        var request = new RestRequest("api/v2/torrents/removeTags", Method.Post);
        AddAuthCookie(request);

        request.AddParameter("hashes", string.Join("|", hashes));
        request.AddParameter("tags", string.Join(",", tags));

        var response = await _client.ExecuteAsync(request, ct);
        return response.IsSuccessful;
    }

    /// <summary>
    /// Create tags in qBittorrent
    /// </summary>
    public async Task<bool> CreateTagsAsync(List<string> tags, CancellationToken ct = default)
    {
        var request = new RestRequest("api/v2/torrents/createTags", Method.Post);
        AddAuthCookie(request);

        request.AddParameter("tags", string.Join(",", tags));

        var response = await _client.ExecuteAsync(request, ct);
        return response.IsSuccessful;
    }

    /// <summary>
    /// Get all tags
    /// </summary>
    public async Task<List<string>> GetTagsAsync(CancellationToken ct = default)
    {
        var request = new RestRequest("api/v2/torrents/tags", Method.Get);
        AddAuthCookie(request);

        var response = await _client.ExecuteAsync(request, ct);

        if (response.IsSuccessful && !string.IsNullOrEmpty(response.Content))
        {
            return JsonConvert.DeserializeObject<List<string>>(response.Content) ?? new List<string>();
        }

        return new List<string>();
    }

    /// <summary>
    /// Get torrent trackers
    /// </summary>
    public async Task<List<TorrentTracker>> GetTorrentTrackersAsync(string hash, CancellationToken ct = default)
    {
        var request = new RestRequest("api/v2/torrents/trackers", Method.Get);
        AddAuthCookie(request);
        request.AddQueryParameter("hash", hash);

        var response = await _client.ExecuteAsync(request, ct);

        if (response.IsSuccessful && !string.IsNullOrEmpty(response.Content))
        {
            return JsonConvert.DeserializeObject<List<TorrentTracker>>(response.Content) ?? new List<TorrentTracker>();
        }

        return new List<TorrentTracker>();
    }

    /// <summary>
    /// Get torrent properties
    /// </summary>
    public async Task<TorrentProperties?> GetTorrentPropertiesAsync(string hash, CancellationToken ct = default)
    {
        var request = new RestRequest("api/v2/torrents/properties", Method.Get);
        AddAuthCookie(request);
        request.AddQueryParameter("hash", hash);

        var response = await _client.ExecuteAsync(request, ct);

        if (response.IsSuccessful && !string.IsNullOrEmpty(response.Content))
        {
            return JsonConvert.DeserializeObject<TorrentProperties>(response.Content);
        }

        return null;
    }

    /// <summary>
    /// Recheck torrent
    /// </summary>
    public async Task<bool> RecheckTorrentsAsync(List<string> hashes, CancellationToken ct = default)
    {
        var request = new RestRequest("api/v2/torrents/recheck", Method.Post);
        AddAuthCookie(request);

        request.AddParameter("hashes", string.Join("|", hashes));

        var response = await _client.ExecuteAsync(request, ct);
        return response.IsSuccessful;
    }

    /// <summary>
    /// Set share limits (ratio and seeding time) for a torrent
    /// </summary>
    public async Task<bool> SetShareLimitsAsync(string hash, double ratioLimit, long seedingTimeLimit, CancellationToken ct = default)
    {
        var request = new RestRequest("api/v2/torrents/setShareLimits", Method.Post);
        AddAuthCookie(request);

        request.AddParameter("hashes", hash);
        request.AddParameter("ratioLimit", ratioLimit);
        request.AddParameter("seedingTimeLimit", seedingTimeLimit);

        var response = await _client.ExecuteAsync(request, ct);
        return response.IsSuccessful;
    }

    /// <summary>
    /// Set download limit for a torrent
    /// </summary>
    public async Task<bool> SetDownloadLimitAsync(string hash, long limit, CancellationToken ct = default)
    {
        var request = new RestRequest("api/v2/torrents/setDownloadLimit", Method.Post);
        AddAuthCookie(request);

        request.AddParameter("hashes", hash);
        request.AddParameter("limit", limit);

        var response = await _client.ExecuteAsync(request, ct);
        return response.IsSuccessful;
    }

    /// <summary>
    /// Set upload limit for a torrent
    /// </summary>
    public async Task<bool> SetUploadLimitAsync(string hash, long limit, CancellationToken ct = default)
    {
        var request = new RestRequest("api/v2/torrents/setUploadLimit", Method.Post);
        AddAuthCookie(request);

        request.AddParameter("hashes", hash);
        request.AddParameter("limit", limit);

        var response = await _client.ExecuteAsync(request, ct);
        return response.IsSuccessful;
    }

    /// <summary>
    /// Set super seeding mode for a torrent
    /// </summary>
    public async Task<bool> SetSuperSeedingAsync(string hash, bool enabled, CancellationToken ct = default)
    {
        var request = new RestRequest("api/v2/torrents/setSuperSeeding", Method.Post);
        AddAuthCookie(request);

        request.AddParameter("hashes", hash);
        request.AddParameter("value", enabled ? "true" : "false");

        var response = await _client.ExecuteAsync(request, ct);
        return response.IsSuccessful;
    }

    /// <summary>
    /// Move a torrent to top priority in qBittorrent queue ordering.
    /// </summary>
    public async Task<bool> SetTopPriorityAsync(string hash, CancellationToken ct = default)
    {
        var request = new RestRequest("api/v2/torrents/topPrio", Method.Post);
        AddAuthCookie(request);
        request.AddParameter("hashes", hash);
        var response = await _client.ExecuteAsync(request, ct);
        return response.IsSuccessful;
    }

    /// <summary>
    /// Add trackers to a torrent
    /// </summary>
    public async Task<bool> AddTrackersAsync(string hash, List<string> urls, CancellationToken ct = default)
    {
        var request = new RestRequest("api/v2/torrents/addTrackers", Method.Post);
        AddAuthCookie(request);

        request.AddParameter("hash", hash);
        request.AddParameter("urls", string.Join("\n", urls));

        var response = await _client.ExecuteAsync(request, ct);
        return response.IsSuccessful;
    }

    /// <summary>
    /// Remove trackers from a torrent
    /// </summary>
    public async Task<bool> RemoveTrackersAsync(string hash, List<string> urls, CancellationToken ct = default)
    {
        var request = new RestRequest("api/v2/torrents/removeTrackers", Method.Post);
        AddAuthCookie(request);

        request.AddParameter("hash", hash);
        request.AddParameter("urls", string.Join("|", urls));

        var response = await _client.ExecuteAsync(request, ct);
        return response.IsSuccessful;
    }

    /// <summary>
    /// Get files in a torrent
    /// </summary>
    public async Task<List<TorrentFile>> GetTorrentFilesAsync(string hash, CancellationToken ct = default)
    {
        var request = new RestRequest("api/v2/torrents/files", Method.Get);
        AddAuthCookie(request);
        request.AddQueryParameter("hash", hash);

        var response = await _client.ExecuteAsync(request, ct);

        if (response.IsSuccessful && !string.IsNullOrEmpty(response.Content))
        {
            return JsonConvert.DeserializeObject<List<TorrentFile>>(response.Content) ?? new List<TorrentFile>();
        }

        return new List<TorrentFile>();
    }

    /// <summary>
    /// Set file priority for specific files in a torrent.
    /// Priority 0 = do not download; 1 = normal; 6 = high; 7 = maximum.
    /// POST /api/v2/torrents/filePrio
    /// </summary>
    public async Task<bool> SetFilePriorityAsync(string hash, int[] fileIds, int priority, CancellationToken ct = default)
    {
        var request = new RestRequest("api/v2/torrents/filePrio", Method.Post);
        AddAuthCookie(request);
        request.AddParameter("hash", hash);
        request.AddParameter("id", string.Join("|", fileIds));
        request.AddParameter("priority", priority);

        var response = await _client.ExecuteAsync(request, ct);
        return response.IsSuccessful;
    }

    /// <summary>
    /// Create a new category
    /// </summary>
    public async Task<bool> CreateCategoryAsync(string name, string? savePath = null, CancellationToken ct = default)
    {
        var request = new RestRequest("api/v2/torrents/createCategory", Method.Post);
        AddAuthCookie(request);

        request.AddParameter("category", name);
        if (!string.IsNullOrEmpty(savePath))
            request.AddParameter("savePath", savePath);

        var response = await _client.ExecuteAsync(request, ct);
        return response.IsSuccessful;
    }

    /// <summary>
    /// Edit category
    /// </summary>
    public async Task<bool> EditCategoryAsync(string name, string savePath, CancellationToken ct = default)
    {
        var request = new RestRequest("api/v2/torrents/editCategory", Method.Post);
        AddAuthCookie(request);

        request.AddParameter("category", name);
        request.AddParameter("savePath", savePath);

        var response = await _client.ExecuteAsync(request, ct);
        return response.IsSuccessful;
    }

    /// <summary>
    /// Delete category
    /// </summary>
    public async Task<bool> DeleteCategoryAsync(string name, CancellationToken ct = default)
    {
        var request = new RestRequest("api/v2/torrents/removeCategories", Method.Post);
        AddAuthCookie(request);

        request.AddParameter("categories", name);

        var response = await _client.ExecuteAsync(request, ct);
        return response.IsSuccessful;
    }

    /// <summary>
    /// Get transfer info (global download/upload speeds)
    /// </summary>
    public async Task<TransferInfo?> GetTransferInfoAsync(CancellationToken ct = default)
    {
        var request = new RestRequest("api/v2/transfer/info", Method.Get);
        AddAuthCookie(request);

        var response = await _client.ExecuteAsync(request, ct);

        if (response.IsSuccessful && !string.IsNullOrEmpty(response.Content))
        {
            return JsonConvert.DeserializeObject<TransferInfo>(response.Content);
        }

        return null;
    }

    /// <summary>
    /// Get main data (sync API for real-time updates)
    /// </summary>
    public async Task<MainData?> GetMainDataAsync(long? rid = null, CancellationToken ct = default)
    {
        var request = new RestRequest("api/v2/sync/maindata", Method.Get);
        AddAuthCookie(request);

        if (rid.HasValue)
            request.AddQueryParameter("rid", rid.Value);

        var response = await _client.ExecuteAsync(request, ct);

        if (response.IsSuccessful && !string.IsNullOrEmpty(response.Content))
        {
            return JsonConvert.DeserializeObject<MainData>(response.Content);
        }

        return null;
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
/// Category information from qBittorrent API
/// </summary>
public class CategoryInfo
{
    [JsonProperty("name")]
    public string Name { get; set; } = "";

    [JsonProperty("savePath")]
    public string SavePath { get; set; } = "";
}

/// <summary>
/// Torrent properties
/// </summary>
public class TorrentProperties
{
    [JsonProperty("save_path")]
    public string SavePath { get; set; } = "";

    [JsonProperty("creation_date")]
    public long CreationDate { get; set; }

    [JsonProperty("piece_size")]
    public long PieceSize { get; set; }

    [JsonProperty("comment")]
    public string Comment { get; set; } = "";

    [JsonProperty("total_wasted")]
    public long TotalWasted { get; set; }

    [JsonProperty("total_uploaded")]
    public long TotalUploaded { get; set; }

    [JsonProperty("total_downloaded")]
    public long TotalDownloaded { get; set; }

    [JsonProperty("up_limit")]
    public long UpLimit { get; set; }

    [JsonProperty("dl_limit")]
    public long DlLimit { get; set; }

    [JsonProperty("time_elapsed")]
    public long TimeElapsed { get; set; }

    [JsonProperty("seeding_time")]
    public long SeedingTime { get; set; }

    [JsonProperty("nb_connections")]
    public int NbConnections { get; set; }

    [JsonProperty("share_ratio")]
    public double ShareRatio { get; set; }

    [JsonProperty("addition_date")]
    public long AdditionDate { get; set; }

    [JsonProperty("completion_date")]
    public long CompletionDate { get; set; }
}

/// <summary>
/// File in a torrent
/// </summary>
public class TorrentFile
{
    [JsonProperty("index")]
    public int Index { get; set; }

    [JsonProperty("name")]
    public string Name { get; set; } = "";

    [JsonProperty("size")]
    public long Size { get; set; }

    [JsonProperty("progress")]
    public double Progress { get; set; }

    [JsonProperty("priority")]
    public int Priority { get; set; }

    [JsonProperty("is_seed")]
    public bool IsSeed { get; set; }

    [JsonProperty("piece_range")]
    public List<int> PieceRange { get; set; } = new();

    [JsonProperty("availability")]
    public double Availability { get; set; }
}

/// <summary>
/// Transfer info (global speeds)
/// </summary>
public class TransferInfo
{
    [JsonProperty("dl_info_speed")]
    public long DownloadSpeed { get; set; }

    [JsonProperty("dl_info_data")]
    public long DownloadedData { get; set; }

    [JsonProperty("up_info_speed")]
    public long UploadSpeed { get; set; }

    [JsonProperty("up_info_data")]
    public long UploadedData { get; set; }

    [JsonProperty("dl_rate_limit")]
    public long DownloadRateLimit { get; set; }

    [JsonProperty("up_rate_limit")]
    public long UploadRateLimit { get; set; }

    [JsonProperty("dht_nodes")]
    public long DhtNodes { get; set; }

    [JsonProperty("connection_status")]
    public string ConnectionStatus { get; set; } = "";

    [JsonProperty("free_space_on_disk")]
    public long FreeSpaceOnDisk { get; set; }

    [JsonProperty("total_peer_connections")]
    public long TotalPeerConnections { get; set; }
}

/// <summary>
/// Main data for sync API
/// </summary>
public class MainData
{
    [JsonProperty("rid")]
    public long Rid { get; set; }

    [JsonProperty("full_update")]
    public bool? FullUpdate { get; set; }

    [JsonProperty("torrents")]
    public Dictionary<string, TorrentInfo>? Torrents { get; set; }

    [JsonProperty("torrents_removed")]
    public List<string>? TorrentsRemoved { get; set; }

    [JsonProperty("categories")]
    public Dictionary<string, CategoryInfo>? Categories { get; set; }

    [JsonProperty("categories_removed")]
    public List<string>? CategoriesRemoved { get; set; }

    [JsonProperty("tags")]
    public List<string>? Tags { get; set; }

    [JsonProperty("tags_removed")]
    public List<string>? TagsRemoved { get; set; }

    [JsonProperty("server_state")]
    public ServerState? ServerState { get; set; }
}

/// <summary>
/// Server state for sync API
/// </summary>
public class ServerState
{
    [JsonProperty("alltime_dl")]
    public long AllTimeDownloaded { get; set; }

    [JsonProperty("alltime_ul")]
    public long AllTimeUploaded { get; set; }

    [JsonProperty("average_time_queue")]
    public long AverageTimeQueue { get; set; }

    [JsonProperty("connection_status")]
    public string ConnectionStatus { get; set; } = "";

    [JsonProperty("dht_nodes")]
    public long DhtNodes { get; set; }

    [JsonProperty("dl_info_data")]
    public long DownloadData { get; set; }

    [JsonProperty("dl_info_speed")]
    public long DownloadSpeed { get; set; }

    [JsonProperty("dl_rate_limit")]
    public long DownloadRateLimit { get; set; }

    [JsonProperty("free_space_on_disk")]
    public long FreeSpaceOnDisk { get; set; }

    [JsonProperty("global_ratio")]
    public string GlobalRatio { get; set; } = "";

    [JsonProperty("queued_io_jobs")]
    public long QueuedIoJobs { get; set; }

    [JsonProperty("queued_network_jobs")]
    public long QueuedNetworkJobs { get; set; }

    [JsonProperty("read_cache_hits")]
    public string ReadCacheHits { get; set; } = "";

    [JsonProperty("read_cache_overload")]
    public string ReadCacheOverload { get; set; } = "";

    [JsonProperty("refresh_interval")]
    public long RefreshInterval { get; set; }

    [JsonProperty("total_buffers_size")]
    public long TotalBuffersSize { get; set; }

    [JsonProperty("total_peer_connections")]
    public long TotalPeerConnections { get; set; }

    [JsonProperty("total_queued_size")]
    public long TotalQueuedSize { get; set; }

    [JsonProperty("total_wasted_session")]
    public long TotalWastedSession { get; set; }

    [JsonProperty("up_info_data")]
    public long UploadData { get; set; }

    [JsonProperty("up_info_speed")]
    public long UploadSpeed { get; set; }

    [JsonProperty("up_rate_limit")]
    public long UploadRateLimit { get; set; }

    [JsonProperty("use_alt_speed_limits")]
    public bool UseAltSpeedLimits { get; set; }

    [JsonProperty("write_cache_overload")]
    public string WriteCacheOverload { get; set; } = "";
}

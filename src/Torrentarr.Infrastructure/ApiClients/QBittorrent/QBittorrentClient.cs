using Torrentarr.Core.Models;
using Torrentarr.Core.Interfaces;
using Newtonsoft.Json;
using RestSharp;
using RestSharp.Authenticators;
using Torrentarr.Infrastructure.Http;

namespace Torrentarr.Infrastructure.ApiClients.QBittorrent;

/// <summary>
/// qBittorrent WebUI API client using RestSharp
/// </summary>
public class QBittorrentClient : ITorrentClient, IDisposable
{
    private readonly RestClient _client;
    private readonly string _host;
    private readonly int _port;
    private readonly string _username;
    private readonly string _password;
    private string? _cookie;
    private Version? _version;
    private DateTimeOffset _versionAt;
    internal TimeProvider Clock { get; set; } = TimeProvider.System;
    private readonly SemaphoreSlim _versionGate = new(1, 1);

    public TorrentClientIdentity Identity { get; }
    public TorrentClientCapabilities Capabilities => TorrentClientCapabilities.QBittorrent;
    public string? LastConnectionFailure => LastLoginFailure;

    /// <summary>Set when <see cref="LoginAsync"/> returns false; includes HTTP status and cookie names.</summary>
    public string? LastLoginFailure { get; private set; }

    public QBittorrentClient(string host, int port, string username, string password, bool skipTlsVerify = false, string instanceId = "qBit")
    {
        Identity = new TorrentClientIdentity(instanceId, "qbittorrent");
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

        _client = new RestClient(TlsSkipHelper.CreateRestOptions(baseUrl, skipTlsVerify));
    }

    public void Dispose() => _client.Dispose();

    private Task<RestResponse> ExecuteAsync(RestRequest request, CancellationToken ct) =>
        HttpRetryHelper.ExecuteQBitAsync(_client, request, ct);

    private async Task<RestResponse> ExecuteAuthenticatedAsync(RestRequest request, CancellationToken ct)
    {
        var response = await ExecuteAsync(request, ct);
        if (response.StatusCode == System.Net.HttpStatusCode.Forbidden && await LoginAsync(ct))
        {
            request.AddOrUpdateHeader("Cookie", _cookie!);
            response = await ExecuteAsync(request, ct);
        }
        return response;
    }

    private async Task<Version?> DetectVersionAsync(CancellationToken ct)
    {
        await _versionGate.WaitAsync(ct);
        try
        {
            if (_version != null && Clock.GetUtcNow() - _versionAt < TimeSpan.FromMinutes(30)) return _version;
            _version = null;
            try
            {
                var text = (await GetVersionAsync(ct)).Trim();
                var match = System.Text.RegularExpressions.Regex.Match(text,
                    @"^[vV]?(\d+\.\d+(?:\.\d+)?)(?:[-+].*|(?:alpha|beta|rc)\d*)?$",
                    System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                if (!match.Success || !Version.TryParse(match.Groups[1].Value, out _version))
                    throw new HttpRequestException("Unrecognized qBittorrent version.");
                _versionAt = Clock.GetUtcNow();
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Serilog.Log.Warning("qBittorrent [{Instance}] version detection failed ({ErrorType}); using legacy requests",
                    Identity.InstanceId, ex.GetType().Name);
            }
            ct.ThrowIfCancellationRequested();
            return _version;
        }
        finally { _versionGate.Release(); }
    }

    private async Task<bool> ExecuteMutationAsync(RestRequest request, string? hash, CancellationToken ct)
    {
        var response = await ExecuteAuthenticatedAsync(request, ct);
        ct.ThrowIfCancellationRequested();
        if (!response.IsSuccessful)
            Serilog.Log.Warning("qBittorrent [{Instance}] {Operation} rejected for {Hash}: HTTP {Status}, {ResponseStatus}",
                Identity.InstanceId, request.Resource, hash, (int)response.StatusCode, response.ResponseStatus);
        return response.IsSuccessful;
    }

    private static List<T> ReadInventoryList<T>(RestResponse response)
    {
        if (!response.IsSuccessful)
            throw new HttpRequestException($"Torrent inventory request failed (HTTP {(int)response.StatusCode}).", null, response.StatusCode);
        try
        {
            return JsonConvert.DeserializeObject<List<T>>(response.Content ?? "")
                ?? throw new JsonSerializationException("Missing inventory array.");
        }
        catch (JsonException)
        {
            throw new HttpRequestException("Torrent inventory response is not a valid array.");
        }
    }

    /// <summary>
    /// Authenticate with qBittorrent.
    /// Compatible with qBittorrent 4.x/5.1 (<c>200</c> + <c>Ok.</c> + <c>SID</c>) and
    /// 5.2+ (<c>204</c> empty body + <c>QBT_SID_{port}</c>).
    /// </summary>
    public async Task<bool> LoginAsync(CancellationToken ct = default)
    {
        LastLoginFailure = null;
        var request = new RestRequest("/api/v2/auth/login", Method.Post);
        request.AddParameter("username", _username);
        request.AddParameter("password", _password);

        var response = await ExecuteAsync(request, ct);
        var cookies = ExtractLoginCookies(response);

        if (QBittorrentLoginParser.TryAccept(
                (int)response.StatusCode,
                response.Content,
                cookies,
                out var cookieHeader,
                out var failureReason))
        {
            _cookie = cookieHeader;
            return true;
        }

        var cookieNames = cookies.Count == 0 ? "(none)" : string.Join(", ", cookies.Select(c => c.Name));
        var body = string.IsNullOrWhiteSpace(response.Content) ? "(empty)" : TruncateForLog(response.Content.Trim());
        LastLoginFailure =
            $"HTTP {(int)response.StatusCode} {response.StatusCode}; body={body}; cookies=[{cookieNames}]; {failureReason}";
        return false;
    }

    private static List<QBittorrentLoginCookie> ExtractLoginCookies(RestResponse response)
    {
        var cookies = new List<QBittorrentLoginCookie>();
        if (response.Cookies != null)
        {
            foreach (var cookie in response.Cookies.OfType<System.Net.Cookie>())
            {
                if (!string.IsNullOrEmpty(cookie.Name))
                    cookies.Add(new QBittorrentLoginCookie(cookie.Name, cookie.Value ?? ""));
            }
        }

        if (cookies.Count == 0 && response.Headers != null)
        {
            foreach (var header in response.Headers)
            {
                if (!string.Equals(header.Name, "Set-Cookie", StringComparison.OrdinalIgnoreCase))
                    continue;
                var raw = header.Value?.ToString();
                if (string.IsNullOrEmpty(raw))
                    continue;
                var firstPart = raw.Split(';', 2)[0];
                var eq = firstPart.IndexOf('=');
                if (eq <= 0)
                    continue;
                cookies.Add(new QBittorrentLoginCookie(
                    firstPart[..eq].Trim(),
                    firstPart[(eq + 1)..].Trim()));
            }
        }

        return cookies;
    }

    private static string TruncateForLog(string value, int max = 80)
        => value.Length <= max ? value : value[..max] + "…";

    /// <summary>
    /// Get application version
    /// </summary>
    public async Task<string> GetVersionAsync(CancellationToken ct = default)
    {
        var request = new RestRequest("/api/v2/app/version", Method.Get);
        AddAuthCookie(request);

        var response = await ExecuteAuthenticatedAsync(request, ct);
        if (!response.IsSuccessful)
            throw new HttpRequestException($"Torrent version request failed (HTTP {(int)response.StatusCode}).", null, response.StatusCode);
        return response.Content ?? "";
    }

    /// <summary>
    /// Get all torrents
    /// </summary>
    public virtual async Task<List<TorrentInfo>> GetTorrentsAsync(string? category = null, string? sort = null, CancellationToken cancellationToken = default)
    {
        var request = new RestRequest("/api/v2/torrents/info", Method.Get);
        AddAuthCookie(request);

        if (!string.IsNullOrEmpty(category))
            request.AddQueryParameter("category", category);
        if (!string.IsNullOrEmpty(sort))
            request.AddQueryParameter("sort", sort);

        var response = await ExecuteAuthenticatedAsync(request, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();

        return ReadInventoryList<TorrentInfo>(response);
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

        var response = await ExecuteAsync(request, ct);
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

        // A retry could hide whether an earlier delete reached the server.
        var response = await _client.ExecuteAsync(request, ct);
        if (response.IsSuccessful) return true;
        ct.ThrowIfCancellationRequested();
        if ((int)response.StatusCode is >= 400 and < 500
            && response.StatusCode is not System.Net.HttpStatusCode.RequestTimeout and not System.Net.HttpStatusCode.TooManyRequests)
            return false;
        throw new HttpRequestException($"Remote removal outcome is uncertain (HTTP {(int)response.StatusCode}).",
            response.ErrorException, response.StatusCode);
    }

    /// <summary>
    /// Pause torrents
    /// </summary>
    public async Task<bool> PauseTorrentsAsync(List<string> hashes, CancellationToken ct = default)
    {
        var version = await DetectVersionAsync(ct);
        var request = new RestRequest(version?.Major >= 5 ? "/api/v2/torrents/stop" : "/api/v2/torrents/pause", Method.Post);
        AddAuthCookie(request);

        request.AddParameter("hashes", string.Join("|", hashes));

        return await ExecuteMutationAsync(request, string.Join("|", hashes), ct);
    }

    /// <summary>
    /// Resume torrents
    /// </summary>
    public async Task<bool> ResumeTorrentsAsync(List<string> hashes, CancellationToken ct = default)
    {
        var version = await DetectVersionAsync(ct);
        var request = new RestRequest(version?.Major >= 5 ? "/api/v2/torrents/start" : "/api/v2/torrents/resume", Method.Post);
        AddAuthCookie(request);

        request.AddParameter("hashes", string.Join("|", hashes));

        return await ExecuteMutationAsync(request, string.Join("|", hashes), ct);
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

        var response = await ExecuteAsync(request, ct);
        return response.IsSuccessful;
    }

    /// <summary>
    /// Get all categories
    /// </summary>
    public async Task<Dictionary<string, CategoryInfo>> GetCategoriesAsync(CancellationToken ct = default)
    {
        var request = new RestRequest("/api/v2/torrents/categories", Method.Get);
        AddAuthCookie(request);

        var response = await ExecuteAsync(request, ct);

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

        var response = await ExecuteAsync(request, ct);
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

        var response = await ExecuteAsync(request, ct);
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

        var response = await ExecuteAsync(request, ct);
        return response.IsSuccessful;
    }

    /// <summary>
    /// Get all tags
    /// </summary>
    public async Task<List<string>> GetTagsAsync(CancellationToken ct = default)
    {
        var request = new RestRequest("api/v2/torrents/tags", Method.Get);
        AddAuthCookie(request);

        var response = await ExecuteAsync(request, ct);

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

        var response = await ExecuteAuthenticatedAsync(request, ct);
        ct.ThrowIfCancellationRequested();

        return ReadInventoryList<TorrentTracker>(response);
    }

    /// <summary>
    /// Get torrent properties
    /// </summary>
    public async Task<TorrentProperties?> GetTorrentPropertiesAsync(string hash, CancellationToken ct = default)
    {
        var request = new RestRequest("api/v2/torrents/properties", Method.Get);
        AddAuthCookie(request);
        request.AddQueryParameter("hash", hash);

        var response = await ExecuteAsync(request, ct);

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

        var response = await ExecuteAsync(request, ct);
        return response.IsSuccessful;
    }

    /// <summary>
    /// Set share limits (ratio and seeding time) for a torrent
    /// </summary>
    public Task<bool> SetShareLimitsAsync(string hash, double ratioLimit, long seedingTimeLimit, long inactiveSeedingTimeLimit, CancellationToken ct = default)
        => SetShareLimitsCoreAsync(hash, ratioLimit, seedingTimeLimit, inactiveSeedingTimeLimit, ct);

    public Task<bool> SetShareLimitsAsync(string hash, double ratioLimit, long seedingTimeLimit, CancellationToken ct = default)
        => SetShareLimitsCoreAsync(hash, ratioLimit, seedingTimeLimit, null, ct);

    private async Task<bool> SetShareLimitsCoreAsync(string hash, double ratio, long time, long? inactive, CancellationToken ct)
    {
        var version = await DetectVersionAsync(ct);
        var modern = version >= new Version(5, 3);
        TorrentInfo? current = null;
        if (modern || (version >= new Version(4, 6) && !inactive.HasValue))
        {
            var inventory = new RestRequest("/api/v2/torrents/info", Method.Get);
            AddAuthCookie(inventory);
            inventory.AddQueryParameter("hashes", hash);
            try
            {
                var response = await ExecuteAuthenticatedAsync(inventory, ct);
                ct.ThrowIfCancellationRequested();
                current = ReadInventoryList<TorrentInfo>(response)
                    .SingleOrDefault(t => t.Hash.Equals(hash, StringComparison.OrdinalIgnoreCase));
            }
            catch (HttpRequestException ex)
            {
                Serilog.Log.Warning("qBittorrent [{Instance}] setShareLimits skipped for {Hash}: current settings read failed (HTTP {Status})",
                    Identity.InstanceId, hash, (int?)ex.StatusCode);
                return false;
            }
            if (current == null || (!inactive.HasValue && !current.InactiveSeedingTimeLimit.HasValue)
                || (modern && (string.IsNullOrEmpty(current.ShareLimitAction) || string.IsNullOrEmpty(current.ShareLimitsMode))))
            {
                Serilog.Log.Warning("qBittorrent [{Instance}] setShareLimits skipped for {Hash}: required current settings are unavailable",
                    Identity.InstanceId, hash);
                return false;
            }
        }
        var request = new RestRequest("api/v2/torrents/setShareLimits", Method.Post);
        AddAuthCookie(request);
        request.AddParameter("hashes", hash);
        request.AddParameter("ratioLimit", ratio);
        request.AddParameter("seedingTimeLimit", time);
        if (version >= new Version(4, 6) || (version == null && inactive.HasValue))
            request.AddParameter("inactiveSeedingTimeLimit", inactive ?? current!.InactiveSeedingTimeLimit!.Value);
        if (modern)
        {
            request.AddParameter("shareLimitAction", current!.ShareLimitAction);
            request.AddParameter("shareLimitsMode", current.ShareLimitsMode);
        }
        return await ExecuteMutationAsync(request, hash, ct);
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

        var response = await ExecuteAsync(request, ct);
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

        var response = await ExecuteAsync(request, ct);
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

        var response = await ExecuteAsync(request, ct);
        return response.IsSuccessful;
    }

    public async Task<bool> SetAutomaticManagementAsync(List<string> hashes, bool enabled, CancellationToken ct = default)
    {
        var request = new RestRequest("api/v2/torrents/setAutoManagement", Method.Post);
        AddAuthCookie(request);
        request.AddParameter("hashes", string.Join("|", hashes));
        request.AddParameter("enable", enabled.ToString().ToLowerInvariant());
        var response = await ExecuteAsync(request, ct);
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
        var response = await ExecuteAsync(request, ct);
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

        var response = await ExecuteAsync(request, ct);
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

        var response = await ExecuteAsync(request, ct);
        return response.IsSuccessful;
    }

    /// <summary>
    /// Get files in a torrent
    /// </summary>
    public virtual async Task<List<TorrentFile>> GetTorrentFilesAsync(string hash, CancellationToken ct = default)
    {
        var request = new RestRequest("api/v2/torrents/files", Method.Get);
        AddAuthCookie(request);
        request.AddQueryParameter("hash", hash);

        var response = await ExecuteAuthenticatedAsync(request, ct);
        ct.ThrowIfCancellationRequested();

        return ReadInventoryList<TorrentFile>(response);
    }

    /// <summary>
    /// Set file priority for specific files in a torrent.
    /// Priority 0 = do not download; 1 = normal; 6 = high; 7 = maximum.
    /// POST /api/v2/torrents/filePrio
    /// </summary>
    public virtual async Task<bool> SetFilePriorityAsync(string hash, int[] fileIds, int priority, CancellationToken ct = default)
    {
        var request = new RestRequest("api/v2/torrents/filePrio", Method.Post);
        AddAuthCookie(request);
        request.AddParameter("hash", hash);
        request.AddParameter("id", string.Join("|", fileIds));
        request.AddParameter("priority", priority);

        var response = await ExecuteAsync(request, ct);
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

        var response = await ExecuteAsync(request, ct);
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

        var response = await ExecuteAsync(request, ct);
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

        var response = await ExecuteAsync(request, ct);
        return response.IsSuccessful;
    }

    /// <summary>
    /// Get transfer info (global download/upload speeds)
    /// </summary>
    public async Task<TransferInfo?> GetTransferInfoAsync(CancellationToken ct = default)
    {
        var request = new RestRequest("api/v2/transfer/info", Method.Get);
        AddAuthCookie(request);

        var response = await ExecuteAsync(request, ct);

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

        var response = await ExecuteAsync(request, ct);

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

    async Task<Dictionary<string, TorrentClientCategory>> ITorrentClient.GetCategoriesAsync(CancellationToken ct)
        => (await GetCategoriesAsync(ct)).ToDictionary(
            pair => pair.Key,
            pair => new TorrentClientCategory { Name = pair.Value.Name, SavePath = pair.Value.SavePath });

    async Task<TorrentPropertiesRecord?> ITorrentClient.GetTorrentPropertiesAsync(string hash, CancellationToken ct)
    {
        var value = await GetTorrentPropertiesAsync(hash, ct);
        if (value == null) return null;
        return new TorrentPropertiesRecord
        {
            IsPrivate = value.IsPrivate,
            SavePath = value.SavePath,
            CreationDate = value.CreationDate,
            PieceSize = value.PieceSize,
            Comment = value.Comment,
            TotalWasted = value.TotalWasted,
            TotalUploaded = value.TotalUploaded,
            TotalDownloaded = value.TotalDownloaded,
            UpLimit = value.UpLimit,
            DlLimit = value.DlLimit,
            TimeElapsed = value.TimeElapsed,
            SeedingTime = value.SeedingTime,
            NbConnections = value.NbConnections,
            ShareRatio = value.ShareRatio,
            AdditionDate = value.AdditionDate,
            CompletionDate = value.CompletionDate
        };
    }

    async Task<List<TorrentFileRecord>> ITorrentClient.GetTorrentFilesAsync(string hash, CancellationToken ct)
        => (await GetTorrentFilesAsync(hash, ct)).Select(value => new TorrentFileRecord
        {
            Index = value.Index,
            Name = value.Name,
            Size = value.Size,
            Progress = value.Progress,
            Priority = value.Priority,
            IsSeed = value.IsSeed,
            PieceRange = value.PieceRange,
            Availability = value.Availability
        }).ToList();

    async Task<TorrentTransferRecord?> ITorrentClient.GetTransferInfoAsync(CancellationToken ct)
    {
        var value = await GetTransferInfoAsync(ct);
        if (value == null) return null;
        return new TorrentTransferRecord
        {
            DownloadSpeed = value.DownloadSpeed,
            DownloadedData = value.DownloadedData,
            UploadSpeed = value.UploadSpeed,
            UploadedData = value.UploadedData,
            DownloadRateLimit = value.DownloadRateLimit,
            UploadRateLimit = value.UploadRateLimit,
            DhtNodes = value.DhtNodes,
            ConnectionStatus = value.ConnectionStatus,
            FreeSpaceOnDisk = value.FreeSpaceOnDisk,
            TotalPeerConnections = value.TotalPeerConnections
        };
    }

    async Task<TorrentClientSnapshot?> ITorrentClient.GetMainDataAsync(long? revision, CancellationToken ct)
    {
        var value = await GetMainDataAsync(revision, ct);
        if (value == null) return null;
        return new TorrentClientSnapshot
        {
            Revision = value.Rid,
            FullUpdate = value.FullUpdate,
            Torrents = value.Torrents,
            TorrentsRemoved = value.TorrentsRemoved,
            Categories = value.Categories?.ToDictionary(
                pair => pair.Key,
                pair => new TorrentClientCategory { Name = pair.Value.Name, SavePath = pair.Value.SavePath }),
            CategoriesRemoved = value.CategoriesRemoved,
            Tags = value.Tags,
            TagsRemoved = value.TagsRemoved
        };
    }

    public Task<IReadOnlyDictionary<string, byte[]>> ExportResumeDataAsync(string hash, CancellationToken ct = default)
        => Task.FromResult<IReadOnlyDictionary<string, byte[]>>(new Dictionary<string, byte[]>());
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
    [JsonProperty("is_private")]
    public bool? IsPrivate { get; set; }
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

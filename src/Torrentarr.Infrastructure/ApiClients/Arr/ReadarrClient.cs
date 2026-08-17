using Newtonsoft.Json;
using RestSharp;

namespace Torrentarr.Infrastructure.ApiClients.Arr;

/// <summary>
/// Readarr API client using RestSharp. Author → book (no track layer).
/// </summary>
public class ReadarrClient
{
    private readonly RestClient _client;
    private readonly string _apiKey;

    public ReadarrClient(string baseUrl, string apiKey, bool skipTlsVerify = false)
    {
        _apiKey = apiKey;
        _client = new RestClient(Torrentarr.Infrastructure.Http.TlsSkipHelper.CreateRestOptions(baseUrl, skipTlsVerify));
    }

    public async Task<List<ReadarrAuthor>> GetAuthorsAsync(CancellationToken ct = default)
    {
        var request = new RestRequest("/api/v1/author", Method.Get);
        AddApiKeyHeader(request);

        var response = await ArrClientResponse.ExecuteAsync(_client, request, ct);
        ArrClientResponse.EnsureSuccess(response, "GET /api/v1/author");

        if (string.IsNullOrEmpty(response.Content))
            return new List<ReadarrAuthor>();

        return JsonConvert.DeserializeObject<List<ReadarrAuthor>>(response.Content) ?? new List<ReadarrAuthor>();
    }

    public async Task<SystemInfo> GetSystemInfoAsync(CancellationToken ct = default)
    {
        var request = new RestRequest("/api/v1/system/status", Method.Get);
        AddApiKeyHeader(request);

        var response = await ArrClientResponse.ExecuteAsync(_client, request, ct);
        ArrClientResponse.EnsureSuccess(response, "GET /api/v1/system/status");
        return JsonConvert.DeserializeObject<SystemInfo>(response.Content ?? "") ?? new SystemInfo();
    }

    public async Task<ReadarrAuthor?> GetAuthorAsync(int authorId, CancellationToken ct = default)
    {
        var request = new RestRequest($"/api/v1/author/{authorId}", Method.Get);
        AddApiKeyHeader(request);

        var response = await ArrClientResponse.ExecuteAsync(_client, request, ct);

        if (response.IsSuccessful && !string.IsNullOrEmpty(response.Content))
        {
            return JsonConvert.DeserializeObject<ReadarrAuthor>(response.Content);
        }

        return null;
    }

    public async Task<List<ReadarrBook>> GetBooksAsync(int? authorId = null, CancellationToken ct = default)
    {
        var request = new RestRequest("/api/v1/book", Method.Get);
        AddApiKeyHeader(request);

        if (authorId.HasValue)
            request.AddQueryParameter("authorId", authorId.Value.ToString());

        var response = await ArrClientResponse.ExecuteAsync(_client, request, ct);
        ArrClientResponse.EnsureSuccess(response, "GET /api/v1/book");

        if (string.IsNullOrEmpty(response.Content))
            return new List<ReadarrBook>();

        return JsonConvert.DeserializeObject<List<ReadarrBook>>(response.Content) ?? new List<ReadarrBook>();
    }

    public async Task<bool> SearchAuthorAsync(int authorId, CancellationToken ct = default)
    {
        var request = new RestRequest("/api/v1/command", Method.Post);
        AddApiKeyHeader(request);
        request.AddJsonBody(new { name = "AuthorSearch", authorId });
        var response = await ArrClientResponse.ExecuteAsync(_client, request, ct);
        return response.IsSuccessful;
    }

    public async Task<bool> SearchBookAsync(List<int> bookIds, CancellationToken ct = default)
    {
        var request = new RestRequest("/api/v1/command", Method.Post);
        AddApiKeyHeader(request);
        request.AddJsonBody(new { name = "BookSearch", bookIds });
        var response = await ArrClientResponse.ExecuteAsync(_client, request, ct);
        return response.IsSuccessful;
    }

    public async Task<ReadarrAuthor?> UpdateAuthorAsync(ReadarrAuthor author, CancellationToken ct = default)
    {
        var request = new RestRequest($"/api/v1/author/{author.Id}", Method.Put);
        AddApiKeyHeader(request);
        request.AddJsonBody(author);

        var response = await ArrClientResponse.ExecuteAsync(_client, request, ct);

        if (response.IsSuccessful && !string.IsNullOrEmpty(response.Content))
        {
            return JsonConvert.DeserializeObject<ReadarrAuthor>(response.Content);
        }

        return null;
    }

    public async Task<bool> UpdateAuthorQualityProfileAsync(int authorId, int qualityProfileId, CancellationToken ct = default)
    {
        var author = await GetAuthorAsync(authorId, ct);
        if (author == null)
            return false;

        author.QualityProfileId = qualityProfileId;
        var updated = await UpdateAuthorAsync(author, ct);
        return updated != null;
    }

    public async Task<ReadarrQueueResponse> GetQueueAsync(int page = 1, int pageSize = 1000, CancellationToken ct = default)
    {
        var request = new RestRequest("/api/v1/queue", Method.Get);
        AddApiKeyHeader(request);
        request.AddQueryParameter("page", page.ToString());
        request.AddQueryParameter("pageSize", pageSize.ToString());

        var response = await ArrClientResponse.ExecuteAsync(_client, request, ct);
        ArrClientResponse.EnsureSuccess(response, "GET /api/v1/queue");

        if (string.IsNullOrEmpty(response.Content))
            return new ReadarrQueueResponse();

        return JsonConvert.DeserializeObject<ReadarrQueueResponse>(response.Content) ?? new ReadarrQueueResponse();
    }

    public async Task<CommandResponse?> TriggerDownloadedBooksScanAsync(
        string path,
        string downloadClientId,
        string importMode = "Auto",
        CancellationToken ct = default)
    {
        var request = new RestRequest("/api/v1/command", Method.Post);
        AddApiKeyHeader(request);
        request.AddJsonBody(new
        {
            name = "DownloadedBooksScan",
            path,
            downloadClientId = downloadClientId.ToUpper(),
            importMode
        });

        var response = await ArrClientResponse.ExecuteAsync(_client, request, ct);

        if (response.IsSuccessful && !string.IsNullOrEmpty(response.Content))
        {
            return JsonConvert.DeserializeObject<CommandResponse>(response.Content);
        }

        return null;
    }

    public async Task<List<CommandStatus>> GetCommandsAsync(CancellationToken ct = default)
    {
        var request = new RestRequest("/api/v1/command", Method.Get);
        AddApiKeyHeader(request);

        var response = await ArrClientResponse.ExecuteAsync(_client, request, ct);

        if (response.IsSuccessful && !string.IsNullOrEmpty(response.Content))
        {
            return JsonConvert.DeserializeObject<List<CommandStatus>>(response.Content) ?? new List<CommandStatus>();
        }

        return new List<CommandStatus>();
    }

    public async Task<bool> DeleteFromQueueAsync(int id, bool removeFromClient = true, bool blocklist = false, CancellationToken ct = default)
    {
        var request = new RestRequest($"/api/v1/queue/{id}", Method.Delete);
        AddApiKeyHeader(request);
        request.AddQueryParameter("removeFromClient", removeFromClient.ToString().ToLower());
        request.AddQueryParameter("blocklist", blocklist.ToString().ToLower());
        var response = await ArrClientResponse.ExecuteAsync(_client, request, ct);
        return response.IsSuccessful;
    }

    public async Task<CommandResponse?> RssSyncAsync(CancellationToken ct = default)
    {
        var request = new RestRequest("/api/v1/command", Method.Post);
        AddApiKeyHeader(request);
        request.AddJsonBody(new { name = "RssSync" });
        var response = await ArrClientResponse.ExecuteAsync(_client, request, ct);

        if (response.IsSuccessful && !string.IsNullOrEmpty(response.Content))
        {
            return JsonConvert.DeserializeObject<CommandResponse>(response.Content);
        }

        return null;
    }

    public async Task<CommandResponse?> RefreshMonitoredDownloadsAsync(CancellationToken ct = default)
    {
        var request = new RestRequest("/api/v1/command", Method.Post);
        AddApiKeyHeader(request);
        request.AddJsonBody(new { name = "RefreshMonitoredDownloads" });
        var response = await ArrClientResponse.ExecuteAsync(_client, request, ct);

        if (response.IsSuccessful && !string.IsNullOrEmpty(response.Content))
        {
            return JsonConvert.DeserializeObject<CommandResponse>(response.Content);
        }

        return null;
    }

    public async Task<List<QualityProfile>> GetQualityProfilesAsync(CancellationToken ct = default)
    {
        var request = new RestRequest("/api/v1/qualityprofile", Method.Get);
        AddApiKeyHeader(request);

        var response = await ArrClientResponse.ExecuteAsync(_client, request, ct);
        ArrClientResponse.EnsureSuccess(response, "GET /api/v1/qualityprofile");

        if (string.IsNullOrEmpty(response.Content))
            return new List<QualityProfile>();

        return JsonConvert.DeserializeObject<List<QualityProfile>>(response.Content) ?? new List<QualityProfile>();
    }

    public async Task<List<ReadarrBookFile>> GetBookFilesByAuthorAsync(int authorId, CancellationToken ct = default)
    {
        var request = new RestRequest("/api/v1/bookfile", Method.Get);
        AddApiKeyHeader(request);
        request.AddQueryParameter("authorId", authorId.ToString());

        var response = await ArrClientResponse.ExecuteAsync(_client, request, ct);

        if (response.IsSuccessful && !string.IsNullOrEmpty(response.Content))
        {
            return JsonConvert.DeserializeObject<List<ReadarrBookFile>>(response.Content) ?? new List<ReadarrBookFile>();
        }

        return new List<ReadarrBookFile>();
    }

    public async Task<bool> RescanAsync(CancellationToken ct = default)
    {
        var request = new RestRequest("/api/v1/command", Method.Post);
        AddApiKeyHeader(request);
        request.AddJsonBody(new { name = "RescanFolders" });
        var response = await ArrClientResponse.ExecuteAsync(_client, request, ct);
        return response.IsSuccessful;
    }

    private void AddApiKeyHeader(RestRequest request)
    {
        request.AddHeader("X-Api-Key", _apiKey);
    }
}

public class ReadarrAuthor
{
    [JsonProperty("id")]
    public int Id { get; set; }

    [JsonProperty("authorName")]
    public string AuthorName { get; set; } = "";

    [JsonProperty("foreignAuthorId")]
    public string ForeignAuthorId { get; set; } = "";

    [JsonProperty("monitored")]
    public bool Monitored { get; set; }

    [JsonProperty("qualityProfileId")]
    public int QualityProfileId { get; set; }

    [JsonProperty("path")]
    public string Path { get; set; } = "";

    [JsonProperty("statistics")]
    public ReadarrAuthorStatistics? Statistics { get; set; }
}

public class ReadarrAuthorStatistics
{
    [JsonProperty("bookCount")]
    public int BookCount { get; set; }

    [JsonProperty("bookFileCount")]
    public int BookFileCount { get; set; }

    [JsonProperty("sizeOnDisk")]
    public long SizeOnDisk { get; set; }
}

public class ReadarrBook
{
    [JsonProperty("id")]
    public int Id { get; set; }

    [JsonProperty("title")]
    public string Title { get; set; } = "";

    [JsonProperty("foreignBookId")]
    public string ForeignBookId { get; set; } = "";

    [JsonProperty("authorId")]
    public int AuthorId { get; set; }

    [JsonProperty("monitored")]
    public bool Monitored { get; set; }

    [JsonProperty("releaseDate")]
    public DateTime? ReleaseDate { get; set; }

    [JsonProperty("statistics")]
    public ReadarrBookStatistics? Statistics { get; set; }

    [JsonProperty("qualityProfileId")]
    public int? QualityProfileId { get; set; }
}

public class ReadarrBookStatistics
{
    [JsonProperty("bookFileCount")]
    public int BookFileCount { get; set; }

    [JsonProperty("sizeOnDisk")]
    public long SizeOnDisk { get; set; }
}

public class ReadarrQueueResponse
{
    [JsonProperty("page")]
    public int Page { get; set; }

    [JsonProperty("pageSize")]
    public int PageSize { get; set; }

    [JsonProperty("totalRecords")]
    public int TotalRecords { get; set; }

    [JsonProperty("records")]
    public List<ReadarrQueueItem> Records { get; set; } = new();
}

public class ReadarrQueueItem
{
    [JsonProperty("id")]
    public int Id { get; set; }

    [JsonProperty("bookId")]
    public int? BookId { get; set; }

    [JsonProperty("authorId")]
    public int? AuthorId { get; set; }

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

public class ReadarrBookFile
{
    [JsonProperty("id")]
    public int Id { get; set; }

    [JsonProperty("bookId")]
    public int BookId { get; set; }

    [JsonProperty("authorId")]
    public int AuthorId { get; set; }

    [JsonProperty("path")]
    public string Path { get; set; } = "";

    [JsonProperty("size")]
    public long Size { get; set; }

    [JsonProperty("customFormatScore")]
    public int? CustomFormatScore { get; set; }

    [JsonProperty("qualityCutoffNotMet")]
    public bool QualityCutoffNotMet { get; set; }
}

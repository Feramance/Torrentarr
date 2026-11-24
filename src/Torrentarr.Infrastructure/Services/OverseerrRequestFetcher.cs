using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Torrentarr.Core.Configuration;
using Torrentarr.Infrastructure.Http;

namespace Torrentarr.Infrastructure.Services;

/// <summary>
/// qBitrr <c>request_providers._get_oversee_requests_all</c> parity: page Seerr or legacy Overseerr
/// requests, skip unreleased titles via <c>GET /api/v1/movie/{tmdbId}</c> and
/// <c>GET /api/v1/tv/{tmdbId}</c>, then return Arr match IDs.
/// </summary>
public sealed class OverseerrRequestFetcher
{
    private const int PendingRequest = 1;
    private const int ApprovedRequest = 2;
    private const int AvailableMedia = 5;

    private static readonly HttpClient VerifyingClient = TlsSkipHelper.CreateHttpClient(false);
    private static readonly HttpClient SkippingClient = TlsSkipHelper.CreateHttpClient(true);
    private static readonly ConcurrentDictionary<string, (DateTime ReleaseDate, int? TvdbId)> ReleaseDateCache = new();

    private readonly ILogger _logger;
    private readonly HttpClient? _httpOverride;

    public OverseerrRequestFetcher(ILogger logger, HttpClient? httpClient = null)
    {
        _logger = logger;
        _httpOverride = httpClient;
    }

    public sealed class Result
    {
        public HashSet<int> TmdbIds { get; } = new();
        public HashSet<int> TvdbIds { get; } = new();
    }

    public async Task<Result> FetchAsync(OverseerrConfig overseerr, string mediaType, CancellationToken ct)
    {
        var result = new Result();
        var http = _httpOverride
            ?? (overseerr.SkipTLSVerify ? SkippingClient : VerifyingClient);
        var filter = overseerr.ApprovedOnly ? "processing" : "unavailable";
        var skip = 0;
        var expectPage = false;
        const int take = 100;
        var now = DateTime.UtcNow;

        while (true)
        {
            ct.ThrowIfCancellationRequested();
            var url = $"{overseerr.OverseerrURI.TrimEnd('/')}/api/v1/request?take={take}&skip={skip}&sort=added&filter={filter}";
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.Add("X-Api-Key", overseerr.OverseerrAPIKey);
            using var resp = await http.SendAsync(req, ct);
            resp.EnsureSuccessStatusCode();

            var json = await resp.Content.ReadAsStringAsync(ct);
            var token = JToken.Parse(json);
            var pageInfo = (token as JObject)?["pageInfo"] as JObject;
            var results = token as JArray
                ?? ((token as JObject)?["results"] ?? (token as JObject)?["data"]) as JArray
                ?? throw new JsonException("Request response is missing the results array.");
            var total = pageInfo?["results"]?.ToObject<int?>();
            var pages = pageInfo?["pages"]?.ToObject<int?>();
            var pageSize = pageInfo?["pageSize"]?.ToObject<int?>() ?? take;
            if (pageSize <= 0)
                throw new JsonException("Request response contains an invalid page size.");
            var hasMore = total.HasValue ? skip + results.Count < total.Value
                : pages.HasValue ? skip / pageSize + 1 < pages.Value
                : results.Count >= take;
            if ((expectPage && results.Count == 0) ||
                (hasMore && results.Count < (total.HasValue ? Math.Min(pageSize, total.Value - skip) : pageSize)))
                throw new JsonException("Request response contains an incomplete page.");

            foreach (var entry in results)
            {
                if (entry is not JObject)
                    throw new JsonException("Request response contains an invalid entry.");
                if (entry["type"]?.ToObject<string>() != mediaType)
                    continue;
                var is4k = entry["is4k"]?.ToObject<bool?>() ?? false;
                if (is4k != overseerr.Is4K)
                    continue;
                var media = entry["media"] as JObject;
                if (media == null)
                    continue;
                // The request status and availability are independent on older servers.
                var status = entry["status"]?.ToObject<int?>();
                if (status.HasValue && status != ApprovedRequest && (overseerr.ApprovedOnly || status != PendingRequest))
                    continue;
                if (media[is4k ? "status4k" : "status"]?.ToObject<int?>() is >= AvailableMedia)
                    continue;

                var tmdbId = media["tmdbId"]?.ToObject<int?>();
                if (!tmdbId.HasValue || tmdbId.Value <= 0)
                    continue;

                var tvdbId = mediaType == "tv" ? media["tvdbId"]?.ToObject<int?>() : null;
                var details = await GetDetailsAsync(http, overseerr, mediaType, tmdbId.Value, now,
                    mediaType == "tv" && tvdbId.GetValueOrDefault() <= 0, ct);
                if (details.ReleaseDate > now)
                    continue;

                if (mediaType == "movie")
                    result.TmdbIds.Add(tmdbId.Value);
                else if (tvdbId is > 0)
                    result.TvdbIds.Add(tvdbId.Value);
                else if (details.TvdbId is > 0)
                    result.TvdbIds.Add(details.TvdbId.Value);
            }

            if (!hasMore)
                break;
            expectPage = total.HasValue || pages.HasValue;
            skip += results.Count;
        }

        return result;
    }

    private async Task<(DateTime ReleaseDate, int? TvdbId)> GetDetailsAsync(
        HttpClient http,
        OverseerrConfig overseerr,
        string mediaType,
        int tmdbId,
        DateTime now,
        bool needsTvdbId,
        CancellationToken ct)
    {
        var cacheKey = ReleaseCacheKey(overseerr, mediaType, tmdbId);
        if (ReleaseDateCache.TryGetValue(cacheKey, out var cached)
            && (!needsTvdbId || cached.TvdbId is > 0))
            return cached;

        try
        {
            var path = mediaType == "movie" ? $"/api/v1/movie/{tmdbId}" : $"/api/v1/tv/{tmdbId}";
            using var req = new HttpRequestMessage(
                HttpMethod.Get,
                $"{overseerr.OverseerrURI.TrimEnd('/')}{path}");
            req.Headers.Add("X-Api-Key", overseerr.OverseerrAPIKey);
            using var resp = await http.SendAsync(req, ct);
            resp.EnsureSuccessStatusCode();
            var json = JObject.Parse(await resp.Content.ReadAsStringAsync(ct));
            var dateString = mediaType == "movie"
                ? json["releaseDate"]?.ToObject<string>()
                : json["firstAirDate"]?.ToObject<string>();
            if (string.IsNullOrWhiteSpace(dateString) || dateString.Length < 10)
                dateString = now.ToString("yyyy-MM-dd");
            var date = DateTime.Parse(dateString[..10], System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.AssumeUniversal | System.Globalization.DateTimeStyles.AdjustToUniversal);
            var externalIds = json["externalIds"] as JObject;
            var tvdbId = externalIds?["tvdbId"]?.ToObject<int?>()
                ?? json["tvdbId"]?.ToObject<int?>();
            var details = (date, tvdbId);
            if (date <= now)
                ReleaseDateCache[cacheKey] = details;
            return details;
        }
        catch (Exception ex) when (!ct.IsCancellationRequested && !needsTvdbId)
        {
            _logger.LogWarning(ex, "Seerr: failed to query release date for {MediaType} {TmdbId}", mediaType, tmdbId);
            return (now, null);
        }
    }

    internal static string ReleaseCacheKey(OverseerrConfig overseerr, string mediaType, int tmdbId) =>
        $"{overseerr.OverseerrURI.TrimEnd('/')}:{mediaType}:{tmdbId}";

    internal static void ClearCacheForTests() => ReleaseDateCache.Clear();
}

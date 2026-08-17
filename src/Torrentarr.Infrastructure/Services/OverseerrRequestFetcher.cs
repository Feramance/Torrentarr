using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json.Linq;
using Torrentarr.Core.Configuration;
using Torrentarr.Infrastructure.Http;

namespace Torrentarr.Infrastructure.Services;

/// <summary>
/// qBitrr <c>request_providers._get_oversee_requests_all</c> parity: page Overseerr
/// requests, skip unreleased titles via <c>GET /api/v1/movie/{tmdbId}</c> and
/// <c>GET /api/v1/tv/{tmdbId}</c>, then return Arr match IDs.
/// </summary>
public sealed class OverseerrRequestFetcher
{
    private static readonly HttpClient VerifyingClient = TlsSkipHelper.CreateHttpClient(false);
    private static readonly HttpClient SkippingClient = TlsSkipHelper.CreateHttpClient(true);
    private static readonly ConcurrentDictionary<int, DateTime> ReleaseDateCache = new();

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
        var filter = overseerr.ApprovedOnly ? "approved" : "unavailable";
        var skip = 0;
        const int take = 100;
        var now = DateTime.UtcNow;

        while (true)
        {
            ct.ThrowIfCancellationRequested();
            var url = $"{overseerr.OverseerrURI.TrimEnd('/')}/api/v1/request?take={take}&skip={skip}&sort=added&filter={filter}";
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.Add("X-Api-Key", overseerr.OverseerrAPIKey);
            using var resp = await http.SendAsync(req, ct);
            if (!resp.IsSuccessStatusCode)
                break;

            var json = await resp.Content.ReadAsStringAsync(ct);
            var token = Newtonsoft.Json.JsonConvert.DeserializeObject<JToken>(json);
            List<JObject>? results = null;
            if (token is JArray jarr)
                results = jarr.OfType<JObject>().ToList();
            else if (token is JObject jobj)
                results = (jobj["results"] ?? jobj["data"])?.ToObject<List<JObject>>();
            if (results == null || results.Count == 0)
                break;

            foreach (var entry in results)
            {
                if (entry["type"]?.ToObject<string>() != mediaType)
                    continue;
                var is4k = entry["is4k"]?.ToObject<bool?>() ?? false;
                if (is4k != overseerr.Is4K)
                    continue;
                var media = entry["media"] as JObject;
                if (media == null)
                    continue;

                var tmdbId = media["tmdbId"]?.ToObject<int?>();
                if (!tmdbId.HasValue || tmdbId.Value <= 0)
                    continue;

                if (!await IsReleasedAsync(http, overseerr, mediaType, tmdbId.Value, now, ct))
                    continue;

                if (mediaType == "movie")
                    result.TmdbIds.Add(tmdbId.Value);
                else
                {
                    var tvdbId = media["tvdbId"]?.ToObject<int?>();
                    if (tvdbId.HasValue && tvdbId.Value > 0)
                        result.TvdbIds.Add(tvdbId.Value);
                }
            }

            if (results.Count < take)
                break;
            skip += take;
        }

        return result;
    }

    private async Task<bool> IsReleasedAsync(
        HttpClient http,
        OverseerrConfig overseerr,
        string mediaType,
        int tmdbId,
        DateTime now,
        CancellationToken ct)
    {
        if (ReleaseDateCache.TryGetValue(tmdbId, out var cached))
            return cached <= now;

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
            if (date > now)
                return false;
            ReleaseDateCache[tmdbId] = date;
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogWarning("Failed to query release date from Overseerr: {Error}", ex.Message);
            return true;
        }
    }

    internal static void ClearCacheForTests() => ReleaseDateCache.Clear();
}

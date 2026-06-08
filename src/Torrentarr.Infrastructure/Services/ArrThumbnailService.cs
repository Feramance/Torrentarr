using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using Torrentarr.Core.Configuration;
using Torrentarr.Infrastructure.ApiClients.Arr;
using Torrentarr.Infrastructure.Database;
using Microsoft.EntityFrameworkCore;

namespace Torrentarr.Infrastructure.Services;

/// <summary>
/// Proxies Arr poster images with disk cache (qBitrr webui_thumbnails.py parity, simplified).
/// </summary>
public class ArrThumbnailService
{
    private const int MaxBytes = 5 * 1024 * 1024;
    private readonly TorrentarrDbContext _db;
    private readonly TorrentarrConfig _config;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly string _cacheDir;

    public ArrThumbnailService(
        TorrentarrDbContext db,
        TorrentarrConfig config,
        IHttpClientFactory httpClientFactory)
    {
        _db = db;
        _config = config;
        _httpClientFactory = httpClientFactory;
        _cacheDir = Path.Combine(ConfigurationLoader.GetDataDirectoryPath(), "cache", "thumbnails");
        Directory.CreateDirectory(_cacheDir);
    }

    public async Task<(byte[] Bytes, string ContentType)?> GetThumbnailAsync(
        string arrType,
        string category,
        int entryId,
        CancellationToken ct = default)
    {
        var instance = _config.ArrInstances.Values
            .FirstOrDefault(a => CategoryPathHelper.CategoryEquals(a.Category, category));
        if (instance is null || string.IsNullOrWhiteSpace(instance.URI))
            return null;

        var cacheKey = $"{arrType}:{category}:{entryId}";
        var cachePath = Path.Combine(_cacheDir, Sha256Hex(cacheKey)[..40] + ".bin");
        var etagPath = cachePath + ".etag";
        if (File.Exists(cachePath))
        {
            var bytes = await File.ReadAllBytesAsync(cachePath, ct);
            var mime = GuessMime(bytes);
            return (bytes, mime);
        }

        var arrId = await ResolveArrIdAsync(arrType, category, entryId, ct);
        if (arrId is null)
            return null;

        var candidates = BuildCandidateUrls(instance.URI.TrimEnd('/'), arrType, arrId.Value);
        foreach (var url in candidates)
        {
            var fetched = await TryFetchSameHostAsync(url, instance.URI, instance.APIKey, ct);
            if (fetched is null)
                continue;

            await File.WriteAllBytesAsync(cachePath, fetched.Value.Bytes, ct);
            var etag = Convert.ToHexString(SHA256.HashData(fetched.Value.Bytes)).ToLowerInvariant();
            await File.WriteAllTextAsync(etagPath, etag, ct);
            return fetched;
        }

        return null;
    }

    private async Task<int?> ResolveArrIdAsync(string arrType, string category, int entryId, CancellationToken ct)
    {
        return arrType.ToLowerInvariant() switch
        {
            "radarr" => await _db.Movies
                .Where(m => m.ArrInstance == category && m.EntryId == entryId)
                .Select(m => (int?)m.ArrId)
                .FirstOrDefaultAsync(ct),
            "sonarr" => await _db.Series
                .Where(s => s.ArrInstance == category && s.EntryId == entryId)
                .Select(s => (int?)s.ArrId)
                .FirstOrDefaultAsync(ct),
            "lidarr_artist" or "lidarr" => await _db.Artists
                .Where(a => a.ArrInstance == category && a.EntryId == entryId)
                .Select(a => (int?)a.ArrId)
                .FirstOrDefaultAsync(ct),
            _ => null
        };
    }

    private static IEnumerable<string> BuildCandidateUrls(string baseUri, string arrType, int arrId)
    {
        return arrType.ToLowerInvariant() switch
        {
            "radarr" => new[]
            {
                $"{baseUri}/MediaCover/{arrId}/poster.jpg",
                $"{baseUri}/api/v3/MediaCover/{arrId}/poster.jpg"
            },
            "sonarr" => new[]
            {
                $"{baseUri}/MediaCover/{arrId}/poster.jpg",
                $"{baseUri}/api/v3/MediaCover/{arrId}/poster.jpg"
            },
            "lidarr_artist" or "lidarr" => new[]
            {
                $"{baseUri}/api/v1/MediaCover/Artist/{arrId}/poster.jpg",
                $"{baseUri}/api/v1/MediaCover/Artist/{arrId}/poster-250.jpg",
                $"{baseUri}/api/v1/MediaCover/{arrId}/poster.jpg"
            },
            _ => Array.Empty<string>()
        };
    }

    private async Task<(byte[] Bytes, string ContentType)?> TryFetchSameHostAsync(
        string url,
        string arrBaseUri,
        string apiKey,
        CancellationToken ct)
    {
        if (!IsSameHost(url, arrBaseUri))
            return null;

        using var client = _httpClientFactory.CreateClient(nameof(ArrThumbnailService));
        client.Timeout = TimeSpan.FromSeconds(20);
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        if (!url.Contains("apikey=", StringComparison.OrdinalIgnoreCase))
            request.Headers.Add("X-Api-Key", apiKey);

        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        if (!response.IsSuccessStatusCode)
            return null;

        var bytes = await response.Content.ReadAsByteArrayAsync(ct);
        if (bytes.Length == 0 || bytes.Length > MaxBytes)
            return null;

        var contentType = response.Content.Headers.ContentType?.MediaType ?? GuessMime(bytes);
        return (bytes, contentType);
    }

    private static bool IsSameHost(string url, string arrBaseUri)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var u)
            || !Uri.TryCreate(arrBaseUri, UriKind.Absolute, out var b))
            return false;
        return string.Equals(u.Host, b.Host, StringComparison.OrdinalIgnoreCase);
    }

    private static string GuessMime(byte[] bytes)
    {
        if (bytes.Length >= 3 && bytes[0] == 0xFF && bytes[1] == 0xD8)
            return "image/jpeg";
        if (bytes.Length >= 8 && bytes[0] == 0x89 && bytes[1] == 0x50)
            return "image/png";
        if (bytes.Length >= 6 && bytes[0] == 'G' && bytes[1] == 'I')
            return "image/gif";
        return "application/octet-stream";
    }

    private static string Sha256Hex(string input)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(input))).ToLowerInvariant();
}

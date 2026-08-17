using Microsoft.EntityFrameworkCore;
using Torrentarr.Core.Configuration;
using Torrentarr.Infrastructure.Database;

namespace Torrentarr.Infrastructure.Services;

/// <summary>
/// Arr catalog rollup aggregates (qBitrr catalog_rollups.py parity).
/// available = monitored AND has_file; missing = max(monitored - available, 0).
/// </summary>
public class CatalogRollupService
{
    private readonly TorrentarrDbContext _db;
    private readonly Dictionary<string, (DateTime Expires, object Snapshot)> _cache = new();
    private static readonly TimeSpan CacheTtl = TimeSpan.FromSeconds(5);

    public CatalogRollupService(TorrentarrDbContext db) => _db = db;

    public record Counts(int Available, int Monitored, int Missing, int QualityMet = 0, int Requests = 0);

    public Task<(Counts Albums, int AlbumTotal, Counts Tracks)> GetLidarrRollupsAsync(
        string instanceName, CancellationToken ct = default)
        => GetLidarrRollupsAsync([instanceName], ct);

    public async Task<(Counts Albums, int AlbumTotal, Counts Tracks)> GetLidarrRollupsAsync(
        IReadOnlyList<string> instanceKeys, CancellationToken ct = default)
    {
        var keys = Materialize(instanceKeys);
        var cacheKey = CacheKey("lidarr", keys);
        if (TryGetCache(cacheKey, out var cached) && cached is (Counts a, int t, Counts tr))
            return (a, t, tr);

        var albums = await _db.Albums.Where(x => keys.Contains(x.ArrInstance)).ToListAsync(ct);
        var monitored = albums.Count(x => x.Monitored);
        var available = albums.Count(x => x.Monitored && x.HasFile);
        var albumCounts = new Counts(
            available,
            monitored,
            Math.Max(monitored - available, 0),
            albums.Count(x => x.QualityMet),
            albums.Count(x => x.IsRequest));

        var tracks = await _db.Tracks
            .Where(t => keys.Contains(t.ArrInstance))
            .Join(_db.Albums.Where(x => keys.Contains(x.ArrInstance)),
                t => t.AlbumId,
                x => x.EntryId,
                (t, _) => t)
            .ToListAsync(ct);
        var trackMonitored = tracks.Count(t => t.Monitored);
        var trackAvailable = tracks.Count(t => t.Monitored && t.HasFile);
        var trackCounts = new Counts(
            trackAvailable,
            trackMonitored,
            Math.Max(trackMonitored - trackAvailable, 0));

        var snapshot = (albumCounts, albums.Count, trackCounts);
        SetCache(cacheKey, snapshot);
        return snapshot;
    }

    public Task<(Counts Books, int BookTotal)> GetReadarrRollupsAsync(
        string instanceName, CancellationToken ct = default)
        => GetReadarrRollupsAsync([instanceName], ct);

    public async Task<(Counts Books, int BookTotal)> GetReadarrRollupsAsync(
        IReadOnlyList<string> instanceKeys, CancellationToken ct = default)
    {
        var keys = Materialize(instanceKeys);
        var cacheKey = CacheKey("readarr", keys);
        if (TryGetCache(cacheKey, out var cached) && cached is (Counts b, int t))
            return (b, t);

        var books = await _db.Books.Where(x => keys.Contains(x.ArrInstance)).ToListAsync(ct);
        var monitored = books.Count(x => x.Monitored);
        var available = books.Count(x => x.Monitored && x.HasFile);
        var bookCounts = new Counts(
            available,
            monitored,
            Math.Max(monitored - available, 0),
            books.Count(x => x.QualityMet),
            books.Count(x => x.IsRequest));
        var snapshot = (bookCounts, books.Count);
        SetCache(cacheKey, snapshot);
        return snapshot;
    }

    public Task<(Counts Movies, int Total)> GetRadarrRollupsAsync(
        string instanceName, CancellationToken ct = default)
        => GetRadarrRollupsAsync([instanceName], ct);

    public async Task<(Counts Movies, int Total)> GetRadarrRollupsAsync(
        IReadOnlyList<string> instanceKeys, CancellationToken ct = default)
    {
        var keys = Materialize(instanceKeys);
        var cacheKey = CacheKey("radarr", keys);
        if (TryGetCache(cacheKey, out var cached) && cached is (Counts c, int total))
            return (c, total);

        var movies = await _db.Movies.Where(m => keys.Contains(m.ArrInstance)).ToListAsync(ct);
        var monitored = movies.Count(m => m.Monitored);
        var available = movies.Count(m => m.Monitored && m.MovieFileId != 0);
        var counts = new Counts(
            available,
            monitored,
            Math.Max(monitored - available, 0),
            movies.Count(m => m.QualityMet),
            movies.Count(m => m.IsRequest));
        var snapshot = (counts, movies.Count);
        SetCache(cacheKey, snapshot);
        return snapshot;
    }

    public Task<(Counts Episodes, int TotalSeries)> GetSonarrRollupsAsync(
        string instanceName, CancellationToken ct = default)
        => GetSonarrRollupsAsync([instanceName], ct);

    public async Task<(Counts Episodes, int TotalSeries)> GetSonarrRollupsAsync(
        IReadOnlyList<string> instanceKeys, CancellationToken ct = default)
    {
        var keys = Materialize(instanceKeys);
        var cacheKey = CacheKey("sonarr", keys);
        if (TryGetCache(cacheKey, out var cached) && cached is (Counts c, int total))
            return (c, total);

        var episodes = await _db.Episodes.Where(e => keys.Contains(e.ArrInstance)).ToListAsync(ct);
        var monitored = episodes.Count(e => e.Monitored == true);
        var available = episodes.Count(e => e.Monitored == true && e.EpisodeFileId is > 0);
        var counts = new Counts(available, monitored, Math.Max(monitored - available, 0));
        var totalSeries = await _db.Series.CountAsync(s => keys.Contains(s.ArrInstance), ct);
        var snapshot = (counts, totalSeries);
        SetCache(cacheKey, snapshot);
        return snapshot;
    }

    public async Task<(Counts Radarr, Counts Sonarr, Counts LidarrTracks, Counts ReadarrBooks)> GetAggregatedTypeCountsAsync(
        TorrentarrConfig config,
        CancellationToken ct = default)
    {
        var radarr = new Counts(0, 0, 0);
        var sonarr = new Counts(0, 0, 0);
        var lidarrTracks = new Counts(0, 0, 0);
        var readarrBooks = new Counts(0, 0, 0);

        foreach (var kvp in config.ArrInstances)
        {
            var keys = ArrCatalogIdentity.QueryKeys(kvp);
            switch (kvp.Value.Type?.ToLowerInvariant())
            {
                case "radarr":
                    var (movies, _) = await GetRadarrRollupsAsync(keys, ct);
                    radarr = Merge(radarr, movies);
                    break;
                case "sonarr":
                    var (episodes, _) = await GetSonarrRollupsAsync(keys, ct);
                    sonarr = Merge(sonarr, episodes);
                    break;
                case "lidarr":
                    var (_, _, tracks) = await GetLidarrRollupsAsync(keys, ct);
                    lidarrTracks = Merge(lidarrTracks, tracks);
                    break;
                case "readarr":
                    var (books, _) = await GetReadarrRollupsAsync(keys, ct);
                    readarrBooks = Merge(readarrBooks, books);
                    break;
            }
        }

        return (radarr, sonarr, lidarrTracks, readarrBooks);
    }

    private static Counts Merge(Counts a, Counts b) => new(
        a.Available + b.Available,
        a.Monitored + b.Monitored,
        a.Missing + b.Missing,
        a.QualityMet + b.QualityMet,
        a.Requests + b.Requests);

    public void Invalidate(string? instanceName = null)
    {
        if (instanceName is null)
        {
            _cache.Clear();
            return;
        }

        var keys = _cache.Keys.Where(k =>
            k.EndsWith($":{instanceName}", StringComparison.Ordinal)
            || k.Contains($":{instanceName}|", StringComparison.Ordinal)
            || k.Contains($"|{instanceName}", StringComparison.Ordinal)).ToList();
        foreach (var k in keys)
            _cache.Remove(k);
    }

    private bool TryGetCache(string key, out object? snapshot)
    {
        if (_cache.TryGetValue(key, out var entry) && entry.Expires > DateTime.UtcNow)
        {
            snapshot = entry.Snapshot;
            return true;
        }
        snapshot = null;
        return false;
    }

    private void SetCache(string key, object snapshot)
        => _cache[key] = (DateTime.UtcNow.Add(CacheTtl), snapshot);

    private static List<string> Materialize(IReadOnlyList<string> instanceKeys)
        => instanceKeys.Where(k => !string.IsNullOrWhiteSpace(k)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

    private static string CacheKey(string prefix, IReadOnlyList<string> keys)
        => $"{prefix}:{string.Join("|", keys)}";
}

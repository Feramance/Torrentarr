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

    private static Counts FromTotals(int available, int monitored, int qualityMet = 0, int requests = 0)
        => new(available, monitored, Math.Max(monitored - available, 0), qualityMet, requests);

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

        var albums = await _db.Albums.Where(x => keys.Contains(x.ArrInstance))
            .GroupBy(_ => 1)
            .Select(group => new
            {
                Monitored = group.Count(x => x.Monitored),
                Available = group.Count(x => x.Monitored && x.HasFile),
                QualityMet = group.Count(x => x.QualityMet),
                Requests = group.Count(x => x.IsRequest),
                Total = group.Count()
            }).SingleOrDefaultAsync(ct);
        var trackQuery = _db.Tracks
            .Where(t => keys.Contains(t.ArrInstance))
            .Join(_db.Albums.Where(x => keys.Contains(x.ArrInstance)), t => t.AlbumId, x => x.EntryId, (t, _) => t);
        var tracks = await trackQuery
            .GroupBy(_ => 1)
            .Select(group => new
            {
                Monitored = group.Count(x => x.Monitored),
                Available = group.Count(x => x.Monitored && x.HasFile)
            }).SingleOrDefaultAsync(ct);

        var albumCounts = FromTotals(albums?.Available ?? 0, albums?.Monitored ?? 0, albums?.QualityMet ?? 0, albums?.Requests ?? 0);
        var trackCounts = FromTotals(tracks?.Available ?? 0, tracks?.Monitored ?? 0);
        var snapshot = (albumCounts, albums?.Total ?? 0, trackCounts);
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

        var books = await _db.Books.Where(x => keys.Contains(x.ArrInstance))
            .GroupBy(_ => 1)
            .Select(group => new
            {
                Monitored = group.Count(x => x.Monitored),
                Available = group.Count(x => x.Monitored && x.HasFile),
                QualityMet = group.Count(x => x.QualityMet),
                Requests = group.Count(x => x.IsRequest),
                Total = group.Count()
            }).SingleOrDefaultAsync(ct);
        var counts = FromTotals(books?.Available ?? 0, books?.Monitored ?? 0, books?.QualityMet ?? 0, books?.Requests ?? 0);
        var snapshot = (counts, books?.Total ?? 0);
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

        var movies = await _db.Movies.Where(x => keys.Contains(x.ArrInstance))
            .GroupBy(_ => 1)
            .Select(group => new
            {
                Monitored = group.Count(x => x.Monitored),
                Available = group.Count(x => x.Monitored && x.MovieFileId != 0),
                QualityMet = group.Count(x => x.QualityMet),
                Requests = group.Count(x => x.IsRequest),
                Total = group.Count()
            }).SingleOrDefaultAsync(ct);
        var counts = FromTotals(movies?.Available ?? 0, movies?.Monitored ?? 0, movies?.QualityMet ?? 0, movies?.Requests ?? 0);
        var snapshot = (counts, movies?.Total ?? 0);
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

        var episodes = await _db.Episodes.Where(x => keys.Contains(x.ArrInstance))
            .GroupBy(_ => 1)
            .Select(group => new
            {
                Monitored = group.Count(x => x.Monitored == true),
                Available = group.Count(x => x.Monitored == true && x.EpisodeFileId > 0)
            }).SingleOrDefaultAsync(ct);
        var counts = FromTotals(episodes?.Available ?? 0, episodes?.Monitored ?? 0);
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
        => instanceKeys.Where(k => !string.IsNullOrWhiteSpace(k)).Distinct(StringComparer.Ordinal).ToList();

    private static string CacheKey(string prefix, IReadOnlyList<string> keys)
        => $"{prefix}:{string.Join("|", keys)}";
}

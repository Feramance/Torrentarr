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

    public async Task<(Counts Albums, int AlbumTotal, Counts Tracks)> GetLidarrRollupsAsync(string instanceName, CancellationToken ct = default)
    {
        var key = $"lidarr:{instanceName}";
        if (TryGetCache(key, out var cached) && cached is (Counts a, int t, Counts tr))
            return (a, t, tr);

        var albums = await _db.Albums.Where(a => a.ArrInstance == instanceName).ToListAsync(ct);
        var monitored = albums.Count(a => a.Monitored);
        var available = albums.Count(a => a.Monitored && a.HasFile);
        var albumCounts = new Counts(
            available,
            monitored,
            Math.Max(monitored - available, 0),
            albums.Count(a => a.QualityMet),
            albums.Count(a => a.IsRequest));

        var tracks = await _db.Tracks
            .Where(t => t.ArrInstance == instanceName)
            .Join(_db.Albums.Where(a => a.ArrInstance == instanceName),
                t => t.AlbumId,
                a => a.EntryId,
                (t, _) => t)
            .ToListAsync(ct);
        var trackMonitored = tracks.Count(t => t.Monitored);
        var trackAvailable = tracks.Count(t => t.Monitored && t.HasFile);
        var trackCounts = new Counts(
            trackAvailable,
            trackMonitored,
            Math.Max(trackMonitored - trackAvailable, 0));

        var snapshot = (albumCounts, albums.Count, trackCounts);
        SetCache(key, snapshot);
        return snapshot;
    }

    public async Task<(Counts Movies, int Total)> GetRadarrRollupsAsync(string instanceName, CancellationToken ct = default)
    {
        var key = $"radarr:{instanceName}";
        if (TryGetCache(key, out var cached) && cached is (Counts c, int total))
            return (c, total);

        var movies = await _db.Movies.Where(m => m.ArrInstance == instanceName).ToListAsync(ct);
        var monitored = movies.Count(m => m.Monitored);
        var available = movies.Count(m => m.Monitored && m.MovieFileId != 0);
        var counts = new Counts(
            available,
            monitored,
            Math.Max(monitored - available, 0),
            movies.Count(m => m.QualityMet),
            movies.Count(m => m.IsRequest));
        var snapshot = (counts, movies.Count);
        SetCache(key, snapshot);
        return snapshot;
    }

    public async Task<(Counts Episodes, int TotalSeries)> GetSonarrRollupsAsync(string instanceName, CancellationToken ct = default)
    {
        var key = $"sonarr:{instanceName}";
        if (TryGetCache(key, out var cached) && cached is (Counts c, int total))
            return (c, total);

        var episodes = await _db.Episodes.Where(e => e.ArrInstance == instanceName).ToListAsync(ct);
        var monitored = episodes.Count(e => e.Monitored == true);
        var available = episodes.Count(e => e.Monitored == true && e.EpisodeFileId is > 0);
        var counts = new Counts(available, monitored, Math.Max(monitored - available, 0));
        var totalSeries = await _db.Series.CountAsync(s => s.ArrInstance == instanceName, ct);
        var snapshot = (counts, totalSeries);
        SetCache(key, snapshot);
        return snapshot;
    }

    public async Task<(Counts Radarr, Counts Sonarr, Counts LidarrTracks)> GetAggregatedTypeCountsAsync(
        TorrentarrConfig config,
        CancellationToken ct = default)
    {
        var radarr = new Counts(0, 0, 0);
        var sonarr = new Counts(0, 0, 0);
        var lidarrTracks = new Counts(0, 0, 0);

        foreach (var inst in config.ArrInstances.Values)
        {
            switch (inst.Type?.ToLowerInvariant())
            {
                case "radarr":
                    var (movies, _) = await GetRadarrRollupsAsync(inst.Category, ct);
                    radarr = Merge(radarr, movies);
                    break;
                case "sonarr":
                    var (episodes, _) = await GetSonarrRollupsAsync(inst.Category, ct);
                    sonarr = Merge(sonarr, episodes);
                    break;
                case "lidarr":
                    var (_, _, tracks) = await GetLidarrRollupsAsync(inst.Category, ct);
                    lidarrTracks = Merge(lidarrTracks, tracks);
                    break;
            }
        }

        return (radarr, sonarr, lidarrTracks);
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

        var keys = _cache.Keys.Where(k => k.EndsWith($":{instanceName}", StringComparison.Ordinal)).ToList();
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
}

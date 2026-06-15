using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Torrentarr.Core.Configuration;
using Torrentarr.Infrastructure.Database;
using Torrentarr.Infrastructure.Database.Models;
using Torrentarr.Infrastructure.Services;

namespace Torrentarr.Infrastructure.Endpoints;

/// <summary>Arr catalog browse endpoints (qBitrr 5.12.0 parity): artists, thumbnails, deep links.</summary>
public static class ArrCatalogEndpoints
{
    public static void MapArrCatalogEndpoints(this WebApplication app)
    {
        MapLidarrArtists(app, "/web/lidarr/{category}/artists");
        MapLidarrArtists(app, "/api/lidarr/{category}/artists");
        MapLidarrArtistDetail(app, "/web/lidarr/{category}/artist/{artistId:int}");
        MapLidarrArtistDetail(app, "/api/lidarr/{category}/artist/{artistId:int}");
        MapThumbnail(app, "/web/lidarr/{category}/artist/{artistId:int}/thumbnail", "lidarr_artist");
        MapThumbnail(app, "/api/lidarr/{category}/artist/{artistId:int}/thumbnail", "lidarr_artist");
        MapThumbnail(app, "/web/radarr/{category}/movie/{id:int}/thumbnail", "radarr");
        MapThumbnail(app, "/api/radarr/{category}/movie/{id:int}/thumbnail", "radarr");
        MapThumbnail(app, "/web/sonarr/{category}/series/{id:int}/thumbnail", "sonarr");
        MapThumbnail(app, "/api/sonarr/{category}/series/{id:int}/thumbnail", "sonarr");
        MapArrOpen(app, "/web/arr/{category}/open/{kind}/{entryId:int}");
        MapArrOpen(app, "/api/arr/{category}/open/{kind}/{entryId:int}");
    }

    private static void MapLidarrArtists(WebApplication app, string pattern)
    {
        app.MapGet(pattern, async (
            string category,
            TorrentarrDbContext db,
            CatalogRollupService rollups,
            int? page,
            int? page_size,
            string? q,
            bool? monitored,
            bool? missing,
            string? reason) =>
        {
            var currentPage = page ?? 0;
            var pageSize = Math.Clamp(page_size ?? 50, 1, 1000);
            var missingOnly = missing == true;
            var reasonFilter = string.IsNullOrWhiteSpace(reason) || reason.Equals("all", StringComparison.OrdinalIgnoreCase)
                ? null
                : reason.Trim();

            var (albumCounts, albumTotal, trackCounts) = await rollups.GetLidarrRollupsAsync(category);

            var query = db.Artists.Where(a => a.ArrInstance == category);
            if (!string.IsNullOrWhiteSpace(q))
                query = query.Where(a => a.Title != null && a.Title.Contains(q));
            if (monitored.HasValue)
                query = query.Where(a => a.Monitored == monitored.Value);

            if (missingOnly || reasonFilter is not null)
            {
                var albumQuery = db.Albums.Where(al => al.ArrInstance == category);
                if (missingOnly)
                    albumQuery = albumQuery.Where(al => al.Monitored && !al.HasFile);
                if (reasonFilter is not null)
                {
                    if (reasonFilter.Equals("Not being searched", StringComparison.OrdinalIgnoreCase))
                        albumQuery = albumQuery.Where(al => al.Reason == null || al.Reason == "Not being searched");
                    else
                        albumQuery = albumQuery.Where(al => al.Reason == reasonFilter);
                }

                var artistIds = await albumQuery.Select(al => al.ArtistId).Distinct().ToListAsync();
                query = query.Where(a => artistIds.Contains(a.EntryId));
            }

            var total = await query.CountAsync();
            var artists = await query
                .OrderBy(a => a.Title)
                .Skip(currentPage * pageSize)
                .Take(pageSize)
                .ToListAsync();

            if (total == 0 && albumTotal > 0 && (missingOnly || reasonFilter is not null || !string.IsNullOrWhiteSpace(q)))
            {
                var albumFallback = db.Albums.Where(al => al.ArrInstance == category);
                if (!string.IsNullOrWhiteSpace(q))
                    albumFallback = albumFallback.Where(al => al.ArtistTitle != null && al.ArtistTitle.Contains(q));
                if (missingOnly)
                    albumFallback = albumFallback.Where(al => al.Monitored && !al.HasFile);
                if (reasonFilter is not null)
                {
                    if (reasonFilter.Equals("Not being searched", StringComparison.OrdinalIgnoreCase))
                        albumFallback = albumFallback.Where(al => al.Reason == null || al.Reason == "Not being searched");
                    else
                        albumFallback = albumFallback.Where(al => al.Reason == reasonFilter);
                }

                var grouped = await albumFallback
                    .GroupBy(al => al.ArtistId)
                    .Select(g => new
                    {
                        ArtistId = g.Key,
                        Title = g.Min(al => al.ArtistTitle) ?? "",
                        Monitored = g.Max(al => al.Monitored ? 1 : 0) == 1
                    })
                    .ToListAsync();

                if (monitored.HasValue)
                    grouped = grouped.Where(g => g.Monitored == monitored.Value).ToList();

                total = grouped.Count;
                artists = grouped
                    .OrderBy(g => g.Title)
                    .Skip(currentPage * pageSize)
                    .Take(pageSize)
                    .Select(g => new ArtistFilesModel
                    {
                        EntryId = g.ArtistId,
                        Title = g.Title,
                        Monitored = g.Monitored,
                        ArrInstance = category
                    })
                    .ToList();
            }

            var artistIdsListed = artists.Select(a => a.EntryId).ToList();
            var albumStats = await db.Albums
                .Where(al => al.ArrInstance == category && artistIdsListed.Contains(al.ArtistId))
                .GroupBy(al => al.ArtistId)
                .Select(g => new
                {
                    ArtistId = g.Key,
                    Monitored = g.Count(a => a.Monitored),
                    Available = g.Count(a => a.Monitored && a.HasFile)
                })
                .ToListAsync();

            var statsByArtist = albumStats.ToDictionary(x => x.ArtistId);

            return Results.Ok(new
            {
                category,
                counts = new
                {
                    available = albumCounts.Available,
                    monitored = albumCounts.Monitored,
                    missing = albumCounts.Missing,
                    quality_met = albumCounts.QualityMet,
                    requests = albumCounts.Requests
                },
                counts_tracks = new
                {
                    available = trackCounts.Available,
                    monitored = trackCounts.Monitored,
                    missing = trackCounts.Missing
                },
                album_total = albumTotal,
                total,
                page = currentPage,
                page_size = pageSize,
                artists = artists.Select(a =>
                {
                    statsByArtist.TryGetValue(a.EntryId, out var st);
                    var mon = st?.Monitored ?? 0;
                    var avail = st?.Available ?? 0;
                    return new
                    {
                        artist = new
                        {
                            id = a.EntryId,
                            name = a.Title,
                            monitored = a.Monitored,
                            qualityProfileName = a.QualityProfileName,
                            searched = a.Searched,
                            albumsMonitored = mon,
                            albumsAvailable = avail,
                            albumsMissing = Math.Max(mon - avail, 0)
                        }
                    };
                })
            });
        });
    }

    private static void MapLidarrArtistDetail(WebApplication app, string pattern)
    {
        app.MapGet(pattern, async (
            string category,
            int artistId,
            TorrentarrDbContext db,
            CatalogRollupService rollups) =>
        {
            var artist = await db.Artists
                .FirstOrDefaultAsync(a => a.ArrInstance == category && a.EntryId == artistId);
            if (artist is null)
                return Results.NotFound(new { error = "Artist not found" });

            var (albumCounts, _, trackCounts) = await rollups.GetLidarrRollupsAsync(category);
            var albums = await db.Albums
                .Where(al => al.ArrInstance == category && al.ArtistId == artistId)
                .OrderBy(al => al.Title)
                .ToListAsync();

            var albumIds = albums.Select(a => a.EntryId).ToList();
            var tracks = await db.Tracks
                .Where(t => t.ArrInstance == category && albumIds.Contains(t.AlbumId))
                .ToListAsync();

            return Results.Ok(new
            {
                category,
                counts = new
                {
                    available = albumCounts.Available,
                    monitored = albumCounts.Monitored,
                    missing = albumCounts.Missing,
                    quality_met = albumCounts.QualityMet,
                    requests = albumCounts.Requests
                },
                counts_tracks = new
                {
                    available = trackCounts.Available,
                    monitored = trackCounts.Monitored,
                    missing = trackCounts.Missing
                },
                artist = new
                {
                    id = artist.EntryId,
                    name = artist.Title,
                    monitored = artist.Monitored,
                    qualityProfileName = artist.QualityProfileName,
                    searched = artist.Searched
                },
                albums = albums.Select(al => new
                {
                    album = new
                    {
                        id = al.EntryId,
                        title = al.Title,
                        artistId = al.ArtistId,
                        artistName = al.ArtistTitle,
                        monitored = al.Monitored,
                        hasFile = al.HasFile,
                        foreignAlbumId = al.ForeignAlbumId,
                        releaseDate = al.ReleaseDate,
                        qualityMet = al.QualityMet,
                        reason = al.Reason,
                        qualityProfileId = al.QualityProfileId,
                        qualityProfileName = al.QualityProfileName
                    },
                    tracks = tracks.Where(t => t.AlbumId == al.EntryId).Select(t => new
                    {
                        id = t.EntryId,
                        trackNumber = t.TrackNumber,
                        title = t.Title,
                        duration = t.Duration,
                        hasFile = t.HasFile,
                        monitored = t.Monitored
                    })
                })
            });
        });
    }

    private static void MapThumbnail(WebApplication app, string pattern, string kind)
    {
        app.MapGet(pattern, async (
            HttpContext httpContext,
            string category,
            ArrThumbnailService thumbnails,
            CancellationToken ct) =>
        {
            if (!httpContext.Request.RouteValues.TryGetValue("id", out var idObj)
                && !httpContext.Request.RouteValues.TryGetValue("artistId", out idObj))
                return Results.BadRequest();
            if (!int.TryParse(idObj?.ToString(), out var id))
                return Results.BadRequest();

            var result = await thumbnails.GetThumbnailAsync(kind, category, id, ct);
            if (result is null)
                return Results.NotFound();
            return Results.File(result.Value.Bytes, result.Value.ContentType);
        });
    }

    private static void MapArrOpen(WebApplication app, string pattern)
    {
        app.MapGet(pattern, async (
            string category,
            string kind,
            int entryId,
            TorrentarrConfig cfg,
            TorrentarrDbContext db) =>
        {
            var instance = cfg.ArrInstances.Values
                .FirstOrDefault(a => CategoryPathHelper.CategoryEquals(a.Category, category));
            if (instance is null || string.IsNullOrWhiteSpace(instance.URI))
                return Results.NotFound(new { error = "Unknown section" });

            var baseUri = instance.URI.TrimEnd('/');
            var slug = await ResolveOpenSlugAsync(kind, category, entryId, db);
            if (slug is null)
                return Results.NotFound(new { error = "Item not found" });

            var path = kind.ToLowerInvariant() switch
            {
                "movie" => $"/movie/{slug}",
                "series" => $"/series/{slug}",
                "artist" => $"/artist/{slug}",
                _ => null
            };
            if (path is null)
                return Results.BadRequest(new { error = "Unknown kind" });

            return Results.Redirect($"{baseUri}{path}");
        });
    }

    private static async Task<string?> ResolveOpenSlugAsync(
        string kind,
        string category,
        int entryId,
        TorrentarrDbContext db)
    {
        return kind.ToLowerInvariant() switch
        {
            "movie" => await db.Movies
                .Where(m => m.ArrInstance == category && m.EntryId == entryId)
                .Select(m => m.ArrId.ToString())
                .FirstOrDefaultAsync(),
            "series" => await db.Series
                .Where(s => s.ArrInstance == category && s.EntryId == entryId)
                .Select(s => s.ArrId.ToString())
                .FirstOrDefaultAsync(),
            "artist" => await db.Artists
                .Where(a => a.ArrInstance == category && a.EntryId == entryId)
                .Select(a => a.ArrId.ToString())
                .FirstOrDefaultAsync(),
            _ => null
        };
    }
}

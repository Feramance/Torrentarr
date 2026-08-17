using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Torrentarr.Core.Configuration;
using Torrentarr.Infrastructure.Database;
using Torrentarr.Infrastructure.Database.Models;
using Torrentarr.Infrastructure.Services;
using Xunit;

namespace Torrentarr.Infrastructure.Tests.Services;

public class CatalogRollupServiceTests
{
    private static TorrentarrDbContext CreateDb()
    {
        var options = new DbContextOptionsBuilder<TorrentarrDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        return new TorrentarrDbContext(options);
    }

    [Fact]
    public async Task GetRadarrRollups_AvailableRequiresMonitoredAndHasFile()
    {
        await using var db = CreateDb();
        db.Movies.AddRange(
            new MoviesFilesModel { EntryId = 1, ArrInstance = "radarr", Monitored = true, MovieFileId = 5, Title = "A" },
            new MoviesFilesModel { EntryId = 2, ArrInstance = "radarr", Monitored = true, MovieFileId = 0, Title = "B" },
            new MoviesFilesModel { EntryId = 3, ArrInstance = "radarr", Monitored = false, MovieFileId = 0, Title = "C" });
        await db.SaveChangesAsync();

        var svc = new CatalogRollupService(db);
        var (counts, total) = await svc.GetRadarrRollupsAsync("radarr");

        total.Should().Be(3);
        counts.Monitored.Should().Be(2);
        counts.Available.Should().Be(1);
        counts.Missing.Should().Be(1);
    }

    [Fact]
    public async Task GetRadarrRollups_ReturnsCachedSnapshotWithinTtl()
    {
        await using var db = CreateDb();
        db.Movies.Add(new MoviesFilesModel
        {
            EntryId = 1,
            ArrInstance = "radarr",
            Monitored = true,
            MovieFileId = 1,
            Title = "A"
        });
        await db.SaveChangesAsync();

        var svc = new CatalogRollupService(db);
        var first = await svc.GetRadarrRollupsAsync("radarr");

        db.Movies.Add(new MoviesFilesModel
        {
            EntryId = 2,
            ArrInstance = "radarr",
            Monitored = true,
            MovieFileId = 0,
            Title = "B"
        });
        await db.SaveChangesAsync();

        var second = await svc.GetRadarrRollupsAsync("radarr");
        second.Should().Be(first);
        second.Total.Should().Be(1);
    }

    [Fact]
    public async Task GetSonarrRollups_AvailableRequiresMonitoredAndEpisodeFile()
    {
        await using var db = CreateDb();
        db.Series.Add(new SeriesFilesModel { EntryId = 10, ArrInstance = "sonarr", Title = "Show" });
        db.Episodes.AddRange(
            new EpisodeFilesModel { EntryId = 1, ArrInstance = "sonarr", SeriesId = 10, Monitored = true, EpisodeFileId = 5 },
            new EpisodeFilesModel { EntryId = 2, ArrInstance = "sonarr", SeriesId = 10, Monitored = true, EpisodeFileId = 0 },
            new EpisodeFilesModel { EntryId = 3, ArrInstance = "sonarr", SeriesId = 10, Monitored = false, EpisodeFileId = 0 });
        await db.SaveChangesAsync();

        var svc = new CatalogRollupService(db);
        var (counts, totalSeries) = await svc.GetSonarrRollupsAsync("sonarr");

        totalSeries.Should().Be(1);
        counts.Monitored.Should().Be(2);
        counts.Available.Should().Be(1);
        counts.Missing.Should().Be(1);
    }

    [Fact]
    public async Task GetLidarrRollups_CountsAlbumsAndTracks()
    {
        await using var db = CreateDb();
        db.Albums.AddRange(
            new AlbumFilesModel { EntryId = 1, ArrInstance = "lidarr", ArtistId = 1, Monitored = true, HasFile = true, Title = "A" },
            new AlbumFilesModel { EntryId = 2, ArrInstance = "lidarr", ArtistId = 1, Monitored = true, HasFile = false, Title = "B" },
            new AlbumFilesModel { EntryId = 3, ArrInstance = "lidarr", ArtistId = 2, Monitored = false, HasFile = false, Title = "C" });
        db.Tracks.AddRange(
            new TrackFilesModel { EntryId = 10, ArrInstance = "lidarr", AlbumId = 1, Monitored = true, HasFile = true },
            new TrackFilesModel { EntryId = 11, ArrInstance = "lidarr", AlbumId = 2, Monitored = true, HasFile = false },
            new TrackFilesModel { EntryId = 12, ArrInstance = "lidarr", AlbumId = 3, Monitored = false, HasFile = false });
        await db.SaveChangesAsync();

        var svc = new CatalogRollupService(db);
        var (albumCounts, albumTotal, trackCounts) = await svc.GetLidarrRollupsAsync("lidarr");

        albumTotal.Should().Be(3);
        albumCounts.Monitored.Should().Be(2);
        albumCounts.Available.Should().Be(1);
        albumCounts.Missing.Should().Be(1);
        trackCounts.Monitored.Should().Be(2);
        trackCounts.Available.Should().Be(1);
        trackCounts.Missing.Should().Be(1);
    }

    [Fact]
    public async Task GetAggregatedTypeCounts_AggregatesAcrossInstances()
    {
        await using var db = CreateDb();
        db.Movies.Add(new MoviesFilesModel
        {
            EntryId = 1,
            ArrInstance = "radarr",
            Monitored = true,
            MovieFileId = 1,
            Title = "Movie"
        });
        db.Episodes.Add(new EpisodeFilesModel
        {
            EntryId = 1,
            ArrInstance = "sonarr",
            SeriesId = 1,
            Monitored = true,
            EpisodeFileId = 1
        });
        db.Albums.Add(new AlbumFilesModel
        {
            EntryId = 1,
            ArrInstance = "lidarr",
            ArtistId = 1,
            Monitored = true,
            HasFile = true,
            Title = "Album"
        });
        db.Tracks.Add(new TrackFilesModel
        {
            EntryId = 1,
            ArrInstance = "lidarr",
            AlbumId = 1,
            Monitored = true,
            HasFile = true
        });
        await db.SaveChangesAsync();

        db.Books.Add(new BookFilesModel
        {
            EntryId = 1,
            ArrInstance = "readarr",
            Monitored = true,
            HasFile = true,
            Title = "Dune"
        });
        await db.SaveChangesAsync();

        var config = new TorrentarrConfig
        {
            ArrInstances = new Dictionary<string, ArrInstanceConfig>
            {
                ["Radarr"] = new() { Category = "radarr", Type = "radarr" },
                ["Sonarr"] = new() { Category = "sonarr", Type = "sonarr" },
                ["Lidarr"] = new() { Category = "lidarr", Type = "lidarr" },
                ["Readarr"] = new() { Category = "readarr", Type = "readarr" }
            }
        };

        var svc = new CatalogRollupService(db);
        var (radarr, sonarr, lidarrTracks, readarrBooks) = await svc.GetAggregatedTypeCountsAsync(config);

        radarr.Available.Should().Be(1);
        sonarr.Available.Should().Be(1);
        lidarrTracks.Available.Should().Be(1);
        readarrBooks.Available.Should().Be(1);
    }
}

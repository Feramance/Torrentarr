using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Torrentarr.Core.Configuration;
using Torrentarr.Core.Services;
using Torrentarr.Infrastructure.Database;
using Torrentarr.Infrastructure.Database.Models;
using Torrentarr.Infrastructure.Services;
using Xunit;

namespace Torrentarr.Infrastructure.Tests.Services;

public class ArrMediaServiceSearchByYearTests
{
    private static (ArrMediaService Service, TorrentarrDbContext Db) Create(SearchYearCursor cursor)
    {
        var options = new DbContextOptionsBuilder<TorrentarrDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        var db = new TorrentarrDbContext(options);
        var cfg = new TorrentarrConfig();
        var mockSearchExecutor = new Mock<ISearchExecutor>();
        mockSearchExecutor.Setup(x => x.ExecuteSearchesAsync(It.IsAny<string>(), It.IsAny<IEnumerable<SearchCandidate>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SearchResult());
        mockSearchExecutor.Setup(x => x.GetActiveCommandCountAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(0);
        mockSearchExecutor.Setup(x => x.CanSearch(It.IsAny<int>(), It.IsAny<int>()))
            .Returns(true);
        var mockSync = new Mock<ArrSyncService>(
            NullLogger<ArrSyncService>.Instance, cfg, db, new DatabaseRestartCoordinator());
        var svc = new ArrMediaService(
            NullLogger<ArrMediaService>.Instance, db, cfg, mockSearchExecutor.Object, mockSync.Object, cursor);
        return (svc, db);
    }

    [Fact]
    public async Task ApplySearchByYear_KeepsCurrentYearAndTodaysRelease()
    {
        var cursor = new SearchYearCursor();
        var (svc, db) = Create(cursor);
        db.Movies.AddRange(
            new MoviesFilesModel { Title = "Old", ArrInstance = "Radarr", Year = 2010, ArrId = 1, Monitored = true },
            new MoviesFilesModel { Title = "New", ArrInstance = "Radarr", Year = 2020, ArrId = 2, Monitored = true });
        await db.SaveChangesAsync();

        var arr = new ArrInstanceConfig { Type = "radarr", Search = new SearchConfig { SearchByYear = true } };
        var candidates = new List<SearchCandidate>
        {
            new() { Title = "Old", Year = 2010 },
            new() { Title = "New", Year = 2020 },
            new() { Title = "Today", Year = 1999, IsTodaysRelease = true }
        };

        var filtered = await svc.ApplySearchByYearAsync("Radarr", arr, candidates, CancellationToken.None);
        filtered.Select(c => c.Title).Should().BeEquivalentTo("Old", "Today");
    }

    [Fact]
    public async Task ApplySearchByYear_SkipsLidarr()
    {
        var (svc, _) = Create(new SearchYearCursor());
        var arr = new ArrInstanceConfig { Type = "lidarr", Search = new SearchConfig { SearchByYear = true } };
        var candidates = new List<SearchCandidate>
        {
            new() { Title = "Album A", Year = 2010 },
            new() { Title = "Album B", Year = 2020 }
        };

        var filtered = await svc.ApplySearchByYearAsync("Lidarr", arr, candidates, CancellationToken.None);
        filtered.Should().HaveCount(2);
    }

    [Fact]
    public async Task CollectYears_IgnoresUnmonitoredRows()
    {
        var (svc, db) = Create(new SearchYearCursor());
        db.Movies.AddRange(
            new MoviesFilesModel { Title = "Unmon", ArrInstance = "Radarr", Year = 1999, ArrId = 1, Monitored = false },
            new MoviesFilesModel { Title = "Mon", ArrInstance = "Radarr", Year = 2020, ArrId = 2, Monitored = true });
        db.Books.AddRange(
            new BookFilesModel
            {
                Title = "OldUnmon",
                ArrInstance = "Readarr",
                ArrId = 1,
                Monitored = false,
                ReleaseDate = new DateTime(2001, 1, 1, 0, 0, 0, DateTimeKind.Utc)
            },
            new BookFilesModel
            {
                Title = "NewMon",
                ArrInstance = "Readarr",
                ArrId = 2,
                Monitored = true,
                ReleaseDate = new DateTime(2018, 6, 1, 0, 0, 0, DateTimeKind.Utc)
            });
        await db.SaveChangesAsync();

        var movieYears = await svc.CollectYearsAsync(
            "Radarr", new ArrInstanceConfig { Type = "radarr" }, CancellationToken.None);
        movieYears.Should().Equal(2020);

        var bookYears = await svc.CollectYearsAsync(
            "Readarr", new ArrInstanceConfig { Type = "readarr" }, CancellationToken.None);
        bookYears.Should().Equal(2018);
    }
}

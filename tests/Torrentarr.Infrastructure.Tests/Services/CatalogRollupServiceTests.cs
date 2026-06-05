using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Torrentarr.Infrastructure.Database;
using Torrentarr.Infrastructure.Database.Models;
using Torrentarr.Infrastructure.Services;
using Xunit;

namespace Torrentarr.Infrastructure.Tests.Services;

public class CatalogRollupServiceTests
{
    [Fact]
    public async Task GetRadarrRollups_AvailableRequiresMonitoredAndHasFile()
    {
        var options = new DbContextOptionsBuilder<TorrentarrDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        await using var db = new TorrentarrDbContext(options);
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
}

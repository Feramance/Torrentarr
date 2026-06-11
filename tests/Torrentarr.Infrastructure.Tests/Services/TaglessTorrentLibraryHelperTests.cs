using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Torrentarr.Infrastructure.Database;
using Torrentarr.Infrastructure.Services;
using Xunit;

namespace Torrentarr.Infrastructure.Tests.Services;

public class TaglessTorrentLibraryHelperTests
{
    private static TorrentarrDbContext CreateDb()
    {
        var options = new DbContextOptionsBuilder<TorrentarrDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        return new TorrentarrDbContext(options);
    }

    [Fact]
    public async Task SetFreeSpacePaused_WhenRowMissing_InsertsRowWithFlagTrue()
    {
        await using var db = CreateDb();

        await TaglessTorrentLibraryHelper.SetFreeSpacePausedAsync(
            db, "abc123", "radarr", "qBit", paused: true);

        var entry = await db.TorrentLibrary.SingleAsync();
        entry.Hash.Should().Be("abc123");
        entry.Category.Should().Be("radarr");
        entry.QbitInstance.Should().Be("qBit");
        entry.FreeSpacePaused.Should().BeTrue();
    }

    [Fact]
    public async Task SetFreeSpacePaused_WhenRowExists_UpdatesExistingRow()
    {
        await using var db = CreateDb();
        db.TorrentLibrary.Add(new()
        {
            Hash = "abc123",
            Category = "radarr",
            QbitInstance = "qBit",
            FreeSpacePaused = false
        });
        await db.SaveChangesAsync();

        await TaglessTorrentLibraryHelper.SetFreeSpacePausedAsync(
            db, "abc123", "radarr", "qBit", paused: true);

        var entry = await db.TorrentLibrary.SingleAsync();
        entry.FreeSpacePaused.Should().BeTrue();
    }

    [Fact]
    public async Task SetFreeSpacePaused_WhenRowMissingAndClearing_DoesNotInsert()
    {
        await using var db = CreateDb();

        await TaglessTorrentLibraryHelper.SetFreeSpacePausedAsync(
            db, "abc123", "radarr", "qBit", paused: false);

        (await db.TorrentLibrary.CountAsync()).Should().Be(0);
    }
}

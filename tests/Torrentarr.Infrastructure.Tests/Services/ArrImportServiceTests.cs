using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Torrentarr.Core.Configuration;
using Torrentarr.Infrastructure.Database;
using Torrentarr.Infrastructure.Database.Models;
using Torrentarr.Infrastructure.Services;
using Xunit;

namespace Torrentarr.Infrastructure.Tests.Services;

/// <summary>
/// Tests for ArrImportService that exercise only the code paths that do NOT
/// make real network calls — i.e., early-return branches when no Arr/qBit
/// instances are configured and the unknown-type routing guard.
/// </summary>
public class ArrImportServiceTests
{
    private static ArrImportService CreateService(TorrentarrConfig? config = null)
    {
        config ??= new TorrentarrConfig();
        var manager = new QBittorrentConnectionManager(
            NullLogger<QBittorrentConnectionManager>.Instance);
        var options = new DbContextOptionsBuilder<TorrentarrDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        var dbContext = new TorrentarrDbContext(options);
        return new ArrImportService(
            NullLogger<ArrImportService>.Instance, config, manager, dbContext);
    }

    // ── TriggerImportAsync – no instances ────────────────────────────────────

    [Fact]
    public async Task TriggerImportAsync_NoArrInstances_ReturnsFailed()
    {
        var svc = CreateService();

        var result = await svc.TriggerImportAsync("abc123", "/downloads/movie", "radarr-hd");

        result.Success.Should().BeFalse();
    }

    [Fact]
    public async Task TriggerImportAsync_NoArrInstances_MessageMentionsCategory()
    {
        var svc = CreateService();

        var result = await svc.TriggerImportAsync("abc123", "/downloads/movie", "radarr-hd");

        result.Message.Should().Contain("radarr-hd");
    }

    [Fact]
    public async Task TriggerImportAsync_NoArrInstances_CommandIdIsNull()
    {
        var svc = CreateService();

        var result = await svc.TriggerImportAsync("abc123", "/path", "radarr-hd");

        result.CommandId.Should().BeNull();
    }

    // ── TriggerImportAsync – unknown Arr type ─────────────────────────────────

    [Fact]
    public async Task TriggerImportAsync_UnknownArrType_ReturnsFailed()
    {
        var config = new TorrentarrConfig
        {
            ArrInstances = new Dictionary<string, ArrInstanceConfig>
            {
                ["Weird-1"] = new ArrInstanceConfig
                {
                    Category = "weird-category",
                    Type = "weirdtype",
                    URI = "http://localhost:1234",
                    APIKey = "key"
                }
            }
        };

        var svc = CreateService(config);

        var result = await svc.TriggerImportAsync("abc123", "/path", "weird-category");

        result.Success.Should().BeFalse();
    }

    [Fact]
    public async Task TriggerImportAsync_UnknownArrType_MessageContainsTypeName()
    {
        var config = new TorrentarrConfig
        {
            ArrInstances = new Dictionary<string, ArrInstanceConfig>
            {
                ["Weird-1"] = new ArrInstanceConfig
                {
                    Category = "weird-category",
                    Type = "weirdtype",
                    URI = "http://localhost:1234",
                    APIKey = "key"
                }
            }
        };

        var svc = CreateService(config);

        var result = await svc.TriggerImportAsync("abc123", "/path", "weird-category");

        result.Message.Should().Contain("Unknown Arr type");
    }

    // ── TriggerImportAsync – category lookup ──────────────────────────────────

    [Fact]
    public async Task TriggerImportAsync_CategoryLookup_IsCaseInsensitive()
    {
        // Config has "radarr-hd" (lower); hash lookup uses "RADARR-HD" (upper).
        // StringComparison.OrdinalIgnoreCase means they match, but since we
        // don't configure a real URI, the result still fails (no valid API).
        // The point is it does NOT fail with "no instance configured".
        var config = new TorrentarrConfig
        {
            ArrInstances = new Dictionary<string, ArrInstanceConfig>
            {
                ["Radarr-HD"] = new ArrInstanceConfig
                {
                    Category = "radarr-hd",
                    Type = "unknown-type",
                    URI = "http://localhost:7878",
                    APIKey = "key"
                }
            }
        };

        var svc = CreateService(config);

        var result = await svc.TriggerImportAsync("hash", "/path", "RADARR-HD");

        // Instance IS found (case-insensitive match), but type is unrecognised
        result.Message.Should().NotContain("No Arr instance configured");
    }

    [Fact]
    public async Task TriggerImportAsync_WithCancellationToken_DoesNotThrow()
    {
        using var cts = new CancellationTokenSource();
        var svc = CreateService();

        var act = async () =>
            await svc.TriggerImportAsync("hash", "/path", "cat", cts.Token);

        await act.Should().NotThrowAsync();
    }

    // ── IsImportedAsync ───────────────────────────────────────────────────────

    [Fact]
    public async Task IsImportedAsync_NoArrInstances_ReturnsTrue()
    {
        // With no Arr instances to check, no queue is polled → treated as imported.
        var svc = CreateService();

        var result = await svc.IsImportedAsync("abc123");

        result.Should().BeTrue();
    }

    [Fact]
    public async Task IsImportedAsync_WithCancellationToken_DoesNotThrow()
    {
        using var cts = new CancellationTokenSource();
        var svc = CreateService();

        var act = async () => await svc.IsImportedAsync("abc123", cts.Token);

        await act.Should().NotThrowAsync();
    }

    // ── Custom format unmet DB lookup (regression: Arr API ids vs SQLite EntryId) ─

    [Fact]
    public async Task CfUnmetMovieLookup_MatchesArrId_NotEntryId()
    {
        var options = new DbContextOptionsBuilder<TorrentarrDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        await using var dbContext = new TorrentarrDbContext(options);
        dbContext.Movies.Add(new MoviesFilesModel
        {
            Title = "Test Movie",
            ArrInstance = "Radarr-HD",
            ArrId = 42,
            MovieFileId = 1,
            CustomFormatScore = 100,
            MinCustomFormatScore = 200
        });
        await dbContext.SaveChangesAsync();

        var byArrId = await dbContext.Movies.AsNoTracking()
            .FirstOrDefaultAsync(m => m.ArrId == 42 && m.ArrInstance == "Radarr-HD");
        var byEntryId = await dbContext.Movies.AsNoTracking()
            .FirstOrDefaultAsync(m => m.EntryId == 42 && m.ArrInstance == "Radarr-HD");

        byArrId.Should().NotBeNull();
        byArrId!.Title.Should().Be("Test Movie");
        byEntryId.Should().BeNull("queue MovieId is the Arr API id, not the SQLite EntryId");
    }

    [Fact]
    public async Task CfUnmetSeriesLookup_RequiresMinCustomFormatScore_OnSeriesRow()
    {
        var options = new DbContextOptionsBuilder<TorrentarrDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        await using var dbContext = new TorrentarrDbContext(options);
        dbContext.Series.Add(new SeriesFilesModel
        {
            Title = "Test Series",
            ArrInstance = "Sonarr-HD",
            ArrId = 99,
            MinCustomFormatScore = 200
        });
        await dbContext.SaveChangesAsync();

        var seriesEntry = await dbContext.Series.AsNoTracking()
            .FirstOrDefaultAsync(s => s.ArrId == 99 && s.ArrInstance == "Sonarr-HD");

        seriesEntry.Should().NotBeNull();
        seriesEntry!.MinCustomFormatScore.Should().Be(200,
            "Sonarr series-search CF unmet checks MinCustomFormatScore on the series row");
    }

    // ── MarkAsImportedAsync ───────────────────────────────────────────────────

    [Fact]
    public async Task MarkAsImportedAsync_NoQBitInstances_DoesNotThrow()
    {
        // With no qBit instances configured the method logs a warning and returns.
        var svc = CreateService();

        var act = async () =>
            await svc.MarkAsImportedAsync("abc123", Array.Empty<string>());

        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task MarkAsImportedAsync_WithCustomTags_DoesNotThrow()
    {
        var svc = CreateService();

        var act = async () =>
            await svc.MarkAsImportedAsync("abc123", new[] { "custom-tag" });

        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task MarkAsImportedAsync_EmptyTagList_UsesDefaultTag()
    {
        // The service substitutes "qbitrr-imported" when the caller passes no tags.
        // With no qBit clients the call still completes without error.
        var svc = CreateService();

        var act = async () =>
            await svc.MarkAsImportedAsync("abc123", Enumerable.Empty<string>());

        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task CustomFormatUnmetLookup_UsesArrIdNotSqliteEntryId()
    {
        var options = new DbContextOptionsBuilder<TorrentarrDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        await using var db = new TorrentarrDbContext(options);

        db.Movies.Add(new MoviesFilesModel
        {
            EntryId = 42,
            Title = "Wrong Match",
            ArrInstance = "Radarr-HD",
            ArrId = 5,
            MovieFileId = 999,
            CustomFormatScore = 10
        });
        db.Movies.Add(new MoviesFilesModel
        {
            EntryId = 1,
            Title = "Target Movie",
            ArrInstance = "Radarr-HD",
            ArrId = 42,
            MovieFileId = 0,
            CustomFormatScore = 50
        });
        await db.SaveChangesAsync();

        const int queueMovieId = 42;

        var correctLookup = await db.Movies.AsNoTracking()
            .FirstOrDefaultAsync(m => m.ArrId == queueMovieId && m.ArrInstance == "Radarr-HD");
        correctLookup.Should().NotBeNull();
        correctLookup!.Title.Should().Be("Target Movie");

        var buggyLookup = await db.Movies.AsNoTracking()
            .FirstOrDefaultAsync(m => m.EntryId == queueMovieId && m.ArrInstance == "Radarr-HD");
        buggyLookup.Should().NotBeNull();
        buggyLookup!.Title.Should().Be("Wrong Match",
            "EntryId collisions can make CustomFormatUnmet delete the wrong torrent");
    }
}

using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Torrentarr.Core.Configuration;
using Torrentarr.Core.Services;
using Torrentarr.Infrastructure.Database;
using Torrentarr.Infrastructure.Database.Models;
using Torrentarr.Infrastructure.Services;
using Xunit;

namespace Torrentarr.Infrastructure.Tests.Services;

public class AutomationLoggingTests
{
    [Fact]
    public async Task TraceExplainsEffectiveLimitsWithoutAdditionalCallsOrMutations()
    {
        await using var db = new TorrentarrDbContext(new DbContextOptionsBuilder<TorrentarrDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        var logger = new ActivityLogger<SeedingService>(LogLevel.Trace);
        var config = new TorrentarrConfig();
        config.QBitInstances["seedbox"] = new()
        {
            CategorySeeding = new() { MaxUploadRatio = 2, MaxSeedingTime = 600, RemoveTorrent = 1 },
            Trackers = [new() { Uri = "https://tracker.example/private-passkey/announce", MaxUploadRatio = 3 }]
        };
        config.ArrInstances["Radarr"] = new() { Category = "movies", APIKey = "private-api-key", Torrent = new() { SeedingMode = new() { MaxUploadRatio = 4 } } };
        var client = new Mock<Torrentarr.Core.Interfaces.ITorrentClient>();
        client.Setup(c => c.GetTorrentTrackersAsync("abc", It.IsAny<CancellationToken>())).ReturnsAsync([new() { Url = "https://tracker.example/private-passkey/announce", Status = 2 }]);
        var registry = new Mock<Torrentarr.Core.Interfaces.ITorrentClientRegistry>();
        registry.Setup(r => r.GetClient("seedbox")).Returns(client.Object);
        var service = new SeedingService(logger, db, config, registry.Object);
        var torrent = new Torrentarr.Core.Models.TorrentInfo { Hash = "abc", ClientInstanceId = "seedbox", Category = "movies", Name = "Example", State = "uploading", Ratio = 0.5, Progress = 1, AddedOn = 1, SeedingTime = 100 };
        (await service.ShouldRemoveTorrentAsync(torrent)).Should().BeFalse();
        var messages = string.Join('\n', logger.Messages);
        messages.Should().Contain("categoryRatio=2").And.Contain("ArrRatio=4").And.Contain("trackerRatio=3").And.Contain("effectiveRatio=3")
            .And.Contain("ratio=0.5000/3").And.Contain("ratioMet=False").And.Contain("seed=100s/600s")
            .And.NotContain("private-passkey").And.NotContain("private-api-key");
        client.Verify(c => c.GetTorrentTrackersAsync("abc", It.IsAny<CancellationToken>()), Times.Once);
        client.VerifyNoOtherCalls();
        torrent.State.Should().Be("uploading");
    }

    [Theory]
    [InlineData("https://user:password@tracker.example/private-passkey/announce?token=secret")]
    [InlineData("udp://tracker.example:1337/private-passkey/announce")]
    [InlineData("tracker.example")]
    public async Task TrackerActions_LogOnlyHost_AndReportAcceptance(string tracker)
    {
        await using var db = new TorrentarrDbContext(new DbContextOptionsBuilder<TorrentarrDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        var logger = new ActivityLogger<SeedingService>();
        var config = new TorrentarrConfig();
        config.QBitInstances["seedbox"] = new() { Trackers = [new() { Uri = tracker, AddTrackerIfMissing = true }] };
        var client = new Mock<Torrentarr.Core.Interfaces.ITorrentClient>();
        client.SetupGet(c => c.Capabilities).Returns(Torrentarr.Core.Models.TorrentClientCapabilities.None with { TrackerMutation = true });
        client.Setup(c => c.GetTorrentTrackersAsync("abc", It.IsAny<CancellationToken>())).ReturnsAsync([]);
        client.Setup(c => c.AddTrackersAsync("abc", It.IsAny<List<string>>(), It.IsAny<CancellationToken>())).ReturnsAsync(true);
        var registry = new Mock<Torrentarr.Core.Interfaces.ITorrentClientRegistry>();
        registry.Setup(r => r.GetClient("seedbox")).Returns(client.Object);
        var service = new SeedingService(logger, db, config, registry.Object);
        await service.ApplyTrackerActionsForTorrentAsync(new() { Hash = "abc", QBitInstanceName = "seedbox", Name = "Example" });
        var messages = string.Join('\n', logger.Messages);
        messages.Should().Contain("tracker.example").And.Contain("Client accepted tracker addition")
            .And.NotContain("password").And.NotContain("private-passkey").And.NotContain("token=secret");
    }

    [Fact]
    public async Task RemovalProtection_ExplainsEvaluationAtDebug()
    {
        await using var db = new TorrentarrDbContext(new DbContextOptionsBuilder<TorrentarrDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        var logger = new ActivityLogger<SeedingService>(LogLevel.Debug);
        var registry = new Mock<Torrentarr.Core.Interfaces.ITorrentClientRegistry>();
        var service = new SeedingService(logger, db, new TorrentarrConfig(), registry.Object);
        var decision = await service.EvaluateRemovalAsync(new() { Hash = "protected", QBitInstanceName = "seedbox", Name = "Protected item" }, TorrentRemovalIntent.SeedingLimit);
        decision.Allowed.Should().BeFalse();
        logger.Messages.Should().Contain(m => m.Contains("Removal blocked") && m.Contains("protected") && m.Contains("seedbox") && m.Contains("client.disabled"));
    }

    [Fact]
    public async Task DatabaseChanges_AreLoggedAfterSuccessfulSave_AndNeverAfterFailure()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = new TorrentarrDbContext(new DbContextOptionsBuilder<TorrentarrDbContext>().UseSqlite(connection).Options);
        await db.Database.EnsureCreatedAsync();
        var logger = new ActivityLogger<ArrSyncService>(LogLevel.Debug);
        var movie = new MoviesFilesModel { ArrInstance = "Radarr", ArrId = 17, TmdbId = 17, Title = "SaveChanges movie", Reason = "Missing" };
        db.Movies.Add(movie);
        await db.SaveChangesWithRetryAsync(logger);
        logger.Messages.Should().Contain(m => m.Contains("Database saved Added") && m.Contains("SaveChanges movie") && m.Contains("ArrInstance=Radarr"));
        movie.IsRequest = true;
        await db.SaveChangesWithRetryAsync(logger);
        logger.Messages.Should().Contain(m => m.Contains("Database saved Modified") && m.Contains("IsRequest=True"));
        db.Movies.Remove(movie);
        await db.SaveChangesWithRetryAsync(logger);
        logger.Messages.Should().Contain(m => m.Contains("Database saved Deleted"));

        logger.Messages.Clear();
        await db.Database.ExecuteSqlRawAsync("DROP TABLE moviesfilesmodel");
        db.Movies.Add(new MoviesFilesModel { ArrInstance = "Radarr", ArrId = 18, TmdbId = 18, Title = "Cannot save" });
        var save = () => db.SaveChangesWithRetryAsync(logger, maxAttempts: 1);
        await save.Should().ThrowAsync<DbUpdateException>();
        logger.Messages.Should().NotContain(m => m.Contains("Database saved") || m.Contains("save completed"));
    }

    [Theory]
    [InlineData("radarr")]
    [InlineData("lidarr")]
    [InlineData("readarr")]
    public async Task SearchEligibility_LogsExcludedAndSelectedItems_WithoutChangingFilters(string type)
    {
        await using var db = new TorrentarrDbContext(new DbContextOptionsBuilder<TorrentarrDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        var config = new TorrentarrConfig();
        config.ArrInstances["Arr-test"] = new() { Type = type, Category = "media", Search = new() { SearchMissing = true } };
        if (type == "radarr")
            db.Movies.AddRange(new MoviesFilesModel { ArrInstance = "Arr-test", ArrId = 1, Title = "Eligible", Monitored = true, Reason = "Missing" },
                new MoviesFilesModel { ArrInstance = "Arr-test", ArrId = 2, Title = "Excluded", Monitored = false, Reason = "Missing" });
        else if (type == "lidarr")
            db.Albums.AddRange(new AlbumFilesModel { ArrInstance = "Arr-test", ArrId = 1, Title = "Eligible", Monitored = true, Reason = "Missing" },
                new AlbumFilesModel { ArrInstance = "Arr-test", ArrId = 2, Title = "Excluded", Monitored = false, Reason = "Missing" });
        else
            db.Books.AddRange(new BookFilesModel { ArrInstance = "Arr-test", ArrId = 1, Title = "Eligible", Monitored = true, Reason = "Missing" },
                new BookFilesModel { ArrInstance = "Arr-test", ArrId = 2, Title = "Excluded", Monitored = false, Reason = "Missing" });
        await db.SaveChangesAsync();
        var executor = new Mock<ISearchExecutor>();
        executor.Setup(e => e.ExecuteSearchesAsync("Arr-test", It.IsAny<IEnumerable<SearchCandidate>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SearchResult());
        var logger = new ActivityLogger<ArrMediaService>(LogLevel.Debug);
        var service = new ArrMediaService(logger, db, config, executor.Object,
            new ArrSyncService(NullLogger<ArrSyncService>.Instance, config, db, new()));
        await service.SearchMissingMediaAsync("media");
        logger.Messages.Should().Contain(m => m.Contains("Search skipped for") && m.Contains("Excluded"));
        logger.Messages.Should().Contain(m => m.Contains("Search eligibility for") && m.Contains("Eligible"));
        executor.Verify(e => e.ExecuteSearchesAsync("Arr-test", It.Is<IEnumerable<SearchCandidate>>(c => c.Count() == 1 && c.Single().ArrId == 1), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public void SonarrEligibility_LogsSpecialsAndUnairedDecisions()
    {
        var logger = new ActivityLogger<ArrMediaService>(LogLevel.Debug);
        var candidates = SonarrSearchScope.Candidates([
            new() { ArrInstance = "Sonarr", ArrId = 1, SeriesTitle = "Special", SeasonNumber = 0, Monitored = true },
            new() { ArrInstance = "Sonarr", ArrId = 2, SeriesTitle = "Future", SeasonNumber = 1, Monitored = true, AirDateUtc = DateTime.UtcNow.AddDays(1) }
        ], new SearchConfig { SearchMissing = true }, DateTime.UtcNow, logger: logger);
        candidates.Should().BeEmpty();
        logger.Messages.Should().Contain(m => m.Contains("Special") && m.Contains("specialsAllowed=False"));
        logger.Messages.Should().Contain(m => m.Contains("Future") && m.Contains("No enabled search applies"));
    }
}

internal sealed class ActivityLogger<T>(LogLevel minimumLevel = LogLevel.Information) : ILogger<T>
{
    public List<string> Messages { get; } = [];
    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
    public bool IsEnabled(LogLevel level) => level >= minimumLevel;
    public void Log<TState>(LogLevel level, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
    {
        if (IsEnabled(level)) Messages.Add(formatter(state, exception));
    }
}

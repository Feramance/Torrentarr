using FluentAssertions;
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

public class SearchExecutorTests
{
    private static SearchExecutor CreateService(TorrentarrConfig? config = null, TorrentarrDbContext? dbContext = null)
    {
        var options = new DbContextOptionsBuilder<TorrentarrDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        var db = dbContext ?? new TorrentarrDbContext(options);
        var cfg = config ?? new TorrentarrConfig();

        var switcher = new QualityProfileSwitcherService(
            NullLogger<QualityProfileSwitcherService>.Instance,
            db,
            new DatabaseRestartCoordinator());

        return new SearchExecutor(
            NullLogger<SearchExecutor>.Instance,
            cfg,
            db,
            switcher,
            new DatabaseRestartCoordinator());
    }

    private static TestSearchExecutor CreateTestService(
        TorrentarrConfig? config = null,
        TorrentarrDbContext? dbContext = null)
    {
        var options = new DbContextOptionsBuilder<TorrentarrDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        var db = dbContext ?? new TorrentarrDbContext(options);
        var cfg = config ?? new TorrentarrConfig();

        var switcher = new QualityProfileSwitcherService(
            NullLogger<QualityProfileSwitcherService>.Instance,
            db,
            new DatabaseRestartCoordinator());

        return new TestSearchExecutor(
            NullLogger<SearchExecutor>.Instance,
            cfg,
            db,
            switcher,
            new DatabaseRestartCoordinator());
    }

    private static TorrentarrConfig CreateConfigWithRadarr(int searchLoopDelay = 30, int searchLimit = 5)
    {
        var config = new TorrentarrConfig();
        config.Settings.SearchLoopDelay = searchLoopDelay;
        config.ArrInstances["Radarr-test"] = new ArrInstanceConfig
        {
            URI = "http://localhost:7878",
            APIKey = "test-key",
            Category = "movies-radarr",
            Type = "radarr",
            Managed = true,
            Search = new SearchConfig
            {
                SearchMissing = true,
                SearchLimit = searchLimit
            }
        };
        return config;
    }

    [Fact]
    public void Constructor_CreatesInstance()
    {
        var service = CreateService();
        service.Should().NotBeNull();
    }

    [Fact]
    public void CanSearch_WhenUnderLimit_ReturnsTrue()
    {
        var service = CreateService();

        var result = service.CanSearch(2, 5);

        result.Should().BeTrue();
    }

    [Fact]
    public void CanSearch_WhenAtLimit_ReturnsFalse()
    {
        var service = CreateService();

        var result = service.CanSearch(5, 5);

        result.Should().BeFalse();
    }

    [Fact]
    public void CanSearch_WhenOverLimit_ReturnsFalse()
    {
        var service = CreateService();

        var result = service.CanSearch(10, 5);

        result.Should().BeFalse();
    }

    [Fact]
    public async Task ExecuteSearchesAsync_NoInstance_ReturnsEmpty()
    {
        var service = CreateService();
        var candidates = new List<SearchCandidate>
        {
            new() { ArrId = 1, Title = "Test Movie", Type = "Movie" }
        };

        var result = await service.ExecuteSearchesAsync("nonexistent", candidates);

        result.SearchesTriggered.Should().Be(0);
        result.SearchedIds.Should().BeEmpty();
        result.LoopCompleted.Should().BeFalse();
    }

    [Fact]
    public async Task ExecuteSearchesAsync_NoCandidates_ReturnsEmpty()
    {
        var config = CreateConfigWithRadarr();
        var service = CreateService(config);

        var result = await service.ExecuteSearchesAsync("Radarr-test", Enumerable.Empty<SearchCandidate>());

        result.SearchesTriggered.Should().Be(0);
        result.LoopCompleted.Should().BeTrue();
    }

    [Fact]
    public async Task ExecuteSearchesAsync_OrdersByPriority()
    {
        var config = CreateConfigWithRadarr();
        var service = CreateTestService(config);
        var candidates = new List<SearchCandidate>
        {
            new() { ArrId = 1, Title = "Upgrade Movie", Type = "Movie", Priority = 4, Reason = "Upgrade" },
            new() { ArrId = 2, Title = "Missing Movie", Type = "Movie", Priority = 1, Reason = "Missing" },
            new() { ArrId = 3, Title = "CF Movie", Type = "Movie", Priority = 2, Reason = "CustomFormat" }
        };

        var result = await service.ExecuteSearchesAsync("Radarr-test", candidates);

        result.SearchedIds.Should().ContainInOrder(2, 3, 1);
        service.TriggeredCandidates.Select(c => c.ArrId).Should().ContainInOrder(2, 3, 1);
        result.LoopCompleted.Should().BeTrue();
    }

    [Fact]
    public async Task ExecuteSearchesAsync_PrioritizesTodaysReleases()
    {
        var config = CreateConfigWithRadarr();
        var service = CreateTestService(config);
        var candidates = new List<SearchCandidate>
        {
            new() { ArrId = 1, Title = "Old Episode", Type = "Episode", Priority = 1, IsTodaysRelease = false },
            new() { ArrId = 2, Title = "Today Episode", Type = "Episode", Priority = 1, IsTodaysRelease = true }
        };

        var result = await service.ExecuteSearchesAsync("Radarr-test", candidates);

        result.SearchedIds.Should().ContainInOrder(2, 1);
        service.TriggeredCandidates.Select(c => c.ArrId).Should().ContainInOrder(2, 1);
    }

    [Fact]
    public async Task GetActiveCommandCountAsync_NoInstance_ReturnsZero()
    {
        var service = CreateService();

        var result = await service.GetActiveCommandCountAsync("nonexistent");

        result.Should().Be(0);
    }

    [Fact]
    public async Task ExecuteSearchesAsync_RespectsSearchLimit()
    {
        var config = CreateConfigWithRadarr(searchLoopDelay: 0, searchLimit: 2);
        var service = CreateTestService(config);
        service.ActiveCommandCount = 2;
        var candidates = new List<SearchCandidate>
        {
            new() { ArrId = 1, Title = "Movie 1", Type = "Movie", Priority = 1 },
            new() { ArrId = 2, Title = "Movie 2", Type = "Movie", Priority = 1 },
            new() { ArrId = 3, Title = "Movie 3", Type = "Movie", Priority = 1 }
        };

        var result = await service.ExecuteSearchesAsync("Radarr-test", candidates);

        result.SearchedIds.Should().BeEmpty();
        service.TriggeredCandidates.Should().BeEmpty();
        result.LoopCompleted.Should().BeFalse();
    }

    [Fact]
    public async Task ExecuteSearchesAsync_ReadarrBooks_OrdersAndTriggers()
    {
        var config = new TorrentarrConfig();
        config.Settings.SearchLoopDelay = 1;
        config.ArrInstances["Readarr-Books"] = new ArrInstanceConfig
        {
            URI = "http://localhost:8787",
            APIKey = "test-key",
            Category = "readarr-books",
            Type = "readarr",
            Managed = true,
            Search = new SearchConfig { SearchMissing = true, SearchLimit = 5 }
        };
        var service = CreateTestService(config);
        var candidates = new List<SearchCandidate>
        {
            new() { ArrId = 2, Title = "Upgrade Book", Type = "Book", Priority = 4, AuthorId = 1, BookId = 2 },
            new() { ArrId = 1, Title = "Missing Book", Type = "Book", Priority = 1, AuthorId = 1, BookId = 1 }
        };

        var result = await service.ExecuteSearchesAsync("Readarr-Books", candidates);

        result.SearchedIds.Should().ContainInOrder(1, 2);
        service.TriggeredCandidates.Select(c => c.Type).Should().OnlyContain(t => t == "Book");
    }

    [Fact]
    public async Task MarkAsSearched_ScopesToArrInstance()
    {
        var options = new DbContextOptionsBuilder<TorrentarrDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        await using var db = new TorrentarrDbContext(options);
        db.Books.AddRange(
            new BookFilesModel
            {
                EntryId = 1,
                ArrInstance = "Readarr-Books",
                Title = "Target",
                ArrId = 10,
                Searched = false,
                Upgrade = false
            },
            new BookFilesModel
            {
                EntryId = 2,
                ArrInstance = "Readarr-Comics",
                Title = "Other instance",
                ArrId = 10,
                Searched = false,
                Upgrade = false
            });
        await db.SaveChangesAsync();

        var config = new TorrentarrConfig();
        config.Settings.SearchLoopDelay = 0;
        config.ArrInstances["Readarr-Books"] = new ArrInstanceConfig
        {
            URI = "http://localhost:8787",
            APIKey = "test-key",
            Category = "readarr-books",
            Type = "readarr",
            Search = new SearchConfig { SearchLimit = 5 }
        };

        var service = new TriggerOnlySearchExecutor(
            NullLogger<SearchExecutor>.Instance,
            config,
            db,
            new QualityProfileSwitcherService(
                NullLogger<QualityProfileSwitcherService>.Instance,
                db,
                new DatabaseRestartCoordinator()),
            new DatabaseRestartCoordinator());

        await service.ExecuteSearchesAsync("Readarr-Books",
        [
            new SearchCandidate { ArrId = 10, Title = "Target", Type = "Book" }
        ]);

        var books = await db.Books.OrderBy(b => b.EntryId).ToListAsync();
        books[0].Searched.Should().BeTrue();
        books[0].Upgrade.Should().BeTrue();
        books[1].Searched.Should().BeFalse();
        books[1].Upgrade.Should().BeFalse();
    }
}

internal sealed class TestSearchExecutor : SearchExecutor
{
    public int ActiveCommandCount { get; set; }
    public List<SearchCandidate> TriggeredCandidates { get; } = new();

    public TestSearchExecutor(
        ILogger<SearchExecutor> logger,
        TorrentarrConfig config,
        TorrentarrDbContext db,
        QualityProfileSwitcherService profileSwitcher,
        DatabaseRestartCoordinator restartCoordinator)
        : base(logger, config, db, profileSwitcher, restartCoordinator)
    {
    }

    public override Task<int> GetActiveCommandCountAsync(string instanceName, CancellationToken cancellationToken = default)
        => Task.FromResult(ActiveCommandCount);

    protected override Task<bool> TriggerSearchForCandidateAsync(
        string instanceName,
        ArrInstanceConfig arrConfig,
        SearchCandidate candidate,
        bool useSeriesSearch,
        CancellationToken cancellationToken)
    {
        TriggeredCandidates.Add(candidate);
        return Task.FromResult(true);
    }

    protected override Task MarkAsSearchedAsync(
        string instanceName,
        ArrInstanceConfig arrConfig,
        SearchCandidate candidate,
        CancellationToken cancellationToken)
        => Task.CompletedTask;
}

internal sealed class TriggerOnlySearchExecutor : SearchExecutor
{
    public TriggerOnlySearchExecutor(
        ILogger<SearchExecutor> logger,
        TorrentarrConfig config,
        TorrentarrDbContext db,
        QualityProfileSwitcherService profileSwitcher,
        DatabaseRestartCoordinator restartCoordinator)
        : base(logger, config, db, profileSwitcher, restartCoordinator)
    {
    }

    public override Task<int> GetActiveCommandCountAsync(string instanceName, CancellationToken cancellationToken = default)
        => Task.FromResult(0);

    protected override Task<bool> TriggerSearchForCandidateAsync(
        string instanceName,
        ArrInstanceConfig arrConfig,
        SearchCandidate candidate,
        bool useSeriesSearch,
        CancellationToken cancellationToken)
        => Task.FromResult(true);
}

public class SearchCandidateTests
{
    [Fact]
    public void SearchCandidate_Defaults()
    {
        var candidate = new SearchCandidate();

        candidate.ArrId.Should().Be(0);
        candidate.Title.Should().BeEmpty();
        candidate.Type.Should().BeEmpty();
        candidate.Reason.Should().BeEmpty();
        candidate.Priority.Should().Be(0);
        candidate.IsTodaysRelease.Should().BeFalse();
    }

    [Fact]
    public void SearchCandidate_WithProperties()
    {
        var candidate = new SearchCandidate
        {
            ArrId = 123,
            Title = "Test Movie",
            Type = "Movie",
            Reason = "Missing",
            Priority = 1,
            Year = 2024,
            SeriesId = 456,
            SeasonNumber = 1,
            EpisodeNumber = 1,
            IsTodaysRelease = true
        };

        candidate.ArrId.Should().Be(123);
        candidate.Title.Should().Be("Test Movie");
        candidate.Type.Should().Be("Movie");
        candidate.Reason.Should().Be("Missing");
        candidate.Priority.Should().Be(1);
        candidate.Year.Should().Be(2024);
        candidate.SeriesId.Should().Be(456);
        candidate.SeasonNumber.Should().Be(1);
        candidate.EpisodeNumber.Should().Be(1);
        candidate.IsTodaysRelease.Should().BeTrue();
    }
}

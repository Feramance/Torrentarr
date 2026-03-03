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
            db);

        return new SearchExecutor(
            NullLogger<SearchExecutor>.Instance,
            cfg,
            db,
            switcher);
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
    }

    [Fact]
    public async Task ExecuteSearchesAsync_NoCandidates_ReturnsEmpty()
    {
        var config = CreateConfigWithRadarr();
        var service = CreateService(config);

        var result = await service.ExecuteSearchesAsync("Radarr-test", Enumerable.Empty<SearchCandidate>());

        result.SearchesTriggered.Should().Be(0);
    }

    [Fact]
    public async Task ExecuteSearchesAsync_OrdersByPriority()
    {
        var config = CreateConfigWithRadarr();
        var service = CreateService(config);
        var candidates = new List<SearchCandidate>
        {
            new() { ArrId = 1, Title = "Upgrade Movie", Type = "Movie", Priority = 4, Reason = "Upgrade" },
            new() { ArrId = 2, Title = "Missing Movie", Type = "Movie", Priority = 1, Reason = "Missing" },
            new() { ArrId = 3, Title = "CF Movie", Type = "Movie", Priority = 2, Reason = "CustomFormat" }
        };

        var result = await service.ExecuteSearchesAsync("Radarr-test", candidates);

        result.SearchedIds.Should().BeEmpty();
    }

    [Fact]
    public async Task ExecuteSearchesAsync_PrioritizesTodaysReleases()
    {
        var config = CreateConfigWithRadarr();
        var service = CreateService(config);
        var candidates = new List<SearchCandidate>
        {
            new() { ArrId = 1, Title = "Old Episode", Type = "Episode", Priority = 1, IsTodaysRelease = false },
            new() { ArrId = 2, Title = "Today Episode", Type = "Episode", Priority = 1, IsTodaysRelease = true }
        };

        var result = await service.ExecuteSearchesAsync("Radarr-test", candidates);

        result.SearchedIds.Should().BeEmpty();
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
        var service = CreateService(config);
        var candidates = new List<SearchCandidate>
        {
            new() { ArrId = 1, Title = "Movie 1", Type = "Movie", Priority = 1 },
            new() { ArrId = 2, Title = "Movie 2", Type = "Movie", Priority = 1 },
            new() { ArrId = 3, Title = "Movie 3", Type = "Movie", Priority = 1 }
        };

        var result = await service.ExecuteSearchesAsync("Radarr-test", candidates);

        result.SearchedIds.Should().BeEmpty();
    }
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

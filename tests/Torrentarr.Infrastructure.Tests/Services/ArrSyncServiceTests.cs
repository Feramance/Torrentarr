using FluentAssertions;
using Microsoft.Data.Sqlite;
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

/// <summary>
/// Tests for ArrSyncService using a test database and stubbed provider responses.
/// </summary>
public sealed class ArrSyncServiceTests : IDisposable
{
    private readonly string _dbName;
    private readonly SqliteConnection _keepAlive;
    private readonly TorrentarrDbContext _db;

    public ArrSyncServiceTests()
    {
        _dbName = $"arrsync-{Guid.NewGuid():N}";
        var cs = $"Data Source={_dbName};Mode=Memory;Cache=Shared";
        _keepAlive = new SqliteConnection(cs);
        _keepAlive.Open();
        var options = new DbContextOptionsBuilder<TorrentarrDbContext>()
            .UseSqlite(cs).Options;
        _db = new TorrentarrDbContext(options);
        _db.Database.EnsureCreated();
    }

    public void Dispose()
    {
        _db.Dispose();
        _keepAlive.Dispose();
    }

    private ArrSyncService CreateService(TorrentarrConfig? config = null)
    {
        config ??= new TorrentarrConfig();
        return new ArrSyncService(NullLogger<ArrSyncService>.Instance, config, _db, new DatabaseRestartCoordinator());
    }

    private static ArrInstanceConfig MakeInstance(string type, string uri = "http://localhost:7878")
        => new() { Category = "test-cat", Type = type, URI = uri, APIKey = "key" };

    // ── SyncAsync – fast exits ─────────────────────────────────────────────────

    [Fact]
    public async Task SyncAsync_InstanceNotFound_DoesNotThrow()
    {
        var svc = CreateService();

        var act = async () => await svc.SyncAsync("nonexistent");

        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task SyncAsync_UnconfiguredUri_ChangeMeValue_DoesNotThrow()
    {
        var config = new TorrentarrConfig
        {
            ArrInstances = new Dictionary<string, ArrInstanceConfig>
            {
                ["test"] = MakeInstance("radarr", "CHANGE_ME")
            }
        };
        var svc = CreateService(config);

        var act = async () => await svc.SyncAsync("test");

        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task SyncAsync_EmptyUri_DoesNotThrow()
    {
        var config = new TorrentarrConfig
        {
            ArrInstances = new Dictionary<string, ArrInstanceConfig>
            {
                ["test"] = MakeInstance("sonarr", "")
            }
        };
        var svc = CreateService(config);

        var act = async () => await svc.SyncAsync("test");

        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task SyncAsync_ReadarrChangeMeUri_DoesNotThrow()
    {
        var config = new TorrentarrConfig
        {
            ArrInstances = new Dictionary<string, ArrInstanceConfig>
            {
                ["Readarr-Books"] = MakeInstance("readarr", "CHANGE_ME")
            }
        };
        var svc = CreateService(config);

        var act = async () => await svc.SyncAsync("Readarr-Books");

        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task SyncAsync_UnknownArrType_DoesNotThrow()
    {
        // Switch default branch: logs a warning and returns, no network call.
        var config = new TorrentarrConfig
        {
            ArrInstances = new Dictionary<string, ArrInstanceConfig>
            {
                ["test"] = MakeInstance("plex")
            }
        };
        var svc = CreateService(config);

        var act = async () => await svc.SyncAsync("test");

        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task SyncAsync_PreCancelledToken_InstanceNotFound_DoesNotThrow()
    {
        var svc = CreateService();
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        // No async work is started when the instance is not found, so the
        // pre-cancelled token is irrelevant and must not cause an exception.
        var act = async () => await svc.SyncAsync("nonexistent", cts.Token);

        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task MarkRequestsAsync_SearchMissingFalse_DoesNotMarkIsRequest()
    {
        var config = new TorrentarrConfig
        {
            ArrInstances = new Dictionary<string, ArrInstanceConfig>
            {
                ["Radarr"] = new ArrInstanceConfig
                {
                    Type = "radarr",
                    Category = "movies",
                    URI = "http://localhost:7878",
                    APIKey = "key",
                    Search = new SearchConfig
                    {
                        SearchMissing = false,
                        Overseerr = new OverseerrConfig
                        {
                            SearchOverseerrRequests = true,
                            OverseerrURI = "http://localhost:5055",
                            OverseerrAPIKey = "key"
                        }
                    }
                }
            }
        };
        var svc = CreateService(config);
        _db.Movies.Add(new MoviesFilesModel
        {
            Title = "Requested",
            ArrInstance = "Radarr",
            TmdbId = 99,
            IsRequest = false
        });
        await _db.SaveChangesAsync();

        await svc.MarkRequestsAsync("Radarr");

        (await _db.Movies.SingleAsync()).IsRequest.Should().BeFalse();
    }

    [Theory]
    [InlineData(true, "1")]
    [InlineData(false, "1,2")]
    public async Task MarkRequestsAsync_FiltersOmbiMoviesAndPreservesFlagsOnFailedPoll(bool approvedOnly, string expectedIds)
    {
        var arr = MakeInstance("radarr");
        arr.Search.SearchMissing = true;
        arr.Search.Ombi = new OmbiConfig
        {
            SearchOmbiRequests = true,
            OmbiURI = "http://ombi.test/proxy/",
            OmbiAPIKey = "key",
            ApprovedOnly = approvedOnly
        };
        var config = new TorrentarrConfig { ArrInstances = new() { ["Radarr"] = arr } };
        var svc = CreateService(config);
        _db.Movies.AddRange(Enumerable.Range(1, 4).Select(id => new MoviesFilesModel
        {
            ArrInstance = "Radarr",
            ArrId = id,
            TmdbId = id,
            IsRequest = true,
            Monitored = true,
            Reason = "Missing"
        }));
        _db.Movies.Add(new MoviesFilesModel { ArrInstance = "Radarr-other", ArrId = 1, TmdbId = 1 });
        await _db.SaveChangesAsync();
        var handler = new OverseerrRequestFetcherTests.StubHandler
        {
            OnSend = req =>
            {
                req.RequestUri!.AbsolutePath.Should().Be("/proxy/api/v1/Request/movie");
                req.Headers.GetValues("ApiKey").Should().Equal("key");
                return OverseerrRequestFetcherTests.Json("""
                    [{"theMovieDbId":1,"approved":true},
                     {"theMovieDbId":2,"approved":false},
                     {"theMovieDbId":3,"approved":true,"denied":true},
                     {"theMovieDbId":4,"approved":true,"available":true}]
                    """);
            }
        };
        using var http = new HttpClient(handler);

        await svc.MarkRequestsAsync("Radarr", http, CancellationToken.None);
        var expected = expectedIds.Split(',').Select(int.Parse).ToArray();
        async Task<int[]> MarkedIds() => await _db.Movies.AsNoTracking().Where(m => m.IsRequest)
            .OrderBy(m => m.TmdbId).Select(m => m.TmdbId).ToArrayAsync();
        (await MarkedIds()).Should().Equal(expected);

        var executor = new Mock<ISearchExecutor>();
        executor.Setup(x => x.ExecuteSearchesAsync("Radarr", It.IsAny<IEnumerable<SearchCandidate>>(), CancellationToken.None))
            .Callback<string, IEnumerable<SearchCandidate>, CancellationToken>((_, candidates, _) =>
            {
                candidates.Where(c => c.Priority == 0).Select(c => c.ArrId).Should().BeEquivalentTo(expected);
            })
            .ReturnsAsync(new SearchResult());
        var media = new ArrMediaService(NullLogger<ArrMediaService>.Instance, _db, config, executor.Object, svc);
        await media.SearchMissingMediaAsync(arr.Category);
        executor.Verify(x => x.ExecuteSearchesAsync("Radarr", It.IsAny<IEnumerable<SearchCandidate>>(), CancellationToken.None), Times.Once);

        handler.OnSend = _ => new HttpResponseMessage(System.Net.HttpStatusCode.ServiceUnavailable);
        await svc.MarkRequestsAsync("Radarr", http, CancellationToken.None);
        (await MarkedIds()).Should().Equal(expected);

        using var canceled = new CancellationTokenSource();
        canceled.Cancel();
        Func<Task> poll = () => svc.MarkRequestsAsync("Radarr", http, canceled.Token);
        await poll.Should().ThrowAsync<OperationCanceledException>();
        (await MarkedIds()).Should().Equal(expected);

        handler.OnSend = _ => OverseerrRequestFetcherTests.Json("[]");
        await svc.MarkRequestsAsync("Radarr", http, CancellationToken.None);
        (await MarkedIds()).Should().BeEmpty();
    }

    [Theory]
    [InlineData(true, 1)]
    [InlineData(false, 2)]
    public async Task MarkRequestsAsync_UsesOmbiChildStatesAndPrioritizesEligibleEpisodes(bool approvedOnly, int preferredCount)
    {
        var arr = MakeInstance("sonarr");
        arr.Search.SearchMissing = true;
        arr.Search.Ombi = new OmbiConfig
        {
            SearchOmbiRequests = true,
            OmbiURI = "http://ombi.test",
            OmbiAPIKey = "key",
            ApprovedOnly = approvedOnly
        };
        var svc = CreateService(new TorrentarrConfig { ArrInstances = new() { ["Sonarr"] = arr } });
        foreach (var id in Enumerable.Range(1, 3))
        {
            _db.Series.Add(new SeriesFilesModel { EntryId = id, ArrId = id, ArrInstance = "Sonarr", TvdbId = id });
            _db.Episodes.Add(new EpisodeFilesModel
            {
                ArrInstance = "Sonarr",
                ArrId = id,
                SeriesId = id,
                ArrSeriesId = id,
                Monitored = true,
                SeasonNumber = 1,
                EpisodeNumber = 1,
                AirDateUtc = DateTime.UtcNow.AddDays(-2)
            });
        }
        await _db.SaveChangesAsync();
        using var http = new HttpClient(new OverseerrRequestFetcherTests.StubHandler
        {
            OnSend = req =>
            {
                req.RequestUri!.AbsolutePath.Should().Be("/api/v1/Request/tvlite");
                return OverseerrRequestFetcherTests.Json("""
                    [{"tvDbId":1,"childRequests":[{"denied":true},{"approved":true}]},
                     {"tvDbId":2,"childRequests":[{"approved":false},{"approved":true,"available":true}]},
                     {"tvDbId":3,"childRequests":[{"approved":true,"denied":true}]}]
                    """);
            }
        });

        await svc.MarkRequestsAsync("Sonarr", http, CancellationToken.None);
        var episodes = await _db.Episodes.AsNoTracking().ToListAsync();
        var candidates = SonarrSearchScope.Candidates(episodes, arr.Search, DateTime.UtcNow);
        candidates.Should().HaveCount(3);
        candidates.Where(c => c.Priority == 0).Select(c => c.ArrId).Should().BeEquivalentTo(Enumerable.Range(1, preferredCount));
    }

    [Fact]
    public async Task SyncQueueAsync_InstanceNotFound_DoesNotThrow()
    {
        var svc = CreateService();

        var act = async () => await svc.SyncQueueAsync("nonexistent");

        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task SyncQueueAsync_UnconfiguredUri_ChangeMeValue_DoesNotThrow()
    {
        var config = new TorrentarrConfig
        {
            ArrInstances = new Dictionary<string, ArrInstanceConfig>
            {
                ["test"] = MakeInstance("radarr", "CHANGE_ME")
            }
        };
        var svc = CreateService(config);

        var act = async () => await svc.SyncQueueAsync("test");

        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task SyncQueueAsync_EmptyUri_DoesNotThrow()
    {
        var config = new TorrentarrConfig
        {
            ArrInstances = new Dictionary<string, ArrInstanceConfig>
            {
                ["test"] = MakeInstance("sonarr", "")
            }
        };
        var svc = CreateService(config);

        var act = async () => await svc.SyncQueueAsync("test");

        await act.Should().NotThrowAsync();
    }

    [Fact]
    public void ShouldSkipDestructiveDelete_EmptyApiWithExistingRows_ReturnsTrue()
    {
        var svc = CreateService();
        var method = typeof(ArrSyncService).GetMethod(
            "ShouldSkipDestructiveDelete",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;

        var result = (bool)method.Invoke(svc, new object[] { 0, 5, "Lidarr-test", "tracks" })!;

        result.Should().BeTrue();
    }

    [Fact]
    public void ShouldSkipDestructiveDelete_EmptyApiAndEmptyDb_ReturnsFalse()
    {
        var svc = CreateService();
        var method = typeof(ArrSyncService).GetMethod(
            "ShouldSkipDestructiveDelete",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;

        var result = (bool)method.Invoke(svc, new object[] { 0, 0, "Lidarr-test", "tracks" })!;

        result.Should().BeFalse();
    }

    [Fact]
    public void ShouldSkipDestructiveDelete_NonEmptyApi_ReturnsFalse()
    {
        var svc = CreateService();
        var method = typeof(ArrSyncService).GetMethod(
            "ShouldSkipDestructiveDelete",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;

        var result = (bool)method.Invoke(svc, new object[] { 10, 5, "Radarr-test", "movies" })!;

        result.Should().BeFalse();
    }

    [Fact]
    public void ShouldSkipDestructiveDelete_Episodes_EmptyApiWithExistingRows_ReturnsTrue()
    {
        var svc = CreateService();
        var method = typeof(ArrSyncService).GetMethod(
            "ShouldSkipDestructiveDelete",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;

        var result = (bool)method.Invoke(svc, new object[] { 0, 24, "Sonarr-test", "episodes" })!;

        result.Should().BeTrue();
    }

    [Theory]
    [InlineData("artists")]
    [InlineData("albums")]
    public void ShouldSkipDestructiveDelete_LidarrEntities_EmptyApiWithExistingRows_ReturnsTrue(string entityName)
    {
        var svc = CreateService();
        var method = typeof(ArrSyncService).GetMethod(
            "ShouldSkipDestructiveDelete",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;

        var result = (bool)method.Invoke(svc, new object[] { 0, 12, "Lidarr-test", entityName })!;

        result.Should().BeTrue();
    }

    [Fact]
    public void ShouldSkipDestructiveTrackSync_EmptyApiWithExistingRows_ReturnsTrue()
    {
        var svc = CreateService();
        var method = typeof(ArrSyncService).GetMethod(
            "ShouldSkipDestructiveTrackSync",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;

        var result = (bool)method.Invoke(svc, new object[] { 0, 8, 0, "Lidarr-test" })!;

        result.Should().BeTrue();
    }

    [Fact]
    public void ShouldSkipDestructiveTrackSync_NonEmptyApiWithNoMappableTracks_ReturnsTrue()
    {
        var svc = CreateService();
        var method = typeof(ArrSyncService).GetMethod(
            "ShouldSkipDestructiveTrackSync",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;

        var result = (bool)method.Invoke(svc, new object[] { 15, 8, 0, "Lidarr-test" })!;

        result.Should().BeTrue("albums API empty + tracks API non-empty must not wipe SQLite tracks");
    }

    [Fact]
    public void ShouldSkipDestructiveTrackSync_MappableTracks_ReturnsFalse()
    {
        var svc = CreateService();
        var method = typeof(ArrSyncService).GetMethod(
            "ShouldSkipDestructiveTrackSync",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;

        var result = (bool)method.Invoke(svc, new object[] { 15, 8, 12, "Lidarr-test" })!;

        result.Should().BeFalse();
    }

    [Fact]
    public async Task SyncQueueAsync_UnknownArrType_DoesNotThrow()
    {
        // The switch has no default case for unknown types — falls through silently.
        var config = new TorrentarrConfig
        {
            ArrInstances = new Dictionary<string, ArrInstanceConfig>
            {
                ["test"] = MakeInstance("plex")
            }
        };
        var svc = CreateService(config);

        var act = async () => await svc.SyncQueueAsync("test");

        await act.Should().NotThrowAsync();
    }

    [Fact]
    public void CalculateLidarrQualityMet_CutoffUpgradeAllowedAndLowQualityId_ReturnsFalse()
    {
        var profile = new Torrentarr.Infrastructure.ApiClients.Arr.QualityProfile
        {
            Cutoff = 10,
            UpgradeAllowed = true
        };
        var files = new List<Torrentarr.Infrastructure.ApiClients.Arr.TrackFile>
        {
            new()
            {
                Quality = new Torrentarr.Infrastructure.ApiClients.Arr.Quality
                {
                    QualityDefinition = new Torrentarr.Infrastructure.ApiClients.Arr.QualityDefinition { Id = 4 }
                }
            }
        };

        ArrSyncService.CalculateLidarrQualityMet(true, profile, files).Should().BeFalse();
    }

    [Fact]
    public void CalculateLidarrQualityMet_UpgradeAllowedFalse_ReturnsTrueWhenHasAllTracks()
    {
        var profile = new Torrentarr.Infrastructure.ApiClients.Arr.QualityProfile
        {
            Cutoff = 10,
            UpgradeAllowed = false
        };
        var files = new List<Torrentarr.Infrastructure.ApiClients.Arr.TrackFile>
        {
            new()
            {
                Quality = new Torrentarr.Infrastructure.ApiClients.Arr.Quality
                {
                    QualityDefinition = new Torrentarr.Infrastructure.ApiClients.Arr.QualityDefinition { Id = 4 }
                }
            }
        };

        ArrSyncService.CalculateLidarrQualityMet(true, profile, files).Should().BeTrue();
    }

    [Fact]
    public void CalculateLidarrQualityMet_PercentOfTracksBelow100_ReturnsFalse()
    {
        var profile = new Torrentarr.Infrastructure.ApiClients.Arr.QualityProfile
        {
            Cutoff = 10,
            UpgradeAllowed = true
        };

        ArrSyncService.CalculateLidarrQualityMet(false, profile, Array.Empty<Torrentarr.Infrastructure.ApiClients.Arr.TrackFile>())
            .Should().BeFalse();
    }

    [Fact]
    public void CalculateLidarrQualityMet_ApiErrorTreatsUnmetAsFalse()
    {
        var profile = new Torrentarr.Infrastructure.ApiClients.Arr.QualityProfile
        {
            Cutoff = 10,
            UpgradeAllowed = true
        };

        ArrSyncService.CalculateLidarrQualityMet(true, profile, trackFiles: null).Should().BeTrue();
    }
}

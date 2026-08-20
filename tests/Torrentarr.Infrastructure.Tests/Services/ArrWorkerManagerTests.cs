using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
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

public class ArrWorkerManagerTests
{
    private static ArrWorkerManager CreateManager()
    {
        var services = new ServiceCollection().BuildServiceProvider();
        return new ArrWorkerManager(
            NullLogger<ArrWorkerManager>.Instance,
            services.GetRequiredService<IServiceScopeFactory>(),
            new TorrentarrConfig(),
            new ProcessStateManager(),
            new StubConnectivityService());
    }

    [Fact]
    public void ShouldRunSearch_ReturnsTrue_WhenIntervalElapsed()
    {
        var manager = CreateManager();
        var cfg = new ArrInstanceConfig { Search = new SearchConfig { SearchRequestsEvery = 1 } };

        manager.ShouldRunSearch("Lidarr", cfg).Should().BeTrue();
    }

    [Fact]
    public void IsWorkerCancellation_ReturnsFalse_ForRequestTimeout()
    {
        using var workerCts = new CancellationTokenSource();
        var timeout = new TaskCanceledException("request timed out");

        ArrWorkerManager.IsWorkerCancellation(timeout, workerCts.Token).Should().BeFalse();
    }

    [Fact]
    public void IsWorkerCancellation_ReturnsTrue_WhenWorkerIsStopping()
    {
        using var workerCts = new CancellationTokenSource();
        workerCts.Cancel();
        var cancellation = new OperationCanceledException(workerCts.Token);

        ArrWorkerManager.IsWorkerCancellation(cancellation, workerCts.Token).Should().BeTrue();
    }

    [Fact]
    public void ShouldRunSearch_ReturnsFalse_WithinInterval()
    {
        var manager = CreateManager();
        var cfg = new ArrInstanceConfig { Search = new SearchConfig { SearchRequestsEvery = 3600 } };

        manager.ShouldRunSearch("Lidarr", cfg).Should().BeTrue();
        manager.ShouldRunSearch("Lidarr", cfg).Should().BeFalse();
    }

    [Fact]
    public void ShouldRunSearch_UpdatesLastSearchTime_OnlyWhenReturningTrue()
    {
        var manager = CreateManager();
        var cfg = new ArrInstanceConfig { Search = new SearchConfig { SearchRequestsEvery = 3600 } };

        manager.ShouldRunSearch("Sonarr", cfg).Should().BeTrue();
        manager.ShouldRunSearch("Sonarr", cfg).Should().BeFalse();

        var otherCfg = new ArrInstanceConfig { Search = new SearchConfig { SearchRequestsEvery = 1 } };
        manager.ShouldRunSearch("Radarr", otherCfg).Should().BeTrue();
    }

    [Fact]
    public void ShouldRunSearch_ReturnsFalse_WhenSearchMissingDisabled()
    {
        var manager = CreateManager();
        var cfg = new ArrInstanceConfig
        {
            Search = new SearchConfig
            {
                SearchMissing = false,
                DoUpgradeSearch = true,
                SearchRequestsEvery = 1
            }
        };

        manager.ShouldRunSearch("Radarr", cfg).Should().BeFalse();
    }

    [Fact]
    public void ShouldRunSearch_ReturnsTrue_WhenSearchMissingEnabledAndIntervalElapsed()
    {
        var manager = CreateManager();
        var cfg = new ArrInstanceConfig
        {
            Search = new SearchConfig { SearchMissing = true, SearchRequestsEvery = 1 }
        };

        manager.ShouldRunSearch("Radarr", cfg).Should().BeTrue();
    }

    [Fact]
    public async Task RunSearchAsync_SearchMissingFalse_DoesNotExecuteSearches()
    {
        var media = new Mock<IArrMediaService>(MockBehavior.Strict);
        using var harness = WorkerHarness.Create(media.Object);
        var cfg = new ArrInstanceConfig
        {
            Type = "radarr",
            Category = "movies",
            Search = new SearchConfig
            {
                SearchMissing = false,
                DoUpgradeSearch = true,
                SearchRequestsEvery = 1
            }
        };

        var result = await harness.Manager.RunSearchAsync("Radarr", cfg, CancellationToken.None);

        result.Should().BeNull();
        media.Verify(m => m.SearchQualityUpgradesAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        media.Verify(m => m.SearchMissingMediaAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task RunSearchAsync_SearchMissingTrue_ExecutesMissingSearch()
    {
        var media = new Mock<IArrMediaService>();
        media.Setup(m => m.SearchMissingMediaAsync("movies", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SearchResult { LoopCompleted = true, SearchesTriggered = 1, ItemsSearched = 1 });
        using var harness = WorkerHarness.Create(media.Object);
        var cfg = new ArrInstanceConfig
        {
            Type = "radarr",
            Category = "movies",
            Search = new SearchConfig { SearchMissing = true, SearchRequestsEvery = 1 }
        };

        var result = await harness.Manager.RunSearchAsync("Radarr", cfg, CancellationToken.None);

        result.Should().NotBeNull();
        result!.SearchesTriggered.Should().Be(1);
        result.LoopCompleted.Should().BeTrue();
        media.Verify(m => m.SearchMissingMediaAsync("movies", It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task RunSearchAsync_DrainedWithSearchAgain_ResetsSearchedAndUpgradeOnNextTick()
    {
        var media = new Mock<IArrMediaService>();
        media.Setup(m => m.SearchMissingMediaAsync("movies", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SearchResult { LoopCompleted = true, SearchesTriggered = 0 });
        using var harness = WorkerHarness.Create(media.Object);
        harness.Db.Movies.Add(new MoviesFilesModel
        {
            Title = "Movie",
            ArrInstance = "Radarr",
            Searched = true,
            Upgrade = true,
            TmdbId = 1
        });
        await harness.Db.SaveChangesAsync();

        var cfg = new ArrInstanceConfig
        {
            Type = "radarr",
            Category = "movies",
            Search = new SearchConfig
            {
                SearchMissing = true,
                SearchAgainOnSearchCompletion = true,
                SearchRequestsEvery = 1
            }
        };

        await harness.Manager.RunSearchAsync("Radarr", cfg, CancellationToken.None);
        (await harness.ReloadMovie()).Searched.Should().BeTrue();
        (await harness.ReloadMovie()).Upgrade.Should().BeTrue();

        await harness.Manager.RunSearchAsync("Radarr", cfg, CancellationToken.None);
        var movie = await harness.ReloadMovie();
        movie.Searched.Should().BeFalse();
        movie.Upgrade.Should().BeFalse();
    }

    [Fact]
    public async Task RunSearchAsync_SearchAgainOff_DoesNotResetFlags()
    {
        var media = new Mock<IArrMediaService>();
        media.Setup(m => m.SearchMissingMediaAsync("movies", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SearchResult { LoopCompleted = true, SearchesTriggered = 0 });
        using var harness = WorkerHarness.Create(media.Object);
        harness.Db.Movies.Add(new MoviesFilesModel
        {
            Title = "Movie",
            ArrInstance = "Radarr",
            Searched = true,
            Upgrade = true,
            TmdbId = 1
        });
        await harness.Db.SaveChangesAsync();

        var cfg = new ArrInstanceConfig
        {
            Type = "radarr",
            Category = "movies",
            Search = new SearchConfig
            {
                SearchMissing = true,
                SearchAgainOnSearchCompletion = false,
                SearchRequestsEvery = 1
            }
        };

        await harness.Manager.RunSearchAsync("Radarr", cfg, CancellationToken.None);
        await harness.Manager.RunSearchAsync("Radarr", cfg, CancellationToken.None);

        var movie = await harness.ReloadMovie();
        movie.Searched.Should().BeTrue();
        movie.Upgrade.Should().BeTrue();
    }

    [Fact]
    public async Task RunSearchAsync_SearchAgain_OnlyResetsSearchedTrueRows()
    {
        var media = new Mock<IArrMediaService>();
        media.Setup(m => m.SearchMissingMediaAsync("movies", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SearchResult { LoopCompleted = true, SearchesTriggered = 0 });
        using var harness = WorkerHarness.Create(media.Object);
        harness.Db.Movies.Add(new MoviesFilesModel
        {
            Title = "Already",
            ArrInstance = "Radarr",
            Searched = false,
            Upgrade = true,
            TmdbId = 2
        });
        harness.Db.Movies.Add(new MoviesFilesModel
        {
            Title = "Movie",
            ArrInstance = "Radarr",
            Searched = true,
            Upgrade = true,
            TmdbId = 1
        });
        await harness.Db.SaveChangesAsync();

        var cfg = new ArrInstanceConfig
        {
            Type = "radarr",
            Category = "movies",
            Search = new SearchConfig
            {
                SearchMissing = true,
                SearchAgainOnSearchCompletion = true,
                SearchRequestsEvery = 1
            }
        };

        await harness.Manager.RunSearchAsync("Radarr", cfg, CancellationToken.None);
        await harness.Manager.RunSearchAsync("Radarr", cfg, CancellationToken.None);

        harness.Db.ChangeTracker.Clear();
        var kept = harness.Db.Movies.Single(m => m.TmdbId == 2);
        kept.Searched.Should().BeFalse();
        kept.Upgrade.Should().BeTrue("qBitrr only resets rows with Searched==true");
        var reset = harness.Db.Movies.Single(m => m.TmdbId == 1);
        reset.Searched.Should().BeFalse();
        reset.Upgrade.Should().BeFalse();
    }

    [Fact]
    public async Task RunSearchAsync_PartialBatch_DoesNotResetFlags()
    {
        var media = new Mock<IArrMediaService>();
        media.Setup(m => m.SearchMissingMediaAsync("movies", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SearchResult { LoopCompleted = false, SearchesTriggered = 5, ItemsSearched = 5 });
        using var harness = WorkerHarness.Create(media.Object);
        harness.Db.Movies.Add(new MoviesFilesModel
        {
            Title = "Movie",
            ArrInstance = "Radarr",
            Searched = true,
            Upgrade = true,
            TmdbId = 1
        });
        await harness.Db.SaveChangesAsync();

        var cfg = new ArrInstanceConfig
        {
            Type = "radarr",
            Category = "movies",
            Search = new SearchConfig
            {
                SearchMissing = true,
                SearchAgainOnSearchCompletion = true,
                SearchRequestsEvery = 1
            }
        };

        await harness.Manager.RunSearchAsync("Radarr", cfg, CancellationToken.None);
        await harness.Manager.RunSearchAsync("Radarr", cfg, CancellationToken.None);

        var movie = await harness.ReloadMovie();
        movie.Searched.Should().BeTrue();
        movie.Upgrade.Should().BeTrue();
    }

    [Fact]
    public async Task DualLoops_StartSearchAndTorrentTasks()
    {
        var torrentGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var torrent = new Mock<ITorrentProcessor>();
        torrent.Setup(p => p.ProcessTorrentsAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(async (string _, CancellationToken ct) =>
            {
                try { await torrentGate.Task.WaitAsync(ct); }
                catch (OperationCanceledException) { }
            });

        var media = new Mock<IArrMediaService>();
        media.Setup(m => m.SearchMissingMediaAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SearchResult { LoopCompleted = true });

        using var harness = WorkerHarness.Create(media.Object, torrent.Object, managedInstance: true);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        await harness.Manager.StartAsync(cts.Token);
        try
        {
            await WaitUntilAsync(() =>
                harness.State.GetState("Radarr-search")?.Alive == true &&
                harness.State.GetState("Radarr-torrent")?.Alive == true,
                TimeSpan.FromSeconds(5));

            harness.State.GetState("Radarr-search")!.Alive.Should().BeTrue();
            harness.State.GetState("Radarr-torrent")!.Alive.Should().BeTrue();
        }
        finally
        {
            torrentGate.TrySetResult();
            cts.Cancel();
            await harness.Manager.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task DualLoops_SearchRunsWhileTorrentProcessingIsInFlight()
    {
        var torrentStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var torrentRelease = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var searchCalled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var torrent = new Mock<ITorrentProcessor>();
        torrent.Setup(p => p.ProcessTorrentsAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(async (string _, CancellationToken ct) =>
            {
                torrentStarted.TrySetResult();
                try { await torrentRelease.Task.WaitAsync(ct); }
                catch (OperationCanceledException) { }
            });

        var media = new Mock<IArrMediaService>();
        media.Setup(m => m.SearchMissingMediaAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Callback(() => searchCalled.TrySetResult())
            .ReturnsAsync(new SearchResult { LoopCompleted = true, SearchesTriggered = 1 });

        using var harness = WorkerHarness.Create(media.Object, torrent.Object, managedInstance: true);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));

        await harness.Manager.StartAsync(cts.Token);
        try
        {
            await torrentStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            torrentRelease.Task.IsCompleted.Should().BeFalse();
            await searchCalled.Task.WaitAsync(TimeSpan.FromSeconds(5));
        }
        finally
        {
            torrentRelease.TrySetResult();
            cts.Cancel();
            await harness.Manager.StopAsync(CancellationToken.None);
        }
    }

    private static async Task WaitUntilAsync(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (condition())
                return;
            await Task.Delay(50);
        }
        condition().Should().BeTrue("condition did not become true within timeout");
    }

    private sealed class WorkerHarness : IDisposable
    {
        public ArrWorkerManager Manager { get; }
        public TorrentarrDbContext Db { get; }
        public ProcessStateManager State { get; }
        private readonly SqliteConnection _keepAlive;

        private WorkerHarness(
            ArrWorkerManager manager,
            TorrentarrDbContext db,
            ProcessStateManager state,
            SqliteConnection keepAlive)
        {
            Manager = manager;
            Db = db;
            State = state;
            _keepAlive = keepAlive;
        }

        public static WorkerHarness Create(
            IArrMediaService media,
            ITorrentProcessor? torrent = null,
            bool managedInstance = false)
        {
            var dbName = $"awm-{Guid.NewGuid():N}";
            var cs = $"Data Source={dbName};Mode=Memory;Cache=Shared";
            var keepAlive = new SqliteConnection(cs);
            keepAlive.Open();
            var options = new DbContextOptionsBuilder<TorrentarrDbContext>().UseSqlite(cs).Options;
            var db = new TorrentarrDbContext(options);
            db.Database.EnsureCreated();

            var config = new TorrentarrConfig();
            config.Settings.LoopSleepTimer = 1;
            config.Settings.FFprobeAutoUpdate = false;
            if (managedInstance)
            {
                config.ArrInstances["Radarr"] = new ArrInstanceConfig
                {
                    Managed = true,
                    URI = "http://127.0.0.1:1",
                    APIKey = "test",
                    Category = "movies",
                    Type = "mock",
                    Search = new SearchConfig { SearchMissing = true, SearchRequestsEvery = 1 }
                };
            }

            var state = new ProcessStateManager();
            var torrentProcessor = torrent ?? Mock.Of<ITorrentProcessor>(p =>
                p.ProcessTorrentsAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()) == Task.CompletedTask);

            var services = new ServiceCollection();
            services.AddSingleton(config);
            services.AddSingleton(db);
            services.AddSingleton<TorrentarrDbContext>(db);
            services.AddSingleton<IArrMediaService>(media);
            services.AddSingleton<ITorrentProcessor>(torrentProcessor);
            services.AddSingleton<IImportPathTracker>(new StubImportPathTracker());
            services.AddSingleton<ILogger<ArrSyncService>>(NullLogger<ArrSyncService>.Instance);
            services.AddSingleton(new DatabaseRestartCoordinator());
            services.AddScoped<ArrSyncService>();
            var provider = services.BuildServiceProvider();

            var manager = new ArrWorkerManager(
                NullLogger<ArrWorkerManager>.Instance,
                provider.GetRequiredService<IServiceScopeFactory>(),
                config,
                state,
                new StubConnectivityService());

            return new WorkerHarness(manager, db, state, keepAlive);
        }

        public async Task<MoviesFilesModel> ReloadMovie()
        {
            Db.ChangeTracker.Clear();
            return await Db.Movies.SingleAsync();
        }

        public void Dispose()
        {
            Db.Dispose();
            _keepAlive.Dispose();
        }
    }

    private sealed class StubImportPathTracker : IImportPathTracker
    {
        public bool IsPathAlreadyScanned(string normalizedPath) => false;
        public bool IsHashAlreadyScanned(string hash) => false;
        public void MarkScanned(string normalizedPath, string hash) { }
        public void RemoveEmptyPathsUnder(string completedFolderRoot) { }
        public void ClearIfFolderEmpty(string completedFolderRoot) { }
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void TryGetKnownArrVersion_EmptyOrWhitespace_IsNotConnected(string? version)
    {
        ArrWorkerManager.TryGetKnownArrVersion(version, out var known).Should().BeFalse();
        known.Should().BeEmpty();
    }

    [Fact]
    public void TryGetKnownArrVersion_PresentVersion_IsConnected()
    {
        ArrWorkerManager.TryGetKnownArrVersion("  6.0.2  ", out var known).Should().BeTrue();
        known.Should().Be("6.0.2");
    }

    private sealed class StubConnectivityService : IConnectivityService
    {
        public bool IsConnected => true;
        public DateTime? LastChecked => DateTime.UtcNow;
        public Task<bool> IsConnectedAsync(CancellationToken cancellationToken = default) => Task.FromResult(true);
        public Task<bool> IsQBittorrentReachableAsync(CancellationToken cancellationToken = default) => Task.FromResult(true);
    }
}

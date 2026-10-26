using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Torrentarr.Core.Configuration;
using Torrentarr.Core.Services;
using Torrentarr.Infrastructure.Database;
using Torrentarr.Infrastructure.Database.Models;
using Torrentarr.Infrastructure.Services;
using Xunit;

namespace Torrentarr.Infrastructure.Tests.Services;

public class ActiveWorkerSearchTests
{
    [Theory]
    [InlineData(false, false, 1, 0)]
    [InlineData(true, false, 0, 1)]
    [InlineData(false, true, 1, 1)]
    public async Task Search_RunsExclusiveOrAdditiveModes_AndPersistsAcrossRestart(bool exclusive, bool additive, int missingCalls, int upgradeCalls)
    {
        var config = new ArrInstanceConfig
        {
            Category = "movies",
            Search = new SearchConfig
            {
                SearchMissing = true,
                DoUpgradeSearch = exclusive,
                QualityUnmetSearch = additive,
                SearchRequestsEvery = 3600
            }
        };
        using var harness = new Harness(config);
        harness.Media.Setup(m => m.SearchMissingMediaAsync("movies", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SearchResult { SearchesTriggered = 2, ItemsSearched = 3 });
        harness.Media.Setup(m => m.SearchQualityUpgradesAsync("movies", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SearchResult { SearchesTriggered = 1, ItemsSearched = 1 });
        await harness.Worker.ProcessSearchAsync(CancellationToken.None);
        await harness.Worker.ProcessSearchAsync(CancellationToken.None);
        harness.Media.Verify(m => m.SearchMissingMediaAsync("movies", It.IsAny<CancellationToken>()), Times.Exactly(missingCalls));
        harness.Media.Verify(m => m.SearchQualityUpgradesAsync("movies", It.IsAny<CancellationToken>()), Times.Exactly(upgradeCalls));
        var metrics = new WorkerRuntimeMetrics();
        using var log = new Serilog.LoggerConfiguration().CreateLogger();
        await ArrWorkerService.LoadSearchActivityAsync(harness.Provider, "Radarr", metrics, log, CancellationToken.None);
        metrics.SearchSummary.Should().Be(harness.Metrics.SearchSummary);
        metrics.SearchTimestamp.Should().Be(harness.Metrics.SearchTimestamp);
        metrics.MetricType.Should().Be("search");
        harness.Db.SearchActivity.Should().ContainSingle();
    }

    [Fact]
    public async Task DisabledSearch_DoesNotCallMedia_AndRequestTimeoutPropagates()
    {
        var config = new ArrInstanceConfig { Category = "movies", Search = new SearchConfig { SearchMissing = false, DoUpgradeSearch = true } };
        using var harness = new Harness(config);
        await harness.Worker.ProcessSearchAsync(CancellationToken.None);
        harness.Media.VerifyNoOtherCalls();
        config.Search.SearchMissing = true;
        harness.Media.Setup(m => m.SearchQualityUpgradesAsync("movies", It.IsAny<CancellationToken>()))
            .ThrowsAsync(new TaskCanceledException("request timeout"));
        await FluentActions.Awaiting(() => harness.Worker.ProcessSearchAsync(CancellationToken.None)).Should().ThrowAsync<TaskCanceledException>();
    }

    [Fact]
    public async Task CompletedLoop_PreservesFlagsUntilSearchDue_AndResetsOnlyThisInstance()
    {
        var config = new ArrInstanceConfig
        {
            Type = "radarr",
            Category = "movies",
            Search = new SearchConfig
            {
                SearchMissing = true,
                SearchAgainOnSearchCompletion = true,
                SearchRequestsEvery = 3600
            }
        };
        using var harness = new Harness(config, sqlite: true);
        harness.Db.Movies.AddRange(new MoviesFilesModel { EntryId = 1, ArrInstance = "Radarr", Searched = true, Upgrade = true },
            new MoviesFilesModel { EntryId = 2, ArrInstance = "Other", Searched = true, Upgrade = true });
        await harness.Db.SaveChangesAsync();
        harness.Media.Setup(m => m.SearchMissingMediaAsync("movies", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SearchResult { LoopCompleted = true });
        await harness.Worker.ProcessSearchAsync(CancellationToken.None);
        await harness.Worker.ProcessSearchAsync(CancellationToken.None);
        harness.Media.Verify(m => m.SearchMissingMediaAsync("movies", It.IsAny<CancellationToken>()), Times.Once);
        harness.Db.ChangeTracker.Clear();
        (await harness.Db.Movies.SingleAsync(m => m.ArrInstance == "Radarr")).Searched.Should().BeTrue();
        (await harness.Db.Movies.SingleAsync(m => m.ArrInstance == "Radarr")).Upgrade.Should().BeTrue();
        using var restarted = harness.CreateWorker(config);
        await restarted.ProcessSearchAsync(CancellationToken.None);
        harness.Db.ChangeTracker.Clear();
        (await harness.Db.Movies.SingleAsync(m => m.ArrInstance == "Radarr")).Searched.Should().BeTrue();
        config.ProcessingOnly = true;
        config.Search.SearchRequestsEvery = 0;
        await harness.Worker.ProcessSearchAsync(CancellationToken.None);
        config.ProcessingOnly = false;
        config.Search.SearchMissing = false;
        await harness.Worker.ProcessSearchAsync(CancellationToken.None);
        harness.Db.ChangeTracker.Clear();
        (await harness.Db.Movies.SingleAsync(m => m.ArrInstance == "Radarr")).Searched.Should().BeTrue();
        config.Search.SearchMissing = true;
        await harness.Worker.ProcessSearchAsync(CancellationToken.None);
        harness.Db.ChangeTracker.Clear();
        var own = await harness.Db.Movies.SingleAsync(m => m.ArrInstance == "Radarr");
        own.Searched.Should().BeFalse();
        own.Upgrade.Should().BeFalse();
        (await harness.Db.Movies.SingleAsync(m => m.ArrInstance == "Other")).Searched.Should().BeTrue();
        (await harness.Db.Movies.SingleAsync(m => m.ArrInstance == "Other")).Upgrade.Should().BeTrue();
    }

    [Fact]
    public async Task CompletedLoop_WithSuccessfulSync_PreservesProgressDuringThrottleAndRestart()
    {
        using var api = new ArrSyncStub();
        var config = new ArrInstanceConfig
        {
            Type = "radarr",
            Category = "movies",
            URI = api.Url,
            Search = new SearchConfig { SearchMissing = true, SearchAgainOnSearchCompletion = true, SearchRequestsEvery = 3600 }
        };
        using var harness = new Harness(config, sqlite: true, skipSync: false);
        var calls = 0;
        harness.Media.Setup(m => m.SearchMissingMediaAsync("movies", It.IsAny<CancellationToken>()))
            .Returns(async () =>
            {
                if (++calls > 1)
                    (await harness.Db.Movies.AsNoTracking().SingleAsync()).Searched.Should().BeTrue();
                await harness.Db.Movies.ExecuteUpdateAsync(s => s.SetProperty(m => m.Searched, true).SetProperty(m => m.Upgrade, true));
                harness.Db.ChangeTracker.Clear();
                return new SearchResult { LoopCompleted = true };
            });
        await harness.Worker.ProcessSearchAsync(CancellationToken.None);
        await harness.Worker.ProcessSearchAsync(CancellationToken.None);
        harness.Media.Verify(m => m.SearchMissingMediaAsync("movies", It.IsAny<CancellationToken>()), Times.Once);
        harness.Db.ChangeTracker.Clear();
        (await harness.Db.Movies.SingleAsync()).Searched.Should().BeTrue();
        (await harness.Db.Movies.SingleAsync()).Upgrade.Should().BeTrue();
        using var restarted = harness.CreateWorker(config);
        await restarted.ProcessSearchAsync(CancellationToken.None);
        harness.Db.ChangeTracker.Clear();
        (await harness.Db.Movies.SingleAsync()).Searched.Should().BeTrue();
        (await harness.Db.Movies.SingleAsync()).Upgrade.Should().BeTrue();
    }

    [Fact]
    public async Task BusyLoops_AcknowledgeCompletedRevisionWithoutSleeping()
    {
        using var api = new ArrSyncStub();
        var instance = new ArrInstanceConfig
        {
            Managed = true,
            Type = "radarr",
            Category = "movies",
            URI = api.Url,
            ProcessingOnly = true,
            RssSyncTimer = 0,
            RefreshDownloadsTimer = 0
        };
        using var harness = new Harness(instance, skipSync: false);
        var config = harness.Provider.GetRequiredService<TorrentarrConfig>();
        config.Settings.LoopSleepTimer = 0;
        config.Apply(config, 42);
        var connectivity = new Mock<IConnectivityService>();
        using var worker = new ArrWorkerService(NullLogger<ArrWorkerService>.Instance, config, instance,
            new WorkerContext { InstanceName = "Radarr" }, harness.Provider,
            new QBittorrentConnectionManager(NullLogger<QBittorrentConnectionManager>.Instance),
            connectivity.Object, new SearchYearCursor(), harness.Metrics);
        const System.Reflection.BindingFlags flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
        typeof(ArrWorkerService).GetField("_initialized", flags)!.SetValue(worker, true);
        typeof(ArrWorkerService).GetField("_initializedRevision", flags)!.SetValue(worker, 42L);
        typeof(ArrWorkerService).GetField("_qbitSetupComplete", flags)!.SetValue(worker, true);
        foreach (var loop in new[] { "Torrent", "Search" })
        {
            config.Apply(config, 42);
            using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var calls = 0;
            connectivity.Setup(c => c.IsConnectedAsync(It.IsAny<CancellationToken>())).Returns(() =>
            {
                if (++calls == 2)
                {
                    stop.Cancel();
                    throw new OperationCanceledException(stop.Token);
                }
                config.Apply(config, 43);
                return Task.FromResult(true);
            });
            await (Task)typeof(ArrWorkerService).GetMethod($"Run{loop}LoopAsync", flags)!.Invoke(worker, [stop.Token])!;
            calls.Should().Be(2);
            (loop == "Torrent" ? harness.Metrics.Torrent : harness.Metrics.Search).LastSuccess.Should().NotBeNull();
            (loop == "Torrent" ? harness.Metrics.TorrentRevision : harness.Metrics.SearchRevision).Should().Be(42);
        }
        harness.Metrics.Snapshot("Radarr", false).ConfigRevision.Should().Be(42);
    }

    [Theory]
    [InlineData("Torrent")]
    [InlineData("Search")]
    public async Task RenameDuringConnectivity_DiscardsThePinnedCycle(string loop)
    {
        var reloader = new Mock<IConfigReloader>();
        var instance = new ArrInstanceConfig { Managed = true, URI = "http://localhost", Category = "movies" };
        using var harness = new Harness(instance, reloader: reloader.Object);
        instance.URI = "http://localhost";
        var config = harness.Provider.GetRequiredService<TorrentarrConfig>();
        config.Settings.LoopSleepTimer = 0;
        config.Apply(config, 42);
        var renamed = new TorrentarrConfig { Settings = config.Settings };
        renamed.ArrInstances["Radarr-renamed"] = instance;
        var renamedSnapshotPublished = false;
        reloader.Setup(r => r.ReloadConfig()).Returns(() =>
        {
            renamedSnapshotPublished.Should().BeTrue();
            config.Apply(renamed, 43);
            return true;
        });
        reloader.Setup(r => r.ResolveInstanceName("Radarr", 0))
            .Returns(() => config.LatestRevision == 43 ? "Radarr-renamed" : "Radarr");
        var connected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var calls = 0;
        var connectivity = new Mock<IConnectivityService>();
        connectivity.Setup(c => c.IsConnectedAsync(It.IsAny<CancellationToken>())).Returns(async () =>
        {
            if (++calls == 2)
            {
                stop.Cancel();
                throw new OperationCanceledException(stop.Token);
            }
            connected.SetResult();
            await release.Task.WaitAsync(stop.Token);
            return true;
        });
        using var worker = new ArrWorkerService(NullLogger<ArrWorkerService>.Instance, config, instance,
            new WorkerContext { InstanceName = "Radarr" }, harness.Provider,
            new QBittorrentConnectionManager(NullLogger<QBittorrentConnectionManager>.Instance),
            connectivity.Object, new SearchYearCursor(), harness.Metrics);
        const System.Reflection.BindingFlags flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
        typeof(ArrWorkerService).GetField("_initialized", flags)!.SetValue(worker, true);
        var running = (Task)typeof(ArrWorkerService).GetMethod($"Run{loop}LoopAsync", flags)!.Invoke(worker, [stop.Token])!;
        await connected.Task.WaitAsync(stop.Token);
        renamedSnapshotPublished = true;
        release.SetResult();
        await running;
        reloader.Verify(r => r.ReloadConfig(), Times.Once);
        harness.Metrics.InstanceName.Should().Be("Radarr-renamed");
        typeof(ArrWorkerService).GetField(loop == "Torrent" ? "_consecutiveErrors" : "_consecutiveSearchErrors", flags)!
            .GetValue(worker).Should().Be(0);
        harness.Metrics.Torrent.LastSuccess.Should().BeNull();
        harness.Metrics.Search.LastSuccess.Should().BeNull();
        harness.Db.Movies.Should().BeEmpty();
    }

    private sealed class Harness : IDisposable
    {
        public Mock<IArrMediaService> Media { get; } = new(MockBehavior.Strict);
        public WorkerRuntimeMetrics Metrics { get; } = new();
        public ServiceProvider Provider { get; }
        public TorrentarrDbContext Db { get; }
        public ArrWorkerService Worker { get; }
        public Harness(ArrInstanceConfig instance, bool sqlite = false, bool skipSync = true, IConfigReloader? reloader = null)
        {
            var config = new TorrentarrConfig();
            config.ArrInstances["Radarr"] = instance;
            if (skipSync) instance.URI = ""; // Other tests isolate the search cycle from sync.
            var options = new DbContextOptionsBuilder<TorrentarrDbContext>();
            if (sqlite) options.UseSqlite("Data Source=:memory:");
            else options.UseInMemoryDatabase(Guid.NewGuid().ToString());
            Db = new TorrentarrDbContext(options.Options);
            if (sqlite) Db.Database.OpenConnection();
            Db.Database.EnsureCreated();
            var services = new ServiceCollection();
            services.AddSingleton(config).AddSingleton(Db).AddSingleton(Media.Object);
            if (reloader != null) services.AddSingleton(reloader);
            services.AddSingleton(Mock.Of<ITorrentProcessor>()).AddSingleton(Mock.Of<ITorrentCacheService>());
            services.AddSingleton(new DatabaseRestartCoordinator());
            services.AddSingleton(Microsoft.Extensions.Logging.Abstractions.NullLogger<ArrSyncService>.Instance as Microsoft.Extensions.Logging.ILogger<ArrSyncService>);
            services.AddScoped<ArrSyncService>();
            Provider = services.BuildServiceProvider();
            Worker = CreateWorker(instance);
        }
        public ArrWorkerService CreateWorker(ArrInstanceConfig instance) =>
            new(NullLogger<ArrWorkerService>.Instance, Provider.GetRequiredService<TorrentarrConfig>(), instance,
                new WorkerContext { InstanceName = "Radarr" }, Provider,
                new QBittorrentConnectionManager(NullLogger<QBittorrentConnectionManager>.Instance),
                Mock.Of<IConnectivityService>(), new SearchYearCursor(), Metrics);
        public void Dispose() { Worker.Dispose(); Provider.Dispose(); Db.Dispose(); }
    }
}

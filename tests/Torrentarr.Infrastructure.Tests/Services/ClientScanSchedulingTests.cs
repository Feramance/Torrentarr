using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Torrentarr.Core.Configuration;
using Torrentarr.Core.Interfaces;
using Torrentarr.Core.Models;
using Torrentarr.Core.Services;
using Torrentarr.Infrastructure.Database;
using Torrentarr.Infrastructure.Services;
using Xunit;

namespace Torrentarr.Infrastructure.Tests.Services;

public class ClientScanSchedulingTests
{
    [Fact]
    public async Task FilteredProcessingAndSeeding_DoNotReadOrMutateOtherClients()
    {
        var fast = Client(); var slow = Client();
        var registry = new Mock<ITorrentClientRegistry>();
        registry.Setup(r => r.GetAllClients()).Returns(new Dictionary<string, ITorrentClient> { ["fast"] = fast.Object, ["slow"] = slow.Object });
        var config = Configuration();
        config.Settings.Tagless = true;
        using var db = Database();
        var seeding = new SeedingService(NullLogger<SeedingService>.Instance, db, config, registry.Object);
        var processor = new TorrentProcessor(NullLogger<TorrentProcessor>.Instance, registry.Object, db, config,
            new TorrentCacheService(NullLogger<TorrentCacheService>.Instance), new DatabaseRestartCoordinator(), seedingService: seeding);
        var due = new HashSet<string> { "fast" };
        await processor.ProcessTorrentsAsync("movies", due);
        await seeding.UpdateSeedingTagsAsync("movies", due);
        await seeding.RemoveCompletedTorrentsAsync("movies", due);
        fast.Verify(c => c.GetTorrentsAsync(It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.AtLeastOnce);
        slow.Invocations.Should().BeEmpty();
    }

    [Fact]
    public async Task ProtectionInspections_FollowClientIntervalsAndKeepFailureBackoff()
    {
        var fast = Client(); var slow = Client();
        var registry = new Mock<ITorrentClientRegistry>();
        registry.Setup(r => r.GetClient("fast")).Returns(fast.Object);
        registry.Setup(r => r.GetClient("slow")).Returns(slow.Object);
        var config = Configuration();
        using var db = Database();
        using var provider = new ServiceCollection().AddSingleton(db).AddSingleton(Mock.Of<ISeedingService>()).BuildServiceProvider();
        var clock = new ScanClock();
        using var service = new TorrentAutomationService(config, registry.Object,
            provider.GetRequiredService<IServiceScopeFactory>(), NullLogger<TorrentAutomationService>.Instance)
        { Clock = clock };
        await service.InspectProtectionAsync(default);
        clock.Now += TimeSpan.FromSeconds(5);
        await service.InspectProtectionAsync(default);
        fast.Verify(c => c.GetTorrentsAsync(It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Exactly(2));
        slow.Verify(c => c.GetTorrentsAsync(It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Once);
        fast.Setup(c => c.GetTorrentsAsync(It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>())).ThrowsAsync(new IOException("offline"));
        clock.Now += TimeSpan.FromSeconds(5);
        await service.InspectProtectionAsync(default);
        clock.Now += TimeSpan.FromSeconds(5);
        await service.InspectProtectionAsync(default);
        fast.Verify(c => c.GetTorrentsAsync(It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Exactly(3));
        clock.Now = DateTimeOffset.UnixEpoch.AddSeconds(120);
        await service.InspectProtectionAsync(default);
        slow.Verify(c => c.GetTorrentsAsync(It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Exactly(2));
    }

    [Fact]
    public async Task InProcessMetrics_RefreshOnlyDueClientsAndRetainOtherCounts()
    {
        var fast = Client(); var slow = Client();
        fast.Setup(c => c.GetTorrentsAsync(It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Enumerable.Range(0, 4).Select(_ => new TorrentInfo()).ToList());
        slow.Setup(c => c.GetTorrentsAsync(It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Enumerable.Range(0, 7).Select(_ => new TorrentInfo()).ToList());
        var registry = new Mock<ITorrentClientRegistry>();
        registry.Setup(r => r.GetAllClients()).Returns(new Dictionary<string, ITorrentClient> { ["fast"] = fast.Object, ["slow"] = slow.Object });
        var config = Configuration();
        using var provider = new ServiceCollection().BuildServiceProvider();
        var states = new ProcessStateManager();
        states.Initialize("Radarr-torrent", new ArrProcessState { Name = "Radarr-torrent" });
        using var manager = new ArrWorkerManager(NullLogger<ArrWorkerManager>.Instance,
            provider.GetRequiredService<IServiceScopeFactory>(), config, states, Mock.Of<IConnectivityService>(), registry: registry.Object);
        var arr = new ArrInstanceConfig { Category = "movies" };
        await manager.UpdateTorrentCountsAsync("Radarr", arr, new HashSet<string> { "fast", "slow" }, default);
        states.GetState("Radarr-torrent")!.CategoryCount.Should().Be(11);
        fast.Setup(c => c.GetTorrentsAsync(It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Enumerable.Range(0, 2).Select(_ => new TorrentInfo()).ToList());
        await manager.UpdateTorrentCountsAsync("Radarr", arr, new HashSet<string> { "fast" }, default);
        states.GetState("Radarr-torrent")!.CategoryCount.Should().Be(9);
        slow.Verify(c => c.GetTorrentsAsync(It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task CategoryLoops_ProcessOnlyDueClientsAndStopCleanly(bool qbitOnly, bool failFirst)
    {
        var clock = new ScanClock();
        var config = Configuration();
        var seen = new List<HashSet<string>>();
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var processor = new Mock<ITorrentProcessor>();
        processor.Setup(p => p.ProcessTorrentsAsync("movies", It.IsAny<IReadOnlySet<string>>(), It.IsAny<CancellationToken>()))
            .Returns((string _, IReadOnlySet<string>? ids, CancellationToken ct) =>
            {
                seen.Add(ids!.ToHashSet());
                if (failFirst && seen.Count == 1) throw new TorrentProcessingUnavailableException("client offline");
                if (clock.Now >= DateTimeOffset.UnixEpoch.AddSeconds(120)) stop.Cancel();
                return Task.CompletedTask;
            });
        var reloader = new Mock<IConfigReloader>();
        reloader.Setup(r => r.WaitForChangeAsync(It.IsAny<long>(), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
            .Returns((long _, TimeSpan delay, CancellationToken ct) =>
            {
                ct.ThrowIfCancellationRequested();
                clock.Now += delay;
                return Task.CompletedTask;
            });
        var registry = new Mock<ITorrentClientRegistry>();
        registry.Setup(r => r.GetAllClients()).Returns(new Dictionary<string, ITorrentClient>());
        var connectivity = new Mock<IConnectivityService>();
        connectivity.Setup(c => c.IsConnectedAsync(It.IsAny<CancellationToken>())).ReturnsAsync(true);
        using var provider = new ServiceCollection().AddSingleton(processor.Object).AddSingleton(Mock.Of<ISeedingService>())
            .AddSingleton<IImportPathTracker, ImportPathTracker>()
            .AddSingleton(new QBitCategoryEnsureService(NullLogger<QBitCategoryEnsureService>.Instance, config, registry.Object)).BuildServiceProvider();
        var scopes = provider.GetRequiredService<IServiceScopeFactory>();
        var states = new ProcessStateManager();
        var logger = new Mock<ILogger<QBitCategoryWorkerManager>>();
        Task running;
        const System.Reflection.BindingFlags flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
        if (qbitOnly)
        {
            using var manager = new QBitCategoryWorkerManager(logger.Object,
                config, scopes, states, connectivity.Object, reloader.Object)
            { Clock = clock };
            running = (Task)typeof(QBitCategoryWorkerManager).GetMethod("RunCategoryLoopAsync", flags)!
                .Invoke(manager, ["movies", "state", stop.Token])!;
            try { await running; }
            catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
        }
        else
        {
            using var manager = new ArrWorkerManager(NullLogger<ArrWorkerManager>.Instance, scopes, config, states,
                connectivity.Object, reloader: reloader.Object, registry: registry.Object)
            { Clock = clock };
            var arr = new ArrInstanceConfig { Category = "movies", RssSyncTimer = 0, RefreshDownloadsTimer = 0 };
            running = (Task)typeof(ArrWorkerManager).GetMethod("RunTorrentLoopAsync", flags)!
                .Invoke(manager, ["Radarr", arr, "state", stop.Token])!;
            await running;
        }
        seen.Should().HaveCount(25);
        seen.Count(ids => ids.Contains("slow")).Should().Be(2);
        seen.Should().OnlyContain(ids => ids.Contains("fast"));
        if (failFirst && !qbitOnly)
            seen[1].Should().Equal(new[] { "fast" }, "a client outage must not force a full rescan");
        if (failFirst && qbitOnly)
            logger.Invocations.Where(i => i.Method.Name == "Log")
                .Should().Contain(i => (LogLevel)i.Arguments[0]! == LogLevel.Information)
                .And.NotContain(i => (LogLevel)i.Arguments[0]! == LogLevel.Error);
    }

    private static Mock<ITorrentClient> Client()
    {
        var client = new Mock<ITorrentClient>();
        client.SetupGet(c => c.Capabilities).Returns(TorrentClientCapabilities.QBittorrent);
        client.Setup(c => c.GetTorrentsAsync(It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>())).ReturnsAsync([]);
        return client;
    }
    private static TorrentarrConfig Configuration()
    {
        var config = new TorrentarrConfig();
        config.Settings.LoopSleepTimer = 3600;
        config.TorrentClients["fast"] = new() { Host = "fixture", TorrentScanIntervalSeconds = 5, ManagedCategories = ["movies"] };
        config.TorrentClients["slow"] = new() { Host = "fixture", TorrentScanIntervalSeconds = 120, ManagedCategories = ["movies"] };
        return config;
    }
    private static TorrentarrDbContext Database() => new(new DbContextOptionsBuilder<TorrentarrDbContext>()
        .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
    private sealed class ScanClock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = DateTimeOffset.UnixEpoch;
        public override DateTimeOffset GetUtcNow() => Now;
    }
}

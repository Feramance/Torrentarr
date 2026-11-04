using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Torrentarr.Core.Configuration;
using Torrentarr.Core.Interfaces;
using Torrentarr.Core.Models;
using Torrentarr.Core.Services;
using Torrentarr.Infrastructure.Database;
using Torrentarr.Infrastructure.Services;
using Xunit;

namespace Torrentarr.Host.Tests;

public class ProcessOrchestratorServiceTests
{
    [Fact]
    public async Task FreeSpace_ConfigSaveWakesSingleManagerAndRefreshesClientMonitors()
    {
        var config = new TorrentarrConfig();
        config.Settings.LoopSleepTimer = 3600;
        config.TorrentClients["one"] = new() { Host = "localhost", UserName = "test", Password = "test", ManagedCategories = ["movies"] };
        config.TorrentClients["two"] = new() { Host = "localhost", UserName = "test", Password = "test", ManagedCategories = ["tv"] };
        var registry = new Mock<ITorrentClientRegistry>();
        registry.Setup(r => r.GetAllClients()).Returns(new Dictionary<string, ITorrentClient>());
        registry.Setup(r => r.IsConnected()).Returns(true);
        var manager = new Mock<IFreeSpaceService>();
        manager.Setup(m => m.ProcessScheduledAsync(It.IsAny<IReadOnlySet<string>>(), It.IsAny<TorrentScanSchedule>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new FreeSpacePassResult(0, 0, 0, true));
        using var provider = new ServiceCollection().AddSingleton(manager.Object).BuildServiceProvider();
        var states = new ProcessStateManager();
        using var service = new ProcessOrchestratorService(NullLogger<ProcessOrchestratorService>.Instance,
            config, registry.Object, provider.GetRequiredService<IServiceScopeFactory>(), states, Mock.Of<ISafeDeletionService>());
        await service.StartAsync(CancellationToken.None);
        try
        {
            await WaitUntilAsync(() => states.GetState("Failed")?.Alive == true);
            manager.Verify(m => m.ProcessScheduledAsync(It.IsAny<IReadOnlySet<string>>(), It.IsAny<TorrentScanSchedule>(), It.IsAny<CancellationToken>()), Times.Never);
            config.TorrentClients["one"].FreeSpace = [new() { Path = Path.GetTempPath(), Limit = "1G", Enabled = true }];
            config.TorrentClients["two"].FreeSpace = [new() { Path = Path.GetTempPath(), Limit = "2G", Enabled = true }];
            service.NotifyConfigurationChanged();
            await WaitUntilAsync(() => states.GetState("FreeSpaceManager")!.Alive);
            manager.Verify(m => m.ProcessScheduledAsync(It.Is<IReadOnlySet<string>>(c => c.Contains("movies") && c.Contains("tv")), It.IsAny<TorrentScanSchedule>(), It.IsAny<CancellationToken>()), Times.Once);
            foreach (var client in config.TorrentClients.Values) client.FreeSpace[0].Enabled = false;
            service.NotifyConfigurationChanged();
            await WaitUntilAsync(() => !states.GetState("FreeSpaceManager")!.Alive);
            manager.Verify(m => m.ProcessScheduledAsync(It.IsAny<IReadOnlySet<string>>(), It.IsAny<TorrentScanSchedule>(), It.IsAny<CancellationToken>()), Times.Once);
        }
        finally { await service.StopAsync(CancellationToken.None); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ClientScans_UseIndependentIntervalsRegardlessOfDatabaseRefresh(bool failFirstPass)
    {
        var config = new TorrentarrConfig();
        config.Settings.LoopSleepTimer = 3600;
        config.TorrentClients["fast"] = new() { Host = "fixture", UserName = "test", Password = "test", TorrentScanIntervalSeconds = 5 };
        config.TorrentClients["slow"] = new() { Host = "fixture", UserName = "test", Password = "test", TorrentScanIntervalSeconds = 120 };
        config.TorrentClients["fast"].ManagedCategories = ["movies"];
        config.TorrentClients["fast"].Trackers = [new() { SortTorrents = true }];
        var fast = new Mock<ITorrentClient>();
        var slow = new Mock<ITorrentClient>();
        foreach (var client in new[] { fast, slow })
            client.Setup(c => c.GetTorrentsAsync(It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>())).ReturnsAsync([]);
        var attempts = 0;
        fast.Setup(c => c.GetTorrentsAsync(It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .Returns(() => Interlocked.Increment(ref attempts) == 1 && failFirstPass
                ? Task.FromException<List<TorrentInfo>>(new IOException("temporary failure"))
                : Task.FromResult(new List<TorrentInfo>()));
        var registry = new Mock<ITorrentClientRegistry>();
        registry.Setup(r => r.GetAllClients()).Returns(new Dictionary<string, ITorrentClient> { ["fast"] = fast.Object, ["slow"] = slow.Object });
        registry.Setup(r => r.IsConnected()).Returns(true);
        using var db = new TorrentarrDbContext(new DbContextOptionsBuilder<TorrentarrDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        using var provider = new ServiceCollection().AddSingleton(db).AddSingleton(Mock.Of<ISeedingService>()).BuildServiceProvider();
        var clock = new ManualClock();
        var states = new ProcessStateManager();
        using var service = new ProcessOrchestratorService(NullLogger<ProcessOrchestratorService>.Instance,
            config, registry.Object, provider.GetRequiredService<IServiceScopeFactory>(), states,
            Mock.Of<ISafeDeletionService>())
        { Clock = clock };
        await service.StartAsync(CancellationToken.None);
        try
        {
            await WaitUntilAsync(() => states.GetState("Failed")?.Alive == true);
            var reads = clock.Reads;
            await WaitUntilAsync(() => clock.Reads > reads);
            // One special-category read, one tracker sync, and one queue-sort read per due client.
            fast.Verify(c => c.GetTorrentsAsync(It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Exactly(3));
            slow.Verify(c => c.GetTorrentsAsync(It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Exactly(3));
            clock.Advance(TimeSpan.FromSeconds(5));
            await WaitUntilAsync(() => fast.Invocations.Count(i => i.Method.Name == nameof(ITorrentClient.GetTorrentsAsync)) == 6);
            slow.Verify(c => c.GetTorrentsAsync(It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Exactly(3));
            reads = clock.Reads;
            await WaitUntilAsync(() => clock.Reads > reads);
            clock.Advance(TimeSpan.FromSeconds(115));
            await WaitUntilAsync(() => slow.Invocations.Count(i => i.Method.Name == nameof(ITorrentClient.GetTorrentsAsync)) == 6);
        }
        finally { await service.StopAsync(CancellationToken.None); }
    }

    [Fact]
    public async Task FreeSpace_ReconnectionWakesManagerBeforeNextDeadline()
    {
        var config = new TorrentarrConfig();
        config.Settings.LoopSleepTimer = 3600;
        config.TorrentClients["one"] = new()
        {
            Type = "fake",
            Host = "fixture",
            UserName = "test",
            Password = "test",
            ManagedCategories = ["movies"],
            FreeSpace = [new() { Path = Path.GetTempPath(), Enabled = true, Limit = "1G" }]
        };
        var client = new Mock<ITorrentClient>();
        client.Setup(c => c.LoginAsync(It.IsAny<CancellationToken>())).ReturnsAsync(true);
        client.Setup(c => c.GetVersionAsync(It.IsAny<CancellationToken>())).ReturnsAsync("5.2");
        client.Setup(c => c.GetTorrentsAsync(It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);
        var factory = new Mock<ITorrentClientFactory>();
        factory.SetupGet(f => f.Type).Returns("fake");
        factory.Setup(f => f.Create(It.IsAny<string>(), It.IsAny<TorrentClientInstanceConfig>())).Returns(client.Object);
        using var registry = new QBittorrentConnectionManager(NullLogger<QBittorrentConnectionManager>.Instance, [factory.Object]);
        using var db = new TorrentarrDbContext(new DbContextOptionsBuilder<TorrentarrDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        var passes = 0;
        var manager = new Mock<IFreeSpaceService>();
        manager.Setup(m => m.ProcessScheduledAsync(It.IsAny<IReadOnlySet<string>>(), It.IsAny<TorrentScanSchedule>(), It.IsAny<CancellationToken>()))
            .Returns(() =>
            {
                Interlocked.Increment(ref passes);
                return Task.FromResult(new FreeSpacePassResult(0, 0, 0, true));
            });
        using var provider = new ServiceCollection().AddSingleton(manager.Object).AddSingleton(db).BuildServiceProvider();
        var clock = new ManualClock();
        using var service = new ProcessOrchestratorService(NullLogger<ProcessOrchestratorService>.Instance,
            config, registry, provider.GetRequiredService<IServiceScopeFactory>(), new ProcessStateManager(),
            Mock.Of<ISafeDeletionService>())
        { Clock = clock };
        await service.StartAsync(CancellationToken.None);
        try
        {
            await WaitUntilAsync(() => Volatile.Read(ref passes) == 1);
            var reads = clock.Reads;
            await WaitUntilAsync(() => clock.Reads > reads);
            (await registry.ForceReconnectAsync("one", config.TorrentClients["one"])).Should().BeTrue();
            await WaitUntilAsync(() => Volatile.Read(ref passes) == 2);
            clock.GetUtcNow().Should().Be(DateTimeOffset.UnixEpoch);
        }
        finally { await service.StopAsync(CancellationToken.None); }
        service.ExecuteTask!.IsCompletedSuccessfully.Should().BeTrue();
    }

    private sealed class ManualClock : TimeProvider
    {
        private long _ticks = DateTimeOffset.UnixEpoch.Ticks;
        private int _reads;
        public int Reads => Volatile.Read(ref _reads);
        public override DateTimeOffset GetUtcNow()
        {
            var now = new DateTimeOffset(Interlocked.Read(ref _ticks), TimeSpan.Zero);
            Interlocked.Increment(ref _reads);
            return now;
        }
        public void Advance(TimeSpan elapsed) => Interlocked.Add(ref _ticks, elapsed.Ticks);
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (!condition()) await Task.Delay(10, timeout.Token);
    }
}

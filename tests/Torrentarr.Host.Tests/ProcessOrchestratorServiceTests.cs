using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Torrentarr.Core.Configuration;
using Torrentarr.Core.Interfaces;
using Torrentarr.Core.Services;
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
        manager.Setup(m => m.ProcessAsync(It.IsAny<IReadOnlySet<string>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new FreeSpacePassResult(0, 0, 0, true));
        using var provider = new ServiceCollection().AddSingleton(manager.Object).BuildServiceProvider();
        var states = new ProcessStateManager();
        using var service = new ProcessOrchestratorService(NullLogger<ProcessOrchestratorService>.Instance,
            config, registry.Object, provider.GetRequiredService<IServiceScopeFactory>(), states, Mock.Of<ISafeDeletionService>());
        await service.StartAsync(CancellationToken.None);
        try
        {
            await WaitUntilAsync(() => states.GetState("Failed")?.Alive == true);
            manager.Verify(m => m.ProcessAsync(It.IsAny<IReadOnlySet<string>>(), It.IsAny<CancellationToken>()), Times.Never);
            config.TorrentClients["one"].FreeSpace = [new() { Path = Path.GetTempPath(), Limit = "1G", Enabled = true }];
            config.TorrentClients["two"].FreeSpace = [new() { Path = Path.GetTempPath(), Limit = "2G", Enabled = true }];
            service.NotifyConfigurationChanged();
            await WaitUntilAsync(() => states.GetState("FreeSpaceManager")!.Alive);
            manager.Verify(m => m.ProcessAsync(It.Is<IReadOnlySet<string>>(c => c.Contains("movies") && c.Contains("tv")), It.IsAny<CancellationToken>()), Times.Once);
            foreach (var client in config.TorrentClients.Values) client.FreeSpace[0].Enabled = false;
            service.NotifyConfigurationChanged();
            await WaitUntilAsync(() => !states.GetState("FreeSpaceManager")!.Alive);
            manager.Verify(m => m.ProcessAsync(It.IsAny<IReadOnlySet<string>>(), It.IsAny<CancellationToken>()), Times.Once);
        }
        finally { await service.StopAsync(CancellationToken.None); }
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!condition()) await Task.Delay(10, timeout.Token);
    }
}

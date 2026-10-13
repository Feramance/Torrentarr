using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Torrentarr.Core.Configuration;
using Torrentarr.Core.Interfaces;
using Torrentarr.Core.Models;
using Torrentarr.Core.Services;
using Torrentarr.Infrastructure.Services;
using Xunit;

namespace Torrentarr.Host.Tests;

public class ProcessOrchestratorServiceTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task FreeSpace_FailedClientDoesNotPreventPausingHealthyDownloads(bool failedFirst)
    {
        var failed = new Mock<ITorrentClient>();
        failed.Setup(c => c.GetTorrentsAsync(It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpRequestException("offline"));
        var healthy = new Mock<ITorrentClient>();
        healthy.Setup(c => c.GetTorrentsAsync(It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([new TorrentInfo { Hash = "abc", Category = "movies", State = "downloading" }]);
        var clients = new Dictionary<string, ITorrentClient>();
        foreach (var id in failedFirst ? new[] { "failed", "healthy" } : new[] { "healthy", "failed" })
            clients[id] = id == "failed" ? failed.Object : healthy.Object;
        var registry = new Mock<ITorrentClientRegistry>();
        registry.Setup(r => r.GetAllClients()).Returns(clients);
        var config = new TorrentarrConfig();
        config.Settings.FreeSpace = "1G";
        using var service = new ProcessOrchestratorService(NullLogger<ProcessOrchestratorService>.Instance,
            config, registry.Object, Mock.Of<IServiceScopeFactory>(), new ProcessStateManager(), Mock.Of<ISafeDeletionService>());
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        typeof(ProcessOrchestratorService).GetField("_freeSpaceFolder", flags)!.SetValue(service, Path.GetTempPath());
        typeof(ProcessOrchestratorService).GetField("_minFreeSpaceBytes", flags)!.SetValue(service, long.MaxValue);

        await (Task)typeof(ProcessOrchestratorService).GetMethod("ProcessFreeSpaceManagerAsync", flags)!
            .Invoke(service, [new HashSet<string> { "movies" }, CancellationToken.None])!;

        healthy.Verify(c => c.PauseTorrentAsync("abc", It.IsAny<CancellationToken>()), Times.Once);
    }
}

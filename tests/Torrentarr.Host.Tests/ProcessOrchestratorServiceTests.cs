using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.EntityFrameworkCore;
using FluentAssertions;
using Torrentarr.Infrastructure.Database;
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
        failed.SetupGet(c => c.Capabilities).Returns(TorrentClientCapabilities.QBittorrent);
        var healthy = new Mock<ITorrentClient>();
        healthy.Setup(c => c.GetTorrentsAsync(It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([new TorrentInfo { Hash = "abc", Category = "movies", State = "downloading" }]);
        var clients = new Dictionary<string, ITorrentClient>();
        foreach (var id in failedFirst ? new[] { "failed", "healthy" } : new[] { "healthy", "failed" })
            clients[id] = id == "failed" ? failed.Object : healthy.Object;
        var registry = new Mock<ITorrentClientRegistry>();
        registry.Setup(r => r.GetAllClients()).Returns(clients);
        healthy.SetupGet(c => c.Capabilities).Returns(TorrentClientCapabilities.QBittorrent);
        healthy.Setup(c => c.AddTagsAsync(It.IsAny<List<string>>(), It.IsAny<List<string>>(), It.IsAny<CancellationToken>())).ReturnsAsync(true);
        healthy.Setup(c => c.PauseTorrentAsync(It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync(true);
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
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task FreeSpace_UsesDatabaseForClientsWithoutTags(bool tags, bool globalTagless)
    {
        using var db = new TorrentarrDbContext(new DbContextOptionsBuilder<TorrentarrDbContext>().UseSqlite("Data Source=:memory:").Options);
        await db.Database.OpenConnectionAsync(); await db.Database.EnsureCreatedAsync();
        using var provider = new ServiceCollection().AddSingleton(db).BuildServiceProvider();
        var torrent = new TorrentInfo { Hash = "abc", Category = "movies", State = "downloading", AmountLeft = 1 };
        var client = new Mock<ITorrentClient>();
        client.SetupGet(c => c.Capabilities).Returns(TorrentClientCapabilities.QBittorrent with { Tags = tags });
        client.Setup(c => c.GetTorrentsAsync(It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>())).ReturnsAsync([torrent]);
        client.Setup(c => c.AddTagsAsync(It.IsAny<List<string>>(), It.IsAny<List<string>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => { torrent.Tags = "qBitrr-free_space_paused"; return true; });
        client.Setup(c => c.RemoveTagsAsync(It.IsAny<List<string>>(), It.IsAny<List<string>>(), It.IsAny<CancellationToken>())).ReturnsAsync(true);
        client.Setup(c => c.PauseTorrentAsync("abc", It.IsAny<CancellationToken>())).ReturnsAsync(() => { torrent.State = "pausedDL"; return true; });
        client.Setup(c => c.ResumeTorrentAsync("abc", It.IsAny<CancellationToken>())).ReturnsAsync(true);
        var registry = new Mock<ITorrentClientRegistry>();
        registry.Setup(r => r.GetAllClients()).Returns(new Dictionary<string, ITorrentClient> { ["named"] = client.Object });
        var config = new TorrentarrConfig(); config.Settings.Tagless = globalTagless;
        using var service = new ProcessOrchestratorService(NullLogger<ProcessOrchestratorService>.Instance,
            config, registry.Object, provider.GetRequiredService<IServiceScopeFactory>(), new ProcessStateManager(), Mock.Of<ISafeDeletionService>());
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        typeof(ProcessOrchestratorService).GetField("_freeSpaceFolder", flags)!.SetValue(service, Path.GetTempPath());
        typeof(ProcessOrchestratorService).GetField("_minFreeSpaceBytes", flags)!.SetValue(service, long.MaxValue);
        var process = typeof(ProcessOrchestratorService).GetMethod("ProcessFreeSpaceManagerAsync", flags)!;
        await (Task)process.Invoke(service, [new HashSet<string> { "movies" }, CancellationToken.None])!;
        typeof(ProcessOrchestratorService).GetField("_minFreeSpaceBytes", flags)!.SetValue(service, 1L);
        await (Task)process.Invoke(service, [new HashSet<string> { "movies" }, CancellationToken.None])!;
        client.Verify(c => c.ResumeTorrentAsync("abc", It.IsAny<CancellationToken>()), Times.Once);
        if (!tags || globalTagless)
        {
            var marker = await db.TorrentLibrary.AsNoTracking().SingleAsync();
            marker.QbitInstance.Should().Be("named"); marker.FreeSpacePaused.Should().BeFalse();
            client.Verify(c => c.AddTagsAsync(It.IsAny<List<string>>(), It.IsAny<List<string>>(), It.IsAny<CancellationToken>()), Times.Never);
            client.Verify(c => c.RemoveTagsAsync(It.IsAny<List<string>>(), It.IsAny<List<string>>(), It.IsAny<CancellationToken>()), Times.Never);
        }
    }

    [Fact]
    public async Task FreeSpace_IncompleteInventory_NeverResumesHealthyClient()
    {
        var healthy = new Mock<ITorrentClient>(); var failed = new Mock<ITorrentClient>();
        foreach (var client in new[] { healthy, failed }) client.SetupGet(c => c.Capabilities).Returns(TorrentClientCapabilities.QBittorrent);
        healthy.Setup(c => c.GetTorrentsAsync(It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([new TorrentInfo { Hash = "abc", Category = "movies", State = "pausedDL", Tags = "qBitrr-free_space_paused", AmountLeft = 1 }]);
        failed.Setup(c => c.GetTorrentsAsync(It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>())).ThrowsAsync(new HttpRequestException());
        var registry = new Mock<ITorrentClientRegistry>();
        registry.Setup(r => r.GetAllClients()).Returns(new Dictionary<string, ITorrentClient> { ["healthy"] = healthy.Object, ["failed"] = failed.Object });
        using var service = new ProcessOrchestratorService(NullLogger<ProcessOrchestratorService>.Instance,
            new TorrentarrConfig(), registry.Object, Mock.Of<IServiceScopeFactory>(), new ProcessStateManager(), Mock.Of<ISafeDeletionService>());
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        typeof(ProcessOrchestratorService).GetField("_freeSpaceFolder", flags)!.SetValue(service, Path.GetTempPath());
        typeof(ProcessOrchestratorService).GetField("_minFreeSpaceBytes", flags)!.SetValue(service, 1L);
        await (Task)typeof(ProcessOrchestratorService).GetMethod("ProcessFreeSpaceManagerAsync", flags)!
            .Invoke(service, [new HashSet<string> { "movies" }, CancellationToken.None])!;
        healthy.Verify(c => c.ResumeTorrentAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FreeSpace_MarkerCleanupFailureRestoresPauseAndRetries(bool tags)
    {
        using var db = new TorrentarrDbContext(new DbContextOptionsBuilder<TorrentarrDbContext>().UseSqlite("Data Source=:memory:").Options);
        await db.Database.OpenConnectionAsync(); await db.Database.EnsureCreatedAsync();
        db.TorrentLibrary.Add(new() { Hash = "abc", QbitInstance = "named", FreeSpacePaused = true });
        await db.SaveChangesAsync();
        if (!tags) await db.Database.ExecuteSqlRawAsync("CREATE TRIGGER reject_clear BEFORE UPDATE ON torrentlibrary WHEN NEW.freespacepaused=0 BEGIN SELECT RAISE(ABORT, 'rejected'); END;");
        using var provider = new ServiceCollection().AddSingleton(db).BuildServiceProvider();
        var torrent = new TorrentInfo { Hash = "abc", Category = "movies", State = "pausedDL", AmountLeft = 1, Tags = "qBitrr-free_space_paused" };
        var client = new Mock<ITorrentClient>();
        client.SetupGet(c => c.Capabilities).Returns(TorrentClientCapabilities.QBittorrent with { Tags = tags });
        client.Setup(c => c.GetTorrentsAsync(It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>())).ReturnsAsync([torrent]);
        var rejectClear = true;
        client.Setup(c => c.RemoveTagsAsync(It.IsAny<List<string>>(), It.IsAny<List<string>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => { if (rejectClear) return false; torrent.Tags = ""; return true; });
        client.Setup(c => c.ResumeTorrentAsync("abc", It.IsAny<CancellationToken>())).ReturnsAsync(() => { torrent.State = "downloading"; return true; });
        client.Setup(c => c.PauseTorrentAsync("abc", It.IsAny<CancellationToken>())).ReturnsAsync(() => { torrent.State = "pausedDL"; return true; });
        var registry = new Mock<ITorrentClientRegistry>();
        registry.Setup(r => r.GetAllClients()).Returns(new Dictionary<string, ITorrentClient> { ["named"] = client.Object });
        using var service = new ProcessOrchestratorService(NullLogger<ProcessOrchestratorService>.Instance,
            new TorrentarrConfig(), registry.Object, provider.GetRequiredService<IServiceScopeFactory>(), new ProcessStateManager(), Mock.Of<ISafeDeletionService>());
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        typeof(ProcessOrchestratorService).GetField("_freeSpaceFolder", flags)!.SetValue(service, Path.GetTempPath());
        typeof(ProcessOrchestratorService).GetField("_minFreeSpaceBytes", flags)!.SetValue(service, 1L);
        var process = typeof(ProcessOrchestratorService).GetMethod("ProcessFreeSpaceManagerAsync", flags)!;
        await (Task)process.Invoke(service, [new HashSet<string> { "movies" }, CancellationToken.None])!;
        torrent.State.Should().Be("pausedDL");
        client.Verify(c => c.PauseTorrentAsync("abc", It.Is<CancellationToken>(ct => !ct.CanBeCanceled)), Times.Once);
        rejectClear = false;
        if (!tags) await db.Database.ExecuteSqlRawAsync("DROP TRIGGER reject_clear");
        await (Task)process.Invoke(service, [new HashSet<string> { "movies" }, CancellationToken.None])!;
        if (!tags) (await db.TorrentLibrary.AsNoTracking().SingleAsync()).FreeSpacePaused.Should().BeFalse();
        else torrent.Tags.Should().BeEmpty();
        torrent.State = "pausedDL";
        await (Task)process.Invoke(service, [new HashSet<string> { "movies" }, CancellationToken.None])!;
        typeof(ProcessOrchestratorService).GetField("_minFreeSpaceBytes", flags)!.SetValue(service, long.MaxValue);
        await (Task)process.Invoke(service, [new HashSet<string> { "movies" }, CancellationToken.None])!;
        typeof(ProcessOrchestratorService).GetField("_minFreeSpaceBytes", flags)!.SetValue(service, 1L);
        await (Task)process.Invoke(service, [new HashSet<string> { "movies" }, CancellationToken.None])!;
        client.Verify(c => c.AddTagsAsync(It.IsAny<List<string>>(), It.IsAny<List<string>>(), It.IsAny<CancellationToken>()), Times.Never);
        client.Verify(c => c.ResumeTorrentAsync("abc", It.IsAny<CancellationToken>()), Times.Exactly(2));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FreeSpace_RejectedPauseRemovesMarkerBeforeLaterManualPause(bool tags)
    {
        using var db = new TorrentarrDbContext(new DbContextOptionsBuilder<TorrentarrDbContext>().UseSqlite("Data Source=:memory:").Options);
        await db.Database.OpenConnectionAsync(); await db.Database.EnsureCreatedAsync();
        using var provider = new ServiceCollection().AddSingleton(db).BuildServiceProvider();
        var torrent = new TorrentInfo { Hash = "abc", Category = "movies", State = "downloading", AmountLeft = 1 };
        var client = new Mock<ITorrentClient>();
        client.SetupGet(c => c.Capabilities).Returns(TorrentClientCapabilities.QBittorrent with { Tags = tags });
        client.Setup(c => c.GetTorrentsAsync(It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>())).ReturnsAsync([torrent]);
        client.Setup(c => c.AddTagsAsync(It.IsAny<List<string>>(), It.IsAny<List<string>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => { torrent.Tags = "qBitrr-free_space_paused"; return true; });
        client.Setup(c => c.RemoveTagsAsync(It.IsAny<List<string>>(), It.IsAny<List<string>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => { torrent.Tags = ""; return true; });
        client.Setup(c => c.PauseTorrentAsync("abc", It.IsAny<CancellationToken>())).ReturnsAsync(false);
        var registry = new Mock<ITorrentClientRegistry>();
        registry.Setup(r => r.GetAllClients()).Returns(new Dictionary<string, ITorrentClient> { ["named"] = client.Object });
        using var service = new ProcessOrchestratorService(NullLogger<ProcessOrchestratorService>.Instance,
            new TorrentarrConfig(), registry.Object, provider.GetRequiredService<IServiceScopeFactory>(), new ProcessStateManager(), Mock.Of<ISafeDeletionService>());
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        typeof(ProcessOrchestratorService).GetField("_freeSpaceFolder", flags)!.SetValue(service, Path.GetTempPath());
        typeof(ProcessOrchestratorService).GetField("_minFreeSpaceBytes", flags)!.SetValue(service, long.MaxValue);
        var process = typeof(ProcessOrchestratorService).GetMethod("ProcessFreeSpaceManagerAsync", flags)!;
        await (Task)process.Invoke(service, [new HashSet<string> { "movies" }, CancellationToken.None])!;
        if (!tags) (await db.TorrentLibrary.AsNoTracking().SingleAsync()).FreeSpacePaused.Should().BeFalse();
        else torrent.Tags.Should().BeEmpty();
        torrent.State = "pausedDL";
        typeof(ProcessOrchestratorService).GetField("_minFreeSpaceBytes", flags)!.SetValue(service, 1L);
        await (Task)process.Invoke(service, [new HashSet<string> { "movies" }, CancellationToken.None])!;
        client.Verify(c => c.ResumeTorrentAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

}

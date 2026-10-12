using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Torrentarr.Core.Configuration;
using Torrentarr.Core.Interfaces;
using Torrentarr.Core.Models;
using Torrentarr.Infrastructure.Database;
using Torrentarr.Infrastructure.Services;
using Xunit;

namespace Torrentarr.Infrastructure.Tests.Services;

public class WorkerSetupTests
{
    [Fact]
    public void ReleaseSourceWorker_UsesReleaseBuildAndPreservesHostWorkingDirectory()
    {
        if (!string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("TORRENTARR_WORKER_PATH"))) return;
        var root = Path.Combine(Path.GetTempPath(), $"torrentarr-worker-resolution-{Guid.NewGuid():N}");
        var hostDirectory = Path.Combine(root, "src", "Torrentarr.Host", "bin", "Release", "net10.0");
        var workerDll = Path.Combine(root, "src", "Torrentarr.Workers", "bin", "Release", "net10.0", "Torrentarr.Workers.dll");
        Directory.CreateDirectory(hostDirectory);
        Directory.CreateDirectory(Path.GetDirectoryName(workerDll)!);
        File.WriteAllText(workerDll, "test");
        File.WriteAllText(Path.Combine(hostDirectory, OperatingSystem.IsWindows() ? "Torrentarr.Workers.exe" : "Torrentarr.Workers"), "orphan apphost");
        File.WriteAllText(Path.Combine(hostDirectory, "Torrentarr.Workers.runtimeconfig.json"), "{}");
        try
        {
            var info = WorkerProcessSupervisor.ResolveWorkerStartInfo("Radarr", hostDirectory);
            info.FileName.Should().Be("dotnet");
            info.ArgumentList[0].Should().Be(workerDll);
            info.WorkingDirectory.Should().Be(Directory.GetCurrentDirectory());
        }
        finally { Directory.Delete(root, true); }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Setup_PropagatesRejectedCategoryAndTagCreation(bool accepted)
    {
        var client = new Mock<ITorrentClient>();
        client.Setup(x => x.GetCategoriesAsync(It.IsAny<CancellationToken>())).ReturnsAsync(new Dictionary<string, TorrentClientCategory>());
        client.Setup(x => x.CreateCategoryAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync(accepted);
        client.Setup(x => x.GetTagsAsync(It.IsAny<CancellationToken>())).ReturnsAsync(new List<string>());
        client.Setup(x => x.CreateTagsAsync(It.IsAny<List<string>>(), It.IsAny<CancellationToken>())).ReturnsAsync(accepted);
        var registry = new Mock<ITorrentClientRegistry>();
        registry.Setup(x => x.GetAllClients()).Returns(new Dictionary<string, ITorrentClient> { ["qBit"] = client.Object });
        var config = new TorrentarrConfig();
        config.QBitInstances["qBit"] = new QBitConfig { Trackers = new List<TrackerConfig> { new() { AddTags = new List<string> { "tracker-tag" } } } };
        var categories = new QBitCategoryEnsureService(NullLogger<QBitCategoryEnsureService>.Instance, config, registry.Object);
        (await categories.EnsureCategoryOnAllInstancesAsync("radarr")).Should().Be(accepted);
        using var db = new TorrentarrDbContext(new DbContextOptionsBuilder<TorrentarrDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        var seeding = new SeedingService(NullLogger<SeedingService>.Instance, db, config, registry.Object);
        (await seeding.EnsureAllTrackerTagsExistAsync()).Should().Be(accepted);
    }
}

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

public sealed class SharedStorageTests : IDisposable
{
    private readonly TorrentarrConfig _config = new();
    private readonly Mock<ITorrentClientRegistry> _registry = new();
    private readonly Dictionary<string, ITorrentClient> _clients = new();
    private readonly TorrentarrDbContext _db;
    private readonly Dictionary<string, FreeSpaceService.StorageSpace> _disks = new();
    private string Root(string name) => Path.Combine(Path.GetTempPath(), "storage-fixture", name);

    public SharedStorageTests()
    {
        _registry.Setup(r => r.GetAllClients()).Returns(_clients);
        _db = new(new DbContextOptionsBuilder<TorrentarrDbContext>().UseSqlite("Data Source=:memory:").Options);
        _db.Database.OpenConnection(); _db.Database.EnsureCreated();
    }

    private Mock<ITorrentClient> Client(string id, string disk, long remaining, string category = "movies", string state = "downloading", long reserve = 10)
    {
        var root = Root(id);
        _config.TorrentClients[id] = new() { Host = "fixture", FreeSpace = [new() { Path = root, Enabled = true, Limit = reserve.ToString() }] };
        _disks[root] = new(disk, 1000, 100);
        var client = new Mock<ITorrentClient>();
        client.SetupGet(c => c.Capabilities).Returns(TorrentClientCapabilities.QBittorrent);
        client.Setup(c => c.GetTorrentsAsync(It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([new() { Hash = id, SavePath = root, Category = category, State = state, AmountLeft = remaining,
                Tags = state == "pausedDL" ? "qBitrr-free_space_paused" : "", AddedOn = _clients.Count }]);
        client.Setup(c => c.GetTagsAsync(It.IsAny<CancellationToken>())).ReturnsAsync(["qBitrr-free_space_paused"]);
        client.Setup(c => c.AddTagsAsync(It.IsAny<List<string>>(), It.IsAny<List<string>>(), It.IsAny<CancellationToken>())).ReturnsAsync(true);
        client.Setup(c => c.RemoveTagsAsync(It.IsAny<List<string>>(), It.IsAny<List<string>>(), It.IsAny<CancellationToken>())).ReturnsAsync(true);
        client.Setup(c => c.PauseTorrentAsync(It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync(true);
        client.Setup(c => c.ResumeTorrentAsync(It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync(true);
        _clients[id] = client.Object;
        return client;
    }
    private sealed class FixtureService(TorrentarrConfig config, ITorrentClientRegistry registry, TorrentarrDbContext db,
        Dictionary<string, FreeSpaceService.StorageSpace> disks) : FreeSpaceService(NullLogger<FreeSpaceService>.Instance, config, registry, db)
    {
        protected override string IdentifyStorage(string path) => disks.TryGetValue(path, out var space) ? space.Identity : throw new IOException("offline");
        public int Samples { get; private set; }
        protected override StorageSpace ReadSpace(string path)
        {
            Samples++;
            return disks.TryGetValue(path, out var space) ? space : throw new IOException("offline");
        }
    }
    private Task<Torrentarr.Core.Services.FreeSpacePassResult> Process() => new FixtureService(_config, _registry.Object, _db, _disks)
        .ProcessAsync(new HashSet<string> { "movies" });

    [Theory]
    [InlineData(40, 0)]
    [InlineData(60, 1)]
    public async Task TwoClientsShareOneBudget_WithoutDoubleReservation(long bytes, int pauses)
    {
        var first = Client("one", "shared", bytes);
        var second = Client("two", "shared", bytes);
        var service = new FixtureService(_config, _registry.Object, _db, _disks);
        (await service.ProcessAsync(new HashSet<string> { "movies" })).Paused.Should().Be(pauses);
        service.Samples.Should().Be(1);
        first.Verify(c => c.PauseTorrentAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        second.Verify(c => c.PauseTorrentAsync("two", It.IsAny<CancellationToken>()), Times.Exactly(pauses));
        foreach (var client in new[] { first, second })
            client.Verify(c => c.GetTorrentsAsync(It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task SharedStorageUsesLargestReserve()
    {
        Client("one", "shared", 45, reserve: 10);
        var second = Client("two", "shared", 40, reserve: 20);
        (await Process()).Paused.Should().Be(1);
        second.Verify(c => c.PauseTorrentAsync("two", It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task IndependentStorageHasIndependentBudgets()
    {
        Client("one", "disk-a", 60);
        Client("two", "disk-b", 60);
        (await Process()).Paused.Should().Be(0);
    }

    [Fact]
    public async Task UnmanagedDownloadsConsumeSharedSpaceButAreNotPaused()
    {
        var managed = Client("one", "shared", 50);
        var unmanaged = Client("two", "shared", 50, category: "other");
        (await Process()).Paused.Should().Be(1);
        managed.Verify(c => c.PauseTorrentAsync("one", It.IsAny<CancellationToken>()), Times.Once);
        unmanaged.Verify(c => c.PauseTorrentAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task FailedInventoryWithUnknownDownloadLocationsWithholdsAllResumes()
    {
        var failed = Client("one", "disk-a", 1);
        failed.Setup(c => c.GetTorrentsAsync(It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>())).ThrowsAsync(new IOException());
        var same = Client("two", "disk-a", 1, state: "pausedDL");
        var independent = Client("three", "disk-b", 1, state: "pausedDL");
        (await Process()).Resumed.Should().Be(0);
        same.Verify(c => c.ResumeTorrentAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        independent.Verify(c => c.ResumeTorrentAsync("three", It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task UnavailablePathPausesAffectedDownload_HealthyDiskContinues()
    {
        var unavailable = Client("one", "disk-a", 1);
        var healthy = Client("two", "disk-b", 1, state: "pausedDL");
        _disks.Remove(Root("one"));
        var result = await Process();
        result.Paused.Should().Be(1); result.Resumed.Should().Be(1);
        unavailable.Verify(c => c.PauseTorrentAsync("one", It.IsAny<CancellationToken>()), Times.Once);
        healthy.Verify(c => c.ResumeTorrentAsync("two", It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task MappingUsesClientPath_AndLongestMonitorPrefix()
    {
        var client = Client("one", "disk-a", 1);
        _config.TorrentClients["one"].Maintenance.PathMappings = [new() { ClientPath = "/remote", LocalPath = Root("one") }];
        var nested = Path.Combine(Root("one"), "nested");
        _config.TorrentClients["one"].FreeSpace.Add(new() { Path = nested, Enabled = true, Limit = "10" });
        client.Setup(c => c.GetTorrentsAsync(It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([new() { Hash = "nested", SavePath = "/remote/nested", Category = "movies", State = "downloading", AmountLeft = 1 }]);
        (await Process()).Paused.Should().Be(1); // The nested monitor is unavailable, despite its healthy parent.
    }

    [Fact]
    public async Task NestedDifferentFilesystemDoesNotUseParentMonitorsBudget()
    {
        var client = Client("one", "disk-a", 1000);
        var nested = Path.Combine(Root("one"), "different-mount");
        _disks[nested] = new("disk-b", 1000, 100);
        client.Setup(c => c.GetTorrentsAsync(It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([new() { Hash = "other-disk", SavePath = nested, Category = "movies", State = "downloading", AmountLeft = 1000 }]);
        (await Process()).Paused.Should().Be(0);
        client.Verify(c => c.PauseTorrentAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task UnmatchedAndDisabledPathsAreUntouched()
    {
        var client = Client("one", "disk-a", 1000);
        client.Setup(c => c.GetTorrentsAsync(It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([new() { Hash = "other", SavePath = Root("one") + "-other", Category = "movies", State = "downloading", AmountLeft = 1000 }]);
        (await Process()).Paused.Should().Be(0);
        _config.TorrentClients["one"].FreeSpace[0].Enabled = false;
        (await Process()).Paused.Should().Be(0);
        client.Verify(c => c.PauseTorrentAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task RealFilesystemProbeGroupsPathsOnSameStorage()
    {
        _config.TorrentClients["local"] = new() { FreeSpace = [new() { Path = Path.GetTempPath(), Enabled = true, Limit = "1" }] };
        var stats = await new FreeSpaceService(NullLogger<FreeSpaceService>.Instance, _config, _registry.Object, _db).GetFreeSpaceStatsAsync();
        stats.Single().Error.Should().BeNull();
        stats.Single().TotalBytes.Should().BeGreaterThan(0);
    }
    public void Dispose() => _db.Dispose();
}

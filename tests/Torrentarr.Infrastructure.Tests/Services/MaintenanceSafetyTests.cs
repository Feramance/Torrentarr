using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Newtonsoft.Json;
using Torrentarr.Core.Configuration;
using Torrentarr.Core.Interfaces;
using Torrentarr.Core.Models;
using Torrentarr.Core.Services;
using Torrentarr.Infrastructure.Database;
using Torrentarr.Infrastructure.Services;
using Xunit;

namespace Torrentarr.Infrastructure.Tests.Services;

[Collection(nameof(ConfigReloaderTests))]
public sealed class MaintenanceSafetyTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "maintenance-test-" + Guid.NewGuid().ToString("N"));
    private readonly TorrentarrConfig _config = new();
    private readonly string? _previousDataDirectory;
    private readonly Mock<ITorrentClientRegistry> _registry = new();
    private readonly Mock<ITorrentClient> _client = new();
    private readonly List<TorrentInfo> _torrents = [];
    private readonly Dictionary<string, List<TorrentFileRecord>> _files = new();
    private readonly TorrentInventoryService _inventory;
    private readonly SafeDeletionService _deletion;
    private readonly SeedingService _seeding;
    private readonly ServiceProvider _services;
    private readonly TorrentarrDbContext _db;
    private readonly MaintenanceCoordinator _maintenance;
    private readonly List<MaintenanceEvent> _events = [];
    private TorrentClientInstanceConfig ClientConfig => _config.TorrentClients["one"];

    public MaintenanceSafetyTests()
    {
        Directory.CreateDirectory(_root);
        _previousDataDirectory = Environment.GetEnvironmentVariable("TORRENTARR_OVERRIDES_DATA_PATH");
        Environment.SetEnvironmentVariable("TORRENTARR_OVERRIDES_DATA_PATH", _root);
        _config.TorrentClients["one"] = new()
        {
            Host = "fixture",
            ManagedCategories = ["movies"],
            Maintenance = new()
            {
                Enabled = true,
                Scope = "all",
                MinimumTorrentAgeMinutes = 0,
                PathMappings = [new() { ClientPath = "/downloads", LocalPath = _root }],
                RecycleBin = new() { Enabled = true, Path = Path.Combine(_root, ".recycle") }
            }
        };
        _client.SetupGet(c => c.Identity).Returns(new TorrentClientIdentity("one", "fake"));
        _client.SetupGet(c => c.Capabilities).Returns(TorrentClientCapabilities.QBittorrent);
        _client.Setup(c => c.GetTorrentsAsync(It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => JsonConvert.DeserializeObject<List<TorrentInfo>>(JsonConvert.SerializeObject(_torrents))!);
        _client.Setup(c => c.GetTorrentFilesAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string hash, CancellationToken _) => _files.GetValueOrDefault(hash) ?? []);
        _client.Setup(c => c.GetTorrentTrackersAsync(It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync([]);
        _client.Setup(c => c.DeleteTorrentsAsync(It.IsAny<List<string>>(), It.IsAny<bool>(), It.IsAny<CancellationToken>())).ReturnsAsync(true);
        _client.Setup(c => c.ResumeTorrentsAsync(It.IsAny<List<string>>(), It.IsAny<CancellationToken>())).ReturnsAsync(true);
        _registry.Setup(r => r.GetClient("one")).Returns(_client.Object);
        _registry.Setup(r => r.GetAllClients()).Returns(new Dictionary<string, ITorrentClient> { ["one"] = _client.Object });
        var paths = new PathMappingService();
        _inventory = new(_registry.Object, _config, paths, NullLogger<TorrentInventoryService>.Instance);
        _deletion = new(_registry.Object, _inventory, paths, _config, NullLogger<SafeDeletionService>.Instance);
        _db = new(new DbContextOptionsBuilder<TorrentarrDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        _seeding = new(NullLogger<SeedingService>.Instance, _db, _config, _registry.Object);
        _services = new ServiceCollection().AddScoped<ISeedingService>(_ => _seeding).BuildServiceProvider();
        var notifications = new Mock<IMaintenanceNotificationService>();
        notifications.Setup(n => n.Publish(It.IsAny<MaintenanceEvent>())).Callback<MaintenanceEvent>(_events.Add);
        _maintenance = new(_config, _registry.Object, _inventory, paths, Mock.Of<IHardlinkInspector>(), _deletion,
            notifications.Object, NullLogger<MaintenanceCoordinator>.Instance, _services.GetRequiredService<IServiceScopeFactory>());
    }

    private TorrentInfo AddTorrent(string hash = "abc", string filename = "movie.mkv")
    {
        var torrent = new TorrentInfo
        {
            Hash = hash,
            Name = filename,
            Category = "movies",
            SavePath = "/downloads",
            ContentPath = "/downloads/" + filename,
            Size = 4,
            Progress = 1,
            AddedOn = 1,
            SeedingTime = 3600,
            Ratio = 2,
            CompletePeers = 5,
            State = "pausedUP",
            ClientInstanceId = "one"
        };
        _torrents.Add(torrent);
        _files[hash] = [new() { Name = filename, Size = 4 }];
        File.WriteAllText(Path.Combine(_root, filename), "data");
        return torrent;
    }

    private async Task<MaintenancePlan> Preview(MaintenanceOperation operation)
        => await _maintenance.PreviewAsync(new() { ClientInstanceIds = ["one"], Operations = [operation] });

    private async Task<MaintenanceRunSummary> Apply(MaintenancePlan plan)
    {
        await _maintenance.ArmAsync(new() { PlanId = plan.PlanId });
        return await _maintenance.ApplyAsync(plan.PlanId);
    }

    [Fact]
    public async Task InventoryFailure_BlocksOrphanPreview()
    {
        ClientConfig.Maintenance.Orphans.Enabled = true;
        _client.Setup(c => c.GetTorrentsAsync(It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpRequestException("unavailable"));
        var plan = await Preview(MaintenanceOperation.OrphanScan);
        plan.CriticalErrors.Should().NotBeEmpty();
    }

    [Fact]
    public async Task FileEnumerationFailure_IsIncomplete()
    {
        AddTorrent();
        _client.Setup(c => c.GetTorrentFilesAsync("abc", It.IsAny<CancellationToken>())).ThrowsAsync(new HttpRequestException());
        (await _inventory.BuildAsync()).IsComplete.Should().BeFalse();
    }

    [Fact]
    public async Task EmptySuccessfulClient_IsComplete()
        => (await _inventory.BuildAsync()).IsComplete.Should().BeTrue();

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RequiredClientUnavailable_BlocksOwnership(bool disabled)
    {
        _config.TorrentClients["two"] = new() { Host = "other", Disabled = disabled };
        (await _inventory.BuildAsync()).IsComplete.Should().Be(disabled);
    }

    [Fact]
    public async Task MissingFileCapability_BlocksOwnership()
    {
        _client.SetupGet(c => c.Capabilities).Returns(TorrentClientCapabilities.None);
        (await _inventory.BuildAsync()).IsComplete.Should().BeFalse();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OrphanApply_RechecksFailuresAndNewOwners(bool newOwner)
    {
        ClientConfig.Maintenance.Orphans.Enabled = true;
        ClientConfig.Maintenance.Orphans.MinimumAgeMinutes = 0;
        var path = Path.Combine(_root, "movie.mkv");
        File.WriteAllText(path, "data");
        var plan = await Preview(MaintenanceOperation.OrphanScan);
        plan.CandidateCount.Should().Be(1);
        if (newOwner) AddTorrent();
        else _client.Setup(c => c.GetTorrentsAsync(It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>())).ThrowsAsync(new HttpRequestException());
        var run = await Apply(plan);
        run.Skipped.Should().Be(1);
        File.Exists(path).Should().BeTrue();
    }

    [Fact]
    public void CategoryHnr_WithUnrelatedTracker_IsStillProtected()
    {
        var torrent = AddTorrent(); torrent.Ratio = 0.2;
        ClientConfig.CategorySeeding.HitAndRunMode = "and";
        ClientConfig.CategorySeeding.MinSeedRatio = 1;
        ClientConfig.Trackers = [new() { Uri = "https://unrelated.example/announce" }];
        _seeding.HnrAllowsDelete(torrent, []).Should().BeFalse();
    }

    [Theory]
    [InlineData("and", 1, 86400, 1, 10, false)]
    [InlineData("or", 1, 86400, 1, 10, true)]
    [InlineData("disabled", 0, 0, 1, 10, true)]
    [InlineData("and", 0, 0, 0.01, 10, true)]
    [InlineData("and", 0.5, 0, 0.5, 10, false)]
    [InlineData("and", 1, 86400, 1, 86401, true)]
    public void SharedHnrDecision_RespectsModesAndPartialThresholds(string mode, double ratio, long time, double progress, int seedTime, bool allowed)
    {
        var torrent = AddTorrent(); torrent.Ratio = ratio; torrent.SeedingTime = seedTime; torrent.Progress = progress;
        ClientConfig.CategorySeeding.HitAndRunMode = mode;
        ClientConfig.CategorySeeding.MinSeedRatio = 1;
        ClientConfig.CategorySeeding.MinSeedingTimeDays = (int)(time / 86400);
        _seeding.HnrAllowsDelete(torrent, []).Should().Be(allowed);
    }

    [Fact]
    public void TrackerUnsetValues_InheritCategoryAndBuffer()
    {
        var torrent = AddTorrent(); torrent.SeedingTime = 86400;
        ClientConfig.CategorySeeding.HitAndRunMode = "and";
        ClientConfig.CategorySeeding.MinSeedingTimeDays = 1;
        ClientConfig.CategorySeeding.TrackerUpdateBuffer = 60;
        ClientConfig.Trackers = [new() { Uri = "https://tracker.example/announce", Priority = 1 }];
        _seeding.HnrAllowsDelete(torrent, [new() { Url = "https://tracker.example/announce" }]).Should().BeFalse();
    }

    [Theory]
    [InlineData("seeds")]
    [InlineData("time")]
    [InlineData("category")]
    [InlineData("tags")]
    public async Task SharePolicyApply_RechecksSafety(string changed)
    {
        var torrent = AddTorrent();
        ClientConfig.Maintenance.SharePolicies = [new() { Name = "policy", MaximumRatio = 1, Action = "delete", MinimumSeedCount = 3, MinimumSeedingMinutes = 30, Categories = ["movies"], Tags = ["keep"] }];
        torrent.Tags = "keep";
        var plan = await Preview(MaintenanceOperation.SharePolicy);
        plan.CandidateCount.Should().Be(1);
        switch (changed)
        {
            case "seeds": torrent.CompletePeers = 1; break;
            case "time": torrent.SeedingTime = 10; break;
            case "category": torrent.Category = "other"; break;
            case "tags": torrent.Tags = ""; break;
        }
        (await Apply(plan)).Skipped.Should().Be(1);
        _client.Verify(c => c.DeleteTorrentsAsync(It.IsAny<List<string>>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task RepairApply_DoesNotResumeTorrentThatWasUnpaused()
    {
        var torrent = AddTorrent();
        ClientConfig.Maintenance.Repair.Enabled = true;
        var plan = await Preview(MaintenanceOperation.RepairPaused);
        torrent.State = "uploading";
        (await Apply(plan)).Skipped.Should().Be(1);
        _client.Verify(c => c.ResumeTorrentsAsync(It.IsAny<List<string>>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task ChangedHnrConfig_InvalidatesPreview()
    {
        AddTorrent();
        ClientConfig.Maintenance.SharePolicies = [new() { Name = "policy", MaximumRatio = 1, Action = "delete" }];
        var plan = await Preview(MaintenanceOperation.SharePolicy);
        ClientConfig.CategorySeeding.MinSeedRatio = 5;
        await FluentActions.Awaiting(() => _maintenance.ArmAsync(new() { PlanId = plan.PlanId })).Should().ThrowAsync<InvalidOperationException>();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Recycling_RollsBackRejectedOrCancelledRemoval(bool cancelled)
    {
        AddTorrent();
        if (cancelled) _client.Setup(c => c.DeleteTorrentsAsync(It.IsAny<List<string>>(), false, It.IsAny<CancellationToken>())).ThrowsAsync(new OperationCanceledException());
        else _client.Setup(c => c.DeleteTorrentsAsync(It.IsAny<List<string>>(), false, It.IsAny<CancellationToken>())).ReturnsAsync(false);
        var result = await _deletion.RemoveMaintenanceTorrentAsync("one", "abc", true, await _inventory.BuildAsync());
        result.Success.Should().BeFalse();
        result.ContentPreserved.Should().BeTrue();
        File.ReadAllText(Path.Combine(_root, "movie.mkv")).Should().Be("data");
        if (cancelled) result.Error.Should().Contain("uncertain");
    }

    [Fact]
    public async Task Rollback_DoesNotOverwriteRecreatedSource()
    {
        AddTorrent();
        _client.Setup(c => c.DeleteTorrentsAsync(It.IsAny<List<string>>(), false, It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => { File.WriteAllText(Path.Combine(_root, "movie.mkv"), "new"); return false; });
        var result = await _deletion.RemoveMaintenanceTorrentAsync("one", "abc", true, await _inventory.BuildAsync());
        result.Error.Should().Contain("refusing to overwrite");
        File.ReadAllText(Path.Combine(_root, "movie.mkv")).Should().Be("new");
        Directory.EnumerateFiles(Path.Combine(_root, ".recycle"), "*", SearchOption.AllDirectories).Should().ContainSingle();
    }

    [Fact]
    public async Task StrictMaintenance_DoesNotUseLegacyDeletionFallback()
    {
        AddTorrent();
        ClientConfig.Maintenance.PathMappings.Clear();
        ClientConfig.Maintenance.RecycleBin.Enabled = false;
        (await _deletion.RemoveMaintenanceTorrentAsync("one", "abc", true, await _inventory.BuildAsync())).Success.Should().BeFalse();
        _client.Verify(c => c.DeleteTorrentsAsync(It.IsAny<List<string>>(), true, It.IsAny<CancellationToken>()), Times.Never);
        (await _deletion.RemoveTorrentAsync("one", "abc", true)).Success.Should().BeTrue();
    }

    [Fact]
    public async Task SharedContent_RemovesOnlyTorrentRecord()
    {
        AddTorrent(); AddTorrent("other");
        (await _deletion.RemoveMaintenanceTorrentAsync("one", "abc", true, await _inventory.BuildAsync())).ContentPreserved.Should().BeTrue();
        _client.Verify(c => c.DeleteTorrentsAsync(It.IsAny<List<string>>(), false, It.IsAny<CancellationToken>()), Times.Once);
        File.Exists(Path.Combine(_root, "movie.mkv")).Should().BeTrue();
    }

    [Fact]
    public async Task InventoryRefresh_ReusesUnchangedFilesButRefreshesCandidateAndNewOwners()
    {
        AddTorrent();
        var inventory = await _inventory.BuildAsync();
        for (var i = 0; i < 100; i++) inventory = await _inventory.RefreshAsync(inventory);
        _client.Verify(c => c.GetTorrentFilesAsync("abc", It.IsAny<CancellationToken>()), Times.Once);
        inventory = await _inventory.RefreshAsync(inventory, "one", "abc");
        _client.Verify(c => c.GetTorrentFilesAsync("abc", It.IsAny<CancellationToken>()), Times.Exactly(2));
        AddTorrent("new", "new.mkv");
        inventory = await _inventory.RefreshAsync(inventory);
        inventory.LocalPathReferences.Should().ContainKey(Path.Combine(_root, "new.mkv"));
        _client.Verify(c => c.GetTorrentFilesAsync("new", It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ChangedLocation_RefreshesFiles()
    {
        var torrent = AddTorrent();
        var inventory = await _inventory.BuildAsync();
        torrent.SavePath = "/downloads/sub";
        inventory = await _inventory.RefreshAsync(inventory);
        inventory.LocalPathReferences.Should().ContainKey(Path.Combine(_root, "sub", "movie.mkv"));
        _client.Verify(c => c.GetTorrentFilesAsync("abc", It.IsAny<CancellationToken>()), Times.Exactly(2));
    }

    [Fact]
    public async Task LargeInventory_DoesNotEnumerateEveryFileForEachOrphan()
    {
        for (var i = 0; i < 1000; i++)
        {
            var hash = "hash-" + i;
            _torrents.Add(new() { Hash = hash, Name = hash, Size = 4, SavePath = "/downloads", ContentPath = "/downloads/" + hash });
            _files[hash] = [new() { Name = hash, Size = 4 }];
        }
        var inventory = await _inventory.BuildAsync();
        for (var i = 0; i < 100; i++) inventory = await _inventory.RefreshAsync(inventory);
        inventory.IsComplete.Should().BeTrue();
        _client.Verify(c => c.GetTorrentFilesAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Exactly(1000));
        _client.Verify(c => c.GetTorrentTrackersAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Exactly(1000));
        _client.Verify(c => c.GetTorrentsAsync(It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Exactly(101));
    }

    [Fact]
    public async Task HigherPriorityPolicyWithSameName_DoesNotReplacePreviewedAction()
    {
        var torrent = AddTorrent();
        ClientConfig.Maintenance.SharePolicies =
        [
            new() { Name = "same", MaximumRatio = 1, Action = "delete" },
            new() { Name = "same", MaximumRatio = 1, Action = "delete", Priority = 10, Tags = ["special"] }
        ];
        var plan = await Preview(MaintenanceOperation.SharePolicy);
        torrent.Tags = "special";
        (await Apply(plan)).Skipped.Should().Be(1);
        _client.Verify(c => c.DeleteTorrentsAsync(It.IsAny<List<string>>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task UnsupportedCategories_AreBlocked()
    {
        AddTorrent();
        _client.SetupGet(c => c.Capabilities).Returns(TorrentClientCapabilities.QBittorrent with { Categories = false });
        ClientConfig.Maintenance.CategoryMappings = [new() { ClientPath = "/downloads", Category = "other" }];
        var plan = await Preview(MaintenanceOperation.CategoryReconcile);
        plan.BlockedCount.Should().Be(1);
    }

    [Fact]
    public async Task MultipleMovedFiles_AreRestoredOnCancellation()
    {
        AddTorrent();
        _files["abc"].Add(new() { Name = "extra.mkv", Size = 4 });
        File.WriteAllText(Path.Combine(_root, "extra.mkv"), "more");
        _client.Setup(c => c.DeleteTorrentsAsync(It.IsAny<List<string>>(), false, It.IsAny<CancellationToken>())).ThrowsAsync(new OperationCanceledException());
        var result = await _deletion.RemoveMaintenanceTorrentAsync("one", "abc", true, await _inventory.BuildAsync());
        result.ContentPreserved.Should().BeTrue();
        File.ReadAllText(Path.Combine(_root, "movie.mkv")).Should().Be("data");
        File.ReadAllText(Path.Combine(_root, "extra.mkv")).Should().Be("more");
    }

    [Fact]
    public async Task ImportedTags_OnlyReachOwningClient()
    {
        var other = new Mock<ITorrentClient>();
        other.SetupGet(c => c.Capabilities).Returns(TorrentClientCapabilities.QBittorrent);
        other.Setup(c => c.CreateTagsAsync(It.IsAny<List<string>>(), It.IsAny<CancellationToken>())).ReturnsAsync(true);
        other.Setup(c => c.AddTagsAsync(It.IsAny<List<string>>(), It.IsAny<List<string>>(), It.IsAny<CancellationToken>())).ReturnsAsync(true);
        _registry.Setup(r => r.GetClient("two")).Returns(other.Object);
        var imports = new ArrImportService(NullLogger<ArrImportService>.Instance, _config, _registry.Object, _db);
        await imports.MarkAsImportedAsync("two", "abc", []);
        other.Verify(c => c.AddTagsAsync(It.Is<List<string>>(h => h.SequenceEqual(new[] { "abc" })), It.IsAny<List<string>>(), It.IsAny<CancellationToken>()), Times.Once);
        _client.Verify(c => c.AddTagsAsync(It.IsAny<List<string>>(), It.IsAny<List<string>>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task SamePlan_CannotBeAppliedTwiceWhileRemovalIsPending()
    {
        AddTorrent();
        ClientConfig.Maintenance.SharePolicies = [new() { Name = "policy", MaximumRatio = 1, Action = "remove" }];
        var plan = await Preview(MaintenanceOperation.SharePolicy);
        await _maintenance.ArmAsync(new() { PlanId = plan.PlanId });
        var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        _client.Setup(c => c.DeleteTorrentsAsync(It.IsAny<List<string>>(), false, It.IsAny<CancellationToken>())).Returns(completion.Task);
        var first = _maintenance.ApplyAsync(plan.PlanId);
        try
        {
            await FluentActions.Awaiting(() => _maintenance.ApplyAsync(plan.PlanId)).Should().ThrowAsync<InvalidOperationException>();
        }
        finally { completion.SetResult(true); await first; }
        _client.Verify(c => c.DeleteTorrentsAsync(It.IsAny<List<string>>(), false, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task MissingFileCapability_StillAllowsNonDestructiveInspection()
    {
        AddTorrent();
        _client.SetupGet(c => c.Capabilities).Returns(TorrentClientCapabilities.QBittorrent with { FileEnumeration = false });
        ClientConfig.Maintenance.CategoryMappings = [new() { ClientPath = "/downloads", Category = "other" }];
        var plan = await Preview(MaintenanceOperation.CategoryReconcile);
        plan.Warnings.Should().NotBeEmpty();
        plan.CandidateCount.Should().Be(1);
        _client.Verify(c => c.GetTorrentFilesAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    public void Dispose()
    {
        _maintenance.Dispose(); _services.Dispose(); _db.Dispose();
        Environment.SetEnvironmentVariable("TORRENTARR_OVERRIDES_DATA_PATH", _previousDataDirectory);
        Directory.Delete(_root, true);
    }
}

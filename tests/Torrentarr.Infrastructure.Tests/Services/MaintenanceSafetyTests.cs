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
    private readonly string _root = Path.Combine(TestPaths.TempPath, "maintenance-test-" + Guid.NewGuid().ToString("N"));
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
    private TorrentClientInstanceConfig ClientConfig => _config.TorrentClients["one"];

    [Fact]
    public async Task PreviewLogs_SanitizeUnknownClientNamesAndErrors()
    {
        var logger = new ActivityLogger<MaintenanceCoordinator>(Microsoft.Extensions.Logging.LogLevel.Debug);
        using var maintenance = new MaintenanceCoordinator(_config, _registry.Object, _inventory, new PathMappingService(),
            Mock.Of<IHardlinkInspector>(), _deletion, logger, _services.GetRequiredService<IServiceScopeFactory>());

        var plan = await maintenance.PreviewAsync(new() { ClientInstanceIds = ["unknown\r\nforged\u001b[31m\u2028 token=secret"] });

        plan.CriticalErrors.Should().ContainSingle();
        logger.Messages.Should().Contain(m => m.Contains("Maintenance preview started"));
        logger.Messages.Should().Contain(m => m.Contains("Maintenance preview stopped"));
        foreach (var message in logger.Messages)
        {
            message.Any(c => char.IsControl(c) || c is '\u2028' or '\u2029').Should().BeFalse();
            message.Should().NotContain("token=secret");
        }
    }

    [Theory]
    [InlineData("ready", false, true)]
    [InlineData("partial", false, true)]
    [InlineData("blocked", false, false)]
    [InlineData("ready", true, false)]
    [InlineData("disabled", false, false)]
    public void OrphanPrerequisiteIssue_ResolvesIndependentlyOnlyAfterSuccessfulCheck(string orphanState, bool stillObserved, bool resolved)
    {
        var preview = new MaintenancePlan
        {
            OperationResults = [
                new() { ClientInstanceId = "one", Operation = MaintenanceOperation.OrphanScan, State = orphanState },
                new() { ClientInstanceId = "one", Operation = MaintenanceOperation.HardlinkAudit, State = "blocked" }
            ]
        };
        if (stillObserved) preview.Diagnostics.Add(new() { ClientInstanceId = "one", Code = "orphan-prerequisite" });
        MaintenanceCoordinator.CanResolvePreviewIssue("maintenance.preview.orphan-prerequisite", "one", preview).Should().Be(resolved);
        preview.CriticalErrors.Add("Inspection unavailable");
        MaintenanceCoordinator.CanResolvePreviewIssue("maintenance.preview.orphan-prerequisite", "one", preview).Should().BeFalse();
    }

    public MaintenanceSafetyTests()
    {
        Directory.CreateDirectory(_root);
        _previousDataDirectory = Environment.GetEnvironmentVariable("TORRENTARR_OVERRIDES_DATA_PATH");
        Environment.SetEnvironmentVariable("TORRENTARR_OVERRIDES_DATA_PATH", _root);
        _config.TorrentClients["one"] = new()
        {
            Host = "fixture",
            CategorySeeding = new() { RemoveTorrent = 1, MaxUploadRatio = 1, StalledDelay = 0 },
            ManagedCategories = ["movies"],
            Maintenance = new()
            {
                Enabled = true,
                Orphans = new() { ScanRoots = [_root] },
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
        _db = new(new DbContextOptionsBuilder<TorrentarrDbContext>().UseSqlite("Data Source=:memory:").Options);
        _db.Database.OpenConnection(); _db.Database.EnsureCreated();
        _seeding = new(NullLogger<SeedingService>.Instance, _db, _config, _registry.Object);
        _services = new ServiceCollection().AddLogging().AddSingleton(_db).AddScoped<ISeedingService>(_ => _seeding).BuildServiceProvider();
        _deletion = new(_registry.Object, _inventory, paths, _config, NullLogger<SafeDeletionService>.Instance, _services.GetRequiredService<IServiceScopeFactory>());
        _maintenance = new(_config, _registry.Object, _inventory, paths, Mock.Of<IHardlinkInspector>(), _deletion,
            NullLogger<MaintenanceCoordinator>.Instance, _services.GetRequiredService<IServiceScopeFactory>());
    }


    [Theory]
    [InlineData("checked")]
    [InlineData("checking")]
    [InlineData("requested")]
    public void CompletedRecovery_LaterCheckingCannotReopenAttempt(string stage)
    {
        var torrent = AddTorrent(); torrent.State = "checkingUP";
        var now = DateTimeOffset.UtcNow;
        var completed = now.AddHours(-2);
        var key = MaintenancePersistence.Key("one", torrent.Hash, "recovery:ErrorRecovery");
        MaintenancePersistence.Write(_db, key, new RecoveryIncident
        {
            AddedOn = torrent.AddedOn,
            Stage = stage,
            Attempts = 1,
            RequestedAt = completed.AddMinutes(-2),
            CheckingAt = completed.AddMinutes(-1),
            CompletedAt = completed,
            LastObserved = now.AddMinutes(-1)
        });
        var cfg = ClientConfig.Maintenance.ErrorRecovery;
        MaintenanceRecovery.Observe(_db, torrent, "ErrorRecovery", false, cfg, now);
        var state = MaintenanceRecovery.Read(_db, torrent, "ErrorRecovery");
        state.Stage.Should().Be("checked");
        state.CompletedAt.Should().Be(completed);
        state.Attempts.Should().Be(1);
        state.HealthySince.Should().BeNull();
        torrent.State = "uploading";
        for (var minute = 1; minute <= cfg.HealthyResetMinutes + 1; minute++)
            MaintenanceRecovery.Observe(_db, torrent, "ErrorRecovery", false, cfg, now.AddMinutes(minute));
        MaintenanceRecovery.Read(_db, torrent, "ErrorRecovery").Stage.Should().Be("new");
    }

    [Theory]
    [InlineData("success", true)]
    [InlineData("draft", false)]
    [InlineData("failed", false)]
    [InlineData("skipped", false)]
    [InlineData("other-client", false)]
    [InlineData("cancelled", false)]
    public async Task PreviewConnectionIssues_ResolveOnlyFromSuccessfulClientListing(string observation, bool resolves)
    {
        foreach (var code in new[] { "client-unavailable", "torrent-list-failed" })
            _db.OperationalIssues.Add(new()
            {
                Code = "maintenance.preview." + code,
                ClientInstance = "one",
                SubjectType = "preview",
                SubjectId = "OrphanScan,ErrorRecovery"
            });
        _db.SaveChanges();
        var reporter = new Mock<IOperationalIssueReporter>();
        await using var services = new ServiceCollection().AddSingleton(_db).AddSingleton(reporter.Object).BuildServiceProvider();
        var preview = new MaintenancePlan
        {
            IsDraft = observation == "draft",
            Operations = [MaintenanceOperation.RepairPaused],
            OperationResults = [
                new() { ClientInstanceId = "one", Operation = MaintenanceOperation.RepairPaused, State = "blocked" },
                new() { ClientInstanceId = "one", Operation = MaintenanceOperation.ErrorRecovery, State = "disabled" }
            ]
        };
        preview.Diagnostics.Add(new() { ClientInstanceId = "one", Code = "missing-mapping" });
        var inspected = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (observation is not ("failed" or "skipped")) inspected.Add(observation == "other-client" ? "two" : "one");
        if (observation == "failed") preview.Diagnostics.Add(new() { ClientInstanceId = "one", Code = "torrent-list-failed" });
        using var cancellation = new CancellationTokenSource();
        if (observation == "cancelled") cancellation.Cancel();
        var observe = () => MaintenanceCoordinator.ObservePreviewIssuesAsync(preview, services, cancellation.Token, inspected);
        if (observation == "cancelled") await observe.Should().ThrowAsync<OperationCanceledException>();
        else await observe();
        reporter.Verify(r => r.ResolveAsync(It.Is<OperationalIssueReport>(i => i.ClientInstance == "one"
            && i.SubjectId == "OrphanScan,ErrorRecovery"), It.IsAny<CancellationToken>()), resolves ? Times.Exactly(2) : Times.Never());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ProtectionWarning_PersistsUntilSafeLimitsAreObserved(bool accepted)
    {
        var torrent = AddTorrent(); torrent.IsPrivate = true; torrent.State = "uploading"; torrent.Ratio = 0;
        torrent.RatioLimit = 1; torrent.SeedingTimeLimit = 60; torrent.InactiveSeedingTimeLimit = 30;
        ClientConfig.Trackers = [new() { Uri = "https://tracker.example", HitAndRunMode = "or", MinSeedRatio = 2 }];
        _client.Setup(c => c.GetTorrentTrackersAsync(torrent.Hash, It.IsAny<CancellationToken>()))
            .ReturnsAsync([new() { Url = "https://tracker.example/announce", Status = 2 }]);
        _client.Setup(c => c.SetShareLimitsAsync(torrent.Hash, -1, -1, -1, It.IsAny<CancellationToken>())).ReturnsAsync(accepted);
        using var automation = new TorrentAutomationService(_config, _registry.Object,
            _services.GetRequiredService<IServiceScopeFactory>(), NullLogger<TorrentAutomationService>.Instance);
        var reporter = new OperationalIssueReporter(_services.GetRequiredService<IServiceScopeFactory>(), NullLogger<OperationalIssueReporter>.Instance, _config);
        await using var services = new ServiceCollection().AddSingleton(_db).AddSingleton(automation).BuildServiceProvider();
        using var monitor = new OperationalIssueMonitor(services.GetRequiredService<IServiceScopeFactory>(), reporter,
            _config, NullLogger<OperationalIssueMonitor>.Instance);
        await Inspect(automation, default);
        await monitor.InspectAsync(default);
        var issue = await _db.OperationalIssues.SingleAsync(i => i.Code == "automation.protection");
        issue.ResolvedAt.Should().BeNull();
        torrent.RatioLimit = -1; torrent.SeedingTimeLimit = -1; torrent.InactiveSeedingTimeLimit = -1;
        AutomationField<System.Collections.Concurrent.ConcurrentDictionary<string, DateTimeOffset>>(automation, "_nextInspection").Clear();
        await Inspect(automation, default);
        await monitor.InspectAsync(default);
        await _db.Entry(issue).ReloadAsync();
        issue.ResolvedAt.Should().NotBeNull();
    }

    [Fact]
    public async Task EmptyTorrentListing_ResolvesOldPreviewConnectionWarnings()
    {
        _db.OperationalIssues.Add(new()
        {
            Code = "maintenance.preview.torrent-list-failed",
            ClientInstance = "one",
            SubjectType = "preview",
            SubjectId = "OrphanScan,ErrorRecovery"
        });
        _db.SaveChanges();
        var reporter = new Mock<IOperationalIssueReporter>();
        await using var services = new ServiceCollection().AddSingleton(_db).AddSingleton(reporter.Object)
            .AddScoped<ISeedingService>(_ => _seeding).BuildServiceProvider();
        using var maintenance = new MaintenanceCoordinator(_config, _registry.Object, _inventory, new PathMappingService(),
            Mock.Of<IHardlinkInspector>(), _deletion, NullLogger<MaintenanceCoordinator>.Instance,
            services.GetRequiredService<IServiceScopeFactory>());
        ClientConfig.Maintenance.ErrorRecovery.Enabled = true;
        await maintenance.PreviewAsync(new() { ClientInstanceIds = ["one"], Operations = [MaintenanceOperation.ErrorRecovery] });
        reporter.Verify(r => r.ResolveAsync(It.Is<OperationalIssueReport>(i => i.Code == "maintenance.preview.torrent-list-failed"),
            It.IsAny<CancellationToken>()), Times.Once());
    }

    [Fact]
    public async Task DeadTrackerMessages_NeverDeleteDirectlyEvenWhenLegacyRemovalIsEnabled()
    {
        var torrent = AddTorrent();
        _config.ArrInstances["Radarr-Movies"] = new() { Category = "movies", Torrent = new() { SeedingMode = new() { RemoveDeadTrackers = true, RemoveTrackerWithMessage = ["unregistered torrent"] } } };
        _client.Setup(c => c.GetTorrentTrackersAsync(torrent.Hash, It.IsAny<CancellationToken>()))
            .ReturnsAsync([new() { Url = "https://tracker.example/announce", Msg = "unregistered torrent" }]);
        await _seeding.ApplyTrackerActionsForTorrentAsync(torrent);
        _client.Verify(c => c.DeleteTorrentsAsync(It.IsAny<List<string>>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()), Times.Never);
    }



    [Fact]
    public void Recovery_RequiresConfirmedCompletionAndContinuousFailure()
    {
        var torrent = AddTorrent(); var now = DateTimeOffset.UtcNow;
        var cfg = ClientConfig.Maintenance.ErrorRecovery;
        var key = MaintenancePersistence.Key("one", torrent.Hash, "recovery:ErrorRecovery");
        MaintenancePersistence.Write(_db, key, new RecoveryIncident { Stage = "requested", RequestedAt = now.AddMinutes(-61), Attempts = 1 });
        torrent.State = "error";
        MaintenanceRecovery.Observe(_db, torrent, "ErrorRecovery", true, cfg, now);
        var state = MaintenanceRecovery.Read(_db, torrent, "ErrorRecovery");
        state.Stage.Should().Be("unconfirmed");
        MaintenanceRecovery.CanRecycle(state, cfg, now.AddDays(3)).Should().BeFalse();
        MaintenancePersistence.Write(_db, key, new RecoveryIncident { Stage = "checking", RequestedAt = now.AddMinutes(-2), CheckingAt = now.AddMinutes(-1), Attempts = 1 });
        MaintenanceRecovery.Observe(_db, torrent, "ErrorRecovery", true, cfg, now);
        state = MaintenanceRecovery.Read(_db, torrent, "ErrorRecovery");
        state.CompletedAt.Should().Be(now); state.Stage.Should().Be("recurring");
        for (var minute = 5; minute <= 1440; minute += 5)
            MaintenanceRecovery.Observe(_db, torrent, "ErrorRecovery", true, cfg, now.AddMinutes(minute));
        MaintenanceRecovery.CanRecycle(MaintenanceRecovery.Read(_db, torrent, "ErrorRecovery"), cfg, now.AddMinutes(1440)).Should().BeTrue();
        MaintenanceRecovery.Observe(_db, torrent, "ErrorRecovery", true, cfg, now.AddMinutes(1500));
        MaintenanceRecovery.CanRecycle(MaintenanceRecovery.Read(_db, torrent, "ErrorRecovery"), cfg, now.AddMinutes(1500)).Should().BeFalse();
    }

    [Fact]
    public void EveryApplicableTrackerCanBlockClearance()
    {
        var torrent = AddTorrent(); torrent.IsPrivate = true;
        ClientConfig.Trackers = [new() { Uri = "https://first.example", HitAndRunMode = "or", MinSeedRatio = 1, Priority = 100 },
            new() { Uri = "https://second.example", Protection = new() { Policy = "onlyencodes" }, Priority = 1 }];
        _seeding.GetProtectionDecisions(torrent, [new() { Url = "https://first.example/announce", Status = 2 }, new() { Url = "https://second.example/announce", Status = 2 }])
            .Should().Contain(d => d.State == "eligible").And.Contain(d => d.State == "protected");
    }

    [Fact]
    public void History_PreservesRetiredAndRemainingOperationIds()
    {
        File.WriteAllText(Path.Combine(_root, "maintenance-history.jsonl"), """
            {"runId":"historical","result":"success","operationResults":[{"clientInstanceId":"one","operation":8,"state":"ready"},{"clientInstanceId":"one","operation":9,"state":"ready"}]}
            """);
        using var coordinator = new MaintenanceCoordinator(_config, _registry.Object, _inventory, new PathMappingService(),
            Mock.Of<IHardlinkInspector>(), _deletion, NullLogger<MaintenanceCoordinator>.Instance, _services.GetRequiredService<IServiceScopeFactory>());
        var history = coordinator.GetHistory().Single();
        history.RunId.Should().Be("historical");
        ((int)history.OperationResults[0].Operation).Should().Be(8);
        Enum.IsDefined(history.OperationResults[0].Operation).Should().BeFalse();
        history.OperationResults[1].Operation.Should().Be(MaintenanceOperation.RecycleRetention);
        ((int)MaintenanceActionKind.RecycleContent).Should().Be(9);
        ((int)MaintenanceActionKind.DeleteExpiredRecycleItem).Should().Be(13);
    }

    [Fact]
    public async Task RemovedOperation_CannotBeRun()
    {
        await FluentActions.Awaiting(() => _maintenance.RunAsync(operations: [(MaintenanceOperation)8]))
            .Should().ThrowAsync<InvalidOperationException>();
        _registry.Verify(r => r.GetClient(It.IsAny<string>()), Times.Never);
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
            IsPrivate = false,
            AddedOn = 1,
            SeedingTime = 3600,
            LastActivity = 1,
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
        return await _maintenance.ApplyOnceAsync(plan.PlanId);
    }

    [Theory]
    [InlineData("https://example.org/announce", true)]
    [InlineData("https://TRACKER.example.org/announce", true)]
    [InlineData("https://notexample.org/announce", false)]
    [InlineData("https://example.org.evil.test/announce", false)]
    public async Task DeadTracker_HealthFailureNeverBypassesProtection(string url, bool allowed)
    {
        var torrent = AddTorrent();
        ClientConfig.CategorySeeding = new() { HitAndRunMode = "and", MinSeedRatio = 10, MinSeedingTimeDays = 10 };
        var tracker = new TrackerConfig { Uri = "https://example.org", HitAndRunMode = "and", MinSeedRatio = 10, MinSeedingTimeDays = 10 };
        ClientConfig.Trackers = [tracker];
        var trackers = new List<TorrentTracker> { new() { Url = url, Msg = "unregistered torrent" } };
        _client.Setup(c => c.GetTorrentTrackersAsync("abc", It.IsAny<CancellationToken>())).ReturnsAsync(trackers);

        _seeding.HnrAllowsDelete(torrent, trackers).Should().BeFalse();
        (await _seeding.HnrAllowsDeleteAsync(torrent, "cleanup")).Should().BeFalse();
        (await _seeding.IsTrackerDeadAsync(torrent, tracker)).Should().Be(allowed);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Orphans_FollowRecyclingMode_WithoutRemovingTorrentRecords(bool recycling)
    {
        ClientConfig.Maintenance.Orphans.Enabled = true;
        ClientConfig.Maintenance.Orphans.MinimumAgeMinutes = 0;
        ClientConfig.Maintenance.RecycleBin.Enabled = recycling;
        var owned = AddTorrent();
        var orphan = Path.Combine(_root, "orphan.mkv");
        File.WriteAllText(orphan, "orphan");
        var plan = await Preview(MaintenanceOperation.OrphanScan);
        plan.OperationResults.Single().State.Should().Be("ready");
        plan.Actions.Should().ContainSingle().Which.Kind.Should().Be(recycling ? MaintenanceActionKind.QuarantineFile : MaintenanceActionKind.DeleteOrphanFile);
        var run = await Apply(plan);
        run.Applied.Should().Be(1);
        run.BytesMoved.Should().Be(recycling ? 6 : 0);
        File.Exists(orphan).Should().BeFalse();
        File.Exists(Path.Combine(_root, "movie.mkv")).Should().BeTrue();
        _torrents.Should().Contain(owned);
        _client.Verify(c => c.DeleteTorrentsAsync(It.IsAny<List<string>>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()), Times.Never());
        _deletion.InspectRecycleEntries("one").Count.Should().Be(recycling ? 1 : 0);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Orphans_RemoveEveryExtensionAndEmptyFolderTree(bool recycling)
    {
        ClientConfig.Maintenance.Orphans.Enabled = true;
        ClientConfig.Maintenance.Orphans.MinimumAgeMinutes = 60;
        ClientConfig.Maintenance.Orphans.MaxFilesPerRun = 50;
        ClientConfig.Maintenance.RecycleBin.Enabled = recycling;
        var folder = Path.Combine(_root, "orphan");
        Directory.CreateDirectory(Path.Combine(folder, "media", "extras"));
        Directory.CreateDirectory(Path.Combine(folder, "already-empty", "nested"));
        foreach (var name in new[] { "movie.mkv", "movie.nfo", "cover.jpg", "no-extension", ".hidden", "unknown.xyz", "abandoned.parts" })
        {
            var path = Path.Combine(folder, "media", "extras", name);
            File.WriteAllText(path, "data");
            File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddHours(-2));
        }
        var plan = await Preview(MaintenanceOperation.OrphanScan);
        plan.Actions.Count(a => a.Kind == MaintenanceActionKind.DeleteEmptyOrphanDirectory).Should().Be(5);
        plan.Actions.Count(a => a.Kind == (recycling ? MaintenanceActionKind.QuarantineFile : MaintenanceActionKind.DeleteOrphanFile)).Should().Be(7);
        (await Apply(plan)).Applied.Should().Be(12);
        Directory.Exists(folder).Should().BeFalse();
        Directory.Exists(_root).Should().BeTrue();
        _deletion.InspectRecycleEntries("one").Count.Should().Be(recycling ? 7 : 0);
    }

    [Fact]
    public async Task OrphanFolderCleanup_RespectsBudgetAcrossBatches()
    {
        ClientConfig.Maintenance.Orphans.Enabled = true;
        ClientConfig.Maintenance.Orphans.MinimumAgeMinutes = 0;
        ClientConfig.Maintenance.Orphans.MaxFilesPerRun = 2;
        var folder = Path.Combine(_root, "orphan");
        Directory.CreateDirectory(Path.Combine(folder, "nested"));
        File.WriteAllText(Path.Combine(folder, "nested", "file.xyz"), "data");
        var first = await Preview(MaintenanceOperation.OrphanScan);
        first.Actions.Should().HaveCount(2);
        (await Apply(first)).Applied.Should().Be(2);
        Directory.Exists(folder).Should().BeTrue();
        var second = await Preview(MaintenanceOperation.OrphanScan);
        second.Actions.Should().ContainSingle().Which.Kind.Should().Be(MaintenanceActionKind.DeleteEmptyOrphanDirectory);
        (await Apply(second)).Applied.Should().Be(1);
        Directory.Exists(folder).Should().BeFalse();
    }

    [Fact]
    public async Task EmptyFolderCleanup_PreservesNestedMappingRootsWithTrailingSeparator()
    {
        ClientConfig.Maintenance.Orphans.Enabled = true;
        ClientConfig.Maintenance.Orphans.MinimumAgeMinutes = 0;
        var folder = Directory.CreateDirectory(Path.Combine(_root, "mapped-root")).FullName;
        ClientConfig.Maintenance.PathMappings.Add(new() { ClientPath = "/other", LocalPath = folder + Path.DirectorySeparatorChar });
        (await Preview(MaintenanceOperation.OrphanScan)).Actions.Should().BeEmpty();
        (await _deletion.DeleteEmptyOrphanDirectoryAsync("one", folder, await _inventory.BuildAsync())).Success.Should().BeFalse();
        Directory.Exists(folder).Should().BeTrue();
    }

    [Fact]
    public async Task EmptyOrphanFolderApply_PreservesNewContentAndMissingOwnedPaths()
    {
        ClientConfig.Maintenance.Orphans.Enabled = true;
        ClientConfig.Maintenance.Orphans.MinimumAgeMinutes = 0;
        var folder = Directory.CreateDirectory(Path.Combine(_root, "empty")).FullName;
        var plan = await Preview(MaintenanceOperation.OrphanScan);
        plan.Actions.Should().ContainSingle().Which.Kind.Should().Be(MaintenanceActionKind.DeleteEmptyOrphanDirectory);
        File.WriteAllText(Path.Combine(folder, "arrived.txt"), "keep");
        (await Apply(plan)).Applied.Should().Be(0);
        File.ReadAllText(Path.Combine(folder, "arrived.txt")).Should().Be("keep");
        File.Delete(Path.Combine(folder, "arrived.txt"));
        AddTorrent("owner", "placeholder.mkv");
        _files["owner"] = [new() { Name = "empty/not-downloaded.mkv", Size = 4 }];
        (await Preview(MaintenanceOperation.OrphanScan)).Actions.Should().NotContain(a => a.Path == folder);
    }

    [Fact]
    public async Task OrphanParts_AreEligibleWhileActiveHashPartFilesRemainOwned()
    {
        ClientConfig.Maintenance.Orphans.Enabled = true;
        ClientConfig.Maintenance.Orphans.MinimumAgeMinutes = 0;
        AddTorrent();
        var active = Path.Combine(_root, ".abc.parts");
        var orphan = Path.Combine(_root, ".def.parts");
        File.WriteAllText(active, "active"); File.WriteAllText(orphan, "orphan");
        var plan = await Preview(MaintenanceOperation.OrphanScan);
        plan.Actions.Should().ContainSingle().Which.Path.Should().Be(orphan);
        (await Apply(plan)).Applied.Should().Be(1);
        File.ReadAllText(active).Should().Be("active");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Orphans_ModeChangesInvalidatePreview(bool initialRecycling)
    {
        ClientConfig.Maintenance.Orphans.Enabled = true;
        ClientConfig.Maintenance.Orphans.MinimumAgeMinutes = 0;
        ClientConfig.Maintenance.RecycleBin.Enabled = initialRecycling;
        var path = Path.Combine(_root, "orphan.mkv");
        File.WriteAllText(path, "keep");
        var plan = await Preview(MaintenanceOperation.OrphanScan);
        ClientConfig.Maintenance.RecycleBin.Enabled = !initialRecycling;
        await FluentActions.Awaiting(() => Apply(plan)).Should().ThrowAsync<InvalidOperationException>();
        File.ReadAllText(path).Should().Be("keep");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Orphans_ProtectIncompleteVariants_AndPartsArtifacts(bool recycling)
    {
        ClientConfig.Maintenance.Orphans.Enabled = true;
        ClientConfig.Maintenance.Orphans.MinimumAgeMinutes = 0;
        ClientConfig.Maintenance.RecycleBin.Enabled = recycling;
        AddTorrent();
        var expected = Path.Combine(_root, "movie.mkv");
        var incomplete = expected + ".!qB";
        File.Move(expected, incomplete);
        File.WriteAllText(Path.Combine(_root, ".abc.parts"), "keep");
        var inventory = await _inventory.BuildAsync();
        inventory.IsComplete.Should().BeTrue();
        inventory.Torrents.Single().LocalFilesComplete.Should().BeTrue();
        inventory.Torrents.Single().Files.Single().LocalPath.Should().Be(incomplete);
        inventory.LocalPathReferences.Should().ContainKey(expected).And.ContainKey(incomplete);
        inventory.Diagnostics.Should().NotContain(d => d.Code == "missing-file");
        (await Preview(MaintenanceOperation.OrphanScan)).Actions.Should().BeEmpty();
        File.Move(incomplete, expected);
        (await Preview(MaintenanceOperation.OrphanScan)).Actions.Should().BeEmpty();
    }

    [Fact]
    public async Task PermanentOrphanApply_RechecksNewIncompleteOwner()
    {
        ClientConfig.Maintenance.Orphans.Enabled = true;
        ClientConfig.Maintenance.Orphans.MinimumAgeMinutes = 0;
        ClientConfig.Maintenance.RecycleBin.Enabled = false;
        var path = Path.Combine(_root, "movie.mkv.!qB");
        File.WriteAllText(path, "keep");
        var plan = await Preview(MaintenanceOperation.OrphanScan);
        plan.CandidateCount.Should().Be(1);
        AddTorrent();
        (await Apply(plan)).Applied.Should().Be(0);
        File.ReadAllText(path).Should().Be("keep");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PermanentOrphanDeletion_RejectsIncompleteInventoryAndOwnedFiles(bool owned)
    {
        ClientConfig.Maintenance.Orphans.Enabled = true;
        ClientConfig.Maintenance.Orphans.MinimumAgeMinutes = 0;
        ClientConfig.Maintenance.RecycleBin.Enabled = false;
        var path = Path.Combine(_root, "movie.mkv");
        File.WriteAllText(path, "keep");
        if (owned) AddTorrent();
        var inventory = owned ? await _inventory.BuildAsync() : new TorrentInventory { OwnershipComplete = false };
        (await _deletion.DeleteOrphanFileAsync("one", path, inventory)).Success.Should().BeFalse();
        File.Exists(path).Should().BeTrue();
    }

    [Fact]
    public async Task MissingSidecars_AreDistinctFromIncompleteMedia()
    {
        ClientConfig.Maintenance.Orphans.Enabled = true;
        AddTorrent();
        File.Move(Path.Combine(_root, "movie.mkv"), Path.Combine(_root, "movie.mkv.!qB"));
        _files["abc"].Add(new() { Name = "movie.nfo", Size = 1, Progress = 1, Priority = 1 });
        var inventory = await _inventory.BuildAsync();
        inventory.IsComplete.Should().BeTrue();
        inventory.Diagnostics.Should().ContainSingle(d => d.Code == "missing-file");
        inventory.Diagnostics.Single(d => d.Code == "missing-file").Remediation.Should().Contain("download progress");
        inventory.Diagnostics.Single(d => d.Code == "missing-file").Examples.Should().ContainSingle()
            .Which.ClientPath.Should().Be("/downloads/movie.nfo");
        var result = (await Preview(MaintenanceOperation.OrphanScan)).OperationResults.Single();
        result.State.Should().Be("ready");
        result.Uninspectable.Should().Be(0);
    }

    [Fact]
    public async Task InventoryFailure_BlocksOrphanPreview()
    {
        ClientConfig.Maintenance.Orphans.Enabled = true;
        _client.Setup(c => c.GetTorrentsAsync(It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpRequestException("unavailable"));
        var plan = await Preview(MaintenanceOperation.OrphanScan);
        plan.CriticalErrors.Should().BeEmpty();
        plan.OperationResults.Should().Contain(r => r.Operation == MaintenanceOperation.OrphanScan && r.State == "blocked");
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(1, 0.5)]
    public async Task ExpectedMissingFiles_KeepOwnershipWithoutWarnings(int priority, double progress)
    {
        AddTorrent();
        _files["abc"].Add(new() { Name = "missing.mkv", Size = 1, Priority = priority, Progress = progress });
        var inventory = await _inventory.BuildAsync();
        inventory.IsComplete.Should().BeTrue();
        inventory.Diagnostics.Should().NotContain(d => d.Code == "missing-file");
        inventory.LocalPathReferences.Should().ContainKey(Path.Combine(_root, "missing.mkv"));
        inventory.LocalPathReferences.Should().ContainKey(Path.Combine(_root, "missing.mkv.!qB"));
        inventory.Torrents.Single().LocalFilesComplete.Should().BeFalse();
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task RecheckResult_DistinguishesDeferralFromClientRejection(bool busy)
    {
        var torrent = AddTorrent();
        if (busy) AddTorrent("checking", "checking.mkv").State = "checkingUP";
        _client.Setup(c => c.RecheckTorrentsAsync(It.IsAny<List<string>>(), It.IsAny<CancellationToken>())).ReturnsAsync(false);
        var result = await MaintenanceRecovery.RequestWithResultAsync(_db, _client.Object, torrent, "ErrorRecovery", new(), default);
        result.Success.Should().BeFalse();
        result.ContentPreserved.Should().BeTrue();
        result.Deferred.Should().Be(busy);
        result.Error.Should().Contain(busy ? "active" : "rejected");
        MaintenanceRecovery.Read(_db, torrent, "ErrorRecovery").Attempts.Should().Be(0);
        _client.Verify(c => c.RecheckTorrentsAsync(It.IsAny<List<string>>(), It.IsAny<CancellationToken>()), busy ? Times.Never() : Times.Once());
    }

    [Fact]
    public async Task MaintenanceRechecks_DeferAfterTheFirstClaimWithoutReportingFailure()
    {
        ClientConfig.Maintenance.ErrorRecovery.Enabled = true;
        ClientConfig.Maintenance.ErrorRecovery.MaxPerRun = 5;
        AddTorrent().State = "error";
        AddTorrent("second", "second.mkv").State = "error";
        _client.Setup(c => c.RecheckTorrentsAsync(It.IsAny<List<string>>(), It.IsAny<CancellationToken>())).ReturnsAsync(true);
        var plan = await Preview(MaintenanceOperation.ErrorRecovery);
        plan.CandidateCount.Should().Be(2);
        var run = await Apply(plan);
        run.Applied.Should().Be(1);
        run.Skipped.Should().Be(1);
        run.Failed.Should().Be(0);
        run.SkippedReasons.Should().ContainSingle().Which.Should().Contain("deferred");
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
        (await _inventory.BuildAsync()).IsComplete.Should().BeFalse();
    }

    [Fact]
    public async Task DisabledOwner_BlocksOrphanMovesWithoutConnectingIt()
    {
        ClientConfig.Maintenance.Orphans.Enabled = true;
        ClientConfig.Maintenance.Orphans.MinimumAgeMinutes = 0;
        File.WriteAllText(Path.Combine(_root, "orphan.mkv"), "other client's data");
        _config.TorrentClients["disabled"] = new() { Host = "fixture", Disabled = true };
        var inventory = await _inventory.BuildAsync();
        inventory.IsComplete.Should().BeFalse();
        inventory.Warnings.Should().Contain(w => w.Contains("Disabled client ownership"));
        var plan = await Preview(MaintenanceOperation.OrphanScan);
        plan.CriticalErrors.Should().BeEmpty();
        plan.OperationResults.Should().Contain(r => r.Operation == MaintenanceOperation.OrphanScan && r.State == "blocked"
            && r.Reason != null && r.Reason.Contains("client-disabled") && r.Reason.Contains("Disabled client ownership is unknown"));
        (await Apply(plan)).Applied.Should().Be(0);
        File.ReadAllText(Path.Combine(_root, "orphan.mkv")).Should().Be("other client's data");
        _registry.Verify(r => r.GetClient("disabled"), Times.Never);
    }

    [Fact]
    public async Task MissingFileCapability_BlocksOwnership()
    {
        _client.SetupGet(c => c.Capabilities).Returns(TorrentClientCapabilities.None);
        (await _inventory.BuildAsync()).IsComplete.Should().BeFalse();
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task OrphanApply_RechecksFailuresAndNewOwners(bool newOwner, bool recycling)
    {
        ClientConfig.Maintenance.RecycleBin.Enabled = recycling;
        ClientConfig.Maintenance.Orphans.Enabled = true;
        ClientConfig.Maintenance.Orphans.MinimumAgeMinutes = 0;
        var path = Path.Combine(_root, "movie.mkv");
        File.WriteAllText(path, "data");
        var plan = await Preview(MaintenanceOperation.OrphanScan);
        plan.CandidateCount.Should().Be(1);
        if (newOwner) AddTorrent();
        else _client.Setup(c => c.GetTorrentsAsync(It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>())).ThrowsAsync(new HttpRequestException());
        var run = await Apply(plan);
        (run.Failed + run.Skipped).Should().Be(1);
        File.Exists(path).Should().BeTrue();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OrphanApply_RechecksFileRenameWithUnchangedTorrentMetadata(bool recycling)
    {
        ClientConfig.Maintenance.RecycleBin.Enabled = recycling;
        ClientConfig.Maintenance.Orphans.Enabled = true;
        ClientConfig.Maintenance.Orphans.MinimumAgeMinutes = 0;
        AddTorrent("owner", "original.mkv");
        var path = Path.Combine(_root, "orphan.mkv");
        File.WriteAllText(path, "data");
        var plan = await Preview(MaintenanceOperation.OrphanScan);
        plan.CandidateCount.Should().Be(1);
        _files["owner"] = [new() { Name = "orphan.mkv", Size = 4 }];
        (await Apply(plan)).Should().Match<MaintenanceRunSummary>(run => run.Failed + run.Skipped == 1);
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
        _seeding.HnrAllowsDelete(torrent, [new() { Url = "https://tracker.example/announce", Status = 2 }]).Should().Be(allowed);
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
    [InlineData(null)]
    [InlineData("")]
    public void EmptyTrackerHost_DoesNotBypassCategoryHnr(string? url)
    {
        var torrent = AddTorrent();
        torrent.Ratio = 0.2;
        ClientConfig.CategorySeeding.HitAndRunMode = "and";
        ClientConfig.CategorySeeding.MinSeedRatio = 1;
        _seeding.HnrAllowsDelete(torrent, [new() { Url = url!, Msg = "unregistered torrent" }]).Should().BeFalse();
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
        _client.Verify(c => c.PauseTorrentsAsync(It.IsAny<List<string>>(), It.IsAny<CancellationToken>()), Times.Never);
        _client.Verify(c => c.ResumeTorrentsAsync(It.IsAny<List<string>>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [SkippableFact]
    public async Task Recycling_PublishedCopyWithFailedSourceDeletion_StaysPausedAndReportsRecoveryPath()
    {
        if (!OperatingSystem.IsLinux()) { Skip.If(true, "Requires Linux filesystem permissions."); return; }
        Skip.If(Environment.UserName == "root", "Requires a user subject to filesystem permissions.");
        Skip.If(!Directory.Exists("/dev/shm"), "Requires a separate writable filesystem.");
        var recycle = Path.Combine("/dev/shm", "torrentarr-recycle-test-" + Guid.NewGuid().ToString("N"));
        var source = Path.Combine(_root, "bundle");
        Directory.CreateDirectory(source);
        var torrent = AddTorrent(filename: "bundle/movie.mkv");
        torrent.State = "downloading";
        ClientConfig.Maintenance.RecycleBin.Path = recycle;
        _client.Setup(c => c.PauseTorrentsAsync(It.IsAny<List<string>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => { torrent.State = "stoppedDL"; return true; });
        // A read-only parent permits copying its file but prevents unlinking it.
        var inventory = await _inventory.BuildAsync();
        var item = inventory.Torrents.Single();
        item.Files[0] = new() { File = item.Files[0].File, ClientPath = "/downloads/bundle", LocalPath = source };
        var originalMode = File.GetUnixFileMode(source);
        try
        {
            File.SetUnixFileMode(source, UnixFileMode.UserRead | UnixFileMode.UserExecute);
            var result = await _deletion.RemoveMaintenanceTorrentAsync("one", "abc", true, inventory);
            result.Success.Should().BeFalse();
            result.ContentPreserved.Should().BeFalse(result.Error);
            result.Error.Should().Contain("Unable to restore").And.Contain(recycle);
            Directory.EnumerateFiles(recycle, "*", SearchOption.AllDirectories).Select(File.ReadAllText).Should().Contain("data");
            _client.Verify(c => c.ResumeTorrentsAsync(It.IsAny<List<string>>(), It.IsAny<CancellationToken>()), Times.Never);
            _client.Verify(c => c.DeleteTorrentsAsync(It.IsAny<List<string>>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()), Times.Never);
        }
        finally
        {
            File.SetUnixFileMode(source, originalMode);
            if (Directory.Exists(recycle)) Directory.Delete(recycle, true);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RecycleMove_CollisionPreservesBothPaths(bool directory)
    {
        var source = Path.Combine(_root, "original");
        var destination = Path.Combine(_root, "recreated");
        if (directory)
        {
            Directory.CreateDirectory(source);
            Directory.CreateDirectory(destination);
        }
        var sourceFile = directory ? Path.Combine(source, "file") : source;
        var destinationFile = directory ? Path.Combine(destination, "file") : destination;
        File.WriteAllText(sourceFile, "original data");
        File.WriteAllText(destinationFile, "new data");
        var move = typeof(SafeDeletionService).GetMethod("MoveToRecycleAsync",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
        await FluentActions.Awaiting(() => (Task<long>)move.Invoke(_deletion, [source, destination, CancellationToken.None])!)
            .Should().ThrowAsync<IOException>();
        File.ReadAllText(sourceFile).Should().Be("original data");
        File.ReadAllText(destinationFile).Should().Be("new data");
        Directory.EnumerateFileSystemEntries(_root, "*.partial").Should().BeEmpty();
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Recycling_StopsActiveTorrentBeforeMoving_AndResumesAfterRejectedRemoval(bool accepted)
    {
        var torrent = AddTorrent();
        torrent.Progress = 0.5;
        torrent.State = "downloading";
        string? contentAtPause = null, contentAtResume = null;
        bool? stoppedAtDelete = null, fileExistedAtDelete = null;
        _client.Setup(c => c.PauseTorrentsAsync(It.IsAny<List<string>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(() =>
            {
                contentAtPause = File.ReadAllText(Path.Combine(_root, "movie.mkv"));
                if (accepted) torrent.State = "stoppedDL";
                return accepted;
            });
        _client.Setup(c => c.DeleteTorrentsAsync(It.IsAny<List<string>>(), false, It.IsAny<CancellationToken>()))
            .ReturnsAsync(() =>
            {
                stoppedAtDelete = torrent.IsStopped;
                fileExistedAtDelete = File.Exists(Path.Combine(_root, "movie.mkv"));
                return false;
            });
        _client.Setup(c => c.ResumeTorrentsAsync(It.IsAny<List<string>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(() =>
            {
                contentAtResume = File.Exists(Path.Combine(_root, "movie.mkv"))
                    ? File.ReadAllText(Path.Combine(_root, "movie.mkv")) : null;
                return true;
            });
        var result = await _deletion.RemoveMaintenanceTorrentAsync("one", "abc", true, await _inventory.BuildAsync());
        contentAtPause.Should().Be("data");
        if (accepted)
        {
            stoppedAtDelete.Should().BeTrue();
            fileExistedAtDelete.Should().BeFalse();
            contentAtResume.Should().Be("data");
        }
        result.Success.Should().BeFalse();
        result.ContentPreserved.Should().BeTrue();
        File.ReadAllText(Path.Combine(_root, "movie.mkv")).Should().Be("data");
        _client.Verify(c => c.DeleteTorrentsAsync(It.IsAny<List<string>>(), false, It.IsAny<CancellationToken>()), accepted ? Times.Once() : Times.Never());
        _client.Verify(c => c.ResumeTorrentsAsync(It.IsAny<List<string>>(), It.IsAny<CancellationToken>()), accepted ? Times.Once() : Times.Never());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Recycling_UncertainRemovalRestoresContentButNeverResumes(bool cancelled)
    {
        var torrent = AddTorrent();
        torrent.Progress = 0.5; torrent.State = "downloading";
        _client.Setup(c => c.PauseTorrentsAsync(It.IsAny<List<string>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => { torrent.State = "stoppedDL"; return true; });
        _client.Setup(c => c.DeleteTorrentsAsync(It.IsAny<List<string>>(), false, It.IsAny<CancellationToken>()))
            .ThrowsAsync(cancelled ? new OperationCanceledException() : new HttpRequestException("response lost"));
        var result = await _deletion.RemoveMaintenanceTorrentAsync("one", "abc", true, await _inventory.BuildAsync());
        result.Success.Should().BeFalse(); result.ContentPreserved.Should().BeTrue();
        result.Error.Should().Contain("Remote removal outcome is uncertain");
        result.Error.Should().NotContain("rejected removal");
        File.ReadAllText(Path.Combine(_root, "movie.mkv")).Should().Be("data");
        _client.Verify(c => c.ResumeTorrentsAsync(It.IsAny<List<string>>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Recycling_DoesNotMoveFilesUntilPauseIsConfirmed()
    {
        var torrent = AddTorrent();
        torrent.State = "downloading";
        using var cancellation = new CancellationTokenSource();
        _client.Setup(c => c.PauseTorrentsAsync(It.IsAny<List<string>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => { cancellation.Cancel(); return true; });
        var result = await _deletion.RemoveMaintenanceTorrentAsync("one", "abc", true, await _inventory.BuildAsync(), ct: cancellation.Token);
        result.Success.Should().BeFalse();
        File.ReadAllText(Path.Combine(_root, "movie.mkv")).Should().Be("data");
        _client.Verify(c => c.DeleteTorrentsAsync(It.IsAny<List<string>>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()), Times.Never);
        _client.Verify(c => c.ResumeTorrentsAsync(It.IsAny<List<string>>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Recycling_DoesNotResumeAfterSuccessfulOrUncertainRemoval(bool uncertain)
    {
        var torrent = AddTorrent();
        torrent.State = "uploading";
        _client.Setup(c => c.PauseTorrentsAsync(It.IsAny<List<string>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => { torrent.State = "pausedUP"; return true; });
        if (uncertain)
            _client.Setup(c => c.DeleteTorrentsAsync(It.IsAny<List<string>>(), false, It.IsAny<CancellationToken>()))
                .ThrowsAsync(new HttpRequestException());
        var result = await _deletion.RemoveMaintenanceTorrentAsync("one", "abc", true, await _inventory.BuildAsync());
        result.Success.Should().Be(!uncertain);
        result.ContentPreserved.Should().Be(uncertain);
        File.Exists(Path.Combine(_root, "movie.mkv")).Should().Be(uncertain);
        if (uncertain) result.Error.Should().Contain("uncertain");
        _client.Verify(c => c.ResumeTorrentsAsync(It.IsAny<List<string>>(), It.IsAny<CancellationToken>()), Times.Never);
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
    public async Task RecyclingWithoutMappings_ExplainsClientPathAndDoesNotDeleteContent()
    {
        AddTorrent();
        ClientConfig.Maintenance.PathMappings.Clear();
        var result = await _deletion.RemoveTorrentAsync("one", "abc", true);
        result.Success.Should().BeFalse();
        result.Error.Should().Contain("Recycling blocked").And.Contain("Client 'one'")
            .And.Contain("/downloads/movie.mkv").And.Contain("Maintenance.PathMappings")
            .And.Contain("mounted and accessible").And.Contain("Identical client/local paths");
        _client.Verify(c => c.DeleteTorrentsAsync(It.IsAny<List<string>>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()), Times.Never());
        File.Exists(Path.Combine(_root, "movie.mkv")).Should().BeTrue();
        ClientConfig.Maintenance.PathMappings.Add(new() { ClientPath = "/downloads", LocalPath = _root });
        (await _deletion.RemoveTorrentAsync("one", "abc", true)).Success.Should().BeTrue();
    }

    [Fact]
    public async Task DisabledRecycle_MaintenanceUsesNativeDeletion()
    {
        AddTorrent();
        ClientConfig.Maintenance.PathMappings.Clear();
        ClientConfig.Maintenance.RecycleBin.Enabled = false;
        (await _deletion.RemoveMaintenanceTorrentAsync("one", "abc", true, await _inventory.BuildAsync())).Success.Should().BeTrue();
        _client.Verify(c => c.DeleteTorrentsAsync(It.IsAny<List<string>>(), true, It.IsAny<CancellationToken>()), Times.Once);
        (await _deletion.RemoveTorrentAsync("one", "abc", true)).Success.Should().BeTrue();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DisabledRecycle_BypassesGlobalInventoryAndLocalPaths(bool maintenance)
    {
        AddTorrent();
        ClientConfig.Maintenance.RecycleBin.Enabled = false;
        ClientConfig.Maintenance.PathMappings.Clear();
        _config.TorrentClients["offline-second"] = new() { Host = "offline" };
        var inventory = new Mock<ITorrentInventoryService>(MockBehavior.Strict);
        var deletion = new SafeDeletionService(_registry.Object, inventory.Object, new PathMappingService(), _config,
            NullLogger<SafeDeletionService>.Instance, _services.GetRequiredService<IServiceScopeFactory>());
        var result = maintenance
            ? await deletion.RemoveMaintenanceTorrentAsync("one", "abc", true, new TorrentInventory { OwnershipRequested = true })
            : await deletion.RemoveTorrentForIntentAsync("one", "abc", true, TorrentRemovalIntent.RejectedByAllowlist);
        result.Success.Should().BeTrue();
        _client.Verify(c => c.DeleteTorrentsAsync(It.IsAny<List<string>>(), true, It.IsAny<CancellationToken>()), Times.Once);
        inventory.VerifyNoOtherCalls();
        File.Exists(Path.Combine(_root, "movie.mkv")).Should().BeTrue("only the mocked native client deletes content; Torrentarr must not move files");
    }

    [Fact]
    public async Task RemovalDecisionNamesEveryKnownBlocker()
    {
        var torrent = AddTorrent();
        torrent.Tags = "qBitrr-ignored";
        torrent.AddedOn = DateTimeOffset.UtcNow.ToUnixTimeSeconds() - 1;
        ClientConfig.CategorySeeding.IgnoreTorrentsYoungerThan = 3600;
        var decision = await _seeding.EvaluateRemovalAsync(torrent, TorrentRemovalIntent.RejectedByAllowlist);
        decision.Allowed.Should().BeFalse();
        decision.Reason.Should().Contain("minimum-age").And.Contain("3600").And.Contain("Age=").And.Contain("Next:");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task NativeCleanupPreviewDoesNotNeedMappingsOrRecycleBudget(bool changeMode)
    {
        var torrent = AddTorrent();
        ClientConfig.Maintenance.RecycleBin.Enabled = false;
        ClientConfig.Maintenance.RecycleBin.MaxTorrentMovesPerRun = 0;
        ClientConfig.Maintenance.PathMappings.Clear();
        ClientConfig.Maintenance.Unregistered.Enabled = true;
        _config.TorrentClients["offline"] = new() { Host = "offline" };
        MaintenancePersistence.Write(_db, MaintenancePersistence.Key("one", torrent.Hash, "recovery:UnregisteredCleanup"), new RecoveryIncident
        {
            AddedOn = torrent.AddedOn,
            Stage = "recurring",
            Attempts = 1,
            CompletedAt = DateTimeOffset.UtcNow.AddDays(-2),
            FailureSince = DateTimeOffset.UtcNow.AddDays(-2),
            LastObserved = DateTimeOffset.UtcNow
        });
        _client.Setup(c => c.GetTorrentTrackersAsync(torrent.Hash, It.IsAny<CancellationToken>()))
            .ReturnsAsync([new() { Url = "https://tracker.example/announce", Msg = "unregistered torrent" }]);
        var plan = await Preview(MaintenanceOperation.UnregisteredCleanup);
        var action = plan.Actions.Should().ContainSingle().Subject;
        action.Kind.Should().Be(MaintenanceActionKind.DeleteContent);
        action.Reason.Should().Contain("permanently deleted");
        action.BlockedReason.Should().BeNull();
        if (changeMode)
        {
            ClientConfig.Maintenance.RecycleBin.Enabled = true;
            await FluentActions.Awaiting(() => Apply(plan)).Should().ThrowAsync<InvalidOperationException>();
            _client.Verify(c => c.DeleteTorrentsAsync(It.IsAny<List<string>>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()), Times.Never);
        }
        else
        {
            (await Apply(plan)).Applied.Should().Be(1);
            _client.Verify(c => c.DeleteTorrentsAsync(It.IsAny<List<string>>(), true, It.IsAny<CancellationToken>()), Times.Once);
        }
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
    public async Task InventoryRefresh_ReenumeratesUnchangedMetadataAndNewOwners()
    {
        AddTorrent();
        var inventory = await _inventory.BuildAsync();
        _files["abc"] = [new() { Name = "renamed.mkv", Size = 4 }];
        inventory = await _inventory.RefreshAsync(inventory);
        inventory.LocalPathReferences.Should().ContainKey(Path.Combine(_root, "renamed.mkv"));
        inventory.LocalPathReferences.Should().NotContainKey(Path.Combine(_root, "movie.mkv"));
        _client.Verify(c => c.GetTorrentFilesAsync("abc", It.IsAny<CancellationToken>()), Times.Exactly(2));
        inventory = await _inventory.RefreshAsync(inventory, "one", "abc");
        _client.Verify(c => c.GetTorrentFilesAsync("abc", It.IsAny<CancellationToken>()), Times.Exactly(3));
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
    public async Task LargeInventory_ReenumeratesEveryOwnerBeforeApply()
    {
        for (var i = 0; i < 1000; i++)
        {
            var hash = "hash-" + i;
            _torrents.Add(new() { Hash = hash, Name = hash, Size = 4, SavePath = "/downloads", ContentPath = "/downloads/" + hash });
            _files[hash] = [new() { Name = hash, Size = 4 }];
        }
        var inventory = await _inventory.BuildAsync();
        inventory = await _inventory.RefreshAsync(inventory);
        inventory.IsComplete.Should().BeTrue();
        _client.Verify(c => c.GetTorrentFilesAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Exactly(2000));
        _client.Verify(c => c.GetTorrentTrackersAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Exactly(2000));
        _client.Verify(c => c.GetTorrentsAsync(It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Exactly(2));
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
    public async Task UnmappedFiles_AreGroupedWithExactCountsAndBoundedExamples()
    {
        ClientConfig.Maintenance.PathMappings.Clear();
        for (var i = 0; i < 300; i++) AddTorrent("hash-" + i, "file-" + i);
        var inventory = await _inventory.BuildAsync();
        var issue = inventory.Diagnostics.Single(d => d.Code == "missing-mapping");
        issue.AffectedFiles.Should().Be(300);
        issue.AffectedTorrents.Should().Be(300);
        issue.Examples.Should().HaveCount(5);
        issue.Examples[0].ClientPath.Should().Be("/downloads/file-0");
        inventory.Warnings.Should().HaveCount(1);
        inventory.IsComplete.Should().BeFalse();
    }

    [Fact]
    public async Task TrackerFailure_PreservesFileOwnershipAndOtherTorrents()
    {
        AddTorrent(); AddTorrent("other", "other.mkv");
        _client.Setup(c => c.GetTorrentTrackersAsync("abc", It.IsAny<CancellationToken>())).ThrowsAsync(new HttpRequestException("https://secret.invalid/passkey"));
        var inventory = await _inventory.BuildAsync();
        inventory.IsComplete.Should().BeTrue();
        inventory.Torrents.Should().HaveCount(2);
        inventory.Torrents.Single(i => i.Torrent.Hash == "abc").TrackersComplete.Should().BeFalse();
        inventory.LocalPathReferences.Should().ContainKey(Path.Combine(_root, "movie.mkv"));
        inventory.Warnings.Should().NotContain(w => w.Contains("passkey"));
    }

    [Fact]
    public async Task FileFailure_DoesNotDiscardTrackerDataOrOtherTorrents()
    {
        AddTorrent(); AddTorrent("other", "other.mkv");
        _client.Setup(c => c.GetTorrentFilesAsync("abc", It.IsAny<CancellationToken>())).ThrowsAsync(new IOException());
        var inventory = await _inventory.BuildAsync();
        inventory.Torrents.Should().HaveCount(2);
        inventory.Torrents.Single(i => i.Torrent.Hash == "abc").TrackersComplete.Should().BeTrue();
        inventory.Torrents.Single(i => i.Torrent.Hash == "other").LocalFilesComplete.Should().BeTrue();
        inventory.IsComplete.Should().BeFalse();
    }








    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OrphanScan_DoesNotFollowSymlinkDirectories(bool recycling)
    {
        ClientConfig.Maintenance.RecycleBin.Enabled = recycling;
        if (OperatingSystem.IsWindows()) return;
        var outside = Path.Combine(TestPaths.TempPath, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(outside);
        try
        {
            File.WriteAllText(Path.Combine(outside, "outside"), "keep");
            Directory.CreateSymbolicLink(Path.Combine(_root, "link"), outside);
            ClientConfig.Maintenance.Orphans.Enabled = true;
            ClientConfig.Maintenance.Orphans.MinimumAgeMinutes = 0;
            (await Preview(MaintenanceOperation.OrphanScan)).Actions.Should().BeEmpty();
            File.ReadAllText(Path.Combine(outside, "outside")).Should().Be("keep");
        }
        finally { Directory.Delete(outside, true); }
    }


    [Fact]
    public async Task RetentionWithoutExpiredFiles_DoesNotProposeMutation()
    {
        var directory = Path.Combine(_root, ".recycle", "one");
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "fresh"), "keep");
        var plan = await Preview(MaintenanceOperation.RecycleRetention);
        plan.Actions.Should().OnlyContain(a => a.Kind == MaintenanceActionKind.RegisterRecycleEntry);
        (await Apply(plan)).Applied.Should().Be(1);
        (await Preview(MaintenanceOperation.RecycleRetention)).Actions.Should().BeEmpty();
    }

    [Fact]
    public async Task CandidateInventory_CannotAuthorizeGlobalOwnership()
    {
        AddTorrent();
        var candidate = await _inventory.CollectAsync(new Dictionary<string, InventoryRequirements> { ["one"] = InventoryRequirements.All }, candidateHash: "abc");
        candidate.IsComplete.Should().BeFalse();
        var metadata = await _inventory.CollectAsync(new Dictionary<string, InventoryRequirements> { ["one"] = InventoryRequirements.Metadata | InventoryRequirements.GlobalOwnership });
        metadata.IsComplete.Should().BeFalse();
        metadata.Diagnostics.Should().Contain(d => d.Code == "ownership-not-inspected");
    }




    [Fact]
    public async Task RecycledOldMediaGetsFullRetention_AndPreviewDoesNotRegisterLegacyFiles()
    {
        var directory = Path.Combine(_root, ".recycle", "one"); Directory.CreateDirectory(directory);
        var old = Path.Combine(directory, "old-file"); File.WriteAllText(old, "keep"); File.SetLastWriteTimeUtc(old, DateTime.UtcNow.AddYears(-5));
        var rows = _db.MaintenanceState.Count();
        var plan = await Preview(MaintenanceOperation.RecycleRetention);
        _db.MaintenanceState.Count().Should().Be(rows);
        plan.Actions.Single().Kind.Should().Be(MaintenanceActionKind.RegisterRecycleEntry);
        (await Apply(plan)).Applied.Should().Be(1);
        (await Preview(MaintenanceOperation.RecycleRetention)).Actions.Should().BeEmpty();
        File.ReadAllText(old).Should().Be("keep");
    }

    [Fact]
    public async Task RetentionUsesCommittedEntryTime_AndRejectsChangedFiles()
    {
        var source = Path.Combine(_root, "old.mkv"); File.WriteAllText(source, "data");
        File.SetLastWriteTimeUtc(source, DateTime.UtcNow.AddYears(-5));
        (await _deletion.QuarantineFileAsync("one", source)).Success.Should().BeTrue();
        var entry = _deletion.InspectRecycleEntries("one").Single();
        File.GetLastWriteTimeUtc(entry.Destination).Should().BeBefore(DateTime.UtcNow.AddYears(-4));
        (await Preview(MaintenanceOperation.RecycleRetention)).Actions.Should().BeEmpty();
        MaintenancePersistence.Write(_db, entry.Key, entry with { RecycledAt = DateTimeOffset.UtcNow.AddDays(-31) });
        var plan = await Preview(MaintenanceOperation.RecycleRetention);
        plan.Actions.Single().Kind.Should().Be(MaintenanceActionKind.DeleteExpiredRecycleItem);
        File.AppendAllText(entry.Destination, "new data");
        (await Apply(plan)).Should().Match<MaintenanceRunSummary>(run => run.Failed + run.Skipped == 1);
        File.Exists(entry.Destination).Should().BeTrue();
        var fresh = await Preview(MaintenanceOperation.RecycleRetention);
        (await Apply(fresh)).BytesReclaimed.Should().Be(12);
        File.Exists(entry.Destination).Should().BeFalse();
    }

    [Theory]
    [InlineData("preparing")]
    [InlineData("needs-recovery")]
    public async Task IncompleteRecyclingNeverExpires_AfterRestart(string state)
    {
        var directory = Path.Combine(_root, ".recycle", "one"); Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "pending"); File.WriteAllText(path, "keep");
        var key = RecycleLedger.Key("one", path);
        MaintenancePersistence.Write(_db, key, new RecycleEntry(key, "one", "original", path, DateTimeOffset.UtcNow.AddYears(-1), state));
        using var restarted = new MaintenanceCoordinator(_config, _registry.Object, _inventory, new PathMappingService(), Mock.Of<IHardlinkInspector>(),
            _deletion, NullLogger<MaintenanceCoordinator>.Instance, _services.GetRequiredService<IServiceScopeFactory>());
        var plan = await restarted.PreviewAsync(new() { ClientInstanceIds = ["one"], Operations = [MaintenanceOperation.RecycleRetention] });
        plan.Actions.Should().BeEmpty(); plan.OperationResults.Single().State.Should().Be("partial");
        (await restarted.ApplyAsync(plan.PlanId)).Result.Should().Be("partial"); File.Exists(path).Should().BeTrue();
    }

    [Fact]
    public async Task DirectoryRetentionCountsActualFilesAcrossPartialPurges()
    {
        var path = Path.Combine(_root, ".recycle", "one", "folder"); Directory.CreateDirectory(path);
        File.WriteAllText(Path.Combine(path, "one"), "one"); File.WriteAllText(Path.Combine(path, "two"), "two");
        var key = RecycleLedger.Key("one", path);
        MaintenancePersistence.Write(_db, key, new RecycleEntry(key, "one", "original", path, DateTimeOffset.UtcNow.AddDays(-40), "committed"));
        ClientConfig.Maintenance.RecycleBin.MaxFilesDeletedPerRun = 1;
        (await Apply(await Preview(MaintenanceOperation.RecycleRetention))).BytesReclaimed.Should().Be(3);
        Directory.GetFiles(path).Should().HaveCount(1);
        (await Apply(await Preview(MaintenanceOperation.RecycleRetention))).BytesReclaimed.Should().Be(3);
        Directory.Exists(path).Should().BeFalse();
    }

    [Fact]
    public async Task PreviewIsSingleUse_AndConfigurationChangesInvalidateIt()
    {
        ClientConfig.Maintenance.Orphans.Enabled = true; ClientConfig.Maintenance.Orphans.MinimumAgeMinutes = 0;
        File.WriteAllText(Path.Combine(_root, "orphan.mkv"), "data");
        var plan = await Preview(MaintenanceOperation.OrphanScan);
        ClientConfig.Maintenance.Orphans.MinimumAgeMinutes = 99;
        await FluentActions.Awaiting(() => Apply(plan)).Should().ThrowAsync<InvalidOperationException>();
        ClientConfig.Maintenance.Orphans.MinimumAgeMinutes = 0;
        plan = await Preview(MaintenanceOperation.OrphanScan);
        (await Apply(plan)).Applied.Should().Be(1);
        await FluentActions.Awaiting(() => Apply(plan)).Should().ThrowAsync<InvalidOperationException>();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DisabledOrExhaustedRecoveryKeepsErrors_WithoutUnboundedRechecks(bool enabled)
    {
        var torrent = AddTorrent(); torrent.State = "missingFiles";
        var config = new RecoveryConfig { Enabled = enabled, MinimumTorrentAgeMinutes = 0 };
        _client.Setup(c => c.RecheckTorrentsAsync(It.IsAny<List<string>>(), It.IsAny<CancellationToken>())).ReturnsAsync(true);
        await MaintenanceRecovery.ProcessAsync(_db, _client.Object, torrent, config, 0, true, default);
        await MaintenanceRecovery.ProcessAsync(_db, _client.Object, torrent, config, 0, true, default);
        MaintenanceRecovery.BlocksAutomaticRemoval(_db, torrent, config).Should().BeTrue();
        _client.Verify(c => c.RecheckTorrentsAsync(It.IsAny<List<string>>(), It.IsAny<CancellationToken>()), enabled ? Times.Once() : Times.Never());
        _client.Verify(c => c.DeleteTorrentsAsync(It.IsAny<List<string>>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()), Times.Never());
    }

    [Fact]
    public async Task ExplicitRecheckDoesNotRepeatUntilObservedExit()
    {
        var torrent = AddTorrent(); torrent.Category = "recheck";
        _client.Setup(c => c.RecheckTorrentsAsync(It.IsAny<List<string>>(), It.IsAny<CancellationToken>())).ReturnsAsync(true);
        await MaintenanceRecovery.RequestExplicitAsync(_db, _client.Object, torrent, default);
        await MaintenanceRecovery.RequestExplicitAsync(_db, _client.Object, torrent, default);
        _client.Verify(c => c.RecheckTorrentsAsync(It.IsAny<List<string>>(), It.IsAny<CancellationToken>()), Times.Once);
        torrent.Category = "movies"; MaintenanceRecovery.ObserveExplicitExits(_db, "one", [torrent], "recheck");
        torrent.Category = "recheck"; await MaintenanceRecovery.RequestExplicitAsync(_db, _client.Object, torrent, default);
        _client.Verify(c => c.RecheckTorrentsAsync(It.IsAny<List<string>>(), It.IsAny<CancellationToken>()), Times.Exactly(2));
    }

    [Fact]
    public async Task CompletedExplicitRecheck_ReleasesOtherRepairsButCannotRepeatUntilCategoryExit()
    {
        var explicitTorrent = AddTorrent("explicit"); explicitTorrent.Category = "recheck";
        var repair = AddTorrent("repair", "repair.mkv"); repair.State = "error";
        _client.Setup(c => c.RecheckTorrentsAsync(It.IsAny<List<string>>(), It.IsAny<CancellationToken>())).ReturnsAsync(true);
        (await MaintenanceRecovery.RequestExplicitAsync(_db, _client.Object, explicitTorrent, default)).Should().BeTrue();
        (await MaintenanceRecovery.RequestAsync(_db, _client.Object, repair, "ErrorRecovery", new(), default)).Should().BeFalse();
        explicitTorrent.State = "checkingUP";
        MaintenanceRecovery.ObserveExplicitExits(_db, "one", _torrents, "recheck");
        MaintenanceRecovery.Read(_db, explicitTorrent, "ExplicitRecheck").Stage.Should().Be("checking");
        explicitTorrent.State = "pausedUP";
        MaintenanceRecovery.ObserveExplicitExits(_db, "one", _torrents, "recheck");
        var state = MaintenanceRecovery.Read(_db, explicitTorrent, "ExplicitRecheck");
        state.Stage.Should().Be("checked"); state.CompletedAt.Should().NotBeNull();
        (await MaintenanceRecovery.RequestAsync(_db, _client.Object, repair, "ErrorRecovery", new(), default)).Should().BeTrue();
        state.HealthySince = DateTimeOffset.UtcNow.AddDays(-10);
        MaintenancePersistence.Write(_db, MaintenancePersistence.Key("one", explicitTorrent.Hash, "recovery:ExplicitRecheck"), state);
        MaintenanceRecovery.ObserveExplicitExits(_db, "one", _torrents, "recheck");
        MaintenanceRecovery.Read(_db, explicitTorrent, "ExplicitRecheck").Stage.Should().Be("checked");
        (await MaintenanceRecovery.RequestExplicitAsync(_db, _client.Object, explicitTorrent, default)).Should().BeFalse();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SyntheticPeerDiscoveryEntries_DoNotBlockTrackerClearanceOrSupplyTrackerHealth(bool realTrackerHealthy)
    {
        var torrent = AddTorrent(); torrent.IsPrivate = true;
        ClientConfig.Trackers = [new() { Uri = "https://tracker.example", HitAndRunMode = "or", MinSeedRatio = 1, TrackerUpdateBuffer = 0 }];
        var trackers = new List<TorrentTracker>
        {
            new() { Url = "https://tracker.example/announce", Tier = 0, Status = realTrackerHealthy ? 2 : 0 },
            new() { Url = "** [DHT] **", Tier = -1, Status = 2 },
            new() { Url = "** [PeX] **", Tier = -1, Status = 2 },
            new() { Url = "** [LSD] **", Tier = -1, Status = 2 }
        };
        _seeding.GetProtectionDecisions(torrent, trackers).Should().ContainSingle();
        MaintenanceRecovery.HealthyTracker(trackers).Should().Be(realTrackerHealthy);
        _seeding.HnrAllowsDelete(torrent, trackers).Should().Be(realTrackerHealthy);
        trackers.Add(new() { Url = "https://unmatched.example/announce", Tier = 1, Status = 2 });
        _seeding.HnrAllowsDelete(torrent, trackers).Should().BeFalse();
    }

    [Theory]
    [InlineData("checked")]
    [InlineData("recurring")]
    public async Task RecoveryRetry_ClearsPreviousCompletionAndRequiresANewCheck(string stage)
    {
        var torrent = AddTorrent(); torrent.State = "error";
        var cfg = ClientConfig.Maintenance.ErrorRecovery;
        cfg.Enabled = true; cfg.MaxRepairAttempts = 2;
        var now = DateTimeOffset.UtcNow;
        var key = MaintenancePersistence.Key("one", torrent.Hash, "recovery:ErrorRecovery");
        MaintenancePersistence.Write(_db, key, new RecoveryIncident
        {
            AddedOn = torrent.AddedOn,
            Stage = stage,
            Attempts = 1,
            RequestedAt = now.AddMinutes(-cfg.RetryMinutes - 5),
            CheckingAt = now.AddMinutes(-4),
            CompletedAt = now.AddMinutes(-3),
            FailureSince = now.AddMinutes(-2)
        });
        _client.Setup(c => c.RecheckTorrentsAsync(It.IsAny<List<string>>(), It.IsAny<CancellationToken>())).ReturnsAsync(true);
        (await Preview(MaintenanceOperation.ErrorRecovery)).Actions.Should().ContainSingle(a => a.Kind == MaintenanceActionKind.Recheck);
        (await MaintenanceRecovery.RequestAsync(_db, _client.Object, torrent, "ErrorRecovery", cfg, default)).Should().BeTrue();
        var state = MaintenanceRecovery.Read(_db, torrent, "ErrorRecovery");
        state.Attempts.Should().Be(2); state.Stage.Should().Be("requested");
        state.CheckingAt.Should().BeNull(); state.CompletedAt.Should().BeNull(); state.FailureSince.Should().BeNull();
        MaintenanceRecovery.Observe(_db, torrent, "ErrorRecovery", true, cfg, now.AddSeconds(1));
        MaintenanceRecovery.Read(_db, torrent, "ErrorRecovery").Stage.Should().Be("requested");
        (await Preview(MaintenanceOperation.ErrorRecovery)).Actions.Should().BeEmpty();
    }

    [Fact]
    public async Task OrphanScan_SharedCursorVisitsAllRootsInGlobalPathOrder()
    {
        var first = Path.Combine(_root, "a");
        var second = Path.Combine(_root, "a-before");
        var third = Path.Combine(_root, "z");
        foreach (var root in new[] { first, second, third }) Directory.CreateDirectory(root);
        ClientConfig.Maintenance.PathMappings = [new() { ClientPath = "/z", LocalPath = third },
            new() { ClientPath = "/a", LocalPath = first }, new() { ClientPath = "/b", LocalPath = second }];
        var cfg = ClientConfig.Maintenance.Orphans;
        cfg.Enabled = true; cfg.ScanRoots = [third, first, second]; cfg.MinimumAgeMinutes = 0; cfg.MaxInspectedFilesPerRun = 1;
        var expected = new[] { Path.Combine(first, "orphan"), Path.Combine(second, "orphan"), Path.Combine(third, "orphan") }.Order(StringComparer.Ordinal).ToList();
        foreach (var path in expected) File.WriteAllText(path, "data");
        var visited = new List<string>();
        for (var pass = 0; pass < 3; pass++)
        {
            var plan = await Preview(MaintenanceOperation.OrphanScan);
            visited.Add(plan.Actions.Single().Path!);
            foreach (var cursor in plan.InspectionCursors)
                MaintenancePersistence.Write(_db, cursor.Key, new ScanCursor { Position = cursor.Value });
        }
        visited.Should().Equal(expected);
    }

    [Fact]
    public async Task RecoveryInspection_ObservesUnregisteredCompletionWithoutProtectionPolicies()
    {
        var torrent = AddTorrent(); torrent.State = "error";
        var now = DateTimeOffset.UtcNow;
        MaintenancePersistence.Write(_db, MaintenancePersistence.Key("one", torrent.Hash, "recovery:UnregisteredCleanup"),
            new RecoveryIncident { AddedOn = torrent.AddedOn, Stage = "checking", Attempts = 1, RequestedAt = now.AddMinutes(-2), CheckingAt = now.AddMinutes(-1) });
        _client.Setup(c => c.GetTorrentTrackersAsync(torrent.Hash, It.IsAny<CancellationToken>()))
            .ReturnsAsync([new() { Url = "https://tracker.example/announce", Msg = "unregistered torrent" }]);
        using var automation = new TorrentAutomationService(_config, _registry.Object, _services.GetRequiredService<IServiceScopeFactory>(), NullLogger<TorrentAutomationService>.Instance);
        await Inspect(automation, default);
        var state = MaintenanceRecovery.Read(_db, torrent, "UnregisteredCleanup");
        state.Stage.Should().Be("recurring"); state.CompletedAt.Should().NotBeNull();
    }

    private static Task Inspect(TorrentAutomationService automation, CancellationToken ct)
        => (Task)typeof(TorrentAutomationService).GetMethod("InspectProtectionAsync", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.Invoke(automation, [ct])!;

    private static T AutomationField<T>(TorrentAutomationService automation, string name)
        => (T)typeof(TorrentAutomationService).GetField(name, System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.GetValue(automation)!;

    [Fact]
    public async Task TrackerIssues_ForRemovedTorrentsClearAfterACompleteClientInventory()
    {
        var torrent = AddTorrent();
        await _seeding.ObserveTrackerIssueAsync(torrent, [new() { Msg = "unregistered torrent" }], default);
        _torrents.Clear();
        using var automation = new TorrentAutomationService(_config, _registry.Object, _services.GetRequiredService<IServiceScopeFactory>(), NullLogger<TorrentAutomationService>.Instance);
        automation.GetStatus().TrackerIssues.Should().ContainSingle();
        await Inspect(automation, default);
        automation.GetStatus().TrackerIssues.Should().BeEmpty();
    }

    [Fact]
    public async Task TrackerCache_ReusesOriginalTimestampAndPrivacyThenExpires()
    {
        var torrent = AddTorrent(); torrent.IsPrivate = null; torrent.Ratio = 0;
        torrent.RatioLimit = -1; torrent.SeedingTimeLimit = -1;
        ClientConfig.Trackers = [new() { Uri = "tracker.example", HitAndRunMode = "or", MinSeedRatio = 1 }];
        _client.Setup(c => c.GetTorrentPropertiesAsync(torrent.Hash, It.IsAny<CancellationToken>())).ReturnsAsync(new TorrentPropertiesRecord { IsPrivate = true });
        _client.Setup(c => c.GetTorrentTrackersAsync(torrent.Hash, It.IsAny<CancellationToken>())).ReturnsAsync([new() { Url = "https://tracker.example/announce", Status = 2 }]);
        using var automation = new TorrentAutomationService(_config, _registry.Object, _services.GetRequiredService<IServiceScopeFactory>(), NullLogger<TorrentAutomationService>.Instance);
        await Inspect(automation, default);
        var cache = AutomationField<System.Collections.Concurrent.ConcurrentDictionary<string, (DateTimeOffset At, List<TorrentTracker> Trackers, bool? IsPrivate)>>(automation, "_trackerInspections");
        var key = "one|" + torrent.Hash;
        var original = cache[key] with { At = DateTimeOffset.UtcNow.AddMinutes(-10) };
        cache[key] = original;
        void AllowInspection()
        {
            AutomationField<System.Collections.Concurrent.ConcurrentDictionary<string, DateTimeOffset>>(automation, "_nextInspection").Clear();
            AutomationField<System.Collections.Concurrent.ConcurrentDictionary<string, DateTimeOffset>>(automation, "_nextFullInspection").Clear();
        }
        AllowInspection(); await Inspect(automation, default);
        cache[key].At.Should().Be(original.At);
        cache[key].IsPrivate.Should().BeTrue();
        _client.Verify(c => c.GetTorrentTrackersAsync(torrent.Hash, It.IsAny<CancellationToken>()), Times.Once);
        cache[key] = original with { At = DateTimeOffset.UtcNow.AddMinutes(-16) };
        AllowInspection(); await Inspect(automation, default);
        _client.Verify(c => c.GetTorrentTrackersAsync(torrent.Hash, It.IsAny<CancellationToken>()), Times.Exactly(2));
    }

    [Fact]
    public async Task UnknownTrackerEvidence_IsRetriedOnTheNextInspectionAndClearsRecoveredTrackerIssue()
    {
        var torrent = AddTorrent(); torrent.IsPrivate = true; torrent.Ratio = 0;
        torrent.RatioLimit = -1; torrent.SeedingTimeLimit = -1;
        ClientConfig.Trackers = [new() { Uri = "tracker.example", HitAndRunMode = "or", MinSeedRatio = 1 }];
        var status = 1;
        _client.Setup(c => c.GetTorrentTrackersAsync(torrent.Hash, It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => [new() { Url = "https://tracker.example/announce", Status = status }]);
        await _seeding.ObserveTrackerIssueAsync(torrent, [new() { Url = "https://tracker.example/announce", Status = 4, Msg = "unregistered torrent" }], default);
        using var automation = new TorrentAutomationService(_config, _registry.Object, _services.GetRequiredService<IServiceScopeFactory>(), NullLogger<TorrentAutomationService>.Instance);
        await Inspect(automation, default);
        automation.GetStatus().Protection.Should().ContainSingle().Which.State.Should().Be("unknown");
        automation.GetStatus().TrackerIssues.Should().ContainSingle();
        status = 2;
        AutomationField<System.Collections.Concurrent.ConcurrentDictionary<string, DateTimeOffset>>(automation, "_nextInspection").Clear();
        await Inspect(automation, default);
        automation.GetStatus().Protection.Should().ContainSingle().Which.State.Should().Be("protected");
        automation.GetStatus().TrackerIssues.Should().BeEmpty();
        _client.Verify(c => c.GetTorrentTrackersAsync(torrent.Hash, It.IsAny<CancellationToken>()), Times.Exactly(2));
    }

    [Theory]
    [InlineData("tracker.tleechreload.org", true)]
    [InlineData("tracker.tleechreload.org.attacker.example", false)]
    [InlineData("different.example", false)]
    public void TorrentLeechAlternateDomain_UsesTheConfiguredObligationsOnlyForTheKnownAlias(string alternate, bool alias)
    {
        var torrent = AddTorrent(); torrent.IsPrivate = true; torrent.Ratio = 0;
        ClientConfig.Trackers = [new() { Uri = "torrentleech.org", HitAndRunMode = "or", MinSeedRatio = 1 }];
        var trackers = new List<TorrentTracker> { new() { Url = "https://tracker.torrentleech.org/announce", Status = 2 }, new() { Url = "https://" + alternate + "/announce", Status = 1 } };
        var decisions = _seeding.GetProtectionDecisions(torrent, trackers);
        decisions.Count.Should().Be(alias ? 1 : 2);
        decisions.Should().Contain(d => d.State == "protected");
        _seeding.HnrAllowsDelete(torrent, trackers).Should().BeFalse();
        torrent.Ratio = 2;
        _seeding.HnrAllowsDelete(torrent, trackers).Should().Be(alias);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ScheduledPreview_ResolvesCoveredOldSelectionsButDraftsDoNot(bool draft)
    {
        var old = new Torrentarr.Infrastructure.Database.Models.OperationalIssue { Code = "maintenance.preview.orphan-prerequisite", ClientInstance = "one", SubjectType = "preview", SubjectId = "OrphanScan" };
        _db.OperationalIssues.Add(old); _db.SaveChanges();
        var reporter = new Mock<IOperationalIssueReporter>();
        await using var services = new ServiceCollection().AddSingleton(_db).AddSingleton(reporter.Object).BuildServiceProvider();
        var preview = new MaintenancePlan
        {
            IsDraft = draft,
            Operations = [MaintenanceOperation.OrphanScan, MaintenanceOperation.ErrorRecovery],
            OperationResults = [
            new() { ClientInstanceId = "one", Operation = MaintenanceOperation.OrphanScan },
            new() { ClientInstanceId = "one", Operation = MaintenanceOperation.ErrorRecovery, State = "blocked" }
        ]
        };
        await MaintenanceCoordinator.ObservePreviewIssuesAsync(preview, services, default);
        reporter.Verify(r => r.ResolveAsync(It.Is<OperationalIssueReport>(i => i.Code == old.Code && i.SubjectId == "OrphanScan"), It.IsAny<CancellationToken>()), draft ? Times.Never() : Times.Once());
    }

    [Fact]
    public async Task ProtectionInspection_PersistsCompletedSnapshotsBeforeSlowFetchEnds()
    {
        ClientConfig.Trackers = [new() { Uri = "https://tracker.example", Protection = new() { Policy = "onlyencodes" } }];
        var fast = AddTorrent("fast"); fast.IsPrivate = true; fast.State = "uploading";
        var slow = AddTorrent("slow", "other.mkv"); slow.IsPrivate = true; slow.State = "uploading";
        _client.Setup(c => c.GetTorrentTrackersAsync(fast.Hash, It.IsAny<CancellationToken>()))
            .ReturnsAsync([new() { Url = "https://tracker.example/announce", Status = 2 }]);
        _client.Setup(c => c.GetTorrentTrackersAsync(slow.Hash, It.IsAny<CancellationToken>()))
            .Returns(async (string _, CancellationToken ct) => { await Task.Delay(Timeout.Infinite, ct); return new List<TorrentTracker>(); });
        using var automation = new TorrentAutomationService(_config, _registry.Object, _services.GetRequiredService<IServiceScopeFactory>(), NullLogger<TorrentAutomationService>.Instance);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var inspection = Inspect(automation, cancellation.Token);
        var decisions = (System.Collections.Concurrent.ConcurrentDictionary<string, List<ProtectionDecision>>)typeof(TorrentAutomationService)
            .GetField("_protection", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.GetValue(automation)!;
        try
        {
            while (!decisions.Values.SelectMany(d => d).Any(d => d.TorrentHash == fast.Hash)) await Task.Delay(10, cancellation.Token);
            inspection.IsCompleted.Should().BeFalse();
            decisions.Values.SelectMany(d => d).Should().Contain(d => d.TorrentHash == fast.Hash && d.State == "protected");
        }
        finally
        {
            await cancellation.CancelAsync();
            await FluentActions.Awaiting(() => inspection).Should().ThrowAsync<OperationCanceledException>();
        }
    }

    [Fact]
    public async Task PausedRepair_CompletionReleasesClaimAndHealthyResetAllowsLaterRepair()
    {
        var torrent = AddTorrent(); torrent.State = "pausedDL"; torrent.Progress = 0.5;
        ClientConfig.Maintenance.Repair.Enabled = true;
        _client.Setup(c => c.RecheckTorrentsAsync(It.IsAny<List<string>>(), It.IsAny<CancellationToken>())).ReturnsAsync(true);
        (await Apply(await Preview(MaintenanceOperation.RepairPaused))).Applied.Should().Be(1);
        async Task ObserveAfterRestart()
        {
            using var automation = new TorrentAutomationService(_config, _registry.Object, _services.GetRequiredService<IServiceScopeFactory>(), NullLogger<TorrentAutomationService>.Instance);
            await Inspect(automation, default);
        }
        torrent.State = "checkingDL";
        await ObserveAfterRestart();
        MaintenanceRecovery.Read(_db, torrent, "RepairPaused").Stage.Should().Be("checking");
        torrent.State = "pausedDL";
        await ObserveAfterRestart();
        var state = MaintenanceRecovery.Read(_db, torrent, "RepairPaused");
        state.Stage.Should().Be("checked"); state.CompletedAt.Should().NotBeNull();
        var other = AddTorrent("other", "other.mkv"); other.State = "error";
        (await MaintenanceRecovery.RequestAsync(_db, _client.Object, other, "ErrorRecovery", new(), default)).Should().BeTrue();
        state.HealthySince = DateTimeOffset.UtcNow.AddMinutes(-ClientConfig.Maintenance.ErrorRecovery.HealthyResetMinutes - 1);
        MaintenancePersistence.Write(_db, MaintenancePersistence.Key("one", torrent.Hash, "recovery:RepairPaused"), state);
        await ObserveAfterRestart();
        MaintenanceRecovery.Read(_db, torrent, "RepairPaused").Stage.Should().Be("new");
    }

    [Fact]
    public async Task ProtectionInspection_RetainsAllPoliciesForTheSameTracker()
    {
        var torrent = AddTorrent(); torrent.IsPrivate = true; torrent.State = "uploading";
        ClientConfig.Trackers = [new() { Uri = "https://tracker.example", Protection = new() { Policy = "onlyencodes" } }];
        _config.ArrInstances["Radarr-Movies"] = new()
        {
            Category = "movies",
            Torrent = new()
            {
                Trackers = [new()
        {
            Uri = "https://tracker.example", Protection = new() { Policy = "custom", MinimumSeedHours = 0, AdditionalSeedHours = 0, AccountingGraceMinutes = 0, AllowClientRatioClearance = true }
        }]
            }
        };
        _client.Setup(c => c.GetTorrentTrackersAsync(torrent.Hash, It.IsAny<CancellationToken>()))
            .ReturnsAsync([new() { Url = "https://tracker.example/announce", Status = 2 }]);
        using var automation = new TorrentAutomationService(_config, _registry.Object, _services.GetRequiredService<IServiceScopeFactory>(), NullLogger<TorrentAutomationService>.Instance);
        await Inspect(automation, default);
        var status = automation.GetStatus();
        status.Protection.Should().HaveCount(2).And.Contain(d => d.State == "eligible").And.Contain(d => d.State == "protected" && d.DeadlineRisk != "none");
        status.Inspections.Single().NextAttempt.Should().BeBefore(DateTimeOffset.UtcNow.AddMinutes(2));
        var next = (System.Collections.Concurrent.ConcurrentDictionary<string, DateTimeOffset>)typeof(TorrentAutomationService)
            .GetField("_nextInspection", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.GetValue(automation)!;
        next.Clear();
        await Inspect(automation, default);
        automation.GetStatus().Protection.Should().HaveCount(2);
        _client.Verify(c => c.GetTorrentTrackersAsync(torrent.Hash, It.IsAny<CancellationToken>()), Times.AtLeast(2));
    }

    private sealed class AdjustableTimeProvider : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow() => Now;
    }

    [Theory]
    [InlineData("none")]
    [InlineData("category")]
    [InlineData("hnr")]
    [InlineData("ignored")]
    public async Task Recycling_OwnedPausePreservesIdleLimitButRevalidatesOtherChanges(string change)
    {
        var torrent = AddTorrent(); torrent.State = "stalledUP"; torrent.SeedingTime = 0;
        ClientConfig.CategorySeeding = new() { RemoveTorrent = 2, MaxSeedingTime = 60, StalledDelay = 0 };
        var time = new AdjustableTimeProvider();
        var idle = new StalledUploadTracker(time);
        var seeding = new SeedingService(NullLogger<SeedingService>.Instance, _db, _config, _registry.Object, idle);
        using var services = new ServiceCollection().AddSingleton(_db).AddScoped<ISeedingService>(_ => seeding).BuildServiceProvider();
        var deletion = new SafeDeletionService(_registry.Object, _inventory, new PathMappingService(), _config, NullLogger<SafeDeletionService>.Instance, services.GetRequiredService<IServiceScopeFactory>());
        (await seeding.ShouldRemoveTorrentAsync(torrent)).Should().BeFalse();
        time.Now = time.Now.AddMinutes(1);
        (await seeding.ShouldRemoveTorrentAsync(torrent)).Should().BeTrue();
        _client.Setup(c => c.PauseTorrentsAsync(It.IsAny<List<string>>(), It.IsAny<CancellationToken>())).ReturnsAsync(() =>
        {
            torrent.State = "pausedUP";
            if (change == "category") torrent.Category = "changed";
            if (change == "hnr") torrent.IsPrivate = true;
            if (change == "ignored") torrent.Tags = "qBitrr-ignored";
            return true;
        });
        var result = await deletion.RemoveMaintenanceTorrentAsync("one", torrent.Hash, true, await _inventory.BuildAsync());
        result.Success.Should().Be(change == "none", result.Error);
        File.Exists(Path.Combine(_root, "movie.mkv")).Should().Be(change != "none");
        _client.Verify(c => c.DeleteTorrentsAsync(It.IsAny<List<string>>(), false, It.IsAny<CancellationToken>()), change == "none" ? Times.Once() : Times.Never());
    }

    [Theory]
    [InlineData("added")]
    [InlineData("completion")]
    [InlineData("counter")]
    public async Task FinalRemovalBoundary_RejectsPersistedClearanceFromBeforeAReset(string change)
    {
        var torrent = AddTorrent(); torrent.IsPrivate = true; torrent.SeedingTime = 200000; torrent.CompletionOn = 1;
        var policy = new TrackerProtectionConfig { Policy = "onlyencodes" };
        ClientConfig.Trackers = [new() { Uri = "https://tracker.example", Protection = policy }];
        _client.Setup(c => c.GetTorrentTrackersAsync(torrent.Hash, It.IsAny<CancellationToken>()))
            .ReturnsAsync([new() { Url = "https://tracker.example/announce", Status = 2 }]);
        var fingerprint = JsonConvert.SerializeObject(policy);
        var policyId = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(fingerprint)));
        MaintenancePersistence.Write(_db, MaintenancePersistence.Key("one", torrent.Hash, "protection:tracker.example:" + policyId), new ProtectionObservation
        {
            AddedOn = torrent.AddedOn,
            CompletionOn = torrent.CompletionOn,
            ClientSeedSeconds = torrent.SeedingTime,
            SeedSeconds = torrent.SeedingTime,
            EligibleSince = DateTimeOffset.UtcNow.AddHours(-2),
            PolicyFingerprint = fingerprint
        });
        (await _seeding.CanDeleteTorrentAsync(torrent, "test")).Should().BeTrue();
        if (change == "added") torrent.AddedOn++;
        if (change == "completion") torrent.CompletionOn++;
        if (change == "counter") torrent.SeedingTime--;
        var result = await _deletion.RemoveMaintenanceTorrentAsync("one", torrent.Hash, true, await _inventory.BuildAsync());
        result.Success.Should().BeFalse(); File.Exists(Path.Combine(_root, "movie.mkv")).Should().BeTrue();
        _client.Verify(c => c.DeleteTorrentsAsync(It.IsAny<List<string>>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Theory]
    [InlineData(MaintenanceOperation.ErrorRecovery, 1)]
    [InlineData(MaintenanceOperation.UnregisteredCleanup, 0)]
    public async Task UnregisteredGraceDoesNotDelayOrdinaryErrorRecovery(MaintenanceOperation operation, int expected)
    {
        var torrent = AddTorrent(); torrent.State = "error"; torrent.AddedOn = DateTimeOffset.UtcNow.AddMinutes(-20).ToUnixTimeSeconds();
        ClientConfig.Maintenance.ErrorRecovery.Enabled = true;
        ClientConfig.Maintenance.Unregistered.Enabled = operation == MaintenanceOperation.UnregisteredCleanup;
        ClientConfig.Maintenance.Unregistered.GraceMinutes = 1440;
        _client.Setup(c => c.GetTorrentTrackersAsync(torrent.Hash, It.IsAny<CancellationToken>()))
            .ReturnsAsync([new() { Url = "https://tracker.example/announce", Msg = "unregistered torrent" }]);
        (await Preview(operation)).Actions.Should().HaveCount(expected);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public async Task UnregisteredCleanup_RepairAndContentMoveBudgetsAreIndependent(int moves)
    {
        ClientConfig.Maintenance.Unregistered.Enabled = true;
        ClientConfig.Maintenance.ErrorRecovery.MaxPerRun = 2;
        ClientConfig.Maintenance.RecycleBin.MaxTorrentMovesPerRun = moves;
        foreach (var hash in new[] { "recycle-one", "recycle-two", "repair-one", "repair-two" })
        {
            var torrent = AddTorrent(hash, hash + ".mkv");
            if (!hash.StartsWith("recycle")) continue;
            MaintenancePersistence.Write(_db, MaintenancePersistence.Key("one", hash, "recovery:UnregisteredCleanup"), new RecoveryIncident
            {
                AddedOn = torrent.AddedOn,
                Stage = "recurring",
                Attempts = 1,
                CompletedAt = DateTimeOffset.UtcNow.AddDays(-2),
                FailureSince = DateTimeOffset.UtcNow.AddDays(-2),
                LastObserved = DateTimeOffset.UtcNow
            });
        }
        _client.Setup(c => c.GetTorrentTrackersAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([new() { Url = "https://tracker.example/announce", Msg = "unregistered torrent" }]);
        var plan = await Preview(MaintenanceOperation.UnregisteredCleanup);
        plan.Actions.Count(a => a.Kind == MaintenanceActionKind.Recheck).Should().Be(2);
        plan.Actions.Count(a => a.Kind == MaintenanceActionKind.RecycleContent).Should().Be(moves);
    }

    [Fact]
    public async Task ProtectionInspectionRunsWithCleanupAndRecoveryDisabled()
    {
        ClientConfig.Maintenance.Enabled = false; ClientConfig.Maintenance.ErrorRecovery.Enabled = false;
        ClientConfig.Trackers = [new() { Uri = "https://tracker.example", Protection = new() { Policy = "onlyencodes" } }];
        var torrent = AddTorrent(); torrent.IsPrivate = true; torrent.State = "uploading";
        _client.Setup(c => c.GetTorrentTrackersAsync(torrent.Hash, It.IsAny<CancellationToken>()))
            .ReturnsAsync([new() { Url = "https://tracker.example/announce", Status = 2, Msg = "" }]);
        var inspected = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var reloader = new Mock<IConfigReloader>();
        reloader.Setup(r => r.Acknowledge("torrent-protection", It.IsAny<long>(), It.IsAny<string?>()))
            .Callback(() => inspected.TrySetResult(true));
        using var automation = new TorrentAutomationService(_config, _registry.Object, _services.GetRequiredService<IServiceScopeFactory>(), NullLogger<TorrentAutomationService>.Instance, reloader.Object);
        await automation.StartAsync(default);
        try
        {
            // The fixture shares a DbContext; read status only after the background inspection completes.
            await inspected.Task.WaitAsync(TimeSpan.FromSeconds(10));
            automation.GetStatus().Inspections.Single().LastSucceededAt.Should().NotBeNull();
            automation.GetStatus().Protection.Should().Contain(p => p.TorrentHash == torrent.Hash && p.State == "protected");
        }
        finally { await automation.StopAsync(default); }
    }

    [Fact]
    public async Task ZeroOrphanBudgetDoesNotScanOrQueryOwners()
    {
        ClientConfig.Maintenance.Orphans.Enabled = true; ClientConfig.Maintenance.Orphans.MaxFilesPerRun = 0;
        var plan = await Preview(MaintenanceOperation.OrphanScan);
        plan.Actions.Should().BeEmpty(); plan.OperationResults.Single().State.Should().Be("ready");
        _client.Verify(c => c.GetTorrentsAsync(It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task PendingRecycleSourcesAreNotOrphanCandidates()
    {
        ClientConfig.Maintenance.Orphans.Enabled = true; ClientConfig.Maintenance.Orphans.MinimumAgeMinutes = 0;
        var source = Path.Combine(_root, "restore-needed"); File.WriteAllText(source, "keep");
        var destination = Path.Combine(_root, ".recycle", "one", "pending");
        var key = RecycleLedger.Key("one", destination);
        MaintenancePersistence.Write(_db, key, new RecycleEntry(key, "one", source, destination, DateTimeOffset.UtcNow, "needs-recovery"));
        (await Preview(MaintenanceOperation.OrphanScan)).Actions.Should().BeEmpty();
        File.Exists(source).Should().BeTrue();
    }

    [Fact]
    public void ArrProtectionFollowsSubcategoryOwnership()
    {
        ClientConfig.MatchSubcategories = true;
        _config.ArrInstances["Radarr-Movies"] = new() { Category = "movies", Torrent = new() { Trackers = [new() { Uri = "https://tracker.example", Protection = new() { Policy = "onlyencodes" } }] } };
        var torrent = AddTorrent(); torrent.Category = "movies/4k"; torrent.IsPrivate = true;
        _seeding.GetProtectionDecisions(torrent, [new() { Url = "https://tracker.example/announce", Status = 2, Msg = "" }])
            .Should().Contain(d => d.State == "protected");
    }

    [Fact]
    public async Task DailyCleanupClaimsOnceAcrossRestart_AndDoesNotCatchUpMissedRuns()
    {
        ClientConfig.Maintenance.Enabled = true; ClientConfig.Maintenance.Operations["OrphanScan"] = new() { Enabled = true, ExecutionMode = "automatic", Schedule = "15 3 * * *" }; ClientConfig.Maintenance.Operations["RecycleRetention"] = new() { Enabled = false }; ClientConfig.Maintenance.Orphans.Enabled = true; ClientConfig.Maintenance.Orphans.MinimumAgeMinutes = 0;
        var source = Path.Combine(_root, "daily-orphan"); File.WriteAllText(source, "data");
        var day = new DateTimeOffset(DateTime.UtcNow.Date.AddHours(3).AddMinutes(15), TimeSpan.Zero);
        await _maintenance.RunDueAsync(day, expectedRevision: _config.Revision + 1);
        File.Exists(source).Should().BeTrue(); _maintenance.GetHistory().Should().BeEmpty();
        await _maintenance.RunDueAsync(day.AddMinutes(1));
        File.Exists(source).Should().BeTrue(); _maintenance.GetHistory().Should().BeEmpty();
        await _maintenance.RunDueAsync(day);
        File.Exists(source).Should().BeFalse(); _maintenance.GetHistory().Single().Trigger.Should().Be("scheduled");
        using var restarted = new MaintenanceCoordinator(_config, _registry.Object, _inventory, new PathMappingService(), Mock.Of<IHardlinkInspector>(),
            _deletion, NullLogger<MaintenanceCoordinator>.Instance, _services.GetRequiredService<IServiceScopeFactory>());
        await restarted.RunDueAsync(day);
        restarted.GetHistory().Should().ContainSingle();
    }

    [Fact]
    public async Task RecoveryBlocksRemoval_WithoutInventingTrackerObligations()
    {
        ClientConfig.CategorySeeding.RemoveTorrent = 1; ClientConfig.CategorySeeding.MaxUploadRatio = 1;
        var torrent = AddTorrent();
        MaintenancePersistence.Write(_db, MaintenancePersistence.Key("one", torrent.Hash, "recovery:ErrorRecovery"), new RecoveryIncident { AddedOn = torrent.AddedOn, Attempts = 1, Stage = "unconfirmed" });
        (await _seeding.ShouldRemoveTorrentAsync(torrent)).Should().BeFalse();
        (await _seeding.HnrAllowsDeleteAsync(torrent, "routine pause")).Should().BeTrue();
    }

    [Theory]
    [InlineData(MaintenanceOperation.OrphanScan)]
    [InlineData(MaintenanceOperation.HardlinkAudit)]
    [InlineData(MaintenanceOperation.UnregisteredCleanup)]
    [InlineData(MaintenanceOperation.CategoryReconcile)]
    [InlineData(MaintenanceOperation.AutomaticManagement)]
    [InlineData(MaintenanceOperation.PrivateTagging)]
    [InlineData(MaintenanceOperation.TrackerErrorTagging)]
    [InlineData(MaintenanceOperation.RepairPaused)]
    [InlineData(MaintenanceOperation.ErrorRecovery)]
    [InlineData(MaintenanceOperation.RecycleRetention)]
    public async Task EveryRestoredToolReachesItsPlanner(MaintenanceOperation operation)
    {
        ClientConfig.Maintenance.Operations[operation.ToString()] = new() { Enabled = true, ExecutionMode = "manual" };
        var plan = await Preview(operation);
        plan.CriticalErrors.Should().BeEmpty();
        plan.OperationResults.Should().ContainSingle(result => result.Operation == operation && result.State != "disabled");
    }

    [Theory]
    [InlineData(MaintenanceOperation.CategoryReconcile, MaintenanceActionKind.SetCategory)]
    [InlineData(MaintenanceOperation.AutomaticManagement, MaintenanceActionKind.SetAutomaticManagement)]
    [InlineData(MaintenanceOperation.PrivateTagging, MaintenanceActionKind.AddTag)]
    [InlineData(MaintenanceOperation.TrackerErrorTagging, MaintenanceActionKind.AddTag)]
    public async Task RestoredOrganizationToolsApplyOnlyAfterReview(MaintenanceOperation operation, MaintenanceActionKind kind)
    {
        var torrent = AddTorrent(); torrent.IsPrivate = operation == MaintenanceOperation.PrivateTagging; torrent.AutoManaged = false;
        ClientConfig.CategorySeeding.HitAndRunMode = "disabled";
        ClientConfig.Maintenance.Operations[operation.ToString()] = new() { Enabled = true, ExecutionMode = "manual" };
        ClientConfig.Maintenance.CategoryMappings = [new() { ClientPath = "/downloads", Category = "seed" }];
        ClientConfig.Maintenance.AutomaticManagement.PrivateTag = "private";
        _client.Setup(c => c.GetTorrentTrackersAsync(torrent.Hash, It.IsAny<CancellationToken>())).ReturnsAsync([new() { Url = "https://tracker.example", Status = 0, Msg = "offline" }]);
        _client.Setup(c => c.SetCategoryAsync(It.IsAny<List<string>>(), "seed", It.IsAny<CancellationToken>())).ReturnsAsync(true);
        _client.Setup(c => c.SetAutomaticManagementAsync(It.IsAny<List<string>>(), true, It.IsAny<CancellationToken>())).ReturnsAsync(true);
        _client.Setup(c => c.AddTagsAsync(It.IsAny<List<string>>(), It.IsAny<List<string>>(), It.IsAny<CancellationToken>())).ReturnsAsync(true);
        var plan = await Preview(operation);
        plan.Actions.Should().ContainSingle(action => action.Kind == kind && action.BlockedReason == null);
        _client.Verify(c => c.AddTagsAsync(It.IsAny<List<string>>(), It.IsAny<List<string>>(), It.IsAny<CancellationToken>()), Times.Never);
        (await Apply(plan)).Applied.Should().Be(1);
        _maintenance.GetStatus().Armed["one"].Should().BeFalse();
    }

    [Fact]
    public async Task SwitchingToManualStopsFurtherAutomaticActionsAndInvalidatesPreview()
    {
        AddTorrent("first", "first.mkv").IsPrivate = true;
        AddTorrent("second", "second.mkv").IsPrivate = true;
        ClientConfig.CategorySeeding.HitAndRunMode = "disabled";
        ClientConfig.Maintenance.AutomaticManagement.PrivateTag = "private";
        var policy = new MaintenanceOperationPolicy { Enabled = true, ExecutionMode = "automatic" };
        ClientConfig.Maintenance.Operations["PrivateTagging"] = policy;
        var preview = await Preview(MaintenanceOperation.PrivateTagging);
        preview.Actions.Should().HaveCount(2);
        _client.Setup(c => c.AddTagsAsync(It.IsAny<List<string>>(), It.IsAny<List<string>>(), It.IsAny<CancellationToken>()))
            .Callback(() => policy.ExecutionMode = "manual").ReturnsAsync(true);
        var run = await _maintenance.RunAsync(["one"], operations: [MaintenanceOperation.PrivateTagging]);
        run.Applied.Should().Be(1);
        _client.Verify(c => c.AddTagsAsync(It.IsAny<List<string>>(), It.IsAny<List<string>>(), It.IsAny<CancellationToken>()), Times.Once);
        await FluentActions.Awaiting(() => Apply(preview)).Should().ThrowAsync<InvalidOperationException>();
    }

    [Theory]
    [InlineData("manual", false)]
    [InlineData("automatic", true)]
    public async Task SchedulingModePersistsAcrossRestartAndManualCannotRun(string mode, bool automatic)
    {
        ClientConfig.Maintenance.Operations["PrivateTagging"] = new() { Enabled = true, ExecutionMode = mode };
        var operation = _maintenance.GetStatus().Operations.Single(o => o.ClientInstanceId == "one" && o.Operation == MaintenanceOperation.PrivateTagging);
        operation.Authorized.Should().Be(automatic);
        (operation.NextRun != null).Should().Be(automatic);
        using var restarted = new MaintenanceCoordinator(_config, _registry.Object, _inventory, new PathMappingService(), Mock.Of<IHardlinkInspector>(),
            _deletion, NullLogger<MaintenanceCoordinator>.Instance, _services.GetRequiredService<IServiceScopeFactory>());
        restarted.GetStatus().Operations.Single(o => o.ClientInstanceId == "one" && o.Operation == MaintenanceOperation.PrivateTagging).Authorized.Should().Be(automatic);
        restarted.GetStatus().Armed.Values.Should().OnlyContain(value => !value);
        restarted.GetStatus().ArmedOperations.Values.Should().OnlyContain(value => value.Count == 0);
        if (!automatic)
            await FluentActions.Awaiting(() => restarted.RunAsync(["one"], operations: [MaintenanceOperation.PrivateTagging])).Should().ThrowAsync<InvalidOperationException>();
        ClientConfig.Maintenance.Operations["PrivateTagging"].ExecutionMode = "manual";
        restarted.GetStatus().Operations.Single(o => o.ClientInstanceId == "one" && o.Operation == MaintenanceOperation.PrivateTagging).Authorized.Should().BeFalse();
        await restarted.RunDueAsync(DateTimeOffset.UtcNow);
        _client.Verify(c => c.AddTagsAsync(It.IsAny<List<string>>(), It.IsAny<List<string>>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SafeRemovalWithoutProtectionServiceNeverDeletes(bool content)
    {
        AddTorrent();
        var deletion = new SafeDeletionService(_registry.Object, _inventory, new PathMappingService(), _config, NullLogger<SafeDeletionService>.Instance);
        (await deletion.RemoveTorrentAsync("one", "abc", content)).Success.Should().BeFalse();
        _client.Verify(c => c.DeleteTorrentsAsync(It.IsAny<List<string>>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()), Times.Never);
        File.Exists(Path.Combine(_root, "movie.mkv")).Should().BeTrue();
    }

    [Fact]
    public async Task ClearanceLostAfterRecyclingRestoresFilesAndPreservesTorrent()
    {
        AddTorrent();
        var seeding = new Mock<ISeedingService>();
        seeding.SetupSequence(s => s.CanDeleteTorrentAsync(It.IsAny<TorrentInfo>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true).ReturnsAsync(true).ReturnsAsync(true).ReturnsAsync(false);
        using var services = new ServiceCollection().AddSingleton(_db).AddScoped<ISeedingService>(_ => seeding.Object).BuildServiceProvider();
        var deletion = new SafeDeletionService(_registry.Object, _inventory, new PathMappingService(), _config,
            NullLogger<SafeDeletionService>.Instance, services.GetRequiredService<IServiceScopeFactory>());
        var result = await deletion.RemoveTorrentAsync("one", "abc", true);
        result.Success.Should().BeFalse();
        result.ContentPreserved.Should().BeTrue();
        File.Exists(Path.Combine(_root, "movie.mkv")).Should().BeTrue();
        _client.Verify(c => c.DeleteTorrentsAsync(It.IsAny<List<string>>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Theory]
    [InlineData(false, false, false, true)]
    [InlineData(true, false, false, false)]
    [InlineData(false, true, false, false)]
    [InlineData(false, false, true, false)]
    public async Task OwnedFileCleanup_UsesAllowlistIntentAndRetainsProtection(bool recovery, bool protectedOwner, bool unsafePath, bool allowed)
    {
        var torrent = AddTorrent();
        torrent.Ratio = 0;
        torrent.SeedingTime = 10;
        ClientConfig.CategorySeeding.MaxSeedingTime = 1209600;
        ClientConfig.CategorySeeding.RemoveTorrent = 2;
        var path = Path.Combine(_root, "movie.mkv");
        if (protectedOwner)
        {
            var shared = AddTorrent("shared");
            shared.State = "forcedUP";
        }
        if (recovery)
            MaintenancePersistence.Write(_db, MaintenancePersistence.Key("one", "abc", "recovery:ErrorRecovery"),
                new RecoveryIncident { AddedOn = torrent.AddedOn, Stage = "requested", Attempts = 1 });
        if (unsafePath) path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".mkv");
        (await _deletion.DeleteOwnedFileAsync("one", "abc", path)).Should().Be(allowed);
        File.Exists(Path.Combine(_root, "movie.mkv")).Should().Be(!allowed);
        _client.Verify(c => c.DeleteTorrentsAsync(It.IsAny<List<string>>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OwnedFileCleanupRequiresHnrClearance(bool allowed)
    {
        var torrent = AddTorrent(); torrent.IsPrivate = true; torrent.Ratio = allowed ? 1 : .000249; torrent.Progress = .5416; torrent.LastActivity = 1;
        ClientConfig.Trackers = [new() { Uri = "https://tracker.example", HitAndRunMode = "and", MinSeedRatio = 1, HitAndRunPartialSeedRatio = 1 }];
        _client.Setup(c => c.GetTorrentTrackersAsync("abc", It.IsAny<CancellationToken>())).ReturnsAsync([new() { Url = "https://tracker.example/announce", Status = 2 }]);
        var path = Path.Combine(_root, "movie.mkv");
        (await _deletion.DeleteOwnedFileAsync("one", "abc", path)).Should().Be(allowed);
        File.Exists(path).Should().Be(!allowed);
    }

    [Theory]
    [InlineData(-1, 2, false)]
    [InlineData(1, .5, false)]
    [InlineData(1, 2, true)]
    public async Task EveryRemovalRequiresSeedingPolicy(int mode, double ratio, bool allowed)
    {
        var torrent = AddTorrent(); torrent.Ratio = ratio;
        ClientConfig.CategorySeeding.RemoveTorrent = mode;
        (await _seeding.CanDeleteTorrentAsync(torrent, "CF unmet")).Should().Be(allowed);
        (await _deletion.RemoveTorrentAsync("one", "abc", false)).Success.Should().Be(allowed);
        _client.Verify(c => c.DeleteTorrentsAsync(It.IsAny<List<string>>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()), allowed ? Times.Once() : Times.Never());
    }

    [Theory]
    [InlineData(-1, 7200, false)]
    [InlineData(60, 1800, false)]
    [InlineData(60, 7200, true)]
    public async Task EveryIncompleteRemovalRequiresStalledGrace(int delay, int idleSeconds, bool allowed)
    {
        var torrent = AddTorrent(); torrent.Progress = .5; torrent.State = "stalledDL";
        torrent.LastActivity = DateTimeOffset.UtcNow.ToUnixTimeSeconds() - idleSeconds;
        ClientConfig.CategorySeeding.StalledDelay = delay;
        (await _deletion.RemoveTorrentAsync("one", "abc", false)).Success.Should().Be(allowed);
    }

    [Theory]
    [InlineData(1, .5, 0, false)]
    [InlineData(1, 2, 0, true)]
    [InlineData(2, 2, 0, false)]
    [InlineData(2, .5, 7200, true)]
    [InlineData(3, 2, 0, true)]
    [InlineData(4, 2, 0, false)]
    [InlineData(4, 2, 7200, true)]
    public async Task FinalRemovalPreservesConfiguredRatioTimeCombination(int mode, double ratio, long seedSeconds, bool allowed)
    {
        var torrent = AddTorrent(); torrent.Ratio = ratio; torrent.SeedingTime = seedSeconds;
        ClientConfig.CategorySeeding.RemoveTorrent = mode;
        ClientConfig.CategorySeeding.MaxSeedingTime = 3600;
        (await _seeding.CanDeleteTorrentAsync(torrent, "cleanup")).Should().Be(allowed);
    }

    [Fact]
    public async Task LegacyTrackerMinimumsRemainMandatoryForCleanup()
    {
        var torrent = AddTorrent(); torrent.Tracker = "https://tracker.example/announce";
        _config.Settings.TrackerRules = [new() { TrackerUrl = "tracker.example", MinimumRatio = 3, MinimumSeedingTime = 120 }];
        (await _seeding.CanDeleteTorrentAsync(torrent, "cleanup")).Should().BeFalse();
        torrent.Ratio = 3; torrent.SeedingTime = 7200;
        (await _seeding.CanDeleteTorrentAsync(torrent, "cleanup")).Should().BeTrue();
    }

    [Fact]
    public async Task EffectiveCategoryAgeBlocksOtherwiseClearedCleanup()
    {
        var torrent = AddTorrent(); torrent.AddedOn = DateTimeOffset.UtcNow.ToUnixTimeSeconds() - 300;
        ClientConfig.CategorySeeding.Categories = [new() { Name = "movies", IgnoreTorrentsYoungerThan = 3600 }];
        (await _deletion.RemoveTorrentAsync("one", "abc", false)).Success.Should().BeFalse();
        _client.Verify(c => c.DeleteTorrentsAsync(It.IsAny<List<string>>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task ArrAgeCannotBypassLongerCategoryProtection()
    {
        var torrent = AddTorrent(); torrent.AddedOn = DateTimeOffset.UtcNow.ToUnixTimeSeconds() - 300;
        ClientConfig.CategorySeeding.IgnoreTorrentsYoungerThan = 3600;
        _config.ArrInstances["Radarr-Movies"] = new() { Category = "movies", Torrent = new() { IgnoreTorrentsYoungerThan = 180 } };
        (await _seeding.CanDeleteTorrentAsync(torrent, "CF unmet")).Should().BeFalse();
    }

    [Fact]
    public async Task CandidateActivityChangesDuringInventoryBlockRemoval()
    {
        var torrent = AddTorrent();
        var inventory = await _inventory.BuildAsync();
        torrent.LastActivity = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        (await _deletion.RemoveMaintenanceTorrentAsync("one", "abc", true, inventory)).Success.Should().BeFalse();
        File.Exists(Path.Combine(_root, "movie.mkv")).Should().BeTrue();
        _client.Verify(c => c.DeleteTorrentsAsync(It.IsAny<List<string>>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public void PublishedConfigurationAndInvalidAccountingCannotClearHnr()
    {
        var torrent = AddTorrent();
        _seeding.HnrAllowsDelete(torrent, []).Should().BeTrue();
        using (_config.BeginOperation())
        {
            _config.Apply(_config, _config.LatestRevision + 1);
            _seeding.HnrAllowsDelete(torrent, []).Should().BeFalse();
        }
        torrent.Ratio = double.NaN;
        _seeding.HnrAllowsDelete(torrent, []).Should().BeFalse();
    }

    public void Dispose()
    {
        _maintenance.Dispose(); _services.Dispose(); _db.Dispose();
        Environment.SetEnvironmentVariable("TORRENTARR_OVERRIDES_DATA_PATH", _previousDataDirectory);
        Directory.Delete(_root, true);
    }
}

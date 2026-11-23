using System.Net;
using System.Text;
using System.Collections.Concurrent;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Newtonsoft.Json.Linq;
using Torrentarr.Core.Configuration;
using Torrentarr.Core.Interfaces;
using Torrentarr.Core.Models;
using Torrentarr.Core.Services;
using Torrentarr.Infrastructure.Database;
using Torrentarr.Infrastructure.Services;
using Xunit;

namespace Torrentarr.Infrastructure.Tests.Services;

public sealed class ArrImportRescueTests : IAsyncDisposable
{
    // macOS can expose the system temporary directory through a symlink.
    private readonly string _directory = Path.Combine(AppContext.BaseDirectory, "rescue-" + Guid.NewGuid().ToString("N"));
    private readonly HttpListener _listener;
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _server;
    private readonly TorrentarrConfig _config = new();
    private readonly Mock<ITorrentClient> _client = new();
    private readonly Mock<ITorrentClientRegistry> _registry = new();
    private readonly Mock<IOperationalIssueReporter> _issues = new();
    private readonly TestClock _clock = new();
    private readonly TorrentarrDbContext _db;
    private readonly ConcurrentQueue<JObject> _commands = new();
    private readonly TorrentInfo _torrent;
    private readonly List<TorrentFileRecord> _files;
    private JArray _queue;
    private JArray _preview;
    private JArray _history = [];
    private JArray _commandInventory = [new JObject { ["id"] = 100, ["name"] = "ManualImport", ["status"] = "completed" }];
    private int _commandStatus = 201;
    private int _queueDeleteStatus = 200;
    private readonly ConcurrentQueue<(string? RemoveClient, string? Blocklist, string? SkipRedownload)> _queueDeletes = new();
    private TimeSpan _commandDelay;
    private readonly string _warning = ArrImportService.RadarrIdMatchWarning;

    public ArrImportRescueTests()
    {
        Directory.CreateDirectory(Path.Combine(_directory, "Movie"));
        File.WriteAllText(Path.Combine(_directory, "Movie", "video.mkv"), "data");
        _listener = LoopbackHttpListener.Start(out var port);
        var url = $"http://127.0.0.1:{port}";
        _server = ServeAsync();
        _config.TorrentClients["fixture"] = new()
        {
            Host = "fixture",
            Maintenance = new()
            { PathMappings = [new() { ClientPath = "/downloads", LocalPath = _directory }] }
        };
        _config.ArrInstances["Radarr-Movies"] = new()
        {
            Type = "radarr",
            Category = "movies",
            URI = url,
            APIKey = "fixture-secret",
            Torrent = new() { RescueBlockedImports = true }
        };
        _torrent = new()
        {
            Hash = "download",
            ClientInstanceId = "fixture",
            Name = "Movie",
            Category = "movies",
            AddedOn = 1,
            CompletionOn = _clock.Now.ToUnixTimeSeconds() - 120,
            Progress = 1,
            AmountLeft = 0,
            State = "uploading",
            SavePath = "/downloads",
            ContentPath = "/downloads/Movie"
        };
        _files = [new() { Index = 0, Name = "Movie/video.mkv", Size = 4, Progress = 1, Priority = 1 }];
        _client.Setup(client => client.GetTorrentsAsync(It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => [_torrent]);
        _client.Setup(client => client.GetTorrentFilesAsync(_torrent.Hash, It.IsAny<CancellationToken>())).ReturnsAsync(() => _files);
        _registry.Setup(registry => registry.GetClient("fixture")).Returns(_client.Object);
        _registry.Setup(registry => registry.GetAllClients()).Returns(new Dictionary<string, ITorrentClient> { ["fixture"] = _client.Object });
        _queue = [new JObject { ["id"] = 7, ["movieId"] = 10, ["downloadId"] = _torrent.Hash, ["status"] = "completed",
            ["trackedDownloadStatus"] = "warning", ["trackedDownloadState"] = "importBlocked", ["sizeleft"] = 0,
            ["outputPath"] = _torrent.ContentPath, ["statusMessages"] = new JArray(new JObject { ["title"] = _warning, ["messages"] = new JArray() }) }];
        _preview = [Candidate("/downloads/Movie/video.mkv", 10, 4)];
        _db = OpenDb();
        _db.Database.EnsureCreated();
    }

    private bool HasPendingReceipt(TorrentarrDbContext db)
        => MaintenancePersistence.Read<ImportRescueReceipt>(db, ImportRescueReceipt.Key(_torrent)).Stage == "pending";

    private TorrentarrDbContext OpenDb() => new(new DbContextOptionsBuilder<TorrentarrDbContext>()
        .UseSqlite(new SqliteConnectionStringBuilder
        { DataSource = Path.Combine(_directory, "rescue.db"), Pooling = false }.ToString()).Options);
    private ArrImportService Service(TorrentarrDbContext? db = null, SharedFileFilterService? filters = null) => new(
        NullLogger<ArrImportService>.Instance, _config, _registry.Object, db ?? _db,
        issues: _issues.Object, sharedFilters: filters, clock: _clock);
    private JObject Candidate(string path, int movie, long size) => new()
    {
        ["path"] = path,
        ["movie"] = new JObject { ["id"] = movie },
        ["downloadId"] = _torrent.Hash,
        ["size"] = size,
        ["movieFileId"] = null,
        ["quality"] = new JObject
        {
            ["quality"] = new JObject { ["id"] = 4 },
            ["revision"] = new JObject { ["version"] = 2, ["real"] = 0, ["isRepack"] = false }
        },
        ["languages"] = new JArray(new JObject { ["id"] = 1 }),
        ["rejections"] = new JArray(),
        ["releaseGroup"] = "fixture"
    };

    [Fact]
    public async Task ConcurrentObserversShareOneDurableClaim()
    {
        using var secondDb = OpenDb();
        var outcomes = await Task.WhenAll(Service().TryRescueImportAsync(_torrent, "movies"),
            Service(secondDb).TryRescueImportAsync(_torrent, "movies"));
        outcomes.Should().OnlyContain(outcome => outcome == ImportRescueOutcome.Pending);
        _commands.Should().ContainSingle();
        HasPendingReceipt(secondDb).Should().BeTrue();
    }

    [Fact]
    public async Task OneMatchCopiesOnceAndOnlyCorrelatedHistoryConfirmsAcrossRestart()
    {
        (await Service().TryRescueImportAsync(_torrent, "movies")).Should().Be(ImportRescueOutcome.Pending);
        _commands.Should().ContainSingle();
        var command = _commands.Single();
        command["name"]!.Value<string>().Should().Be("ManualImport");
        command["importMode"]!.Value<string>().Should().Be("Copy");
        command["files"]![0]!["movieId"]!.Value<int>().Should().Be(10);
        command["files"]![0]!["quality"]!["revision"]!["version"]!.Value<int>().Should().Be(2);
        HasPendingReceipt(_db).Should().BeTrue();
        _queue = [];
        using var restartedDb = OpenDb();
        var restarted = Service(restartedDb);
        (await restarted.TryRescueImportAsync(_torrent, "movies")).Should().Be(ImportRescueOutcome.Pending);
        _history = [new JObject { ["id"] = 99, ["movieId"] = 20, ["downloadId"] = _torrent.Hash,
            ["eventType"] = "downloadFolderImported", ["date"] = _clock.Now.ToString("O") }];
        (await restarted.TryRescueImportAsync(_torrent, "movies")).Should().Be(ImportRescueOutcome.Pending);
        _history[0]!["movieId"] = 10;
        (await restarted.TryRescueImportAsync(_torrent, "movies")).Should().Be(ImportRescueOutcome.Confirmed);
        HasPendingReceipt(restartedDb).Should().BeFalse();
        _commands.Should().ContainSingle();
        _client.Verify(client => client.DeleteTorrentsAsync(It.IsAny<List<string>>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Theory]
    [InlineData(true, false, false)]
    [InlineData(false, false, false)]
    [InlineData(true, true, false)]
    [InlineData(true, false, true)]
    public async Task BackgroundReconciliationRequiresHistoryAndTerminalCommandWithoutReaddedDownload(
        bool imported, bool activeCommand, bool readded)
    {
        var service = Service();
        await service.TryRescueImportAsync(_torrent, "movies");
        _db.TorrentLibrary.Add(new()
        {
            Hash = _torrent.Hash,
            QbitInstance = "fixture",
            Category = "movies",
            ArrInstance = "Radarr-Movies",
            TorrentAddedOn = readded ? 2 : 1,
            ImportSubmittedAt = _clock.Now.ToUnixTimeSeconds()
        });
        await _db.SaveChangesAsync();
        _queue = [];
        if (imported) _history = [new JObject { ["id"] = 99, ["movieId"] = 10, ["downloadId"] = _torrent.Hash,
            ["eventType"] = "downloadFolderImported", ["date"] = _clock.Now.ToString("O") }];
        if (activeCommand) _commandInventory[0]!["status"] = "started";
        _client.Setup(client => client.GetTorrentsAsync(It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => readded ? [new TorrentInfo { Hash = _torrent.Hash, AddedOn = 2 }] : []);
        _config.ArrInstances["Radarr-Movies"].Torrent.RescueBlockedImports = false;

        AddArrSharingServer();
        await service.ReconcileImportRescuesAsync("other");
        HasPendingReceipt(_db).Should().BeTrue();
        await service.ReconcileImportRescuesAsync("movies");

        _db.ChangeTracker.Clear();
        var confirmed = imported && !activeCommand && !readded;
        (await _db.TorrentLibrary.SingleAsync()).Imported.Should().Be(confirmed);
        HasPendingReceipt(_db).Should().Be(!confirmed);
        _commands.Should().ContainSingle();
    }

    [Fact]
    public async Task FailedSubmissionAndRestartDoNotRepeatAnUncertainAttempt()
    {
        _commandStatus = 500;
        (await Service().TryRescueImportAsync(_torrent, "movies")).Should().Be(ImportRescueOutcome.Pending);
        using var restartedDb = OpenDb();
        _config.ArrInstances["Radarr-Movies"].Torrent.RescueBlockedImports = false;
        (await Service(restartedDb).TryRescueImportAsync(_torrent, "movies")).Should().Be(ImportRescueOutcome.Pending);
        _clock.Now = _clock.Now.AddMinutes(16);
        (await Service(restartedDb).TryRescueImportAsync(_torrent, "movies")).Should().Be(ImportRescueOutcome.Unconfirmed);
        _config.ArrInstances["Radarr-Movies"].Torrent.RescueBlockedImports = true;
        (await Service(restartedDb).TryRescueImportAsync(_torrent, "movies")).Should().Be(ImportRescueOutcome.Unconfirmed);
        _commands.Should().ContainSingle();
    }

    [Fact]
    public async Task SubmissionTimeoutRetainsClaimAndCannotSubmitTwice()
    {
        _commandDelay = TimeSpan.FromSeconds(18);
        (await Service().TryRescueImportAsync(_torrent, "movies")).Should().Be(ImportRescueOutcome.Pending);
        HasPendingReceipt(_db).Should().BeTrue();
        using var restartedDb = OpenDb();
        (await Service(restartedDb).TryRescueImportAsync(_torrent, "movies")).Should().Be(ImportRescueOutcome.Pending);
        _commands.Should().ContainSingle();
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("not-json")]
    public async Task UnreadableReceiptCannotLoseItsClaimOrAuthorizeRemoval(string data)
    {
        var key = ImportRescueReceipt.Key(_torrent);
        _db.MaintenanceState.Add(new() { Key = key, Data = data });
        await _db.SaveChangesAsync();
        (await Service().TryRescueImportAsync(_torrent, "movies")).Should().Be(ImportRescueOutcome.Pending);
        ImportRescueReceipt.BlocksRemoval(_db, _torrent, TorrentRemovalIntent.OrdinaryRetirement).Should().BeTrue();
        _commands.Should().BeEmpty();
        (await _db.MaintenanceState.AsNoTracking().SingleAsync()).Data.Should().Be(data);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ANewFailureIncidentRequiresAnObservedClearBeforeAnotherAttempt(bool mapped)
    {
        if (mapped)
        {
            _queue[0]!["outputPath"] = Path.Combine(_directory, "Movie").Replace('\\', '/');
            _preview[0]!["path"] = Path.Combine(_directory, "Movie", "video.mkv").Replace('\\', '/');
        }
        _commandStatus = 500;
        var service = Service();
        (await service.TryRescueImportAsync(_torrent, "movies")).Should().Be(ImportRescueOutcome.Pending);
        _clock.Now = _clock.Now.AddMinutes(16);
        (await service.TryRescueImportAsync(_torrent, "movies")).Should().Be(ImportRescueOutcome.Unconfirmed);
        _queue[0]!["id"] = 8;
        (await service.TryRescueImportAsync(_torrent, "movies")).Should().Be(ImportRescueOutcome.Unconfirmed);
        _commands.Should().ContainSingle();
        var row = _queue[0]!.DeepClone();
        _queue = [];
        (await service.TryRescueImportAsync(_torrent, "movies")).Should().Be(ImportRescueOutcome.NotApplicable);
        _queue = [row];
        (await service.TryRescueImportAsync(_torrent, "movies")).Should().Be(ImportRescueOutcome.Unconfirmed);
        _queue[0]!["trackedDownloadStatus"] = "ok";
        _queue[0]!["trackedDownloadState"] = "importPending";
        (await service.TryRescueImportAsync(_torrent, "movies")).Should().Be(ImportRescueOutcome.NotApplicable);
        _queue[0]!["trackedDownloadStatus"] = "warning";
        _queue[0]!["trackedDownloadState"] = "importBlocked";
        (await service.TryRescueImportAsync(_torrent, "movies")).Should().Be(ImportRescueOutcome.Pending);
        _commands.Should().HaveCount(2);
    }

    [Fact]
    public async Task MissingUnselectedSidecarDoesNotBlockAnEligibleMediaFile()
    {
        _files.Add(new() { Index = 1, Name = "Movie/subtitle.srt", Size = 20, Progress = 0, Priority = 0 });
        (await Service().TryRescueImportAsync(_torrent, "movies")).Should().Be(ImportRescueOutcome.Pending);
        _commands.Should().ContainSingle();
    }

    [Fact]
    public async Task MovingTheSameDownloadDoesNotResetAnUncertainIncident()
    {
        _commandStatus = 500;
        var service = Service();
        (await service.TryRescueImportAsync(_torrent, "movies")).Should().Be(ImportRescueOutcome.Pending);
        Directory.CreateDirectory(Path.Combine(_directory, "Moved"));
        File.Copy(Path.Combine(_directory, "Movie", "video.mkv"), Path.Combine(_directory, "Moved", "video.mkv"));
        _torrent.ContentPath = "/downloads/Moved";
        _files[0].Name = "Moved/video.mkv";
        _queue[0]!["outputPath"] = _torrent.ContentPath;
        _preview = [Candidate("/downloads/Moved/video.mkv", 10, 4)];
        _clock.Now = _clock.Now.AddMinutes(16);
        (await service.TryRescueImportAsync(_torrent, "movies")).Should().Be(ImportRescueOutcome.Unconfirmed);
        (await service.TryRescueImportAsync(_torrent, "movies")).Should().Be(ImportRescueOutcome.Unconfirmed);
        _commands.Should().ContainSingle();
    }

    [Fact]
    public async Task HistoryCannotConfirmAReaddedTorrentThroughAnOldSnapshot()
    {
        var service = Service();
        (await service.TryRescueImportAsync(_torrent, "movies")).Should().Be(ImportRescueOutcome.Pending);
        _queue = [];
        _history = [new JObject { ["id"] = 99, ["movieId"] = 10, ["downloadId"] = _torrent.Hash,
            ["eventType"] = "downloadFolderImported", ["date"] = _clock.Now.ToString("O") }];
        _client.Setup(client => client.GetTorrentsAsync(It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([new() { Hash = _torrent.Hash, AddedOn = 2, Category = "movies", State = "uploading", Progress = 1,
                SavePath = _torrent.SavePath, ContentPath = _torrent.ContentPath }]);
        (await service.TryRescueImportAsync(_torrent, "movies")).Should().Be(ImportRescueOutcome.Pending);
        HasPendingReceipt(_db).Should().BeTrue();
        _commands.Should().ContainSingle();
    }

    [Fact]
    public async Task ArrCanUseTheExplicitlyMappedLocalPathNamespace()
    {
        var output = Path.Combine(_directory, "Movie").Replace('\\', '/');
        var file = Path.Combine(_directory, "Movie", "video.mkv").Replace('\\', '/');
        _queue[0]!["outputPath"] = output;
        _preview[0]!["path"] = file;
        (await Service().TryRescueImportAsync(_torrent, "movies")).Should().Be(ImportRescueOutcome.Pending);
        _commands.Single()["files"]![0]!["path"]!.Value<string>().Should().Be(file);
    }

    [Theory]
    [InlineData(201)]
    [InlineData(500)]
    public async Task ActiveImportsKeepTheHoldBeyondTheConfirmationWindow(int responseStatus)
    {
        _commandStatus = responseStatus;
        var service = Service();
        (await service.TryRescueImportAsync(_torrent, "movies")).Should().Be(ImportRescueOutcome.Pending);
        _clock.Now = _clock.Now.AddMinutes(16);
        _commandInventory[0]!["status"] = "started";
        (await service.TryRescueImportAsync(_torrent, "movies")).Should().Be(ImportRescueOutcome.Pending);
        HasPendingReceipt(_db).Should().BeTrue();
        _commandInventory[0]!["status"] = "completed";
        (await service.TryRescueImportAsync(_torrent, "movies")).Should().Be(ImportRescueOutcome.Unconfirmed);
        _commands.Should().ContainSingle();
    }

    [Theory]
    [InlineData("multiple")]
    [InlineData("wrong-movie")]
    [InlineData("wrong-download")]
    [InlineData("rejected")]
    [InlineData("missing-path")]
    [InlineData("incomplete-file")]
    [InlineData("changed-identity")]
    [InlineData("unsupported-warning")]
    public async Task InvalidOrAmbiguousEvidenceCannotSubmitRescue(string scenario)
    {
        switch (scenario)
        {
            case "multiple": _preview.Add(_preview[0]!.DeepClone()); break;
            case "wrong-movie": _preview[0]!["movie"]!["id"] = 20; break;
            case "wrong-download": _preview[0]!["downloadId"] = "other"; break;
            case "rejected": _preview[0]!["rejections"] = new JArray(new JObject { ["reason"] = "Sample" }); break;
            case "incomplete-file": _files[0].Progress = 0; break;
            case "missing-path": File.Delete(Path.Combine(_directory, "Movie", "video.mkv")); break;
            case "changed-identity":
                _client.Setup(client => client.GetTorrentsAsync(It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
                    .ReturnsAsync([new() { Hash = _torrent.Hash, AddedOn = 2, Category = "movies", Progress = 1, State = "uploading" }]);
                break;
            case "unsupported-warning": _queue[0]!["statusMessages"] = new JArray(new JObject { ["title"] = "Unrecognized failure" }); break;
        }
        (await Service().TryRescueImportAsync(_torrent, "movies")).Should().Be(ImportRescueOutcome.NotApplicable);
        _commands.Should().BeEmpty();
    }

    [Fact]
    public async Task SharedExcludedExtraIsKeptWhileEligibleMediaIsSubmitted()
    {
        File.WriteAllText(Path.Combine(_directory, "Movie", "extra.exe"), "extra");
        File.WriteAllText(Path.Combine(_directory, "reviewed.txt"), @"\.exe$");
        _config.Settings.SharedFileFilters = [new() { Name = "reviewed", Source = "reviewed.txt" }];
        var policy = _config.ArrInstances["Radarr-Movies"].Torrent;
        policy.SharedFileFilters = ["reviewed"];
        policy.FileExtensionAllowlist = [];
        _files.Add(new() { Index = 1, Name = "Movie/extra.exe", Size = 5, Progress = 1, Priority = 1 });
        _preview.Add(Candidate("/downloads/Movie/extra.exe", 20, 5));
        using var filters = new SharedFileFilterService(_config, _issues.Object, NullLogger<SharedFileFilterService>.Instance);
        await filters.RefreshAsync(default, _directory);
        (await Service(filters: filters).TryRescueImportAsync(_torrent, "movies")).Should().Be(ImportRescueOutcome.Pending);
        _commands.Single()["files"]!.Should().HaveCount(1);
        File.Exists(Path.Combine(_directory, "Movie", "extra.exe")).Should().BeTrue();
    }

    [Fact]
    public async Task SymlinkedLocalMappingCannotAuthorizeRescue()
    {
        if (OperatingSystem.IsWindows()) return; // Symlink creation requires extra privileges on Windows.
        var alias = Path.Combine(_directory, "alias");
        Directory.CreateSymbolicLink(alias, _directory);
        _config.TorrentClients["fixture"].Maintenance.PathMappings[0].LocalPath = alias;

        (await Service().TryRescueImportAsync(_torrent, "movies")).Should().Be(ImportRescueOutcome.NotApplicable);
        _commands.Should().BeEmpty();
        HasPendingReceipt(_db).Should().BeFalse();
    }

    private void AddArrSharingServer() => _config.ArrInstances["Radarr-Other"] = new()
    {
        URI = _config.ArrInstances["Radarr-Movies"].URI,
        APIKey = "fixture-secret",
        Type = "radarr",
        Category = "other"
    };

    private void EnableParking()
    {
        var settings = _config.ArrInstances["Radarr-Movies"].Torrent;
        settings.RescueBlockedImports = false;
        settings.FailedTorrentAction = FailedTorrentRouting.Move;
        settings.FailedTorrentDestinations = [new() { Client = "fixture", Category = "parked" }];
        _torrent.AutoManaged = false;
        _client.SetupGet(client => client.Capabilities).Returns(TorrentClientCapabilities.QBittorrent);
        _client.Setup(client => client.GetCategoriesAsync(It.IsAny<CancellationToken>())).ReturnsAsync(
            new Dictionary<string, TorrentClientCategory> { ["parked"] = new() { Name = "parked" } });
        _client.Setup(client => client.SetCategoryAsync(It.IsAny<List<string>>(), "parked", It.IsAny<CancellationToken>()))
            .Callback(() => _torrent.Category = "parked").ReturnsAsync(true);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ParkingRetainsPayloadAndResumesOnlyTheQueueStepAfterRestart(bool blocklist)
    {
        EnableParking();
        _queueDeleteStatus = 403;
        await Service().ParkFailedTorrentAsync(_torrent, "movies", blocklist, replace: true);
        _torrent.Category.Should().Be("parked");
        File.ReadAllText(Path.Combine(_directory, "Movie", "video.mkv")).Should().Be("data");
        FailedTorrentReceipt.BlocksRemoval(_db, _torrent).Should().BeTrue();
        (await _db.SonarrSearchRetries.CountAsync()).Should().Be(0);
        using var restartedDb = OpenDb();
        _queueDeleteStatus = 200;
        _config.ArrInstances["Radarr-Movies"].Torrent.FailedTorrentAction = FailedTorrentRouting.Existing;
        AddArrSharingServer();
        await Service(restartedDb).ReconcileFailedTorrentMovesAsync("other");
        _queueDeletes.Should().ContainSingle();
        (await restartedDb.SonarrSearchRetries.CountAsync()).Should().Be(0);
        await Service(restartedDb).ReconcileFailedTorrentMovesAsync("movies");
        await Service(restartedDb).ReconcileFailedTorrentMovesAsync("movies");
        _client.Verify(client => client.SetCategoryAsync(It.IsAny<List<string>>(), "parked", It.IsAny<CancellationToken>()), Times.Once);
        _client.Verify(client => client.DeleteTorrentsAsync(It.IsAny<List<string>>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()), Times.Never);
        _queueDeletes.Should().Equal(("false", blocklist ? "true" : "false", "true"),
            ("false", blocklist ? "true" : "false", "true"));
        var replacement = await restartedDb.SonarrSearchRetries.SingleAsync();
        replacement.BlocklistPending.Should().BeFalse();
        FailedTorrentReceipt.BlocksRemoval(restartedDb, _torrent).Should().BeTrue();
    }

    [Theory]
    [InlineData(null)]
    [InlineData(true)]
    public async Task ParkingHoldsWhenAutomaticManagementIsUnknownOrEnabled(bool? automatic)
    {
        EnableParking();
        _torrent.AutoManaged = automatic;
        await Service().ParkFailedTorrentAsync(_torrent, "movies", blocklist: true, replace: false);
        _torrent.Category.Should().Be("movies");
        _queueDeletes.Should().BeEmpty();
    }

    [Fact]
    public async Task ParkingNeverRepeatsAnAmbiguousBlocklist()
    {
        EnableParking();
        _queueDeleteStatus = 500;
        await Service().ParkFailedTorrentAsync(_torrent, "movies", blocklist: true, replace: false);
        _queueDeleteStatus = 200;
        await Service().ReconcileFailedTorrentMovesAsync("movies");
        _queueDeletes.Should().ContainSingle();
        _torrent.Category.Should().Be("parked");
        FailedTorrentReceipt.BlocksRemoval(_db, _torrent).Should().BeTrue();
    }

    [Fact]
    public async Task ParkingCannotSupersedeConfirmedImportRescue()
    {
        await Service().TryRescueImportAsync(_torrent, "movies");
        var originalQueue = _queue;
        _queue = [];
        _history = [new JObject { ["id"] = 99, ["movieId"] = 10, ["downloadId"] = _torrent.Hash,
            ["eventType"] = "downloadFolderImported", ["date"] = _clock.Now.ToString("O") }];
        (await Service().TryRescueImportAsync(_torrent, "movies")).Should().Be(ImportRescueOutcome.Confirmed);
        _queue = originalQueue;
        EnableParking();

        await Service().ParkFailedTorrentAsync(_torrent, "movies", blocklist: true, replace: true);

        _torrent.Category.Should().Be("movies");
        _queueDeletes.Should().BeEmpty();
        FailedTorrentReceipt.BlocksRemoval(_db, _torrent).Should().BeFalse();
    }

    [Fact]
    public async Task ParkingHoldsWhenTheDestinationIsMissing()
    {
        EnableParking();
        _client.Setup(client => client.GetCategoriesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Dictionary<string, TorrentClientCategory>());
        await Service().ParkFailedTorrentAsync(_torrent, "movies", blocklist: true, replace: false);
        _torrent.Category.Should().Be("movies");
        _queueDeletes.Should().BeEmpty();
        _issues.Verify(reporter => reporter.ReportAsync(It.Is<OperationalIssueReport>(report =>
            report.Code == "torrent.failed-category"), It.IsAny<CancellationToken>()), Times.Once);
    }

    private async Task ServeAsync()
    {
        while (!_stop.IsCancellationRequested)
        {
            HttpListenerContext context;
            try { context = await _listener.GetContextAsync().WaitAsync(_stop.Token); }
            catch (Exception) when (_stop.IsCancellationRequested) { break; }
            try
            {
                JToken response;
                switch (context.Request.Url!.AbsolutePath)
                {
                    case "/api/v3/queue": response = new JObject { ["page"] = 1, ["pageSize"] = 100, ["totalRecords"] = _queue.Count, ["records"] = _queue.DeepClone() }; break;
                    case "/api/v3/queue/7" when context.Request.HttpMethod == "DELETE":
                        _queueDeletes.Enqueue((context.Request.QueryString["removeFromClient"],
                            context.Request.QueryString["blocklist"], context.Request.QueryString["skipRedownload"]));
                        context.Response.StatusCode = _queueDeleteStatus;
                        if (_queueDeleteStatus == 200) _queue = [];
                        response = new JObject();
                        break;
                    case "/api/v3/manualimport": response = _preview.DeepClone(); break;
                    case "/api/v3/history/since": response = _history.DeepClone(); break;
                    case "/api/v3/command" when context.Request.HttpMethod == "GET": response = _commandInventory.DeepClone(); break;
                    case "/api/v3/command":
                        using (var reader = new StreamReader(context.Request.InputStream)) _commands.Enqueue(JObject.Parse(await reader.ReadToEndAsync()));
                        if (_commandDelay > TimeSpan.Zero) await Task.Delay(_commandDelay, _stop.Token);
                        context.Response.StatusCode = _commandStatus;
                        response = new JObject { ["id"] = 100, ["status"] = "queued" };
                        break;
                    default: context.Response.StatusCode = 404; response = new JObject(); break;
                }
                context.Response.ContentType = "application/json";
                await context.Response.OutputStream.WriteAsync(Encoding.UTF8.GetBytes(response.ToString()));
            }
            catch (IOException) { }
            catch (HttpListenerException) { }
            finally { context.Response.Close(); }
        }
    }
    public async ValueTask DisposeAsync()
    {
        _stop.Cancel(); _listener.Close();
        await _server;
        _stop.Dispose(); _db.Dispose();
        Directory.Delete(_directory, true);
    }
    private sealed class TestClock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow() => Now;
    }
}

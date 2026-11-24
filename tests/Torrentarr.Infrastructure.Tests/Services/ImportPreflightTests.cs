using System.Collections.Concurrent;
using System.Net;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
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

public sealed class ImportPreflightTests : IAsyncDisposable
{
    private readonly string _root = Path.Combine(AppContext.BaseDirectory, "import-preflight-" + Guid.NewGuid().ToString("N"));
    private readonly HttpListener _listener = LoopbackHttpListener.Start(out _);
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _server;
    private readonly TorrentarrConfig _config = new();
    private readonly Mock<ITorrentClient> _client = new();
    private readonly Mock<ITorrentClientRegistry> _registry = new();
    private readonly Clock _clock = new();
    private readonly TorrentInfo _torrent;
    private readonly TorrentarrDbContext _db;
    private readonly List<TorrentFileRecord> _files = [];
    private readonly ConcurrentQueue<JObject> _posts = new();
    private readonly ConcurrentQueue<string> _reads = new();
    private readonly ConcurrentQueue<string> _previewRequests = new();
    private readonly Dictionary<string, JToken> _resources = new();
    private JArray _preview = [];
    private JArray _history = [];
    private JArray _commands = [];
    private bool _queueClear;
    private int _postStatus = 201;
    private int _previewReads;
    private bool _changePreviewOnRefresh;
    private string _apiVersion = "v3";
    private const string Type = "sonarr";

    public ImportPreflightTests()
    {
        Directory.CreateDirectory(Path.Combine(_root, "Pack"));
        _config.TorrentClients["fixture"] = new()
        {
            Host = "fixture",
            Maintenance = new()
            { PathMappings = [new() { ClientPath = "/downloads", LocalPath = _root }] }
        };
        _config.ArrInstances["Arr"] = new()
        {
            URI = _listener.Prefixes.Single(),
            APIKey = "fixture-secret",
            Type = Type,
            Category = "media",
            Torrent = new() { RescueBlockedImports = true, FileExtensionAllowlist = [], FileNameExclusionRegex = [], FolderExclusionRegex = [] }
        };
        _torrent = new()
        {
            Hash = "download",
            ClientInstanceId = "fixture",
            Category = "media",
            Name = "Pack",
            AddedOn = 1,
            CompletionOn = _clock.Now.ToUnixTimeSeconds() - 120,
            Progress = 1,
            AmountLeft = 0,
            State = "uploading",
            ContentPath = "/downloads/Pack",
            SavePath = "/downloads"
        };
        _client.Setup(client => client.GetTorrentsAsync(It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>())).ReturnsAsync(() => [_torrent]);
        _client.Setup(client => client.GetTorrentFilesAsync("download", It.IsAny<CancellationToken>())).ReturnsAsync(() => _files);
        _registry.Setup(registry => registry.GetClient("fixture")).Returns(_client.Object);
        _registry.Setup(registry => registry.GetAllClients()).Returns(new Dictionary<string, ITorrentClient> { ["fixture"] = _client.Object });
        _resources["album/20"] = new JObject
        {
            ["id"] = 20,
            ["artistId"] = 10,
            ["releases"] = new JArray(new JObject { ["id"] = 30, ["monitored"] = true })
        };
        _resources["trackfile?albumId=20"] = new JArray(new JObject { ["id"] = 101 });
        _resources["series/10"] = new JObject { ["id"] = 10, ["qualityProfileId"] = 1 };
        _resources["qualityprofile"] = new JArray(new JObject { ["id"] = 1, ["formatItems"] = new JArray(new JObject { ["format"] = 7, ["score"] = 1000 }) });
        _db = OpenDb();
        _db.Database.EnsureCreated();
        _server = ServeAsync();
    }

    private TorrentarrDbContext OpenDb() => new(new DbContextOptionsBuilder<TorrentarrDbContext>().UseSqlite(
        new SqliteConnectionStringBuilder { DataSource = Path.Combine(_root, "state.db"), Pooling = false }.ToString()).Options);
    private ArrImportService Service(TorrentarrDbContext? db = null) => new(NullLogger<ArrImportService>.Instance, _config, _registry.Object, db ?? _db, clock: _clock);
    private ImportPlanReceipt Receipt() => ImportPlanReceipt.Parse(_db.MaintenanceState.AsNoTracking().Single(row => row.Key == ImportPlanReceipt.Key(_torrent)).Data);
    private void Advance(int seconds = 901) => _clock.Now = _clock.Now.AddSeconds(seconds);
    private JObject AddEpisode(string name, int[] ids, int? incoming, params int?[] existing)
    {
        var path = Path.Combine(_root, "Pack", name);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "data");
        _files.Add(new() { Name = "Pack/" + name, Index = _files.Count, Size = 4, Progress = 1, Priority = 1 });
        for (var i = 0; i < ids.Length; i++)
        {
            var fileId = i < existing.Length ? ids[i] + 100 : 0;
            _resources["episode/" + ids[i]] = new JObject { ["id"] = ids[i], ["seriesId"] = 10, ["episodeFileId"] = fileId, ["hasFile"] = fileId > 0 };
            if (fileId > 0) _resources["episodefile/" + fileId] = new JObject { ["id"] = fileId, ["customFormatScore"] = existing[i] };
        }
        var candidate = new JObject
        {
            ["path"] = "/arr/Pack/" + name,
            ["size"] = 4,
            ["downloadId"] = "download",
            ["series"] = new JObject { ["id"] = 10 },
            ["episodes"] = new JArray(ids.Select(id => new JObject { ["id"] = id })),
            ["quality"] = new JObject { ["quality"] = new JObject { ["id"] = 4 } },
            ["customFormatScore"] = incoming,
            ["languages"] = new JArray(new JObject { ["id"] = 1 }),
            ["rejections"] = new JArray()
        };
        _preview.Add(candidate);
        return candidate;
    }

    [Theory]
    [InlineData(2106, 2206, false)]
    [InlineData(2206, 2206, true)]
    [InlineData(2306, 2206, true)]
    public async Task ReplacementUsesPerEpisodeScoreAndAllowsEquality(int incoming, int existing, bool accepted)
    {
        AddEpisode("episode.mkv", [1], incoming, existing)["rejections"] = new JArray(new JObject { ["reason"] = "Not a Custom Format upgrade for existing episode file(s)." });
        var result = await Service().TriggerImportAsync(_torrent, "media");
        result.Success.Should().Be(accepted);
        result.Deferred.Should().Be(!accepted);
        _posts.Count.Should().Be(accepted ? 1 : 0);
        Receipt().Files.Single().IncomingScore.Should().Be(incoming);
        Receipt().Files.Single().ExistingScores.Should().Equal(existing);
        if (accepted)
        {
            _posts.Single()["importMode"]!.Value<string>().Should().Be("Copy");
            _posts.Single()["files"]![0]!["path"]!.Value<string>().Should().Be("/arr/Pack/episode.mkv");
        }
    }

    [Theory]
    [InlineData(null, 1000)]
    [InlineData(1000, null)]
    public async Task UnknownReplacementScoresDoNotDefaultToZero(int? incoming, int? existing)
    {
        AddEpisode("episode.mkv", [1], incoming, existing);
        (await Service().TriggerImportAsync(_torrent, "media")).Success.Should().BeFalse();
        _posts.Should().BeEmpty();
        Receipt().Files.Single().Reason.Should().Contain("unavailable");
    }

    [Fact]
    public async Task MissingMediaImportsWithoutReplacementScores()
    {
        AddEpisode("episode.mkv", [1], null);
        (await Service().TriggerImportAsync(_torrent, "media")).Success.Should().BeTrue();
    }

    [Theory]
    [InlineData("v3", "downloadId")]
    [InlineData("v5", "downloadIds")]
    public async Task SonarrPreflightUsesVersionedDownloadParameterForBothPreviews(string version, string parameter)
    {
        _apiVersion = version;
        AddEpisode("episode.mkv", [1], null);

        (await Service().TriggerImportAsync(_torrent, "media")).Success.Should().BeTrue();

        _previewRequests.Should().HaveCount(2).And.OnlyContain(path =>
            path == $"/api/{version}/manualimport?{parameter}=download&filterExistingFiles=true&replaceExistingFiles=true");
        _posts.Should().ContainSingle();
    }

    [Theory]
    [InlineData("v3")]
    [InlineData("v5")]
    public async Task EmptySonarrPreviewDoesNotAuthorizeImport(string version)
    {
        _apiVersion = version;
        AddEpisode("episode.mkv", [1], null);
        _preview.Clear();

        var result = await Service().TriggerImportAsync(_torrent, "media");

        result.Success.Should().BeFalse();
        result.Deferred.Should().BeTrue();
        _posts.Should().BeEmpty();
        Receipt().Files.Single().Reason.Should().Contain("exactly one owned file");
    }

    [Fact]
    public async Task PartialPackSubmitsOnlyEligibleFilesAndNeverConfirmsWholeTorrent()
    {
        AddEpisode("good.mkv", [1], 1000, 900);
        AddEpisode("bad.mkv", [2], 1000, 1100);
        await Service().TriggerImportAsync(_torrent, "media");
        (_posts.Single()["files"] as JArray).Should().ContainSingle();
        _history = [ImportEvent(1, "good.mkv")];
        _commands = [new JObject { ["id"] = 42, ["status"] = "completed" }];
        Advance(61);
        (await Service().TryRescueImportAsync(_torrent, "media")).Should().Be(ImportRescueOutcome.NotApplicable);
        Receipt().State.Should().Be("partial");
        (await Service().IsImportedAsync("download", "media", DateTimeOffset.UnixEpoch)).Should().BeFalse();
        ImportRescueReceipt.BlocksRemoval(_db, _torrent, TorrentRemovalIntent.RejectedByCustomFormat).Should().BeTrue();
        Advance();
        await Service().TriggerImportAsync(_torrent, "media");
        _posts.Should().ContainSingle();
        Receipt().State.Should().Be("partial");
    }

    [Fact]
    public async Task MultiEpisodeFileMustPassForEveryEpisode()
    {
        AddEpisode("double.mkv", [1, 2], 1000, 900, 1100);
        await Service().TriggerImportAsync(_torrent, "media");
        _posts.Should().BeEmpty();
    }

    [Fact]
    public async Task IdentifiedSeasonPackNeedsNoQueueSeriesGuess()
    {
        for (var i = 1; i <= 12; i++) AddEpisode($"episode{i}.mkv", [i], 1000, 1000);
        (await Service().TriggerImportAsync(_torrent, "media")).Success.Should().BeTrue();
        ((JArray)_posts.Single()["files"]!).Count.Should().Be(12);
    }

    [Fact]
    public async Task PolicyForcingRequiresOptInAndNeverOverridesStructuralFailures()
    {
        var candidate = AddEpisode("episode.mkv", [1], 1000, 1000);
        candidate["rejections"] = new JArray(new JObject { ["reason"] = "Not a quality upgrade" });
        _config.ArrInstances["Arr"].Torrent.RescueBlockedImports = false;
        await Service().TriggerImportAsync(_torrent, "media");
        _posts.Should().BeEmpty();
        Advance();
        _config.ArrInstances["Arr"].Torrent.RescueBlockedImports = true;
        candidate["rejections"] = new JArray(new JObject { ["reason"] = "File is still being unpacked" });
        await Service().TriggerImportAsync(_torrent, "media");
        _posts.Should().BeEmpty();
        Advance();
        candidate["rejections"] = new JArray(new JObject { ["reason"] = "Not a quality upgrade" });
        (await Service().TriggerImportAsync(_torrent, "media")).Success.Should().BeTrue();
    }

    [Fact]
    public async Task BlockedEvidenceIsRecheckedAfterCooldownWithoutRepeatingCommands()
    {
        AddEpisode("episode.mkv", [1], 900, 1000);
        await Service().TriggerImportAsync(_torrent, "media");
        var count = _reads.Count;
        await Service().TriggerImportAsync(_torrent, "media");
        _reads.Count.Should().Be(count);
        Advance();
        await Service().TriggerImportAsync(_torrent, "media");
        _reads.Count.Should().BeGreaterThan(count);
        _posts.Should().BeEmpty();
    }

    [Fact]
    public async Task RestartAndAmbiguousPostNeverResubmit()
    {
        AddEpisode("episode.mkv", [1], 1000);
        _postStatus = 500;
        (await Service().TriggerImportAsync(_torrent, "media")).Success.Should().BeFalse();
        _posts.Should().ContainSingle();
        Advance();
        using var restarted = OpenDb();
        _config.ArrInstances["Arr"].Torrent.RescueBlockedImports = false;
        await Service(restarted).TryRescueImportAsync(_torrent, "media");
        await Service(restarted).TriggerImportAsync(_torrent, "media");
        _posts.Should().ContainSingle();
        ImportRescueReceipt.BlocksRemoval(restarted, _torrent, TorrentRemovalIntent.RejectedByAllowlist).Should().BeTrue();
    }

    [Fact]
    public async Task ConcurrentObserversShareDurableSubmissionClaim()
    {
        AddEpisode("episode.mkv", [1], 1000);
        using var second = OpenDb();
        await Task.WhenAll(Service().TriggerImportAsync(_torrent, "media"), Service(second).TriggerImportAsync(_torrent, "media"));
        _posts.Should().ContainSingle();
    }

    [Fact]
    public async Task CommandCompletionAndQueueDisappearanceAreInsufficientEvidence()
    {
        AddEpisode("episode.mkv", [1], 1000);
        await Service().TriggerImportAsync(_torrent, "media");
        _commands = [new JObject { ["id"] = 42, ["status"] = "completed" }];
        _queueClear = true;
        Advance(61);
        (await Service().TryRescueImportAsync(_torrent, "media")).Should().Be(ImportRescueOutcome.Pending);
        _history = [ImportEvent(1, "other.mkv")];
        Advance(61);
        (await Service().TryRescueImportAsync(_torrent, "media")).Should().Be(ImportRescueOutcome.Pending);
        _history = [ImportEvent(1, "episode.mkv")];
        Advance(61);
        (await Service().TryRescueImportAsync(_torrent, "media")).Should().Be(ImportRescueOutcome.Confirmed);
        Receipt().State.Should().Be("confirmed");
    }

    [Fact]
    public async Task CompletedButUnconfirmedFilesAreNotReplayedUnlessEvidenceChanges()
    {
        AddEpisode("episode.mkv", [1], 1000, 900);
        await Service().TriggerImportAsync(_torrent, "media");
        _commands = [new JObject { ["id"] = 42, ["status"] = "failed", ["message"] = "Permission denied" }];
        Advance();
        await Service().TryRescueImportAsync(_torrent, "media");
        Receipt().Files.Single().Reason.Should().Contain("Permission denied");
        Advance();
        await Service().TriggerImportAsync(_torrent, "media");
        _posts.Should().ContainSingle();
        ((JObject)_resources["episodefile/101"])["customFormatScore"] = 800;
        Advance();
        await Service().TriggerImportAsync(_torrent, "media");
        _posts.Count.Should().Be(2);
    }

    [Theory]
    [InlineData("ambiguous")]
    [InlineData("missing")]
    [InlineData("filtered")]
    [InlineData("incomplete")]
    [InlineData("cross-client")]
    public async Task StructuralAndOwnershipFailuresPreventSubmission(string scenario)
    {
        var candidate = AddEpisode("episode.mkv", [1], 1000);
        if (scenario == "ambiguous") _preview.Add(candidate.DeepClone());
        if (scenario == "missing") File.Delete(Path.Combine(_root, "Pack", "episode.mkv"));
        if (scenario == "filtered") _config.ArrInstances["Arr"].Torrent.FileNameExclusionRegex = ["episode"];
        if (scenario == "incomplete") _files[0].Progress = 0.9;
        if (scenario == "cross-client") _config.TorrentClients["other"] = new() { Host = "other" };
        await Service().TriggerImportAsync(_torrent, "media");
        _posts.Should().BeEmpty();
    }

    [Fact]
    public void ScoresCanBeCalculatedFromProfileFormatWeights()
    {
        ArrImportService.CustomFormatScore(new JObject { ["customFormats"] = new JArray(new JObject { ["id"] = 7 }) },
            (JObject)((JArray)_resources["qualityprofile"])[0]).Should().Be(1000);
        ArrImportService.CustomFormatScore(new JObject { ["customFormats"] = new JArray(new JObject { ["id"] = 8 }) },
            (JObject)((JArray)_resources["qualityprofile"])[0]).Should().BeNull();
    }

    [Theory]
    [InlineData(900, false)]
    [InlineData(1000, true)]
    [InlineData(1100, true)]
    public async Task RadarrUsesMovieScoreAndExactMoviePayload(int incoming, bool accepted)
    {
        _config.ArrInstances["Arr"].Type = "radarr";
        var candidate = AddEpisode("movie.mkv", [1], incoming, 1000);
        candidate.Remove("series"); candidate.Remove("episodes");
        candidate["movie"] = new JObject { ["id"] = 10 };
        _resources["movie/10"] = new JObject { ["id"] = 10, ["qualityProfileId"] = 1, ["movieFileId"] = 100 };
        _resources["moviefile/100"] = new JObject { ["id"] = 100, ["customFormatScore"] = 1000 };
        (await Service().TriggerImportAsync(_torrent, "media")).Success.Should().Be(accepted);
        if (accepted) _posts.Single()["files"]![0]!["movieId"]!.Value<int>().Should().Be(10);
    }

    [Fact]
    public async Task LidarrComparesTracksIndividuallyAndUsesItsAlbumReleaseContract()
    {
        _config.ArrInstances["Arr"].Type = "lidarr";
        _resources["artist/10"] = new JObject { ["id"] = 10, ["qualityProfileId"] = 1 };
        for (var id = 1; id <= 2; id++)
        {
            var candidate = AddEpisode($"track{id}.flac", [id], 1000);
            candidate.Remove("series"); candidate.Remove("episodes");
            candidate["artist"] = new JObject { ["id"] = 10 };
            candidate["album"] = new JObject { ["id"] = 20 };
            candidate["albumReleaseId"] = 30;
            candidate["tracks"] = new JArray(new JObject { ["id"] = id, ["albumId"] = 20 });
            _resources[$"track?trackIds={id}"] = new JArray(new JObject { ["id"] = id, ["artistId"] = 10, ["albumId"] = 20, ["trackFileId"] = 100 + id });
            _resources[$"trackfile/{100 + id}"] = new JObject { ["id"] = 100 + id, ["customFormatScore"] = id == 1 ? 900 : 1100 };
        }
        await Service().TriggerImportAsync(_torrent, "media");
        var payload = (JObject)((JArray)_posts.Single()["files"]!).Single();
        payload["trackIds"]!.Values<int>().Should().Equal(1);
        payload["albumId"]!.Value<int>().Should().Be(20);
        payload["albumReleaseId"]!.Value<int>().Should().Be(30);
        _posts.Single()["replaceExistingFiles"]!.Value<bool>().Should().BeFalse("partial album imports must not delete unselected tracks");
        payload["disableReleaseSwitching"].Should().BeNull();
        _commands = [new JObject { ["id"] = 42, ["status"] = "completed" }];
        var history = ImportEvent(1, "track1.flac");
        history["eventType"] = "downloadImported"; history["albumId"] = 20;
        _history = [history];
        Advance(61);
        await Service().TryRescueImportAsync(_torrent, "media");
        Receipt().State.Should().Be("partial");
    }

    [Fact]
    public async Task LidarrMultiTrackFileCannotDowngradeAnyTrack()
    {
        _config.ArrInstances["Arr"].Type = "lidarr";
        var candidate = AddEpisode("multi.flac", [1, 2], 1000);
        candidate.Remove("series"); candidate.Remove("episodes");
        candidate["artist"] = new JObject { ["id"] = 10 };
        candidate["album"] = new JObject { ["id"] = 20 }; candidate["albumReleaseId"] = 30;
        candidate["tracks"] = new JArray(new JObject { ["id"] = 1, ["albumId"] = 20 }, new JObject { ["id"] = 2, ["albumId"] = 20 });
        _resources["artist/10"] = new JObject { ["id"] = 10, ["qualityProfileId"] = 1 };
        for (var id = 1; id <= 2; id++)
        {
            _resources[$"track?trackIds={id}"] = new JArray(new JObject { ["id"] = id, ["artistId"] = 10, ["albumId"] = 20, ["trackFileId"] = 100 + id });
            _resources[$"trackfile/{100 + id}"] = new JObject { ["id"] = 100 + id, ["customFormatScore"] = id == 1 ? 900 : 1100 };
        }
        await Service().TriggerImportAsync(_torrent, "media");
        _posts.Should().BeEmpty();
    }

    [Fact]
    public async Task ReadarrUsesSingularBookAndEditionAndHoldsUnscoredReplacements()
    {
        _config.ArrInstances["Arr"].Type = "readarr";
        var candidate = AddEpisode("book.epub", [1], null);
        candidate.Remove("series"); candidate.Remove("episodes");
        candidate["author"] = new JObject { ["id"] = 10 };
        candidate["book"] = new JObject { ["id"] = 1 }; candidate["foreignEditionId"] = "edition";
        _resources["author/10"] = new JObject { ["id"] = 10, ["qualityProfileId"] = 1 };
        _resources["book/1"] = new JObject { ["id"] = 1, ["authorId"] = 10 };
        _resources["bookfile?bookId=1"] = new JArray(new JObject { ["id"] = 100 });
        await Service().TriggerImportAsync(_torrent, "media");
        _posts.Should().BeEmpty();
        _resources["bookfile?bookId=1"] = new JArray();
        Advance();
        (await Service().TriggerImportAsync(_torrent, "media")).Success.Should().BeTrue();
        var payload = _posts.Single()["files"]![0]!;
        payload["bookId"]!.Value<int>().Should().Be(1);
        payload["foreignEditionId"]!.Value<string>().Should().Be("edition");
        payload["bookIds"].Should().BeNull();
    }

    [Fact]
    public async Task CorruptReceiptProtectsPayloadAndCannotBeOverwritten()
    {
        MaintenancePersistence.Write(_db, ImportPlanReceipt.Key(_torrent), new { State = "pending", Server = (string?)null });
        await Service().TriggerImportAsync(_torrent, "media");
        (await Service().TryRescueImportAsync(_torrent, "media")).Should().Be(ImportRescueOutcome.Pending);
        ImportRescueReceipt.BlocksRemoval(_db, _torrent, TorrentRemovalIntent.RejectedByAllowlist).Should().BeTrue();
        _posts.Should().BeEmpty();
    }

    [Fact]
    public async Task SourceChangesAfterConfirmationDoNotInheritOldSuccess()
    {
        AddEpisode("episode.mkv", [1], 1000);
        await Service().TriggerImportAsync(_torrent, "media");
        _history = [ImportEvent(1, "episode.mkv")]; _queueClear = true;
        Advance(61);
        await Service().TryRescueImportAsync(_torrent, "media");
        File.WriteAllText(Path.Combine(_root, "Pack", "episode.mkv"), "changed");
        Advance();
        await Service().TriggerImportAsync(_torrent, "media");
        Receipt().State.Should().Be("partial");
        _posts.Should().ContainSingle();
    }

    [Fact]
    public async Task ProcessorUsesMappedStorageAndPassesClientIdentityToImportService()
    {
        AddEpisode("episode.mkv", [1], 1000);
        _db.TorrentLibrary.Add(new() { Hash = _torrent.Hash, Category = "media", ArrInstance = "Arr", QbitInstance = "fixture", TorrentAddedOn = 1 });
        await _db.SaveChangesAsync();
        var processor = new TorrentProcessor(NullLogger<TorrentProcessor>.Instance, _registry.Object, _db, _config,
            Mock.Of<ITorrentCacheService>(), new DatabaseRestartCoordinator(), importService: Service());
        await processor.ImportTorrentAsync(_torrent.Hash, "fixture");
        _posts.Should().ContainSingle();
        (await _db.TorrentLibrary.SingleAsync()).ImportSubmittedAt.Should().NotBeNull();
        (await _db.TorrentLibrary.SingleAsync()).Imported.Should().BeFalse();
    }

    [Fact]
    public async Task ActiveArrImportPreventsCompetingSubmission()
    {
        AddEpisode("episode.mkv", [1], 1000);
        _commands = [new JObject { ["id"] = 1, ["status"] = "started", ["body"] = new JObject { ["downloadClientId"] = "download" } }];
        await Service().TriggerImportAsync(_torrent, "media");
        _posts.Should().BeEmpty();
    }

    [Fact]
    public async Task ChangedArrIdentityDuringPreflightPreventsSubmission()
    {
        AddEpisode("episode.mkv", [1], 1000);
        _changePreviewOnRefresh = true;
        _resources["episode/2"] = new JObject { ["id"] = 2, ["seriesId"] = 10, ["episodeFileId"] = 0 };
        await Service().TriggerImportAsync(_torrent, "media");
        _posts.Should().BeEmpty();
    }

    [Fact]
    public async Task MissingTorrentCanReconcilePositiveHistoryAfterRestartWithoutSubmitting()
    {
        AddEpisode("episode.mkv", [1], 1000);
        await Service().TriggerImportAsync(_torrent, "media");
        _history = [ImportEvent(1, "episode.mkv")]; _queueClear = true;
        Advance(61);
        _client.Setup(client => client.GetTorrentsAsync(It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>())).ReturnsAsync([]);
        using var restarted = OpenDb();
        await Service(restarted).ReconcileImportRescuesAsync("media");
        Receipt().State.Should().Be("confirmed");
        _posts.Should().ContainSingle();
    }

    [Fact]
    public async Task ExistingMultiEpisodeFileCannotLoseUntargetedEpisodes()
    {
        AddEpisode("episode.mkv", [1], 1000, 900);
        _resources["episode/2"] = new JObject { ["id"] = 2, ["seriesId"] = 10, ["episodeFileId"] = 101 };
        await Service().TriggerImportAsync(_torrent, "media");
        _posts.Should().BeEmpty();
        Receipt().Files.Single().Reason.Should().Contain("outside");
    }

    [Fact]
    public async Task AmbiguousLocalPathAliasesCannotApproveTheFirstFile()
    {
        AddEpisode("A/episode.mkv", [1], 1000);
        AddEpisode("B/episode.mkv", [2], 1000);
        _preview.RemoveAt(1);
        _preview[0]!["path"] = Path.Combine(_root, "Pack", "episode.mkv");
        File.WriteAllText(Path.Combine(_root, "Pack", "episode.mkv"), "data");
        _config.TorrentClients["fixture"].Maintenance.PathMappings =
        [new() { ClientPath = "/downloads/Pack/A", LocalPath = Path.Combine(_root, "Pack") },
         new() { ClientPath = "/downloads/Pack/B", LocalPath = Path.Combine(_root, "Pack") }];
        await Service().TriggerImportAsync(_torrent, "media");
        _posts.Should().BeEmpty();
    }

    private JObject ImportEvent(int episodeId, string name) => new()
    {
        ["id"] = episodeId + 1000,
        ["downloadId"] = "download",
        ["episodeId"] = episodeId,
        ["eventType"] = "downloadFolderImported",
        ["date"] = _clock.Now.ToString("O"),
        ["data"] = new JObject { ["droppedPath"] = "/arr/Pack/" + name }
    };

    private async Task ServeAsync()
    {
        while (!_stop.IsCancellationRequested)
        {
            HttpListenerContext context;
            try { context = await _listener.GetContextAsync().WaitAsync(_stop.Token); }
            catch (Exception) when (_stop.IsCancellationRequested) { break; }
            try
            {
                var route = context.Request.Url!.AbsolutePath.Split('/', 4).Last();
                _reads.Enqueue(route);
                JToken response;
                if (route == "system/status") response = new JObject { ["version"] = _apiVersion == "v5" ? "5.0.0" : "4.0.0" };
                else if (route == "queue") response = new JObject
                {
                    ["page"] = 1,
                    ["pageSize"] = 100,
                    ["totalRecords"] = _queueClear ? 0 : 1,
                    ["records"] = _queueClear ? new JArray() : new JArray(new JObject { ["id"] = 5, ["downloadId"] = "download", ["status"] = "completed", ["outputPath"] = "/arr/Pack" })
                };
                else if (route == "manualimport")
                {
                    _previewRequests.Enqueue(context.Request.Url.PathAndQuery);
                    var parameter = _apiVersion == "v5" ? "downloadIds" : "downloadId";
                    var wrongParameter = _apiVersion == "v5" ? "downloadId" : "downloadIds";
                    if (context.Request.QueryString[parameter] != "download" || context.Request.QueryString[wrongParameter] != null)
                    {
                        context.Response.StatusCode = 500;
                        response = new JObject { ["message"] = "Missing version-specific download parameter." };
                    }
                    else
                    {
                        if (++_previewReads > 1 && _changePreviewOnRefresh) _preview[0]!["episodes"] = new JArray(new JObject { ["id"] = 2 });
                        response = _preview.DeepClone();
                    }
                }
                else if (route == "history/since") response = _history.DeepClone();
                else if (route == "command" && context.Request.HttpMethod == "GET") response = _commands.DeepClone();
                else if (route == "command")
                {
                    using var reader = new StreamReader(context.Request.InputStream);
                    _posts.Enqueue(JObject.Parse(await reader.ReadToEndAsync()));
                    context.Response.StatusCode = _postStatus;
                    response = new JObject { ["id"] = 42, ["status"] = "queued" };
                }
                else if (route == "episode") response = new JArray(_resources.Where(pair => pair.Key.StartsWith("episode/", StringComparison.Ordinal)).Select(pair => pair.Value.DeepClone()));
                else if (route == "track" && context.Request.QueryString["albumId"] != null)
                    response = new JArray(_resources.Where(pair => pair.Key.StartsWith("track?trackIds=", StringComparison.Ordinal)).SelectMany(pair => (JArray)pair.Value).Select(item => item.DeepClone()));
                else if (_resources.TryGetValue(route + context.Request.Url.Query, out var resource)
                    || _resources.TryGetValue(route, out resource)) response = resource.DeepClone();
                else { context.Response.StatusCode = 404; response = new JObject(); }
                var bytes = System.Text.Encoding.UTF8.GetBytes(response.ToString());
                context.Response.ContentType = "application/json";
                await context.Response.OutputStream.WriteAsync(bytes, _stop.Token);
                context.Response.Close();
            }
            catch (Exception) when (_stop.IsCancellationRequested) { break; }
        }
    }

    public async ValueTask DisposeAsync()
    {
        _stop.Cancel(); _listener.Close(); await _server;
        _db.Dispose(); _stop.Dispose(); Directory.Delete(_root, true);
    }
    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow() => Now;
    }
}

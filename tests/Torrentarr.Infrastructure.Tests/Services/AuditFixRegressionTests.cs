using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Torrentarr.Core.Configuration;
using Torrentarr.Core.Services;
using Torrentarr.Infrastructure.ApiClients.Arr;
using Torrentarr.Infrastructure.Database;
using Torrentarr.Infrastructure.Database.Models;
using Torrentarr.Infrastructure.Services;
using Xunit;

namespace Torrentarr.Infrastructure.Tests.Services;

public sealed class AuditFixRegressionTests
{
    [Fact]
    public async Task QueueReadsSecondPage_AndRejectsTruncationOrDuplicates()
    {
        var calls = new List<int>();
        var result = await ArrQueuePaging.ReadAllAsync<int>((page, size) =>
        {
            calls.Add(page);
            return Task.FromResult((page, 2, 3, page == 1 ? new List<int> { 1, 2 } : [3]));
        }, id => id, CancellationToken.None);
        result.Should().Equal(1, 2, 3); calls.Should().Equal(1, 2);
        await FluentActions.Awaiting(() => ArrQueuePaging.ReadAllAsync<int>((page, _) =>
            Task.FromResult((page, 2, 3, page == 1 ? new List<int> { 1, 2 } : [])), id => id, CancellationToken.None)).Should().ThrowAsync<IOException>();
        await FluentActions.Awaiting(() => ArrQueuePaging.ReadAllAsync<int>((page, _) =>
            Task.FromResult((page, 2, 4, new List<int> { 1, 2 })), id => id, CancellationToken.None)).Should().ThrowAsync<IOException>();
    }

    [Fact]
    public async Task QueueRejectsWrongPage_ChangedTotal_AndCancellation()
    {
        await FluentActions.Awaiting(() => ArrQueuePaging.ReadAllAsync<int>((page, _) => Task.FromResult((0, 2, 0, new List<int>())), x => x, default)).Should().ThrowAsync<IOException>();
        await FluentActions.Awaiting(() => ArrQueuePaging.ReadAllAsync<int>((page, _) => Task.FromResult((page, 1, page == 1 ? 2 : 3, new List<int> { page })), x => x, default)).Should().ThrowAsync<IOException>();
        using var cancel = new CancellationTokenSource(); cancel.Cancel();
        await FluentActions.Awaiting(() => ArrQueuePaging.ReadAllAsync<int>((page, _) => throw new InvalidOperationException(), x => x, cancel.Token)).Should().ThrowAsync<OperationCanceledException>();
        (await ArrQueuePaging.ReadAllAsync<int>((page, size) => Task.FromResult((page, size, 0, new List<int>())), x => x, default)).Should().BeEmpty();
    }

    [Theory]
    [InlineData("radarr")]
    [InlineData("sonarr")]
    [InlineData("lidarr")]
    [InlineData("readarr")]
    public async Task ProfileIntentSurvivesRestart_AndConflictingManualProfileIsPreserved(string type)
    {
        using var stub = new ArrStub(type);
        var options = new DbContextOptionsBuilder<TorrentarrDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options;
        await using var db = new FailingDb(options);
        object row = type switch
        {
            "radarr" => new MoviesFilesModel { EntryId = 1, ArrId = 42, ArrInstance = "Arr", QualityProfileId = 5 },
            "sonarr" => new SeriesFilesModel { EntryId = 1, ArrId = 42, ArrInstance = "Arr", QualityProfileId = 5 },
            "lidarr" => new ArtistFilesModel { EntryId = 1, ArrId = 42, ArrInstance = "Arr", QualityProfileId = 5 },
            _ => new AuthorFilesModel { EntryId = 1, ArrId = 42, ArrInstance = "Arr", QualityProfileId = 5 }
        };
        db.Add(row); await db.SaveChangesAsync();
        var cfg = Config(type, stub.Url);
        var switcher = new QualityProfileSwitcherService(NullLogger<QualityProfileSwitcherService>.Instance, db, new());
        var candidates = new[] { new SearchCandidate { ArrId = 42, SeriesId = 42, ArtistId = 42, AuthorId = 42, Reason = "Missing" } };
        (await switcher.SwitchToTempProfilesAsync("Arr", cfg, candidates)).Should().BeTrue();
        db.Entry(row).Property("OriginalProfileId").CurrentValue.Should().Be(7, "the remote profile wins over stale catalog data");
        stub.Profile.Should().Be(99); stub.Puts.Should().Be(1);
        // A new switcher/context owner must use durable intent rather than replacing the original.
        await using var restartedDb = new TorrentarrDbContext(options);
        (await new QualityProfileSwitcherService(NullLogger<QualityProfileSwitcherService>.Instance, restartedDb, new()).SwitchToTempProfilesAsync("Arr", cfg, candidates)).Should().BeTrue();
        stub.Puts.Should().Be(1);
        stub.Profile = 11;
        (await switcher.SwitchToTempProfilesAsync("Arr", cfg, candidates)).Should().BeFalse();
        await switcher.ForceResetAllTempProfilesAsync("Arr", cfg);
        stub.Profile.Should().Be(11); db.Entry(row).Property("OriginalProfileId").CurrentValue.Should().Be(7);
        stub.Profile = 99;
        await switcher.ForceResetAllTempProfilesAsync("Arr", cfg);
        stub.Profile.Should().Be(7);
        db.ChangeTracker.Clear();
        db.Entry((await db.FindAsync(row.GetType(), 1))!).Property("OriginalProfileId").CurrentValue.Should().BeNull();
    }

    [Fact]
    public async Task FailedIntentSavePreventsRemotePut_AndFailedPutRetainsIntent()
    {
        using var stub = new ArrStub("radarr");
        await using var db = new FailingDb(new DbContextOptionsBuilder<TorrentarrDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        db.Movies.Add(new() { EntryId = 1, ArrId = 42, ArrInstance = "Arr", QualityProfileId = 7 }); await db.SaveChangesAsync();
        var switcher = new QualityProfileSwitcherService(NullLogger<QualityProfileSwitcherService>.Instance, db, new());
        var candidates = new[] { new SearchCandidate { ArrId = 42, Reason = "Missing" } };
        db.FailSaves = true;
        (await switcher.SwitchToTempProfilesAsync("Arr", Config("radarr", stub.Url), candidates)).Should().BeFalse();
        stub.Puts.Should().Be(0);
        db.FailSaves = false; stub.RejectPut = true;
        (await switcher.SwitchToTempProfilesAsync("Arr", Config("radarr", stub.Url), candidates)).Should().BeFalse();
        db.ChangeTracker.Clear();
        (await db.Movies.SingleAsync()).OriginalProfileId.Should().Be(7);
    }

    [Fact]
    public void TargetedInvalidationPreservesOtherClientsAndTorrents()
    {
        var cache = new TorrentCacheService(NullLogger<TorrentCacheService>.Instance);
        var tracker = new ImportPathTracker();
        foreach (var (client, hash) in new[] { ("one", "a"), ("two", "a"), ("one", "b") })
        { cache.MarkFileFiltered(client, hash); cache.MarkFilePrioritiesApplied(client, hash); tracker.MarkScanned(client, "/" + hash, hash); }
        cache.InvalidateTorrent("one", "a"); tracker.InvalidateTorrent("one", "a");
        cache.IsFileFiltered("one", "a").Should().BeFalse(); tracker.IsHashAlreadyScanned("one", "a").Should().BeFalse();
        cache.IsFileFiltered("two", "a").Should().BeTrue(); tracker.IsHashAlreadyScanned("one", "b").Should().BeTrue();
    }

    [Theory]
    [InlineData("90.5", 90.5)]
    [InlineData("0", 0)]
    public void ProbeDurationUsesSeconds(string value, double expected) => MediaValidationService.ParseDuration(value).Should().Be(TimeSpan.FromSeconds(expected));
    [Fact]
    public void ProbeDurationRejectsInvalidValues()
    {
        MediaValidationService.ParseDuration("N/A").Should().BeNull(); MediaValidationService.ParseDuration(null).Should().BeNull();
        foreach (var value in new[] { "NaN", "-1", "oops", "Infinity" }) FluentActions.Invoking(() => MediaValidationService.ParseDuration(value)).Should().Throw<InvalidDataException>();
        var type = typeof(MediaValidationService).GetNestedType("FFprobeLatestVersion", System.Reflection.BindingFlags.NonPublic)!;
        var result = JsonSerializer.Deserialize("""{"version":"6.1","bin":{"linux-64":{"ffprobe":"https://example.test/probe.zip"}}}""", type)!;
        type.GetProperty("Version")!.GetValue(result).Should().Be("6.1"); type.GetProperty("Bin")!.GetValue(result).Should().NotBeNull();
    }

    [Fact]
    public async Task ImportConfirmationDoesNotMistakePageTwoOrFailedPageForSuccess()
    {
        using var stub = new ArrStub("radarr");
        var config = new TorrentarrConfig();
        config.ArrInstances.Clear(); config.ArrInstances["Arr"] = Config("radarr", stub.Url);
        await using var db = new TorrentarrDbContext(new DbContextOptionsBuilder<TorrentarrDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        var service = new ArrImportService(NullLogger<ArrImportService>.Instance, config, Moq.Mock.Of<Torrentarr.Core.Interfaces.ITorrentClientRegistry>(), db);
        stub.QueueTarget = true;
        (await service.IsImportedAsync("TARGET")).Should().BeFalse();
        stub.QueueTarget = false;
        (await service.IsImportedAsync("TARGET")).Should().BeTrue();
        stub.FailQueuePageTwo = true;
        (await service.IsImportedAsync("TARGET")).Should().BeFalse();
    }

    [Fact]
    public async Task OwnerReconciliationRepairsFreeSpaceRowsAndClearsOnlyOldOwnerState()
    {
        var config = new TorrentarrConfig(); config.ArrInstances.Clear();
        config.ArrInstances["Radarr-Movies"] = new() { Category = "movies", Type = "radarr", MatchSubcategories = true };
        config.ArrInstances["Radarr-Other"] = new() { Category = "other", Type = "radarr" };
        await using var db = new TorrentarrDbContext(new DbContextOptionsBuilder<TorrentarrDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        db.TorrentLibrary.Add(new() { Hash = "a", QbitInstance = "qBit", Category = "movies/4k", FreeSpacePaused = true, Imported = true, AllowedSeeding = true });
        await db.SaveChangesAsync();
        var cache = new TorrentCacheService(NullLogger<TorrentCacheService>.Instance); var tracker = new ImportPathTracker();
        cache.MarkFileFiltered("qBit", "a"); tracker.MarkScanned("qBit", "/movie", "a"); tracker.MarkScanned("other-client", "/movie", "a");
        var processor = new TorrentProcessor(NullLogger<TorrentProcessor>.Instance, Moq.Mock.Of<Torrentarr.Core.Interfaces.ITorrentClientRegistry>(), db, config, cache, new(), pathTracker: tracker);
        var torrent = new Torrentarr.Core.Models.TorrentInfo { Hash = "a", Category = "movies/4k", ClientInstanceId = "qBit" };
        var ensure = typeof(TorrentProcessor).GetMethod("EnsureTorrentInDatabaseAsync", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
        await (Task)ensure.Invoke(processor, [torrent, "movies", CancellationToken.None])!;
        var row = await db.TorrentLibrary.SingleAsync();
        row.Category.Should().Be("movies"); row.ArrInstance.Should().Be("Radarr-Movies"); row.Imported.Should().BeFalse();
        row.FreeSpacePaused.Should().BeTrue(); row.AllowedSeeding.Should().BeTrue();
        tracker.IsHashAlreadyScanned("qBit", "a").Should().BeFalse(); tracker.IsHashAlreadyScanned("other-client", "a").Should().BeTrue();
        row.Imported = true; await db.SaveChangesAsync(); torrent.Category = "other";
        await (Task)ensure.Invoke(processor, [torrent, "other", CancellationToken.None])!;
        row.ArrInstance.Should().Be("Radarr-Other"); row.Imported.Should().BeFalse();
    }

    [Fact]
    public async Task FullCommandQueueDoesNotPrepareProfiles_AndOnlySubmittedPlansArePrepared()
    {
        using var stub = new ArrStub("radarr");
        var config = new TorrentarrConfig(); config.Settings.SearchLoopDelay = 1; config.ArrInstances.Clear(); config.ArrInstances["Arr"] = Config("radarr", stub.Url);
        config.ArrInstances["Arr"].Search.SearchLimit = 1;
        await using var db = new TorrentarrDbContext(new DbContextOptionsBuilder<TorrentarrDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        db.Movies.AddRange(new MoviesFilesModel { EntryId = 1, ArrId = 42, ArrInstance = "Arr", QualityProfileId = 7 }, new MoviesFilesModel { EntryId = 2, ArrId = 43, ArrInstance = "Arr", QualityProfileId = 7 });
        await db.SaveChangesAsync();
        var switcher = new QualityProfileSwitcherService(NullLogger<QualityProfileSwitcherService>.Instance, db, new());
        var candidates = new[] { new SearchCandidate { ArrId = 42, Type = "Movie", Reason = "Missing" }, new SearchCandidate { ArrId = 43, Type = "Movie", Reason = "Missing" } };
        var full = new CapacityExecutor(config, db, switcher, () => 1);
        (await full.ExecuteSearchesAsync("Arr", candidates)).SearchesTriggered.Should().Be(0); stub.Puts.Should().Be(0);
        var checks = 0;
        var one = new CapacityExecutor(config, db, switcher, () => ++checks <= 2 ? 0 : 1);
        (await one.ExecuteSearchesAsync("Arr", candidates)).SearchesTriggered.Should().Be(1); stub.Puts.Should().Be(1);
        (await db.Movies.SingleAsync(m => m.ArrId == 43)).OriginalProfileId.Should().BeNull();
        (await db.Movies.SingleAsync(m => m.ArrId == 43)).Searched.Should().BeFalse();
    }

    [Fact]
    public async Task FailedProfilePreparationSkipsOnlyItsSearchPlan()
    {
        using var stub = new ArrStub("radarr") { RejectId = 42 };
        var config = new TorrentarrConfig(); config.Settings.SearchLoopDelay = 1; config.ArrInstances.Clear(); config.ArrInstances["Arr"] = Config("radarr", stub.Url);
        await using var db = new TorrentarrDbContext(new DbContextOptionsBuilder<TorrentarrDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        db.Movies.AddRange(new MoviesFilesModel { EntryId = 1, ArrId = 42, ArrInstance = "Arr", QualityProfileId = 7 }, new MoviesFilesModel { EntryId = 2, ArrId = 43, ArrInstance = "Arr", QualityProfileId = 7 }); await db.SaveChangesAsync();
        var switcher = new QualityProfileSwitcherService(NullLogger<QualityProfileSwitcherService>.Instance, db, new());
        var executor = new CapacityExecutor(config, db, switcher, () => 0);
        var result = await executor.ExecuteSearchesAsync("Arr", [new SearchCandidate { ArrId = 42, Type = "Movie", Reason = "Missing" }, new SearchCandidate { ArrId = 43, Type = "Movie", Reason = "Missing" }]);
        result.SearchesTriggered.Should().Be(1); result.Errors.Should().ContainSingle(); result.LoopCompleted.Should().BeFalse();
        db.ChangeTracker.Clear();
        (await db.Movies.SingleAsync(m => m.ArrId == 42)).Searched.Should().BeFalse();
        (await db.Movies.SingleAsync(m => m.ArrId == 42)).OriginalProfileId.Should().Be(7);
        (await db.Movies.SingleAsync(m => m.ArrId == 43)).Searched.Should().BeTrue();
    }

    [Fact]
    public async Task ManualChangeBetweenInspectionAndPutIsPreserved()
    {
        using var stub = new ArrStub("radarr") { ChangeOnSecondRead = true };
        await using var db = new TorrentarrDbContext(new DbContextOptionsBuilder<TorrentarrDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        db.Movies.Add(new() { EntryId = 1, ArrId = 42, ArrInstance = "Arr", QualityProfileId = 7 }); await db.SaveChangesAsync();
        var switcher = new QualityProfileSwitcherService(NullLogger<QualityProfileSwitcherService>.Instance, db, new());
        (await switcher.SwitchToTempProfilesAsync("Arr", Config("radarr", stub.Url), [new SearchCandidate { ArrId = 42, Reason = "Missing" }])).Should().BeFalse();
        stub.Profile.Should().Be(11); stub.Puts.Should().Be(0);
        db.ChangeTracker.Clear(); (await db.Movies.SingleAsync()).OriginalProfileId.Should().Be(7);
    }

    private sealed class CapacityExecutor(TorrentarrConfig config, TorrentarrDbContext db, QualityProfileSwitcherService switcher, Func<int> active)
        : SearchExecutor(NullLogger<SearchExecutor>.Instance, config, db, switcher, new())
    {
        public override Task<int> GetActiveCommandCountAsync(string instanceName, CancellationToken ct = default) => Task.FromResult(active());
        protected override Task<bool> TriggerSearchForCandidateAsync(string instanceName, ArrInstanceConfig config, SearchCandidate candidate, bool series, CancellationToken ct) => Task.FromResult(true);
    }

    private static ArrInstanceConfig Config(string type, string url) => new() { Type = type, URI = url, APIKey = "test", Search = new() { UseTempForMissing = true, ForceResetTempProfiles = true, ProfileSwitchRetryAttempts = 1, QualityProfileMappings = new() { ["HD"] = "Any" } } };
    private sealed class FailingDb(DbContextOptions<TorrentarrDbContext> options) : TorrentarrDbContext(options)
    {
        public bool FailSaves { get; set; }
        public override Task<int> SaveChangesAsync(CancellationToken ct = default) => FailSaves ? throw new IOException("simulated save failure") : base.SaveChangesAsync(ct);
    }

    private sealed class ArrStub : IDisposable
    {
        private readonly HttpListener _listener = new();
        private readonly Task _loop;
        public string Url { get; }
        public int Profile = 7;
        public int Puts;
        public bool RejectPut;
        public int RejectId;
        public bool ChangeOnSecondRead;
        private int _profileReads;
        public bool QueueTarget;
        public bool FailQueuePageTwo;
        public ArrStub(string type)
        {
            var socket = new TcpListener(IPAddress.Loopback, 0); socket.Start(); var port = ((IPEndPoint)socket.LocalEndpoint).Port; socket.Stop();
            Url = $"http://127.0.0.1:{port}"; _listener.Prefixes.Add(Url + "/"); _listener.Start();
            _loop = Task.Run(async () =>
            {
                while (_listener.IsListening)
                {
                    HttpListenerContext context;
                    try { context = await _listener.GetContextAsync(); } catch (Exception ex) when (ex is HttpListenerException or ObjectDisposedException) { break; }
                    var path = context.Request.Url!.AbsolutePath;
                    if (context.Request.HttpMethod == "GET" && path.EndsWith("/movie/42") && ++_profileReads == 2 && ChangeOnSecondRead) Profile = 11;
                    var body = path == "/api" ? """{"current":"v3"}""" : path.EndsWith("qualityprofile") ? """[{"id":7,"name":"HD"},{"id":99,"name":"Any"}]"""
                        : path.EndsWith("system/status") ? """{"version":"4.0.0"}"""
                        : $"{{\"id\":42,\"qualityProfileId\":{Profile},\"title\":\"Item\",\"tags\":[3],\"metadataProfileId\":1}}";
                    if (path.EndsWith("/queue"))
                    {
                        var page = int.Parse(context.Request.QueryString["page"] ?? "1");
                        body = JsonSerializer.Serialize(new
                        {
                            page,
                            pageSize = 2,
                            totalRecords = 3,
                            records = page == 1
                            ? new[] { new { id = 1, downloadId = "first" }, new { id = 2, downloadId = "second" } }
                            : new[] { new { id = 3, downloadId = QueueTarget ? "TARGET" : "third" } }
                        });
                        if (page == 2 && FailQueuePageTwo) context.Response.StatusCode = 400;
                    }
                    if (context.Request.HttpMethod == "PUT")
                    {
                        Puts++;
                        using var reader = new StreamReader(context.Request.InputStream);
                        var raw = await reader.ReadToEndAsync();
                        if (RejectPut || RejectId != 0 && path.EndsWith("/" + RejectId)) context.Response.StatusCode = 400;
                        else { Profile = JsonDocument.Parse(raw).RootElement.GetProperty("qualityProfileId").GetInt32(); body = raw; }
                    }
                    var bytes = Encoding.UTF8.GetBytes(body); context.Response.ContentType = "application/json"; context.Response.ContentLength64 = bytes.Length;
                    await context.Response.OutputStream.WriteAsync(bytes); context.Response.Close();
                }
            });
        }
        public void Dispose() { _listener.Close(); _loop.GetAwaiter().GetResult(); }
    }
}

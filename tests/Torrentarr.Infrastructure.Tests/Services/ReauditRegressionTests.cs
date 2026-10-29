using System.Net;
using System.Net.Sockets;
using System.Text;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Torrentarr.Core.Configuration;
using Torrentarr.Core.Interfaces;
using Torrentarr.Core.Models;
using Torrentarr.Core.Services;
using Torrentarr.Infrastructure.Database;
using Torrentarr.Infrastructure.Database.Models;
using Torrentarr.Infrastructure.Services;
using Xunit;

namespace Torrentarr.Infrastructure.Tests.Services;

public sealed class ReauditRegressionTests
{
    private static readonly SearchCandidate[] Candidates = [new() { ArrId = 42, Type = "Movie", Reason = "Retry" }];
    private static Mock<IOperationalIssueReporter> Reporter(List<string> reports, List<string> resolutions)
    {
        var mock = new Mock<IOperationalIssueReporter>();
        mock.Setup(r => r.ReportAsync(It.IsAny<OperationalIssueReport>(), It.IsAny<CancellationToken>())).Callback<OperationalIssueReport, CancellationToken>((r, _) => reports.Add(r.Code)).Returns(Task.CompletedTask);
        mock.Setup(r => r.ResolveAsync(It.IsAny<OperationalIssueReport>(), It.IsAny<CancellationToken>())).Callback<OperationalIssueReport, CancellationToken>((r, _) => resolutions.Add(r.Code)).Returns(Task.CompletedTask);
        return mock;
    }
    private static DbContextOptions<TorrentarrDbContext> Options() => new DbContextOptionsBuilder<TorrentarrDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options;
    private static ArrInstanceConfig Config(string type, string url) => new() { Type = type, URI = url, APIKey = "test", Category = "media", ReSearch = true };

    [Theory]
    [InlineData("radarr")]
    [InlineData("sonarr")]
    [InlineData("lidarr")]
    [InlineData("readarr")]
    public async Task SaveFailurePreventsAnyBlocklisting(string type)
    {
        using var stub = new ArrStub();
        await using var db = new FailingDb(Options()) { FailAll = true };
        await FluentActions.Awaiting(() => SonarrRetryQueue.StageAndExecuteAsync(db, Mock.Of<ISearchExecutor>(), "Arr", "ABC", Candidates.ToList(), Config(type, stub.Url), NullLogger.Instance, null, default)).Should().ThrowAsync<IOException>();
        stub.Deletes.Should().Be(0);
    }

    [Theory]
    [InlineData("radarr")]
    [InlineData("sonarr")]
    [InlineData("lidarr")]
    [InlineData("readarr")]
    public async Task CrashAfterDeletePreservesIntent_AndRestartRetriesWithoutDeletingAgain(string type)
    {
        using var stub = new ArrStub();
        using var connection = new Microsoft.Data.Sqlite.SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<TorrentarrDbContext>().UseSqlite(connection).Options;
        var executor = new Mock<ISearchExecutor>();
        executor.Setup(e => e.ExecuteSearchesAsync("Arr", It.IsAny<IEnumerable<SearchCandidate>>(), It.IsAny<CancellationToken>())).ReturnsAsync(new SearchResult { LoopCompleted = true });
        await using (var db = new FailingDb(options) { FailConfirmation = true })
        {
            await db.Database.EnsureCreatedAsync();
            (await SonarrRetryQueue.StageAndExecuteAsync(db, executor.Object, "Arr", "ABC", Candidates.ToList(), Config(type, stub.Url), NullLogger.Instance, null, default)).Should().BeFalse();
            stub.Deletes.Should().Be(1);
            executor.Verify(e => e.ExecuteSearchesAsync(It.IsAny<string>(), It.IsAny<IEnumerable<SearchCandidate>>(), It.IsAny<CancellationToken>()), Times.Never);
        }
        await using var restarted = new TorrentarrDbContext(options);
        (await restarted.SonarrSearchRetries.SingleAsync()).BlocklistPending.Should().BeTrue();
        var log = new CaptureLogger();
        await SonarrRetryQueue.ExecuteAsync(restarted, executor.Object, "Arr", log, default, Config(type, stub.Url));
        stub.Deletes.Should().Be(1);
        (await restarted.SonarrSearchRetries.CountAsync()).Should().Be(0, log.Message);
        executor.Verify(e => e.ExecuteSearchesAsync("Arr", It.IsAny<IEnumerable<SearchCandidate>>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Theory]
    [InlineData("radarr")]
    [InlineData("lidarr")]
    [InlineData("readarr")]
    public async Task RejectedReplacementPersistsAndReports_ThenSuccessfulRetryResolves(string type)
    {
        using var stub = new ArrStub { RejectSearch = true };
        var cfg = new TorrentarrConfig(); cfg.ArrInstances["Arr"] = Config(type, stub.Url);
        var reports = new List<string>(); var resolved = new List<string>(); var reporter = Reporter(reports, resolved);
        await using var db = new TorrentarrDbContext(Options());
        var imports = new ArrImportService(NullLogger<ArrImportService>.Instance, cfg, Mock.Of<ITorrentClientRegistry>(), db, issues: reporter.Object);
        (await imports.BlocklistAndReSearchAsync("ABC", "media")).Should().BeFalse();
        (await db.SonarrSearchRetries.SingleAsync()).BlocklistPending.Should().BeFalse();
        reports.Should().Contain("import.replacement"); resolved.Should().NotContain("import.replacement");
        stub.RejectSearch = false;
        (await imports.BlocklistAndReSearchAsync("ABC", "media")).Should().BeTrue();
        (await db.SonarrSearchRetries.CountAsync()).Should().Be(0);
        resolved.Should().Contain("import.replacement");
        stub.Deletes.Should().Be(1);
    }

    [Theory]
    [InlineData("radarr")]
    [InlineData("sonarr")]
    [InlineData("lidarr")]
    [InlineData("readarr")]
    public async Task FailedCapacityReadCannotPrepareOrSubmit_AndRecoveryClosesIssue(string type)
    {
        using var stub = new ArrStub { RejectCapacity = true };
        var cfg = new TorrentarrConfig(); cfg.ArrInstances["Arr"] = Config(type, stub.Url);
        cfg.ArrInstances["Arr"].Search.UseTempForMissing = true;
        cfg.ArrInstances["Arr"].Search.QualityProfileMappings["Original"] = "Temporary";
        await using var db = new TorrentarrDbContext(Options());
        var reports = new List<string>(); var resolved = new List<string>(); var reporter = Reporter(reports, resolved);
        var executor = new SearchExecutor(NullLogger<SearchExecutor>.Instance, cfg, db, new QualityProfileSwitcherService(NullLogger<QualityProfileSwitcherService>.Instance, db, new()), new(), reporter.Object);
        await FluentActions.Awaiting(() => executor.GetActiveCommandCountAsync("Arr")).Should().ThrowAsync<Exception>();
        if (type == "radarr") await FluentActions.Awaiting(() => executor.ExecuteSearchesAsync("Arr", [new() { ArrId = 42, Reason = "Missing" }])).Should().ThrowAsync<Exception>();
        stub.Puts.Should().Be(0); stub.Searches.Should().Be(0); reports.Should().Contain("search.capacity");
        stub.RejectCapacity = false;
        (await executor.GetActiveCommandCountAsync("Arr")).Should().Be(0);
        resolved.Should().Contain("search.capacity");
    }

    [Theory]
    [InlineData("radarr")]
    [InlineData("sonarr")]
    [InlineData("lidarr")]
    [InlineData("readarr")]
    public async Task RejectedBlocklistPreservesStagedSearchUntilVerifiedRetry(string type)
    {
        using var stub = new ArrStub { RejectDelete = true };
        await using var db = new TorrentarrDbContext(Options());
        var executor = new Mock<ISearchExecutor>();
        executor.Setup(e => e.ExecuteSearchesAsync("Arr", It.IsAny<IEnumerable<SearchCandidate>>(), It.IsAny<CancellationToken>())).ReturnsAsync(new SearchResult { LoopCompleted = true });
        var reports = new List<string>(); var resolved = new List<string>(); var reporter = Reporter(reports, resolved);
        (await SonarrRetryQueue.StageAndExecuteAsync(db, executor.Object, "Arr", "ABC", Candidates.ToList(), Config(type, stub.Url), NullLogger.Instance, reporter.Object, default)).Should().BeFalse();
        (await db.SonarrSearchRetries.SingleAsync()).BlocklistPending.Should().BeTrue();
        executor.Verify(e => e.ExecuteSearchesAsync(It.IsAny<string>(), It.IsAny<IEnumerable<SearchCandidate>>(), It.IsAny<CancellationToken>()), Times.Never);
        reports.Should().Contain("import.replacement"); resolved.Should().BeEmpty();
        stub.RejectDelete = false;
        await SonarrRetryQueue.ExecuteAsync(db, executor.Object, "Arr", NullLogger.Instance, default, Config(type, stub.Url), reporter.Object);
        (await db.SonarrSearchRetries.CountAsync()).Should().Be(0);
        resolved.Should().Contain("import.replacement");
    }

    [Fact]
    public async Task CapacityExhaustionRetainsReplacementWithoutIssue()
    {
        using var stub = new ArrStub();
        await using var db = new TorrentarrDbContext(Options());
        var executor = new Mock<ISearchExecutor>();
        executor.Setup(e => e.ExecuteSearchesAsync(It.IsAny<string>(), It.IsAny<IEnumerable<SearchCandidate>>(), It.IsAny<CancellationToken>())).ReturnsAsync(new SearchResult());
        var reports = new List<string>(); var resolved = new List<string>(); var reporter = Reporter(reports, resolved);
        (await SonarrRetryQueue.StageAndExecuteAsync(db, executor.Object, "Arr", "ABC", Candidates.ToList(), Config("radarr", stub.Url), NullLogger.Instance, reporter.Object, default)).Should().BeFalse();
        reports.Should().BeEmpty(); resolved.Should().BeEmpty();
        (await db.SonarrSearchRetries.CountAsync()).Should().Be(1);
    }

    [Fact]
    public async Task ExistingClientObjectRequiresFreshConnectivityForRecovery()
    {
        var cfg = new TorrentarrConfig(); cfg.QBitInstances.Clear(); cfg.QBitInstances["qBit"] = new() { Host = "localhost" };
        var online = false;
        var client = new Mock<ITorrentClient>(); client.Setup(c => c.GetVersionAsync(It.IsAny<CancellationToken>())).Returns(() => online ? Task.FromResult("5.0") : Task.FromException<string>(new IOException()));
        var registry = new Mock<ITorrentClientRegistry>(); registry.Setup(r => r.GetClient("qBit")).Returns(client.Object);
        var reports = new List<string>(); var resolved = new List<string>(); var reporter = Reporter(reports, resolved);
        await using var services = new ServiceCollection().AddSingleton(registry.Object).BuildServiceProvider();
        using var monitor = new OperationalIssueMonitor(services.GetRequiredService<IServiceScopeFactory>(), reporter.Object, cfg, NullLogger<OperationalIssueMonitor>.Instance);
        await monitor.InspectAsync(default); reports.Should().Contain("client.connection"); resolved.Should().BeEmpty();
        online = true; await monitor.InspectAsync(default); resolved.Should().Contain("client.connection");
    }

    [Fact]
    public async Task FailedTorrentWorkReportsAndFailsCycle_WhileIndependentTorrentsContinue()
    {
        var cfg = new TorrentarrConfig(); cfg.Settings.Tagless = true; cfg.ArrInstances["Arr"] = new() { Type = "radarr", Category = "media" };
        var client = new Mock<ITorrentClient>();
        var bad = new TorrentInfo { Hash = "bad", Category = "media", AddedOn = long.MaxValue };
        var good = new TorrentInfo { Hash = "good", Category = "media", AddedOn = 0, State = "uploading" };
        client.Setup(c => c.GetTorrentsAsync(It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>())).ReturnsAsync([bad, good]);
        var registry = new Mock<ITorrentClientRegistry>(); registry.Setup(r => r.GetAllClients()).Returns(new Dictionary<string, ITorrentClient> { ["qBit"] = client.Object });
        registry.Setup(r => r.GetClient("qBit")).Returns(client.Object);
        await using var db = new TorrentarrDbContext(Options());
        var reports = new List<string>(); var resolved = new List<string>(); var reporter = Reporter(reports, resolved);
        var processor = new TorrentProcessor(NullLogger<TorrentProcessor>.Instance, registry.Object, db, cfg, new TorrentCacheService(NullLogger<TorrentCacheService>.Instance), new(), issues: reporter.Object);
        await FluentActions.Awaiting(() => processor.ProcessTorrentsAsync("media")).Should().ThrowAsync<AggregateException>();
        reports.Should().Contain("torrent.processing");
        (await db.TorrentLibrary.AnyAsync(t => t.Hash == "good")).Should().BeTrue();
        bad.AddedOn = 0; bad.State = "uploading"; resolved.Clear();
        await processor.ProcessTorrentsAsync("media"); resolved.Should().Contain("torrent.processing");
        client.Setup(c => c.GetTorrentsAsync(It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>())).ThrowsAsync(new IOException());
        await FluentActions.Awaiting(() => processor.ProcessTorrentsAsync("media")).Should().ThrowAsync<IOException>();
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task PerTorrent404_IsQuietOnlyAfterVerifiedDisappearance(bool stillPresent, bool refreshFails)
    {
        var cfg = new TorrentarrConfig(); cfg.Settings.Tagless = true;
        cfg.ArrInstances["Arr"] = new() { Type = "radarr", Category = "media" };
        var torrent = new TorrentInfo { Hash = "gone", ClientInstanceId = "client", Category = "media", AddedOn = 1 };
        var client = new Mock<ITorrentClient>();
        client.Setup(c => c.GetTorrentsAsync(It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .Returns(() => refreshFails ? Task.FromException<List<TorrentInfo>>(new IOException("refresh failed"))
                : Task.FromResult(stillPresent ? new List<TorrentInfo> { torrent } : []));
        var registry = new Mock<ITorrentClientRegistry>(); registry.Setup(r => r.GetClient("client")).Returns(client.Object);
        var seeding = new Mock<ISeedingService>();
        seeding.Setup(s => s.ApplySeedingLimitsAsync(torrent, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpRequestException("inventory missing", null, HttpStatusCode.NotFound));
        await using var db = new TorrentarrDbContext(Options());
        var reports = new List<string>(); var resolved = new List<string>(); var reporter = Reporter(reports, resolved);
        var processor = new TorrentProcessor(NullLogger<TorrentProcessor>.Instance, registry.Object, db, cfg,
            new TorrentCacheService(NullLogger<TorrentCacheService>.Instance), new(), seedingService: seeding.Object, issues: reporter.Object);
        var method = typeof(TorrentProcessor).GetMethod("ProcessObservedTorrentAsync", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
        Func<Task> action = () => (Task)method.Invoke(processor, [torrent, "media", new TorrentProcessingStats(), CancellationToken.None])!;
        if (refreshFails) await FluentActions.Awaiting(action).Should().ThrowAsync<IOException>();
        else if (stillPresent) await FluentActions.Awaiting(action).Should().ThrowAsync<HttpRequestException>();
        else
        {
            await action();
            reports.Should().BeEmpty();
            resolved.Should().Contain("torrent.processing");
        }
        client.Verify(c => c.GetTorrentsAsync(null, null, It.IsAny<CancellationToken>()), Times.Once);
    }

    private sealed class CaptureLogger : Microsoft.Extensions.Logging.ILogger
    {
        public string Message = "";
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(Microsoft.Extensions.Logging.LogLevel level) => true;
        public void Log<TState>(Microsoft.Extensions.Logging.LogLevel level, Microsoft.Extensions.Logging.EventId id, TState state, Exception? exception, Func<TState, Exception?, string> formatter) => Message += exception?.ToString();
    }
    private sealed class FailingDb(DbContextOptions<TorrentarrDbContext> options) : TorrentarrDbContext(options)
    {
        public bool FailAll { get; init; }
        public bool FailConfirmation { get; init; }
        public override Task<int> SaveChangesAsync(CancellationToken ct = default)
        {
            if (FailAll || FailConfirmation && ChangeTracker.Entries<SonarrSearchRetry>().Any(e => e.State == EntityState.Modified && !e.Entity.BlocklistPending)) throw new IOException("Injected save failure");
            return base.SaveChangesAsync(ct);
        }
    }

    private sealed class ArrStub : IDisposable
    {
        private readonly HttpListener _listener = new(); private readonly Task _loop;
        public string Url { get; }
        public bool RejectSearch { get; set; }
        public bool RejectCapacity { get; set; }
        public bool RejectDelete { get; set; }
        public int Deletes, Puts, Searches;
        public ArrStub()
        {
            var socket = new TcpListener(IPAddress.Loopback, 0); socket.Start(); var port = ((IPEndPoint)socket.LocalEndpoint).Port; socket.Stop();
            Url = $"http://127.0.0.1:{port}"; _listener.Prefixes.Add(Url + "/"); _listener.Start();
            _loop = Task.Run(async () =>
            {
                while (_listener.IsListening)
                {
                    HttpListenerContext context; try { context = await _listener.GetContextAsync(); } catch { break; }
                    var path = context.Request.Url!.AbsolutePath; var method = context.Request.HttpMethod; var body = "[]";
                    if (path.EndsWith("/api")) body = "{\"current\":\"v3\"}";
                    else if (path.EndsWith("/system/status")) body = """{"version":"4.0.0","appName":"Sonarr"}""";
                    else if (path.EndsWith("/queue")) body = Deletes > 0 ? """{"page":1,"pageSize":1000,"totalRecords":0,"records":[]}""" : """{"page":1,"pageSize":1000,"totalRecords":1,"records":[{"id":1,"downloadId":"ABC","movieId":42,"albumId":42,"bookId":42,"episodeIds":[42],"seriesId":7}]}""";
                    else if (method == "DELETE") { if (RejectDelete) context.Response.StatusCode = 400; else Deletes++; body = "{}"; }
                    else if (method == "PUT") { Puts++; body = "{}"; }
                    else if (path.EndsWith("/command"))
                    {
                        if (method == "POST") { Searches++; if (RejectSearch) context.Response.StatusCode = 400; }
                        else if (RejectCapacity) context.Response.StatusCode = 400;
                    }
                    context.Response.ContentType = "application/json";
                    var bytes = Encoding.UTF8.GetBytes(body); await context.Response.OutputStream.WriteAsync(bytes); context.Response.Close();
                }
            });
        }
        public void Dispose() { _listener.Stop(); _listener.Close(); _loop.GetAwaiter().GetResult(); }
    }
}

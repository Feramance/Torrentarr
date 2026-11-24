using System.Net;
using System.Text.Json;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Newtonsoft.Json.Linq;
using RestSharp;
using Torrentarr.Core.Configuration;
using Torrentarr.Core.Models;
using Torrentarr.Infrastructure.Database;
using Torrentarr.Infrastructure.Database.Models;
using Torrentarr.Infrastructure.Endpoints;
using Torrentarr.Infrastructure.Services;
using Xunit;

namespace Torrentarr.Infrastructure.Tests.Services;

public sealed class WorkerHistoryTests : IDisposable
{
    private readonly SqliteConnection _connection = new("Data Source=:memory:");
    private readonly ServiceProvider _services;
    private readonly ArrInstanceConfig _arr = new() { URI = "http://arr", Type = "radarr" };
    public WorkerHistoryTests()
    {
        _connection.Open();
        _services = new ServiceCollection().AddDbContext<TorrentarrDbContext>(o => o.UseSqlite(_connection)).BuildServiceProvider();
        using var db = Db();
        db.Database.EnsureCreated();
    }
    private TorrentarrDbContext Db() => new(new DbContextOptionsBuilder<TorrentarrDbContext>().UseSqlite(_connection).Options);
    private IDisposable Begin(string instance = "Radarr-one", string loop = "search") =>
        WorkerHistory.Begin(_services.GetRequiredService<IServiceScopeFactory>(), NullLogger.Instance, instance, loop, _arr);
    public void Dispose() { _services.Dispose(); _connection.Dispose(); }

    [Fact]
    public async Task RecordingFollowsAsyncWorkerIdentityAndKeepsLoopsIndependent()
    {
        using (Begin())
        {
            using (WorkerHistory.Active("Searching movie"))
            {
                WorkerHistory.Describe("Radarr-one", "search", new("Running", DateTimeOffset.UtcNow)).CurrentAction.Should().Be("Searching movie");
                using (Begin("Radarr-two", "torrent"))
                    await WorkerHistory.RecordAsync(new() { Type = "torrent-removal", Summary = "Removed torrent", Outcome = "completed", ClientInstance = "seedbox" });
                await WorkerHistory.RecordAsync(new() { Type = "search", Summary = "Searched movie", Outcome = "submitted" });
            }
            WorkerHistory.Describe("Radarr-one", "search", new("Sleeping", DateTimeOffset.UtcNow)).CurrentAction.Should().BeNull();
        }
        using var db = Db();
        var rows = await db.WorkerHistory.OrderBy(e => e.Id).ToListAsync();
        rows.Select(e => e.Instance).Should().Equal("Radarr-two", "Radarr-one");
        rows.Select(e => e.Loop).Should().Equal("torrent", "search");
        var restored = WorkerHistory.RestoreLast(null, false, "Radarr-one", "search", await WorkerHistory.LastActionsAsync(db, ["Radarr-one", "Radarr-two"]));
        restored!.LastAction.Should().Be("Searched movie");
        restored.CurrentAction.Should().BeNull();
    }

    [Theory]
    [InlineData(typeof(MoviesFilesModel), "movie")]
    [InlineData(typeof(SeriesFilesModel), "series")]
    [InlineData(typeof(EpisodeFilesModel), "episode")]
    [InlineData(typeof(ArtistFilesModel), "artist")]
    [InlineData(typeof(AlbumFilesModel), "album")]
    [InlineData(typeof(TrackFilesModel), "track")]
    [InlineData(typeof(AuthorFilesModel), "author")]
    [InlineData(typeof(BookFilesModel), "book")]
    public async Task CatalogRecordsCommittedAddsRealUpdatesAndDeletes(Type model, string subject)
    {
        using var context = Begin();
        using var observe = WorkerHistory.ObserveCatalog();
        using var db = Db();
        var entity = Activator.CreateInstance(model)!;
        model.GetProperty("ArrInstance")!.SetValue(entity, "Radarr-one");
        model.GetProperty("ArrId")!.SetValue(entity, 42);
        model.GetProperty("Title")?.SetValue(entity, "Original title");
        db.Add(entity);
        await db.SaveChangesWithRetryAsync();
        await db.SaveChangesWithRetryAsync(); // An unchanged sync creates no event.
        if (model.GetProperty("Searched") is { } searched)
        {
            searched.SetValue(entity, true);
            await db.SaveChangesWithRetryAsync(); // Internal search bookkeeping is not a catalog update.
        }
        model.GetProperty("Title")!.SetValue(entity, "Changed title");
        await db.SaveChangesWithRetryAsync();
        db.Remove(entity);
        await db.SaveChangesWithRetryAsync();
        var events = await db.WorkerHistory.OrderBy(e => e.Id).ToListAsync();
        events.Select(e => e.Type).Should().Equal("add", "update", "delete");
        events.Should().OnlyContain(e => e.Source == "observed" && e.SubjectType == subject && e.SubjectId == "42");
        events[1].Details.Should().Contain("Original title").And.Contain("Changed title");
    }

    [Fact]
    public async Task FailedCatalogSaveDoesNotPublishAnEvent()
    {
        using var context = Begin();
        using var observe = WorkerHistory.ObserveCatalog();
        using var db = Db();
        db.Movies.Add(new() { ArrId = 42, ArrInstance = "Radarr-one", Title = null! });
        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesWithRetryAsync(maxAttempts: 1));
        (await db.WorkerHistory.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task RequestsRecordOnceAndNeverPersistCredentialsOrTrackerUrls()
    {
        using var context = Begin();
        var request = new RestRequest("/api/v3/command", Method.Post).AddJsonBody(new { name = "MoviesSearch", movieIds = new[] { 42 }, apiKey = "secret" });
        var response = new RestResponse { IsSuccessStatusCode = true, StatusCode = HttpStatusCode.Accepted, ResponseStatus = ResponseStatus.Completed, Content = "{\"id\":9}" };
        await WorkerHistory.ExecuteAsync(request, () => Task.FromResult(response));
        var tracker = new RestRequest("/api/v2/torrents/addTrackers", Method.Post).AddParameter("hash", "abc").AddParameter("urls", "https://private/secret");
        await WorkerHistory.ExecuteAsync(tracker, () => Task.FromResult(response), "seedbox");
        using var db = Db();
        var rows = await db.WorkerHistory.OrderBy(e => e.Id).ToListAsync();
        rows.Should().HaveCount(2);
        rows[0].Outcome.Should().Be("submitted");
        rows[0].CommandId.Should().Be(9);
        rows[0].Details.Should().Contain("42").And.NotContain("secret");
        rows[1].Details.Should().NotContain("private").And.NotContain("secret");
        rows[1].ClientInstance.Should().Be("seedbox");
    }

    [Fact]
    public async Task TransportFailureAndMissingReceiptRemainUnknownAndHistoryFailureDoesNotRetryMutation()
    {
        using var context = Begin();
        var request = new RestRequest("/api/v3/command", Method.Post).AddJsonBody(new { name = "MoviesSearch" });
        await Assert.ThrowsAsync<IOException>(() => WorkerHistory.ExecuteAsync(request, () => throw new IOException("private")));
        var accepted = new RestResponse { IsSuccessStatusCode = true, StatusCode = HttpStatusCode.Accepted, ResponseStatus = ResponseStatus.Completed, Content = "{}" };
        await WorkerHistory.ExecuteAsync(request, () => Task.FromResult(accepted));
        using var db = Db();
        (await db.WorkerHistory.ToListAsync()).Should().OnlyContain(e => e.Outcome == "unknown");
        await db.Database.ExecuteSqlRawAsync("DROP TABLE torrentarr_worker_history");
        var calls = 0;
        await WorkerHistory.ExecuteAsync(request, () => { calls++; return Task.FromResult(accepted); });
        calls.Should().Be(1);
    }

    [Fact]
    public void ReconciliationRequiresCompletionEvidenceAndKeepsImportSubmissionPending()
    {
        var now = DateTimeOffset.UtcNow;
        var server = ArrActivityTracking.ServerKey(_arr);
        var search = new WorkerHistoryEvent { CommandId = 1, Type = "search", Outcome = "submitted", Server = server, Timestamp = now };
        var import = new WorkerHistoryEvent { CommandId = 2, Type = "import", Outcome = "submitted", Server = server, Timestamp = now, SubmittedAt = now.ToUnixTimeSeconds() };
        var commands = JArray.Parse("[{\"id\":1,\"status\":\"completed\",\"result\":\"successful\"},{\"id\":2,\"status\":\"completed\",\"result\":\"successful\"}]");
        ArrActivityCollector.ReconcileHistory([search, import], [], commands, server, now);
        search.Outcome.Should().Be("completed");
        import.Outcome.Should().Be("submitted");
        ArrActivityCollector.ReconcileHistory([import], [new() { CommandId = 2, Server = server, SubmittedAt = now.ToUnixTimeSeconds(), Outcome = "completed" }], commands, server, now);
        import.Outcome.Should().Be("completed");
    }

    [Fact]
    public void ImportHistoryRetainsFailureReasonsAndDoesNotTurnPartialImportsIntoSuccess()
    {
        var now = DateTimeOffset.UtcNow;
        var entry = new WorkerHistoryEvent { Type = "import", Outcome = "submitted", Server = "server", Timestamp = now, CommandId = 42 };
        var command = new JObject { ["id"] = 42, ["name"] = "ManualImport", ["status"] = "failed", ["message"] = "Permission denied" };
        var file = new ImportPlanFile { CommandId = 42, State = "pending" };
        var plan = new ImportPlanReceipt { State = "pending", Files = [file] };
        ArrActivityCollector.ReconcileHistory([entry], [], new JArray(command), "server", now, [plan]);
        entry.Outcome.Should().Be("submitted");
        JObject.Parse(entry.Details)["failureReason"]!.Value<string>().Should().Be("Permission denied");
        file.State = "confirmed";
        plan.State = "partial";
        ArrActivityCollector.ReconcileHistory([entry], [], new JArray(command), "server", now, [plan]);
        entry.Outcome.Should().Be("partial");
        entry.CompletedAt.Should().Be(now);
    }

    [Fact]
    public async Task UpgradeAddsHistoryTableWithoutChangingExistingCatalog()
    {
        using var db = Db();
        db.Movies.Add(new() { ArrId = 42, Title = "Existing movie", ArrInstance = "Radarr-one" });
        await db.SaveChangesAsync();
        await db.Database.ExecuteSqlRawAsync("DROP TABLE torrentarr_worker_history");
        ManualSqliteMigrations.Apply(db);
        ManualSqliteMigrations.Apply(db);
        using (Begin()) await WorkerHistory.RecordAsync(new() { Type = "search", Summary = "After upgrade" });
        (await db.Movies.SingleAsync()).Title.Should().Be("Existing movie");
        (await db.WorkerHistory.SingleAsync()).Summary.Should().Be("After upgrade");
    }

    [Fact]
    public async Task FilteringPaginationRetentionAndRepeatedMigrationPreserveHistory()
    {
        using var context = Begin();
        await WorkerHistory.RecordAsync(new() { Type = "search", Summary = "First" });
        await WorkerHistory.RecordAsync(new() { Type = "import", Summary = "Second" });
        using (Begin("Removed-worker")) await WorkerHistory.RecordAsync(new() { Type = "search", Summary = "Third" });
        using var db = Db();
        ManualSqliteMigrations.Apply(db);
        ManualSqliteMigrations.Apply(db);
        var config = new TorrentarrConfig();
        config.ArrInstances["Configured-worker"] = _arr;
        var page = JsonSerializer.SerializeToElement(await WorkerHistoryEndpoints.ReadAsync(db, config, null, "search", null, 1, default));
        page.GetProperty("entries")[0].GetProperty("Summary").GetString().Should().Be("Third");
        page.GetProperty("workers").ToString().Should().Contain("Removed-worker").And.Contain("Configured-worker");
        var next = page.GetProperty("nextCursor").GetInt64();
        var older = JsonSerializer.SerializeToElement(await WorkerHistoryEndpoints.ReadAsync(db, config, "Radarr-one", "search", next, 50, default));
        older.GetProperty("entries").GetArrayLength().Should().Be(1);
        var now = DateTimeOffset.UtcNow;
        var rows = await db.WorkerHistory.OrderBy(e => e.Id).ToListAsync();
        rows[0].Timestamp = now.AddDays(-31);
        rows[1].Timestamp = now.AddDays(-30);
        await db.SaveChangesAsync();
        (await ArrActivityCollector.PruneHistoryAsync(db, 30, now)).Should().Be(1);
        (await db.WorkerHistory.CountAsync()).Should().Be(2);
    }
}

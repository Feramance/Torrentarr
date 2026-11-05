using System.Net;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Newtonsoft.Json.Linq;
using RestSharp;
using Torrentarr.Core.Configuration;
using Torrentarr.Core.Interfaces;
using Torrentarr.Infrastructure.Database;
using Torrentarr.Infrastructure.Database.Models;
using Torrentarr.Infrastructure.Services;
using Xunit;

namespace Torrentarr.Infrastructure.Tests.Services;

public sealed class ArrActivityTests : IDisposable
{
    private readonly SqliteConnection _connection = new("Data Source=:memory:");
    private readonly ServiceProvider _services;
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-11-04T12:00:00Z");
    private readonly ArrInstanceConfig _settings = new() { Type = "sonarr", URI = "http://fixture", Managed = true };
    private readonly TorrentarrConfig _config = new();
    public ArrActivityTests()
    {
        _connection.Open();
        var services = new ServiceCollection();
        services.AddDbContext<TorrentarrDbContext>(o => o.UseSqlite(_connection));
        _services = services.BuildServiceProvider();
        using var scope = _services.CreateScope();
        scope.ServiceProvider.GetRequiredService<TorrentarrDbContext>().Database.EnsureCreated();
        _config.ArrInstances["Sonarr-one"] = _settings;
    }
    private TorrentarrDbContext Db() => new(new DbContextOptionsBuilder<TorrentarrDbContext>().UseSqlite(_connection).Options);
    private ArrActivityOperation Operation(int id, string kind = "search", string hash = "") => new()
    {
        Instance = "Sonarr-one",
        CommandId = id,
        Type = "sonarr",
        Kind = kind,
        DownloadId = hash,
        Server = ArrActivityTracking.ServerKey(_settings),
        SubmittedAt = Now.AddMinutes(-5).ToUnixTimeSeconds()
    };
    public void Dispose() { _services.Dispose(); _connection.Dispose(); }

    [Fact]
    public async Task Tracking_AttributesNestedScopesAndPersistsWithoutChangingMainResponse()
    {
        await using var db = Db();
        var request = new RestRequest("/api/v5/command", Method.Post);
        var response = new RestResponse { StatusCode = HttpStatusCode.Accepted, IsSuccessStatusCode = true, ResponseStatus = ResponseStatus.Completed, Content = "{\"id\":42}" };
        using (ArrActivityTracking.ForClient("client-one"))
        using (ArrActivityTracking.Begin(db, NullLogger.Instance, "Sonarr-one", _settings, "import", "hash"))
        {
            await ArrActivityTracking.RecordAcceptedAsync(request, response, default);
            await ArrActivityTracking.RecordAcceptedAsync(request, response, default);
        }
        response.Content = "{\"id\":43}";
        await ArrActivityTracking.RecordAcceptedAsync(request, response, default);
        var operation = await db.ArrActivityOperations.SingleAsync();
        operation.CommandId.Should().Be(42); operation.ClientInstance.Should().Be("client-one");
        operation.DownloadId.Should().Be("HASH"); operation.Kind.Should().Be("import");
        db.Database.ExecuteSqlRaw("DROP TABLE torrentarr_arr_operations");
        using (ArrActivityTracking.Begin(db, NullLogger.Instance, "Sonarr-one", _settings, "search"))
            await ArrActivityTracking.RecordAcceptedAsync(request, response, default);
        response.IsSuccessful.Should().BeTrue();
    }

    [Theory]
    [InlineData("started", null, false)]
    [InlineData("completed", "successful", true)]
    [InlineData("completed", "unsuccessful", false)]
    [InlineData("failed", null, false)]
    [InlineData("cancelled", null, false)]
    [InlineData("completed", "unknown", false)]
    [InlineData("completed", null, false)]
    [InlineData("completed", "1", true)]
    public async Task Search_RequiresSuccessfulCommandCompletion(string status, string? result, bool completed)
    {
        await using var db = Db(); var operation = Operation(1); db.Add(operation); await db.SaveChangesAsync();
        var commands = new JArray(new JObject { ["id"] = 1, ["status"] = status, ["result"] = result });
        await ArrActivityCollector.ReconcileAsync(db, [operation], commands, [], Now);
        await db.SaveChangesAsync();
        operation.ConfirmedAt.HasValue.Should().Be(completed);
        await ArrActivityCollector.ReconcileAsync(db, [operation], commands, [], Now.AddMinutes(1));
        if (completed) operation.ConfirmedAt.Should().Be(Now.ToUnixTimeSeconds());
    }

    [Fact]
    public async Task Imports_RequireNewCorrelatedPositiveEvidenceAndDeduplicateBatchRetries()
    {
        await using var db = Db(); var first = Operation(1, "import", "HASH"); var retry = Operation(2, "import", "HASH");
        db.AddRange(first, retry); await db.SaveChangesAsync();
        var commands = JArray.Parse("[{\"id\":1,\"status\":\"completed\"},{\"id\":2,\"status\":\"completed\"}]");
        await ArrActivityCollector.ReconcileAsync(db, [first, retry], commands, [], Now);
        first.ConfirmedAt.Should().BeNull();
        var history = new JArray(
            new JObject { ["id"] = 5, ["downloadId"] = "OTHER", ["eventType"] = "downloadFolderImported", ["date"] = Now.ToString("O") },
            new JObject { ["id"] = 6, ["downloadId"] = "HASH", ["eventType"] = "grabbed", ["date"] = Now.ToString("O") },
            new JObject { ["id"] = 7, ["downloadId"] = "HASH", ["eventType"] = "downloadFolderImported", ["date"] = Now.AddDays(-1).ToString("O") });
        await ArrActivityCollector.ReconcileAsync(db, [first, retry], commands, history, Now);
        first.ConfirmedAt.Should().BeNull();
        history.Add(new JObject { ["id"] = 8, ["downloadId"] = "hash", ["eventType"] = "downloadFolderImported", ["date"] = Now.ToString("O") });
        history.Add(new JObject { ["id"] = 9, ["downloadId"] = "HASH", ["eventType"] = "downloadFolderImported", ["date"] = Now.ToString("O") });
        await ArrActivityCollector.ReconcileAsync(db, [first, retry], commands, history, Now);
        await db.SaveChangesAsync(); first.ConfirmedAt.Should().Be(Now.ToUnixTimeSeconds());
        retry.Outcome.Should().Be("duplicate"); retry.ConfirmedAt.Should().BeNull();
    }

    [Theory]
    [InlineData("radarr", "downloadFolderImported", true)]
    [InlineData("sonarr", "downloadFolderImported", true)]
    [InlineData("lidarr", "downloadImported", true)]
    [InlineData("readarr", "downloadImported", true)]
    [InlineData("lidarr", "trackFileImported", false)]
    [InlineData("readarr", "bookImportIncomplete", false)]
    public void Imports_UseTypeSpecificBatchEvidence(string type, string eventType, bool expected)
    {
        var operation = Operation(1, "import", "HASH"); operation.Type = type;
        var evidence = new JObject { ["id"] = 2, ["downloadId"] = "HASH", ["eventType"] = eventType, ["date"] = Now.ToString("O") };
        ArrActivityCollector.PositiveImport(evidence, operation).Should().Be(expected);
    }

    [Fact]
    public async Task Collector_RestartsWithPendingWorkAndSamplesFreshHeartbeatsOncePerMinute()
    {
        await using (var db = Db()) { db.Add(Operation(1)); await db.SaveChangesAsync(); }
        var statuses = new Dictionary<string, ProcessStatus> { ["Sonarr-one"] = new() { IsAlive = true, LastHeartbeat = Now.UtcDateTime, QueueCount = 7 } };
        var calls = 0;
        Task<JArray> Read(ArrInstanceConfig _, string resource, CancellationToken ct) { calls++; return Task.FromResult(JArray.Parse("[{\"id\":1,\"status\":\"completed\",\"result\":\"successful\"}]")); }
        var collector = new ArrActivityCollector(_services.GetRequiredService<IServiceScopeFactory>(), _services, _config, NullLogger<ArrActivityCollector>.Instance);
        await collector.CollectAsync(Now, statuses, Read);
        await collector.CollectAsync(Now.AddSeconds(10), statuses, Read);
        calls.Should().Be(1);
        await collector.CollectAsync(Now.AddMinutes(1), statuses, Read);
        await using var check = Db();
        (await check.ArrActivityOperations.SingleAsync()).Outcome.Should().Be("completed");
        var rows = await check.ArrActivitySamples.OrderBy(s => s.Minute).ToListAsync();
        rows[0].QueueDepth.Should().Be(7); rows[0].CompletionObserved.Should().BeTrue();
        rows[1].QueueDepth.Should().BeNull(); rows[1].CompletionObserved.Should().BeFalse();
    }

    [Fact]
    public async Task Query_AveragesQueuesSumsConfirmedEventsAndPreservesUnknownAndZero()
    {
        await using var db = Db(); var minute = Now.ToUnixTimeSeconds() / 60;
        db.AddRange(new ArrActivitySample { Instance = "Sonarr-one", Type = "sonarr", Minute = minute - 5, QueueDepth = 4, CompletionObserved = true },
            new ArrActivitySample { Instance = "Sonarr-one", Type = "sonarr", Minute = minute - 4, QueueDepth = 8, CompletionObserved = true });
        var operation = Operation(1); operation.Outcome = "completed"; operation.ConfirmedAt = Now.AddMinutes(-2).ToUnixTimeSeconds(); db.Add(operation);
        await db.SaveChangesAsync();
        var history = await ArrActivityApi.QueryAsync(db, _config, "24h", null, Now);
        var point = history.Series.Single().Points.Last();
        point.QueueDepth.Should().Be(6); point.QueueSamples.Should().Be(2); point.ExpectedSamples.Should().Be(5);
        point.SearchesCompleted.Should().Be(1); point.ImportsConfirmed.Should().Be(0);
        history.Series.Single().Points.First().SearchesCompleted.Should().BeNull();
        foreach (var range in new[] { "1h", "7d" })
            (await ArrActivityApi.QueryAsync(db, _config, range, null, Now)).Series.Single().Points.Count.Should().Be(range == "1h" ? 60 : 336);
    }

    [Fact]
    public async Task Collector_PrunesExpiredHistoryAndMigrationIsRepeatable()
    {
        await using var db = Db();
        db.Add(new ArrActivitySample { Instance = "old", Type = "sonarr", Minute = Now.AddDays(-8).ToUnixTimeSeconds() / 60 });
        var old = Operation(10); old.SubmittedAt = Now.AddDays(-8).ToUnixTimeSeconds(); db.Add(old); await db.SaveChangesAsync();
        ManualSqliteMigrations.Apply(db); ManualSqliteMigrations.Apply(db);
        var collector = new ArrActivityCollector(_services.GetRequiredService<IServiceScopeFactory>(), _services, _config, NullLogger<ArrActivityCollector>.Instance);
        await collector.CollectAsync(Now, new Dictionary<string, ProcessStatus>(), (_, _, _) => throw new IOException("must not poll expired operations"));
        db.ChangeTracker.Clear(); (await db.ArrActivitySamples.CountAsync(s => s.Instance == "old")).Should().Be(0);
        (await db.ArrActivityOperations.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task Collector_FailedEvidenceLeavesGapAndDoesNotBlockOtherWorkers()
    {
        await using var db = Db(); db.Add(Operation(1)); await db.SaveChangesAsync();
        _config.ArrInstances["Sonarr-two"] = new() { Type = "sonarr", URI = "http://fixture-two" };
        var statuses = new Dictionary<string, ProcessStatus>
        {
            ["Sonarr-one"] = new() { IsAlive = true, LastHeartbeat = Now.UtcDateTime, QueueCount = 7 },
            ["Sonarr-two"] = new() { IsAlive = true, LastHeartbeat = Now.UtcDateTime, QueueCount = 3 }
        };
        var collector = new ArrActivityCollector(_services.GetRequiredService<IServiceScopeFactory>(), _services, _config, NullLogger<ArrActivityCollector>.Instance);
        await collector.CollectAsync(Now, statuses, (_, _, _) => throw new IOException("unavailable"));
        var rows = await db.ArrActivitySamples.OrderBy(s => s.Instance).ToListAsync();
        rows[0].QueueDepth.Should().Be(7); rows[0].CompletionObserved.Should().BeFalse();
        rows[1].QueueDepth.Should().Be(3); rows[1].CompletionObserved.Should().BeTrue();
        (await db.ArrActivityOperations.SingleAsync()).Outcome.Should().Be("pending");
    }

    [Fact]
    public async Task Migration_AddsActivityTablesToExistingDatabaseWithoutLosingData()
    {
        await using var db = Db();
        db.Database.ExecuteSqlRaw("DROP TABLE torrentarr_arr_samples");
        db.Database.ExecuteSqlRaw("DROP TABLE torrentarr_arr_operations");
        ManualSqliteMigrations.Apply(db); ManualSqliteMigrations.Apply(db);
        db.Add(Operation(1)); db.Add(new ArrActivitySample { Instance = "Sonarr-one", Type = "sonarr", Minute = 123 });
        await db.SaveChangesAsync(); ManualSqliteMigrations.Apply(db);
        (await db.ArrActivityOperations.SingleAsync()).CommandId.Should().Be(1);
        (await db.ArrActivitySamples.SingleAsync()).Minute.Should().Be(123);
    }
}

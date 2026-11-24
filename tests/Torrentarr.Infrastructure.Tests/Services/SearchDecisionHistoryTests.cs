using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Newtonsoft.Json.Linq;
using Torrentarr.Core.Configuration;
using Torrentarr.Core.Services;
using Torrentarr.Infrastructure.Database;
using Torrentarr.Infrastructure.Database.Models;
using Torrentarr.Infrastructure.Endpoints;
using Torrentarr.Infrastructure.Services;
using Xunit;

namespace Torrentarr.Infrastructure.Tests.Services;

public sealed class SearchDecisionHistoryTests
{
    [Fact]
    public void GrabEvidenceRequiresMatchingMediaAndARealDatedGrab()
    {
        var now = DateTimeOffset.Parse("2026-11-17T12:00:00Z");
        var decision = new SearchDecisionHistory.Decision(
            new(1, "Episode", 4, 1, null, null, "Missing", "SeasonSearch"),
            "submitted", 17, now.AddMinutes(-2).ToUnixTimeSeconds(), "server", "cycle", [1, 2]);
        var history = JArray.Parse("""
            [
                {"eventType":"grabbed","episodeId":2,"date":"2026-11-17T11:57:00Z","downloadId":"old"},
                {"eventType":"grabbed","episodeId":3,"date":"2026-11-17T11:59:00Z","downloadId":"wrong"},
                {"eventType":"downloadFolderImported","episodeId":2,"date":"2026-11-17T11:59:00Z","downloadId":"import"},
                {"eventType":"grabbed","episodeId":2,"date":"2026-11-17T11:59:00Z"}
            ]
            """);
        Assert.Null(ArrActivityCollector.FindSearchGrab(decision, history, now));
        history.Add(JObject.Parse("""{"eventType":1,"episodeId":2,"date":"2026-11-17T11:59:00Z","downloadId":"grab"}"""));
        Assert.Equal(now.AddMinutes(-1).ToUnixTimeSeconds(), ArrActivityCollector.FindSearchGrab(decision, history, now));
    }

    [Fact]
    public async Task RecordingPrunesOldAndExcessDecisionsExceptUnreconciledSubmissions()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = new TorrentarrDbContext(new DbContextOptionsBuilder<TorrentarrDbContext>().UseSqlite(connection).Options);
        await db.Database.EnsureCreatedAsync();
        var now = TimeProvider.System.GetUtcNow().ToUnixTimeSeconds();
        db.SearchDecisions.AddRange(Enumerable.Range(1, SearchDecisionHistory.MaxHistory).Select(age =>
            new SearchDecision { Instance = "Radarr", Key = $"fresh:{age}", ObservedAt = now - age }));
        db.SearchDecisions.AddRange(
            new() { Instance = "Radarr", Key = "submitted:pending", ObservedAt = now - 3600 },
            new() { Instance = "Radarr", Key = "submitted:reconciled", ObservedAt = now - 3600, NoGrabAt = now - 1800 },
            new() { Instance = "Radarr", Key = "expired", ObservedAt = now - SearchDecisionHistory.RetentionSeconds - 1 },
            new() { Instance = "Other", Key = "untouched", ObservedAt = now - SearchDecisionHistory.RetentionSeconds - 1 });
        await db.SaveChangesAsync();

        var history = new SearchDecisionHistory(db, NullLogger.Instance, "Radarr", new() { URI = "http://fixture", Search = { UnsuccessfulSearchRestMinutes = 60 } }, TimeProvider.System);
        var candidate = new SearchCandidate { ArrId = 1000, Type = "Movie", Reason = "Missing" };
        await history.RecordAsync(new(candidate, "", [candidate], 1, 1), "command limit", "cycle", default);

        var retained = await db.SearchDecisions.AsNoTracking().Where(row => row.Instance == "Radarr").ToListAsync();
        Assert.Equal(SearchDecisionHistory.MaxHistory + 1, retained.Count);
        Assert.DoesNotContain(retained, row => row.Key is "expired" or "submitted:reconciled" || row.Key == $"fresh:{SearchDecisionHistory.MaxHistory}");
        Assert.Contains(retained, row => row.Key == "submitted:pending");
        Assert.Contains(retained, row => row.Key.StartsWith("decision:"));
        Assert.True(await db.SearchDecisions.AsNoTracking().AnyAsync(row => row.Instance == "Other" && row.Key == "untouched"));
    }

    [Fact]
    public async Task PreviewDefersSeasonSearchWhenAnyCoveredEpisodeIsOnCooldown()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = new TorrentarrDbContext(new DbContextOptionsBuilder<TorrentarrDbContext>().UseSqlite(connection).Options);
        await db.Database.EnsureCreatedAsync();
        var arr = new ArrInstanceConfig { Type = "sonarr", Search = { RecentGrabCooldownMinutes = 30 } };
        var now = TimeProvider.System.GetUtcNow().ToUnixTimeSeconds();
        var trigger = new SearchCandidate { ArrId = 1, Type = "Episode", SeriesId = 4, SeasonNumber = 1 };
        var covered = new SearchCandidate { ArrId = 2, Type = "Episode", SeriesId = 4, SeasonNumber = 1 };
        var plan = new SonarrSearchPlan(trigger, "SeasonSearch", [trigger, covered], 2, 2);

        var response = JsonSerializer.SerializeToElement(await SearchDecisionEndpoints.ReadAsync(
            "Sonarr", arr, db, TimeProvider.System, default,
            _ => Task.FromResult(new SearchPacingObservation(null, new Dictionary<int, long> { [2] = now })),
            _ => Task.FromResult<(long, int, SonarrSearchPlan[])?>((now, 1, [plan]))),
            new JsonSerializerOptions(JsonSerializerDefaults.Web));

        var preview = response.GetProperty("preview");
        Assert.Empty(preview.GetProperty("items").EnumerateArray());
        var deferred = Assert.Single(preview.GetProperty("deferred").EnumerateArray());
        Assert.StartsWith("Recent grab cooldown", deferred.GetProperty("reason").GetString());
        Assert.Equal(1, deferred.GetProperty("item").GetProperty("id").GetInt32());
    }

    [Fact]
    public async Task PreviewReadsCurrentCatalogAndDoesNotMutateHistory()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = new TorrentarrDbContext(new DbContextOptionsBuilder<TorrentarrDbContext>().UseSqlite(connection).Options);
        await db.Database.EnsureCreatedAsync();
        var arr = new ArrInstanceConfig { URI = "http://localhost:7878", Managed = true, Type = "radarr" };
        var clock = TimeProvider.System;
        var history = new SearchDecisionHistory(db, NullLogger.Instance, "Radarr", arr, clock);
        var unrelated = new SearchDecisionHistory(db, NullLogger.Instance, "Other", arr, clock);
        static SonarrSearchPlan Plan(int id, string reason)
        {
            var candidate = new SearchCandidate { ArrId = id, Reason = reason, Type = "Movie", Title = "private title" };
            return new(candidate, "", [candidate], 1, 1);
        }
        var missing = Plan(1, "Missing");
        var upgrade = Plan(2, "Upgrade");
        var config = new TorrentarrConfig();
        config.ArrInstances["Radarr"] = arr;
        arr.Category = "movies";
        arr.Search.SearchByYear = false;
        arr.Search.QualityUnmetSearch = true;
        var restart = new DatabaseRestartCoordinator();
        var executor = new SearchExecutor(NullLogger<SearchExecutor>.Instance, config, db,
            new QualityProfileSwitcherService(NullLogger<QualityProfileSwitcherService>.Instance, db, restart), restart);
        var media = new ArrMediaService(NullLogger<ArrMediaService>.Instance, db, config, executor,
            new ArrSyncService(NullLogger<ArrSyncService>.Instance, config, db, restart));
        MaintenancePersistence.Write(db, SearchCatalogState.ObservationKey("Radarr"),
            new SearchCatalogState(ArrActivityTracking.ServerKey(arr), arr.Category, clock.GetUtcNow().ToUnixTimeSeconds(), true, "fixture"));
        db.Movies.AddRange(
            new() { ArrInstance = "Radarr", ArrId = 1, Monitored = true, Reason = "Missing", Year = 2000 },
            new()
            {
                ArrInstance = "Radarr",
                ArrId = 2,
                Monitored = true,
                HasFile = true,
                QualityMet = false,
                CustomFormatMet = true,
                Reason = "Quality",
                Year = 2010
            });
        await db.SaveChangesAsync();
        await history.RecordAsync(upgrade, "command limit", "skip-a", default);
        await history.RecordAsync(upgrade, "command limit", "skip-b", default);
        await unrelated.RecordAsync(Plan(3, "Missing"), "command limit", "skip-other", default);

        async Task<JsonElement> ReadAsync() => JsonSerializer.SerializeToElement(
            await SearchDecisionEndpoints.ReadAsync("Radarr", arr, db, clock, default,
                plan: token => media.PreviewCurrentAsync("Radarr", arr, token)),
            new JsonSerializerOptions(JsonSerializerDefaults.Web));

        var snapshot = await ReadAsync();
        Assert.Equal(new[] { 1, 2 }, snapshot.GetProperty("preview").GetProperty("items").EnumerateArray()
            .Select(item => item.GetProperty("id").GetInt32()));
        Assert.Equal(2, snapshot.GetProperty("preview").GetProperty("candidateCount").GetInt32());
        Assert.Equal("Quality", snapshot.GetProperty("preview").GetProperty("items")[1].GetProperty("reason").GetString());
        var skip = Assert.Single(snapshot.GetProperty("history").EnumerateArray());
        Assert.Equal(2, skip.GetProperty("occurrences").GetInt32());
        Assert.Equal("Upgrade", skip.GetProperty("item").GetProperty("reason").GetString());

        var submitted = clock.GetUtcNow().ToUnixTimeSeconds();
        await history.RecordAsync(missing, "submitted", "sent", default, 17, submitted);
        await db.Movies.Where(movie => movie.ArrId == 1).ExecuteUpdateAsync(update => update.SetProperty(movie => movie.Searched, true));
        snapshot = await ReadAsync();
        Assert.Equal(2, Assert.Single(snapshot.GetProperty("preview").GetProperty("items").EnumerateArray()).GetProperty("id").GetInt32());
        var send = snapshot.GetProperty("history").EnumerateArray().Single(item => item.GetProperty("outcome").GetString() == "submitted");
        Assert.Equal(JsonValueKind.Null, send.GetProperty("completedAt").ValueKind);
        Assert.DoesNotContain("private title", snapshot.GetRawText());

        db.ArrActivityOperations.Add(new()
        {
            Instance = "Radarr",
            Server = ArrActivityTracking.ServerKey(arr),
            CommandId = 17,
            Outcome = "completed",
            Kind = "search",
            SubmittedAt = submitted,
            ConfirmedAt = submitted + 1
        });
        await db.SaveChangesAsync();
        snapshot = await ReadAsync();
        send = snapshot.GetProperty("history").EnumerateArray().Single(item => item.GetProperty("outcome").GetString() == "submitted");
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(submitted + 1), send.GetProperty("completedAt").GetDateTimeOffset());
        Assert.Equal(3, await db.SearchDecisions.CountAsync());

        arr.Search.ActiveDownloadCeiling = 2;
        var paced = JsonSerializer.SerializeToElement(await SearchDecisionEndpoints.ReadAsync(
            "Radarr", arr, db, clock, default, _ => Task.FromResult(new SearchPacingObservation(2, null)),
            token => media.PreviewCurrentAsync("Radarr", arr, token)),
            new JsonSerializerOptions(JsonSerializerDefaults.Web));
        Assert.Empty(paced.GetProperty("preview").GetProperty("items").EnumerateArray());
        Assert.Equal("Active-download ceiling reached (2/2)",
            Assert.Single(paced.GetProperty("preview").GetProperty("deferred").EnumerateArray()).GetProperty("reason").GetString());
        Assert.Equal(3, await db.SearchDecisions.CountAsync());
        arr.URI = "http://different:7878";
        Assert.Equal(JsonValueKind.Null, (await ReadAsync()).GetProperty("preview").ValueKind);
    }
}

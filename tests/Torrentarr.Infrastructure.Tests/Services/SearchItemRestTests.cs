using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Newtonsoft.Json.Linq;
using Torrentarr.Core.Configuration;
using Torrentarr.Infrastructure.Database;
using Torrentarr.Infrastructure.Database.Models;
using Torrentarr.Infrastructure.Services;
using Xunit;

namespace Torrentarr.Infrastructure.Tests.Services;

public sealed class SearchItemRestTests
{
    [Theory]
    [InlineData("completed", "[]", true)]
    [InlineData("completed-boundary", "[]", true)]
    [InlineData("completed-recent", "[]", false)]
    [InlineData("completed-late", "[]", false)]
    [InlineData("completed-retry", "[]", false)]
    [InlineData("pending", "[]", false)]
    [InlineData("failed", "[]", false)]
    [InlineData("completed", "[{}]", false)]
    [InlineData("completed", "[{\"eventType\":1,\"movieId\":1,\"date\":\"2026-11-21T11:10:00Z\",\"downloadId\":\"grab\"}]", false)]
    [InlineData("completed", "[{\"eventType\":1,\"movieId\":2,\"date\":\"2026-11-21T11:10:00Z\",\"downloadId\":\"other\"}]", true)]
    [InlineData("completed", "[{\"eventType\":1,\"movieId\":2,\"date\":\"2026-11-21T11:10:00Z\"}]", false)]
    public async Task OnlyReliableCompletedNoGrabStartsPersistedScopedRest(string outcome, string history, bool rests)
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<TorrentarrDbContext>().UseSqlite(connection).Options;
        await using var db = new TorrentarrDbContext(options);
        await db.Database.EnsureCreatedAsync();
        var now = DateTimeOffset.Parse("2026-11-21T12:00:00Z");
        var arr = new ArrInstanceConfig
        {
            URI = "http://fixture",
            Type = "radarr",
            Search = new() { UnsuccessfulSearchRestMinutes = 60 }
        };
        var server = ArrActivityTracking.ServerKey(arr);
        var submitted = now.AddHours(-2).ToUnixTimeSeconds();
        var completed = now.AddMinutes(outcome switch { "completed-recent" => -28, "completed-boundary" => -30, "completed-late" => -46, _ => -40 }).ToUnixTimeSeconds();
        var operation = new ArrActivityOperation
        {
            Instance = "Radarr",
            Server = server,
            CommandId = 7,
            SubmittedAt = submitted,
            ConfirmedAt = completed,
            Kind = "search",
            Outcome = outcome.StartsWith("completed") ? "completed" : outcome
        };
        db.ArrActivityOperations.Add(operation);
        await db.SaveChangesAsync();
        var decision = new SearchDecisionHistory.Decision(new(1, "Movie", null, null, null, null, outcome == "completed-retry" ? "Retry" : "Missing", "Movie"),
            "submitted", 7, submitted, server, "cycle", [1]);
        var row = new SearchDecision { Instance = "Radarr", Key = "submitted:cycle" };
        var decisions = new List<(SearchDecision, SearchDecisionHistory.Decision?)> { (row, decision) };

        await SearchItemRest.ReconcileAsync(db, "Radarr", arr, decisions, JArray.Parse(history), now, default);
        // A restart or duplicate observation must keep the same expiry.
        await SearchItemRest.ReconcileAsync(db, "Radarr", arr, decisions, JArray.Parse(history), now.AddMinutes(1), default);
        await using var restarted = new TorrentarrDbContext(options);
        var observation = await SearchItemRest.ObserveAsync(restarted, "Radarr", arr, default);
        Assert.Equal(rests, observation.Hold(1, "Movie", 60, now.ToUnixTimeSeconds()) != null);
        Assert.Equal(rests, row.NoGrabAt.HasValue);
        Assert.Null(observation.Hold(2, "Movie", 60, now.ToUnixTimeSeconds()));
        Assert.Null(observation.Hold(1, "EpisodeSearch", 60, now.ToUnixTimeSeconds()));
        Assert.Null(observation.Hold(1, "Movie", 60, completed + SearchItemRest.ConfirmationSeconds + 3600));
        var unrelated = await SearchItemRest.ObserveAsync(restarted, "Other", arr, default);
        Assert.Null(unrelated.Hold(1, "Movie", 60, now.ToUnixTimeSeconds()));
    }

    [Fact]
    public async Task UnavailableStorageDefersOnlyWhenRestIsEnabled()
    {
        var arr = new ArrInstanceConfig { Search = new() { UnsuccessfulSearchRestMinutes = 60 } };
        var unavailable = await SearchItemRest.ObserveAsync(null, "Radarr", arr, default);
        Assert.Contains("unavailable", unavailable.Hold(1, "Movie", 60, 0));
        Assert.Null(unavailable.Hold(1, "Movie", 0, 0));
    }
}

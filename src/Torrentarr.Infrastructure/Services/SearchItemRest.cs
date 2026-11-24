using Microsoft.EntityFrameworkCore;
using Newtonsoft.Json.Linq;
using Torrentarr.Core.Configuration;
using Torrentarr.Infrastructure.Database;
using Torrentarr.Infrastructure.Database.Models;

namespace Torrentarr.Infrastructure.Services;

internal sealed record SearchRestState(long SearchRestUntil);

internal sealed class SearchItemRest(IReadOnlyDictionary<string, long>? expiries)
{
    internal const int ConfirmationSeconds = 30 * 60;
    private static string ItemKey(int id, string scope) => id + ":" + scope.ToUpperInvariant();
    private static string Prefix(string instance, ArrInstanceConfig arr) =>
        MaintenancePersistence.Key(instance, "", "search-rest:" + ArrActivityTracking.ServerKey(arr) + ":");

    internal string? Hold(int id, string scope, int minutes, long now)
    {
        if (minutes <= 0) return null;
        if (expiries == null) return "Item rest evidence unavailable";
        var expiry = expiries.GetValueOrDefault(ItemKey(id, scope));
        return expiry > now ? $"Unsuccessful-search rest until {DateTimeOffset.FromUnixTimeSeconds(expiry):O}" : null;
    }

    internal static async Task<SearchItemRest> ObserveAsync(TorrentarrDbContext? db, string? instance,
        ArrInstanceConfig arr, CancellationToken ct)
    {
        if (arr.Search.UnsuccessfulSearchRestMinutes <= 0) return new(new Dictionary<string, long>());
        if (db == null || instance == null) return new(null);
        try
        {
            var prefix = Prefix(instance, arr);
            var rows = await db.MaintenanceState.AsNoTracking().Where(row => row.Key.StartsWith(prefix)).ToListAsync(ct);
            var result = new Dictionary<string, long>();
            foreach (var row in rows)
            {
                var state = Newtonsoft.Json.JsonConvert.DeserializeObject<SearchRestState>(row.Data);
                if (state == null || state.SearchRestUntil is <= 0 or > 253402300799) return new(null);
                result[row.Key[prefix.Length..]] = state.SearchRestUntil;
            }
            return new(result);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception) { return new(null); }
    }

    internal static async Task ReconcileAsync(TorrentarrDbContext db, string instance, ArrInstanceConfig arr,
        IReadOnlyList<(SearchDecision Row, SearchDecisionHistory.Decision? Entry)> decisions,
        JArray history, DateTimeOffset now, CancellationToken ct)
    {
        if (arr.Search.UnsuccessfulSearchRestMinutes <= 0 || !db.Database.IsSqlite()) return;
        var dates = SearchPacing.ReadGrabDates(arr.Type, history, now.ToUnixTimeSeconds());
        if (dates == null || history.Any(token => token is not JObject
            || token["eventType"]?.Value<string>()?.ToLowerInvariant() is not ("grabbed" or "1")
            || string.IsNullOrWhiteSpace(token["downloadId"]?.Value<string>()))) return;
        var ids = decisions.Where(pair => pair.Entry?.CommandId != null).Select(pair => pair.Entry!.CommandId!.Value).ToArray();
        var server = ArrActivityTracking.ServerKey(arr);
        var operations = await db.ArrActivityOperations.AsNoTracking().Where(operation => operation.Instance == instance
            && operation.Server == server && operation.Kind == "search" && operation.Outcome == "completed"
            && ids.Contains(operation.CommandId)).ToDictionaryAsync(operation => operation.CommandId, ct);
        foreach (var (row, decision) in decisions)
        {
            if (decision?.CommandId is not int id || row.GrabbedAt != null || row.NoGrabAt != null
                || !operations.TryGetValue(id, out var operation) || operation.ConfirmedAt is not long completed
                || decision.SubmittedAt != operation.SubmittedAt || completed < operation.SubmittedAt
                || completed + ConfirmationSeconds > now.ToUnixTimeSeconds()
                // Late collection cannot establish absence during a missed confirmation window.
                || now.ToUnixTimeSeconds() - completed - ConfirmationSeconds > 15 * 60
                || decision.Server != server || decision.CoveredIds is not { Length: > 0 }
                || ArrActivityCollector.FindSearchGrab(decision, history, now) != null
                || decision.Item.Reason.Equals("Retry", StringComparison.OrdinalIgnoreCase)) continue;
            var expiry = completed + ConfirmationSeconds + arr.Search.UnsuccessfulSearchRestMinutes * 60L;
            var prefix = Prefix(instance, arr);
            foreach (var item in decision.CoveredIds.Distinct())
            {
                var key = prefix + ItemKey(item, decision.Item.Scope);
                var data = Newtonsoft.Json.JsonConvert.SerializeObject(new SearchRestState(expiry));
                await db.Database.ExecuteSqlInterpolatedAsync($"""
                    INSERT INTO torrentarr_maintenance_state (Key, Data) VALUES ({key}, {data})
                    ON CONFLICT(Key) DO UPDATE SET Data=excluded.Data
                    WHERE CASE WHEN json_valid(Data) THEN json_extract(Data, '$.SearchRestUntil') END < {expiry}
                    """, ct);
            }
            row.NoGrabAt = completed + ConfirmationSeconds;
        }
    }
}

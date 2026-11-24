using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Torrentarr.Core.Configuration;
using Torrentarr.Core.Interfaces;
using Torrentarr.Core.Services;
using Torrentarr.Infrastructure.Database;
using Torrentarr.Infrastructure.Services;

namespace Torrentarr.Infrastructure.Endpoints;

public static class SearchDecisionEndpoints
{
    public static void MapSearchDecisions(this WebApplication app, string prefix) =>
        app.MapGet(prefix + "/processes/{instance}/search-activity", async (
            string instance, TorrentarrConfig config, TorrentarrDbContext db, ITorrentClientRegistry registry,
            IArrMediaService media, CancellationToken ct) =>
        {
            using var snapshot = config.BeginOperation();
            var name = config.ArrInstances.Keys.FirstOrDefault(k => k.Equals(instance, StringComparison.OrdinalIgnoreCase));
            if (name == null) return Results.NotFound();
            var arr = config.ArrInstances[name];
            return Results.Ok(await ReadAsync(name, arr, db, TimeProvider.System, ct,
                token => SearchPacing.ObserveAsync(config, arr, registry, TimeProvider.System, token, db, name),
                token => media is ArrMediaService planner
                    ? planner.PreviewCurrentAsync(name, arr, token)
                    : Task.FromResult<(long At, int Count, SonarrSearchPlan[] Plans)?>(null)));
        });

    internal static async Task<object> ReadAsync(string instance, ArrInstanceConfig arr, TorrentarrDbContext db, TimeProvider clock,
        CancellationToken ct, Func<CancellationToken, Task<SearchPacingObservation>>? observe = null,
        Func<CancellationToken, Task<(long At, int Count, SonarrSearchPlan[] Plans)?>>? plan = null)
    {
        var now = clock.GetUtcNow();
        var server = ArrActivityTracking.ServerKey(arr);
        var oldest = now.ToUnixTimeSeconds() - SearchDecisionHistory.RetentionSeconds;
        var query = db.SearchDecisions.AsNoTracking().Where(row => row.Instance == instance && row.ObservedAt >= oldest);
        var rows = await query.Where(row => row.Key != "preview:search")
            .OrderByDescending(row => row.ObservedAt).ThenByDescending(row => row.Key)
            .Take(SearchDecisionHistory.MaxHistory).ToListAsync(ct);
        var decisions = rows.Select(row => new
        {
            Row = row,
            Entry = JsonSerializer.Deserialize<SearchDecisionHistory.Decision>(row.Data)
        }).Where(row => row.Entry?.Server == server).ToList();
        var ids = decisions.Select(d => d.Entry?.CommandId).OfType<int>().Distinct().ToArray();
        var operations = await db.ArrActivityOperations.AsNoTracking()
            .Where(o => o.Instance == instance && o.Server == server && ids.Contains(o.CommandId))
            .ToDictionaryAsync(o => o.CommandId, ct);
        SonarrSearchPlan[] plans = [];
        long? plannedAt = null;
        var candidateCount = 0;
        if (plan != null)
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(12));
            try
            {
                if (await plan(timeout.Token) is { } fresh)
                {
                    plannedAt = fresh.At;
                    candidateCount = fresh.Count;
                    plans = fresh.Plans;
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception) { /* Unusable catalog evidence leaves the preview unavailable. */ }
        }
        var evidence = plans.Length > 0 && observe != null &&
            (arr.Search.ActiveDownloadCeiling > 0 || arr.Search.RecentGrabCooldownMinutes > 0
                || arr.Search.QuietHoursEnabled || SearchLibraryGates.Enabled(arr.Search)
                || arr.Search.UnsuccessfulSearchRestMinutes > 0)
                ? await observe(ct) : new SearchPacingObservation(null, null);
        var evaluatedAt = clock.GetUtcNow();
        var evaluated = plans.Select(search => new
        {
            item = SearchDecisionHistory.Describe(search),
            reason = evidence.Hold(search, arr, evaluatedAt.ToUnixTimeSeconds())
        }).ToArray();
        return new
        {
            preview = plannedAt is long at ? new
            {
                observedAt = DateTimeOffset.FromUnixTimeSeconds(at),
                ageSeconds = Math.Max(0, evaluatedAt.ToUnixTimeSeconds() - at),
                candidateCount,
                items = evaluated.Where(entry => entry.reason == null).Select(entry => entry.item),
                deferred = evaluated.Where(entry => entry.reason != null)
            } : null,
            history = decisions.Select(d =>
            {
                var entry = d.Entry!;
                var operation = entry.CommandId is int id ? operations.GetValueOrDefault(id) : null;
                return new
                {
                    entry.Item,
                    entry.Outcome,
                    entry.CycleId,
                    observedAt = DateTimeOffset.FromUnixTimeSeconds(d.Row.ObservedAt),
                    d.Row.Occurrences,
                    grabbedAt = d.Row.GrabbedAt is long grabbed ? DateTimeOffset.FromUnixTimeSeconds(grabbed) : (DateTimeOffset?)null,
                    noGrabAt = d.Row.NoGrabAt is long noGrab ? DateTimeOffset.FromUnixTimeSeconds(noGrab) : (DateTimeOffset?)null,
                    completedAt = operation?.Outcome == "completed" && operation.Kind == "search"
                        && operation.SubmittedAt == entry.SubmittedAt && operation.ConfirmedAt is long completed
                            ? DateTimeOffset.FromUnixTimeSeconds(completed) : (DateTimeOffset?)null
                };
            })
        };
    }
}

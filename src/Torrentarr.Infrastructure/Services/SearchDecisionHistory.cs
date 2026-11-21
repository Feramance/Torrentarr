using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Torrentarr.Core.Configuration;
using Torrentarr.Core.Services;
using Torrentarr.Infrastructure.Database;

namespace Torrentarr.Infrastructure.Services;

internal sealed class SearchDecisionHistory(TorrentarrDbContext db, ILogger logger, string instance, ArrInstanceConfig config, TimeProvider clock)
{
    internal const int MaxPreview = 50;
    internal const int MaxHistory = 200;
    internal const long RetentionSeconds = 7 * 24 * 60 * 60;
    private readonly string _server = ArrActivityTracking.ServerKey(config);
    internal sealed record Item(int Id, string Type, int? SeriesId, int? Season, int? ArtistId, int? AuthorId, string Reason, string Scope);
    internal sealed record Decision(Item Item, string Outcome, int? CommandId, long? SubmittedAt, string Server, string CycleId, int[] CoveredIds);

    internal static string SettingsKey(ArrInstanceConfig arr) => Convert.ToHexString(
        System.Security.Cryptography.SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(new { arr.Search, arr.Category, arr.Type })));

    internal static Item Describe(SonarrSearchPlan plan)
    {
        var candidate = plan.Trigger;
        var item = candidate.ArrId > 0 ? candidate : plan.Covered.FirstOrDefault() ?? candidate;
        return new(item.ArrId, DiagnosticText.Safe(item.Type), item.SeriesId, item.SeasonNumber, item.ArtistId, item.AuthorId,
            DiagnosticText.Safe(item.Reason), plan.Command.Length == 0 ? DiagnosticText.Safe(item.Type) : plan.Command);
    }

    internal Task RecordAsync(SonarrSearchPlan plan, string outcome, string cycle, CancellationToken ct, int? commandId = null, long? submittedAt = null)
    {
        var item = Describe(plan);
        var key = outcome == "submitted"
            ? "submitted:" + cycle
            : $"decision:{item.Type}:{item.Id}:{item.SeriesId}:{item.Season}:{item.Scope}:{item.Reason}:{outcome}";
        var covered = outcome == "submitted"
            ? plan.Covered.Select(c => c.ArrId).Where(id => id > 0).Distinct().Take(1000).ToArray() : [];
        return WriteAsync(key, new Decision(item, outcome, commandId, submittedAt, _server, cycle, covered), ct);
    }

    private async Task WriteAsync<T>(string key, T data, CancellationToken ct)
    {
        if (!db.Database.IsSqlite()) return;
        var now = clock.GetUtcNow().ToUnixTimeSeconds();
        var timeout = db.Database.GetCommandTimeout();
        try
        {
            db.Database.SetCommandTimeout(1);
            await db.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO torrentarr_search_decisions (Instance, Key, ObservedAt, Occurrences, Data)
                VALUES ({instance}, {key}, {now}, 1, {JsonSerializer.Serialize(data)})
                ON CONFLICT (Instance, Key) DO UPDATE SET ObservedAt = excluded.ObservedAt,
                    Occurrences = min(2147483647, Occurrences + 1), Data = excluded.Data
                WHERE excluded.ObservedAt >= ObservedAt
                """, ct);
            await db.Database.ExecuteSqlInterpolatedAsync($"""
                    DELETE FROM torrentarr_search_decisions
                    WHERE Instance = {instance} AND Key NOT LIKE 'preview:%' AND
                        (ObservedAt < {now - RetentionSeconds} OR Key IN (
                            SELECT Key FROM torrentarr_search_decisions
                            WHERE Instance = {instance} AND Key NOT LIKE 'preview:%'
                            ORDER BY ObservedAt DESC, Key DESC LIMIT -1 OFFSET {MaxHistory}
                        ))
                    """, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            logger.LogDebug("Search decision history unavailable for {Instance}: {Error}", instance, DiagnosticText.Error(ex));
        }
        finally { db.Database.SetCommandTimeout(timeout); }
    }
}

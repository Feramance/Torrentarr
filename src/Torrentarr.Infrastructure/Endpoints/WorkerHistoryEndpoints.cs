using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Torrentarr.Core.Configuration;
using Torrentarr.Infrastructure.Database;
using Torrentarr.Infrastructure.Services;

namespace Torrentarr.Infrastructure.Endpoints;

public static class WorkerHistoryEndpoints
{
    public static readonly string[] Types = ["search", "torrent-removal", "import", "add", "update", "delete", "temp-profile",
        "queue-removal", "torrent-add", "torrent-update", "pause", "resume", "tags", "trackers", "category", "recheck", "command"];

    public static void MapWorkerHistory(this WebApplication app, string prefix) =>
        app.MapGet(prefix + "/history", async (string? worker, string? type, long? cursor, int? pageSize,
            TorrentarrConfig config, TorrentarrDbContext db, CancellationToken ct) =>
        {
            if (pageSize is < 1 or > 100 || cursor is < 1 || (type != null && !Types.Contains(type)))
                return Results.BadRequest(new { error = "Invalid history type, cursor or page size." });
            return Results.Ok(await ReadAsync(db, config, worker, type, cursor, pageSize ?? 50, ct));
        });

    internal static async Task<object> ReadAsync(TorrentarrDbContext db, TorrentarrConfig config, string? worker,
        string? type, long? cursor, int pageSize, CancellationToken ct)
    {
        var query = db.WorkerHistory.AsNoTracking();
        if (!string.IsNullOrEmpty(worker)) query = query.Where(e => e.Instance == worker);
        if (!string.IsNullOrEmpty(type)) query = query.Where(e => e.Type == type);
        if (cursor.HasValue) query = query.Where(e => e.Id < cursor.Value);
        var entries = await query.OrderByDescending(e => e.Id).Take(pageSize + 1).ToListAsync(ct);
        var more = entries.Count > pageSize;
        if (more) entries.RemoveAt(pageSize);
        var workers = (await db.WorkerHistory.Select(e => e.Instance).Distinct().ToListAsync(ct))
            .Concat(config.ArrInstances.Keys).Distinct(StringComparer.Ordinal).Order().ToArray();
        return new { entries, workers, types = Types, nextCursor = more ? (long?)entries[^1].Id : null };
    }
}

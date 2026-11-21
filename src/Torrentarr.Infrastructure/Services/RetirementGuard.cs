using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Torrentarr.Core.Configuration;
using Torrentarr.Core.Models;
using Torrentarr.Core.Services;
using Torrentarr.Infrastructure.Database;

namespace Torrentarr.Infrastructure.Services;

internal static class RetirementGuard
{
    internal static (int Seeders, int Inactivity) Thresholds(TorrentarrConfig config, TorrentInfo torrent)
    {
        var client = config.GetTorrentClient(torrent.ClientInstanceId);
        if (client == null) return default;
        var policy = SeedingService.ApplyCategoryOverride(client.CategorySeeding, torrent.Category, client.MatchSubcategories);
        return (policy.MinimumReportedSeeders, policy.MinimumUploadInactivitySeconds);
    }

    internal static TorrentRemovalIntent Intent(TorrentarrConfig config, TorrentInfo torrent) =>
        Thresholds(config, torrent) == default ? TorrentRemovalIntent.SeedingLimit : TorrentRemovalIntent.OrdinaryRetirement;

    internal static async Task<List<RemovalBlocker>> EvaluateAsync(TorrentarrDbContext db, TorrentarrConfig config, TorrentInfo torrent,
        long now, CancellationToken ct)
    {
        var (seeders, inactivity) = Thresholds(config, torrent);
        if (seeders <= 0 && inactivity <= 0) return [];
        var blockers = new List<RemovalBlocker>();
        var client = config.GetTorrentClient(torrent.ClientInstanceId)!;
        var maxAge = 2 * client.TorrentScanIntervalSeconds;
        var age = now - torrent.ObservedAtUnixSeconds;
        var fresh = torrent.ObservedAtUnixSeconds > 0 && age >= 0 && age <= maxAge;
        var ageText = torrent.ObservedAtUnixSeconds > 0 ? $"{age}s" : "unavailable";

        if (seeders > 0 && (!fresh || torrent.ObservedSeeders is not >= 0 || torrent.ObservedSeeders < seeders))
            blockers.Add(new("retirement.seeders",
                $"Reported seeders={(torrent.ObservedSeeders is >= 0 ? torrent.ObservedSeeders.ToString() : "unavailable")}; minimum={seeders}; observation age={ageText} (limit {maxAge}s).",
                "Wait for a fresh swarm count meeting the required minimum."));
        if (inactivity > 0)
        {
            long? idle = null;
            if (fresh && torrent.AddedOn > 0 && torrent.AddedOn <= torrent.ObservedAtUnixSeconds && db.Database.IsSqlite())
            {
                try { idle = await ObserveAsync(db, client, torrent, maxAge, ct); }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                catch (System.Data.Common.DbException) { /* An unavailable observation store cannot clear this guard. */ }
            }
            if (idle == null || idle < inactivity)
                blockers.Add(new("retirement.upload-inactivity",
                    $"Verified upload inactivity={(idle.HasValue ? $"{idle}s" : "unavailable")}; minimum={inactivity}s; observation age={ageText} (limit {maxAge}s).",
                    "Wait for continuous fresh upload-counter evidence."));
        }
        return blockers;
    }

    private static async Task<long?> ObserveAsync(TorrentarrDbContext db, TorrentClientInstanceConfig client,
        TorrentInfo torrent, int maxGap, CancellationToken ct)
    {
        var id = torrent.ClientInstanceId.ToUpperInvariant();
        var hash = torrent.Hash.ToUpperInvariant();
        if (hash.Length == 0) return null;
        var identity = Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(new
        { client.Host, client.Port, client.Type, torrent.CompletionOn, torrent.ContentPath, torrent.Size })));
        var category = CategoryPathHelper.NormalizeCategory(torrent.Category);
        var seen = torrent.ObservedAtUnixSeconds;
        var uploaded = torrent.ObservedUploaded ?? -1;
        var currentlyUploading = torrent.UploadSpeed > 0;
        var addedOn = torrent.AddedOn;
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO torrentarr_retirement_upload (Client, Hash, Identity, Category, AddedOn, Uploaded, Seen, Since)
            VALUES ({id}, {hash}, {identity}, {category}, {addedOn}, {uploaded}, {seen}, {seen})
            ON CONFLICT (Client, Hash) DO UPDATE SET
                Since = CASE WHEN Identity = excluded.Identity AND Category = excluded.Category
                    AND AddedOn = excluded.AddedOn AND Uploaded >= 0 AND Uploaded = excluded.Uploaded
                    AND excluded.Seen - Seen <= {maxGap} AND NOT {currentlyUploading} THEN Since ELSE excluded.Seen END,
                Identity = excluded.Identity, Category = excluded.Category, AddedOn = excluded.AddedOn, Uploaded = excluded.Uploaded, Seen = excluded.Seen
            WHERE excluded.Seen >= Seen
            """, ct);
        var row = await db.RetirementUploads.AsNoTracking().SingleOrDefaultAsync(o => o.Client == id && o.Hash == hash, ct);
        return row != null && row.Seen == seen && row.Identity == identity && row.Category == category
            && row.AddedOn == addedOn && row.Uploaded == uploaded && uploaded >= 0 ? seen - row.Since : null;
    }

    internal static async Task ForgetAsync(TorrentarrDbContext db, TorrentarrConfig config, string clientId,
        string? category, IReadOnlyList<TorrentInfo>? inventory, CancellationToken ct)
    {
        if (!db.Database.IsSqlite()) return;
        var policy = config.GetTorrentClient(clientId)?.CategorySeeding;
        if (policy == null || policy.MinimumUploadInactivitySeconds <= 0 && !policy.Categories.Any(c => c.MinimumUploadInactivitySeconds > 0))
            return;
        var id = clientId.ToUpperInvariant();
        var query = db.RetirementUploads.Where(o => o.Client == id);
        if (inventory != null)
        {
            if (category != null)
            {
                var normalized = CategoryPathHelper.NormalizeCategory(category);
                query = query.Where(o => o.Category == normalized);
            }
            var hashes = inventory.Where(t => t.Progress >= 1 && t.AmountLeft == 0)
                .Select(t => t.Hash.ToUpperInvariant()).ToArray();
            query = query.Where(o => !hashes.Contains(o.Hash));
        }
        await query.ExecuteDeleteAsync(ct);
    }
}

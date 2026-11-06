using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Torrentarr.Core.Configuration;
using Torrentarr.Core.Interfaces;
using Torrentarr.Infrastructure.Database;
using Torrentarr.Infrastructure.Database.Models;

namespace Torrentarr.Infrastructure.Services;

public sealed record TransferBucket(DateTimeOffset Timestamp, double? DownloadRate, double? UploadRate, int Samples, int ExpectedSamples);
public sealed record TransferSeries(string Instance, List<TransferBucket> Points);
public sealed record TransferHistoryResponse(string Range, int BucketMinutes, DateTimeOffset GeneratedAt, List<TransferSeries> Series);

/// <summary>One lightweight transfer request per enabled client per minute; no torrent scans.</summary>
public sealed class TransferHistoryCollector(
    IServiceScopeFactory scopes, TorrentarrConfig config, ITorrentClientRegistry registry,
    ILogger<TransferHistoryCollector> logger) : BackgroundService
{
    private long _lastPruneMinute = long.MinValue;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(1));
        do
        {
            try { await CollectAsync(DateTimeOffset.UtcNow, stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception ex) { logger.LogWarning(ex, "Transfer history collection failed"); }
        } while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    internal async Task CollectAsync(DateTimeOffset now, CancellationToken ct)
    {
        var minute = now.ToUnixTimeSeconds() / 60;
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TorrentarrDbContext>();
        foreach (var (name, settings) in config.GetAllTorrentClients().ToArray())
        {
            if (settings.Disabled || registry.GetClient(name) is not { } client) continue;
            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeout.CancelAfter(TimeSpan.FromSeconds(10));
                var transfer = await client.GetTransferInfoAsync(timeout.Token);
                if (transfer == null || transfer.DownloadSpeed < 0 || transfer.UploadSpeed < 0) continue;
                if (await db.TransferSamples.AnyAsync(s => s.ClientInstance == name && s.Minute == minute, ct)) continue;
                db.TransferSamples.Add(new TransferSample
                {
                    ClientInstance = name,
                    Minute = minute,
                    DownloadRate = transfer.DownloadSpeed,
                    UploadRate = transfer.UploadSpeed
                });
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex) { logger.LogDebug(ex, "Transfer history unavailable for {Client}", name); }
        }
        await db.SaveChangesAsync(ct);
        if (_lastPruneMinute == long.MinValue || minute - _lastPruneMinute >= 60)
        {
            await db.TransferSamples.Where(s => s.Minute < minute - 7 * 24 * 60).ExecuteDeleteAsync(ct);
            _lastPruneMinute = minute;
        }
    }
}

public static class TransferHistoryApi
{
    public static void MapTransferHistory(this IEndpointRouteBuilder app)
    {
        app.MapGet("/web/statistics/catalog", async (string kind, string? instance,
            TorrentarrConfig config, CatalogRollupService rollups, TorrentarrDbContext db, CancellationToken ct) =>
        {
            if (kind is not ("radarr" or "sonarr" or "lidarr" or "readarr"))
                return Results.BadRequest(new { error = "Unknown catalog kind" });
            var keys = config.ArrInstances.Where(a => a.Value.Type == kind &&
                (string.IsNullOrEmpty(instance) || instance == "aggregate" || a.Key.Equals(instance, StringComparison.OrdinalIgnoreCase)))
                .SelectMany(a => ArrCatalogIdentity.QueryKeys(a)).Distinct().ToList();
            if (!string.IsNullOrEmpty(instance) && instance != "aggregate" && keys.Count == 0)
                return Results.BadRequest(new { error = "Unknown catalog instance" });
            CatalogRollupService.Counts counts;
            int total;
            string unit;
            switch (kind)
            {
                case "radarr":
                    (counts, total) = await rollups.GetRadarrRollupsAsync(keys, ct); unit = "movies"; break;
                case "sonarr":
                    (counts, _) = await rollups.GetSonarrRollupsAsync(keys, ct);
                    total = await db.Episodes.CountAsync(e => keys.Contains(e.ArrInstance), ct); unit = "episodes"; break;
                case "lidarr":
                    (counts, total, _) = await rollups.GetLidarrRollupsAsync(keys, ct); unit = "albums"; break;
                default:
                    (counts, total) = await rollups.GetReadarrRollupsAsync(keys, ct); unit = "books"; break;
            }
            return Results.Ok(new { unit, total, counts.Available, counts.Monitored, counts.Missing });
        });
        app.MapGet("/web/statistics/transfers", async (string? instance, string? range,
            TorrentarrConfig config, TorrentarrDbContext db, CancellationToken ct) =>
        {
            range ??= "24h";
            if (range is not ("1h" or "24h" or "7d"))
                return Results.BadRequest(new { error = "range must be 1h, 24h, or 7d" });
            var names = config.GetAllTorrentClients().Where(c => !c.Value.Disabled).Select(c => c.Key).ToList();
            if (!string.IsNullOrEmpty(instance) && instance != "all")
            {
                var match = names.Find(n => n.Equals(instance, StringComparison.OrdinalIgnoreCase));
                if (match == null) return Results.BadRequest(new { error = "Unknown client instance" });
                names = [match];
            }
            return Results.Ok(await QueryAsync(db, names, range, DateTimeOffset.UtcNow, ct));
        });
    }

    public static async Task<TransferHistoryResponse> QueryAsync(TorrentarrDbContext db,
        List<string> names, string range, DateTimeOffset now, CancellationToken ct = default)
    {
        var (minutes, bucket) = range switch
        {
            "1h" => (60, 1),
            "24h" => (1440, 5),
            "7d" => (10080, 30),
            _ => throw new ArgumentOutOfRangeException(nameof(range))
        };
        // Use completed wall-clock buckets so the expected coverage is unambiguous.
        var end = now.ToUnixTimeSeconds() / 60 / bucket * bucket;
        var start = end - minutes;
        var rows = await db.TransferSamples.AsNoTracking().Where(s => names.Contains(s.ClientInstance)
            && s.Minute >= start && s.Minute < end).ToListAsync(ct);
        var lookup = rows.ToLookup(s => (s.ClientInstance, Bucket: s.Minute / bucket * bucket));
        var series = names.Select(name => new TransferSeries(name,
            Enumerable.Range(0, minutes / bucket).Select(i =>
            {
                var minute = start + i * bucket;
                var samples = lookup[(name, minute)].ToList();
                return new TransferBucket(DateTimeOffset.FromUnixTimeSeconds(minute * 60),
                    samples.Count == 0 ? null : samples.Average(s => (double)s.DownloadRate),
                    samples.Count == 0 ? null : samples.Average(s => (double)s.UploadRate), samples.Count, bucket);
            }).ToList())).ToList();
        return new(range, bucket, now, series);
    }
}

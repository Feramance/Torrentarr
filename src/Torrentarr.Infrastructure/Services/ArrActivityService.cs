using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json.Linq;
using Torrentarr.Core.Configuration;
using Torrentarr.Core.Interfaces;
using Torrentarr.Infrastructure.ApiClients.Arr;
using Torrentarr.Infrastructure.Database;
using Torrentarr.Infrastructure.Database.Models;

namespace Torrentarr.Infrastructure.Services;

public sealed class ArrActivityCollector(IServiceScopeFactory scopes, IServiceProvider services,
    TorrentarrConfig config, ILogger<ArrActivityCollector> logger) : BackgroundService
{
    private long _lastPrune;
    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(1));
        do
        {
            try
            {
                var supervisor = services.GetService<IProcessOrchestrator>();
                var statuses = supervisor == null ? new Dictionary<string, ProcessStatus>() : await supervisor.GetProcessStatusAsync();
                await CollectAsync(DateTimeOffset.UtcNow, statuses, ReadAsync, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
            catch (Exception ex) { logger.LogWarning(ex, "Arr activity collection unavailable"); }
        } while (await timer.WaitForNextTickAsync(ct));
    }

    internal static async Task<JArray> ReadAsync(ArrInstanceConfig settings, string resource, CancellationToken ct)
    {
        using IArrActivityClient client = settings.Type.ToLowerInvariant() switch
        {
            "radarr" => new RadarrClient(settings.URI, settings.APIKey, settings.SkipTLSVerify),
            "sonarr" => new SonarrClient(settings.URI, settings.APIKey, settings.SkipTLSVerify),
            "lidarr" => new LidarrClient(settings.URI, settings.APIKey, settings.SkipTLSVerify),
            "readarr" => new ReadarrClient(settings.URI, settings.APIKey, settings.SkipTLSVerify),
            _ => throw new InvalidOperationException("Unsupported Arr activity type")
        };
        return await client.ReadActivityAsync(resource, ct);
    }

    internal async Task CollectAsync(DateTimeOffset now, IReadOnlyDictionary<string, ProcessStatus> statuses,
        Func<ArrInstanceConfig, string, CancellationToken, Task<JArray>> read, CancellationToken ct = default)
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TorrentarrDbContext>();
        var minute = now.ToUnixTimeSeconds() / 60;
        foreach (var (name, settings) in config.ArrInstances.ToArray())
        {
            if (!settings.Managed || settings.Type is not ("radarr" or "sonarr" or "lidarr" or "readarr")) continue;
            var status = statuses.GetValueOrDefault(name);
            var fresh = status?.IsAlive == true && status.ConfigRevision >= config.Revision && status.LastHeartbeat is { } heartbeat
                && now.UtcDateTime - heartbeat <= TimeSpan.FromSeconds(15) && heartbeat <= now.UtcDateTime;
            var observed = fresh;
            try
            {
                // Repeated callers in one minute must not poll Arr again or rewrite coverage.
                if (await db.ArrActivitySamples.AnyAsync(s => s.Instance == name && s.Minute == minute, ct)) continue;
                var pending = await db.ArrActivityOperations.Where(o => o.Instance == name && o.Outcome == "pending").ToListAsync(ct);
                var eligible = new List<ArrActivityOperation>();
                foreach (var operation in pending)
                {
                    if (operation.Server != ArrActivityTracking.ServerKey(settings) || operation.SubmittedAt < now.ToUnixTimeSeconds() - 604800)
                        operation.Outcome = "expired";
                    else eligible.Add(operation);
                }
                if (eligible.Count > 0)
                {
                    using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    timeout.CancelAfter(TimeSpan.FromSeconds(20));
                    var commands = await read(settings, "command", timeout.Token);
                    JArray history = [];
                    if (eligible.Any(o => o.Kind == "import"))
                    {
                        var since = DateTimeOffset.FromUnixTimeSeconds(eligible.Where(o => o.Kind == "import").Min(o => o.SubmittedAt));
                        var eventType = settings.Type is "lidarr" or "readarr" ? 8 : 3;
                        history = await read(settings, $"history/since?date={Uri.EscapeDataString(since.ToString("O"))}&eventType={eventType}", timeout.Token);
                    }
                    await ReconcileAsync(db, eligible, commands, history, now, ct);
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex) { observed = false; logger.LogDebug(ex, "Arr completion evidence unavailable for {Instance}", name); }
            db.ArrActivitySamples.Add(new ArrActivitySample
            {
                Instance = name,
                Type = settings.Type,
                Minute = minute,
                QueueDepth = fresh && status!.QueueCount is >= 0 ? status.QueueCount : null,
                CompletionObserved = observed
            });
            await db.SaveChangesAsync(ct);
        }
        if (minute - _lastPrune >= 60)
        {
            await db.ArrActivitySamples.Where(s => s.Minute < minute - 10080).ExecuteDeleteAsync(ct);
            await db.ArrActivityOperations.Where(o => (o.ConfirmedAt ?? o.SubmittedAt) < now.ToUnixTimeSeconds() - 604800).ExecuteDeleteAsync(ct);
            _lastPrune = minute;
        }
    }

    internal static async Task ReconcileAsync(TorrentarrDbContext db, List<ArrActivityOperation> pending,
        JArray commands, JArray history, DateTimeOffset now, CancellationToken ct = default)
    {
        var importInstances = pending.Where(o => o.Kind == "import").Select(o => o.Instance).Distinct().ToList();
        var importServers = pending.Where(o => o.Kind == "import").Select(o => o.Server).Distinct().ToList();
        var importHashes = pending.Where(o => o.Kind == "import").Select(o => o.DownloadId).Distinct().ToList();
        var previousImports = importInstances.Count == 0 ? new List<ArrActivityOperation>() : await db.ArrActivityOperations
            .Where(o => o.Kind == "import" && importInstances.Contains(o.Instance) && importServers.Contains(o.Server) && importHashes.Contains(o.DownloadId)).ToListAsync(ct);
        var claimed = previousImports.Where(o => o.EvidenceId != null).Select(o => (o.Instance, o.Server, o.EvidenceId)).ToHashSet();
        var confirmedImports = previousImports.Where(o => o.Outcome == "completed").ToList();
        foreach (var operation in pending.OrderBy(o => o.SubmittedAt).ThenBy(o => o.CommandId))
        {
            if (operation.Outcome != "pending") continue;
            var command = commands.FirstOrDefault(c => c["id"]?.Value<int>() == operation.CommandId);
            var state = command?["status"]?.Value<string>()?.ToLowerInvariant();
            if (state is "failed" or "aborted" or "cancelled" or "canceled") { operation.Outcome = "failed"; continue; }
            var result = command?["result"]?.Value<string>()?.ToLowerInvariant();
            if (result is "failed" or "unsuccessful" or "2") { operation.Outcome = "failed"; continue; }
            if (result is "unknown" or "0") continue;
            if (state != "completed" && !(operation.Kind == "import" && command == null)) continue;
            if (operation.Kind == "search" && result is not ("successful" or "1")) continue;
            if (operation.Kind == "import")
            {
                if (confirmedImports.Any(o => o.Instance == operation.Instance && o.Server == operation.Server && o.DownloadId == operation.DownloadId
                    && o.ConfirmedAt >= operation.SubmittedAt)) { operation.Outcome = "duplicate"; continue; }
                var evidence = history.FirstOrDefault(h => PositiveImport(h, operation)
                    && !claimed.Contains((operation.Instance, operation.Server, "history:" + h["id"]!.Value<string>())));
                if (evidence == null) continue;
                operation.EvidenceId = "history:" + evidence["id"]!.Value<string>();
                claimed.Add((operation.Instance, operation.Server, operation.EvidenceId));
            }
            operation.Outcome = "completed";
            operation.ConfirmedAt = now.ToUnixTimeSeconds();
            if (operation.Kind == "import") confirmedImports.Add(operation);
        }
    }

    internal static bool PositiveImport(JToken history, ArrActivityOperation operation)
    {
        var type = history["eventType"]?.Value<string>();
        var expected = operation.Type is "lidarr" or "readarr" ? "downloadImported" : "downloadFolderImported";
        var numeric = operation.Type is "lidarr" or "readarr" ? "8" : "3";
        return history["id"] != null && !string.IsNullOrEmpty(operation.DownloadId)
            && string.Equals(history["downloadId"]?.Value<string>(), operation.DownloadId, StringComparison.OrdinalIgnoreCase)
            && (string.Equals(type, expected, StringComparison.OrdinalIgnoreCase) || type == numeric)
            && DateTimeOffset.TryParse(history["date"]?.Value<string>(), System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.RoundtripKind, out var date)
            && date.ToUnixTimeSeconds() >= operation.SubmittedAt;
    }
}

public sealed record ArrActivityPoint(DateTimeOffset Timestamp, int? QueueDepth, int? SearchesCompleted,
    int? ImportsConfirmed, int QueueSamples, int CompletionSamples, int ExpectedSamples);
public sealed record ArrActivitySeries(string Instance, string Type, List<ArrActivityPoint> Points);
public sealed record ArrActivityHistory(string Range, int BucketMinutes, DateTimeOffset GeneratedAt, List<ArrActivitySeries> Series);

public static class ArrActivityApi
{
    public static void MapArrActivity(this IEndpointRouteBuilder app) => app.MapGet("/web/statistics/arr-activity",
        async (string? range, string? worker, TorrentarrConfig config, TorrentarrDbContext db, CancellationToken ct) =>
        {
            range ??= "24h";
            if (range is not ("1h" or "24h" or "7d")) return Results.BadRequest(new { error = "range must be 1h, 24h, or 7d" });
            if (!string.IsNullOrEmpty(worker) && !config.ArrInstances.ContainsKey(worker))
                return Results.BadRequest(new { error = "Unknown Arr worker" });
            var canonicalWorker = string.IsNullOrEmpty(worker) ? null : config.ArrInstances.Keys.First(n => n.Equals(worker, StringComparison.OrdinalIgnoreCase));
            return Results.Ok(await QueryAsync(db, config, range, canonicalWorker, DateTimeOffset.UtcNow, ct));
        });

    public static async Task<ArrActivityHistory> QueryAsync(TorrentarrDbContext db, TorrentarrConfig config,
        string range, string? worker, DateTimeOffset now, CancellationToken ct = default)
    {
        var (duration, bucket) = range switch
        {
            "1h" => (60, 1),
            "24h" => (1440, 60),
            "7d" => (10080, 60),
            _ => throw new ArgumentOutOfRangeException(nameof(range))
        };
        var end = now.ToUnixTimeSeconds() / 60 / bucket * bucket;
        var start = end - duration;
        var samples = await db.ArrActivitySamples.AsNoTracking().Where(s => s.Minute >= start && s.Minute < end
            && (worker == null || worker == "" || s.Instance == worker))
            .GroupBy(s => new { s.Instance, s.Type, Bucket = s.Minute / bucket * bucket })
            .Select(g => new
            {
                g.Key.Instance,
                g.Key.Type,
                g.Key.Bucket,
                QueueDepth = g.Average(s => (double?)s.QueueDepth),
                QueueSamples = g.Count(s => s.QueueDepth != null),
                CompletionSamples = g.Count(s => s.CompletionObserved)
            }).ToListAsync(ct);
        var events = await db.ArrActivityOperations.AsNoTracking().Where(o => o.Outcome == "completed" && o.ConfirmedAt >= start * 60 && o.ConfirmedAt < end * 60
            && (worker == null || worker == "" || o.Instance == worker))
            .GroupBy(o => new { o.Instance, o.Type, Bucket = o.ConfirmedAt!.Value / 60 / bucket * bucket })
            .Select(g => new
            {
                g.Key.Instance,
                g.Key.Type,
                g.Key.Bucket,
                Searches = g.Count(o => o.Kind == "search"),
                Imports = g.Count(o => o.Kind == "import")
            }).ToListAsync(ct);
        var names = config.ArrInstances.Where(a => a.Value.Managed && (string.IsNullOrEmpty(worker) || a.Key == worker))
            .Select(a => a.Key).Union(samples.Select(s => s.Instance)).Union(events.Select(s => s.Instance)).Order().ToList();
        var sampleLookup = samples.ToLookup(s => (s.Instance, s.Bucket));
        var eventLookup = events.ToLookup(s => (s.Instance, s.Bucket));
        var series = names.Select(name => new ArrActivitySeries(name,
            samples.OrderBy(s => s.Bucket).LastOrDefault(s => s.Instance == name)?.Type ?? events.FirstOrDefault(e => e.Instance == name)?.Type ?? config.ArrInstances[name].Type,
            Enumerable.Range(0, duration / bucket).Select(i =>
            {
                var minute = start + i * bucket;
                var rows = sampleLookup[(name, minute)].ToList();
                var queueSamples = rows.Sum(s => s.QueueSamples);
                var coverage = rows.Sum(s => s.CompletionSamples);
                var completed = eventLookup[(name, minute)].ToList();
                var searches = completed.Sum(e => e.Searches);
                var imports = completed.Sum(e => e.Imports);
                return new ArrActivityPoint(DateTimeOffset.FromUnixTimeSeconds(minute * 60),
                    queueSamples == 0 ? null : (int)Math.Round(rows.Sum(s => (s.QueueDepth ?? 0) * s.QueueSamples) / queueSamples,
                        MidpointRounding.AwayFromZero),
                    coverage == 0 && searches == 0 ? null : searches,
                    coverage == 0 && imports == 0 ? null : imports, queueSamples, coverage, bucket);
            }).ToList())).ToList();
        return new(range, bucket, now, series);
    }
}

using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Torrentarr.Core.Configuration;
using Torrentarr.Core.Services;
using Torrentarr.Infrastructure.ApiClients.Arr;
using Torrentarr.Infrastructure.Database;
using Torrentarr.Infrastructure.Database.Models;

namespace Torrentarr.Infrastructure.Services;

// Keeps the historical Sonarr table/API name while sharing durable replacement handling across Arr types.
public static class SonarrRetryQueue
{
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> Gates = new(StringComparer.OrdinalIgnoreCase);
    private static SemaphoreSlim Gate(string instance) => Gates.GetOrAdd(instance, _ => new(1, 1));
    private static OperationalIssueReport Issue(string instance, string hash) => new("import.replacement", "automation",
        "Replacement search could not complete", "The replacement request is preserved for retry; completion has not been confirmed.",
        "Check Arr connectivity, command access and Logs. Torrentarr retries the saved request after recovery.",
        ArrInstance: instance, SubjectType: "torrent", SubjectId: hash);

    public static async Task<bool> StageAndExecuteAsync(TorrentarrDbContext db, ISearchExecutor executor, string instance,
        string hash, List<SearchCandidate> candidates, ArrInstanceConfig config, ILogger logger,
        IOperationalIssueReporter? issues, CancellationToken ct)
    {
        if (candidates.Count == 0) throw new IOException("Replacement search has no verified media identity; blocklisting was skipped.");
        var gate = Gate(instance);
        await gate.WaitAsync(ct);
        try
        {
            if (!await db.SonarrSearchRetries.AnyAsync(r => r.ArrInstance == instance && r.DownloadId == hash, ct))
            {
                db.SonarrSearchRetries.Add(new()
                {
                    ArrInstance = instance,
                    DownloadId = hash,
                    CandidatesJson = JsonSerializer.Serialize(candidates),
                    BlocklistPending = true
                });
                // No external mutation until the recovery details are durable.
                await db.SaveChangesAsync(ct);
            }
        }
        finally { gate.Release(); }
        await ExecuteAsync(db, executor, instance, logger, ct, config, issues);
        return !await db.SonarrSearchRetries.AnyAsync(r => r.ArrInstance == instance && r.DownloadId == hash, ct);
    }

    public static async Task ExecuteAsync(TorrentarrDbContext db, ISearchExecutor executor,
        string instanceName, ILogger logger, CancellationToken ct, ArrInstanceConfig? config = null,
        IOperationalIssueReporter? issues = null)
        => await ExecuteAsync(db, executor, instanceName, logger, ct, config, issues, null);

    public static async Task ExecuteAsync(TorrentarrDbContext db, ISearchExecutor executor,
        string instanceName, ILogger logger, CancellationToken ct, ArrInstanceConfig? config,
        IOperationalIssueReporter? issues, DateTimeOffset? deadline)
    {
        var gate = Gate(instanceName);
        await gate.WaitAsync(ct);
        try
        {
            var pending = await db.SonarrSearchRetries.Where(r => r.ArrInstance == instanceName).ToListAsync(ct);
            foreach (var retry in pending)
            {
                ct.ThrowIfCancellationRequested();
                if (deadline is { } end && end - DateTimeOffset.UtcNow <= TimeSpan.FromMinutes(1)) break;
                await db.Entry(retry).ReloadAsync(ct);
                if (db.Entry(retry).State == EntityState.Detached) continue;
                try
                {
                    if (retry.BlocklistPending)
                    {
                        if (config == null) throw new IOException("Arr configuration is required to verify pending blocklisting.");
                        await ConfirmBlocklistAsync(config, retry.DownloadId, ct);
                        retry.BlocklistPending = false;
                        await db.SaveChangesAsync(ct);
                    }
                    var candidates = JsonSerializer.Deserialize<List<SearchCandidate>>(retry.CandidatesJson)!;
                    var result = await (deadline.HasValue
                        ? executor.ExecuteSearchesAsync(instanceName, candidates, deadline, ct)
                        : executor.ExecuteSearchesAsync(instanceName, candidates, ct));
                    if (result.LoopCompleted)
                        db.SonarrSearchRetries.Remove(retry);
                    else
                        retry.CandidatesJson = JsonSerializer.Serialize((result.RetryCandidates ?? candidates)
                            .Where(c => !result.SearchedIds.Contains(c.ArrId)));
                    await db.SaveChangesAsync(ct);
                    if (issues != null)
                    {
                        if (result.LoopCompleted) await issues.ResolveAsync(Issue(instanceName, retry.DownloadId), ct);
                        else if (result.Errors.Count > 0) await issues.ReportAsync(Issue(instanceName, retry.DownloadId), ct);
                    }
                    // Capacity limits retain work without creating an operational issue.
                    if (!result.LoopCompleted && !result.OnlyRetryResolutionFailed) break;
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                catch (Exception ex)
                {
                    db.Entry(retry).State = EntityState.Detached;
                    logger.LogWarning(ex, "Replacement search for {Instance}/{Hash} retained for retry", instanceName, retry.DownloadId);
                    if (issues != null) await issues.ReportAsync(Issue(instanceName, retry.DownloadId), ct);
                }
            }
        }
        finally { gate.Release(); }
    }

    private static async Task ConfirmBlocklistAsync(ArrInstanceConfig config, string hash, CancellationToken ct)
    {
        // A complete fresh queue also confirms a DELETE whose response was lost before a crash.
        List<int> ids;
        Func<int, Task<bool>> remove;
        switch (config.Type.ToLowerInvariant())
        {
            case "sonarr":
                var s = new SonarrClient(config.URI, config.APIKey, config.SkipTLSVerify);
                ids = (await s.GetAllQueueAsync(ct)).Where(q => string.Equals(q.DownloadId, hash, StringComparison.OrdinalIgnoreCase)).Select(q => q.Id).ToList();
                remove = id => s.DeleteFromQueueAsync(id, false, true, ct, skipRedownload: true); break;
            case "radarr":
                var r = new RadarrClient(config.URI, config.APIKey, config.SkipTLSVerify);
                ids = (await r.GetAllQueueAsync(ct)).Where(q => string.Equals(q.DownloadId, hash, StringComparison.OrdinalIgnoreCase)).Select(q => q.Id).ToList();
                remove = id => r.DeleteFromQueueAsync(id, false, true, ct, skipRedownload: true); break;
            case "lidarr":
                var l = new LidarrClient(config.URI, config.APIKey, config.SkipTLSVerify);
                ids = (await l.GetAllQueueAsync(ct)).Where(q => string.Equals(q.DownloadId, hash, StringComparison.OrdinalIgnoreCase)).Select(q => q.Id).ToList();
                remove = id => l.DeleteFromQueueAsync(id, false, true, ct, skipRedownload: true); break;
            case "readarr":
                var b = new ReadarrClient(config.URI, config.APIKey, config.SkipTLSVerify);
                ids = (await b.GetAllQueueAsync(ct)).Where(q => string.Equals(q.DownloadId, hash, StringComparison.OrdinalIgnoreCase)).Select(q => q.Id).ToList();
                remove = id => b.DeleteFromQueueAsync(id, false, true, ct, skipRedownload: true); break;
            default: throw new IOException("Unknown Arr type for pending blocklisting.");
        }
        foreach (var id in ids)
            if (!await remove(id)) throw new IOException("Arr rejected blocklisting; replacement search remains staged.");
    }
}

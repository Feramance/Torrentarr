using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Torrentarr.Core.Services;
using Torrentarr.Infrastructure.Database;

namespace Torrentarr.Infrastructure.Services;

public static class SonarrRetryQueue
{
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> Gates = new(StringComparer.OrdinalIgnoreCase);

    public static async Task ExecuteAsync(TorrentarrDbContext db, ISearchExecutor executor,
        string instanceName, ILogger logger, CancellationToken ct)
    {
        // Torrent processing and the search loop can both drain the same instance's retries.
        var gate = Gates.GetOrAdd(instanceName, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct);
        try
        {
            var pending = await db.SonarrSearchRetries.Where(r => r.ArrInstance == instanceName).ToListAsync(ct);
            foreach (var retry in pending)
            {
                var candidates = JsonSerializer.Deserialize<List<SearchCandidate>>(retry.CandidatesJson)!;
                var result = await executor.ExecuteSearchesAsync(instanceName, candidates, ct);
                if (result.LoopCompleted)
                    db.SonarrSearchRetries.Remove(retry);
                else
                {
                    retry.CandidatesJson = JsonSerializer.Serialize((result.RetryCandidates ?? candidates)
                        .Where(c => !result.SearchedIds.Contains(c.ArrId)));
                    logger.LogWarning("Sonarr re-search after blocklist of {Hash} was not fully submitted ({Triggered} triggered); remaining retries are saved",
                        retry.DownloadId, result.SearchesTriggered);
                }
                await db.SaveChangesAsync(ct);
                if (!result.LoopCompleted && !result.OnlyRetryResolutionFailed) break;
            }
        }
        finally { gate.Release(); }
    }
}

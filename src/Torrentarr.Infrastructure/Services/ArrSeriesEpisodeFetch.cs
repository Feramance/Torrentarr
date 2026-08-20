using Microsoft.Extensions.Logging;
using Torrentarr.Infrastructure.ApiClients.Arr;

namespace Torrentarr.Infrastructure.Services;

/// <summary>
/// qBitrr 5.14.4: per-series Arr HTTP errors skip that series; transport errors abort;
/// all-fail rethrows so ingest is not marked complete.
/// </summary>
internal static class ArrSeriesEpisodeFetch
{
    /// <summary>
    /// Classify a failed episode list fetch. Returns true to skip this series.
    /// Transport errors are rethrown.
    /// </summary>
    public static bool ShouldSkipSeries(Exception ex)
    {
        if (ArrClientResponse.IsArrTransportError(ex))
            throw ex;
        if (ArrClientResponse.IsArrHttpError(ex))
            return true;
        throw ex;
    }

    public static async Task<(bool SkipEpisodePrune, List<int> EpisodeIds)> CollectEpisodeIdsAsync(
        IReadOnlyList<int> seriesIds,
        Func<int, CancellationToken, Task<IReadOnlyList<int>>> getEpisodeIds,
        ILogger logger,
        string instanceName,
        CancellationToken ct)
    {
        var episodeIds = new List<int>();
        var failed = 0;
        foreach (var sid in seriesIds)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var ids = await getEpisodeIds(sid, ct);
                episodeIds.AddRange(ids);
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                if (!ShouldSkipSeries(ex))
                    throw;
                failed++;
                logger.LogWarning(ex,
                    "{Instance}: skipping series {Id} during episode fetch",
                    instanceName, sid);
            }
        }

        return (failed > 0, episodeIds);
    }
}

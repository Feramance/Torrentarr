using Torrentarr.Core.Configuration;
using Torrentarr.Core.Interfaces;
using Torrentarr.Core.Models;
using Torrentarr.Infrastructure.ApiClients.QBittorrent;

namespace Torrentarr.Infrastructure.Services;

/// <summary>qBitrr <c>GET /web/qbit/overview</c> payload: monitored categories with torrent lists.</summary>
public static class QbitOverviewBuilder
{
    public const int MaxTorrentsPerCategory = 500;

    public static async Task<object> BuildAsync(
        TorrentarrConfig cfg,
        ITorrentClientRegistry qbitManager,
        string? instanceFilter,
        CancellationToken ct = default)
    {
        var filter = (instanceFilter ?? "").Trim();
        var includeAll = string.IsNullOrEmpty(filter) || filter.Equals("all", StringComparison.OrdinalIgnoreCase);

        var instanceNames = cfg.GetAllTorrentClients().Keys
            .Where(name => includeAll || name.Equals(filter, StringComparison.OrdinalIgnoreCase))
            .ToList();

        var arrCategories = cfg.ArrInstances.Values
            .Select(a => a.Category)
            .Where(c => !string.IsNullOrWhiteSpace(c))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var categories = new List<object>();
        foreach (var instanceName in instanceNames)
        {
            var qbit = cfg.GetTorrentClient(instanceName);
            if (qbit == null)
                continue;
            if (qbit.Disabled || qbit.Host == "CHANGE_ME" || string.IsNullOrEmpty(qbit.Host))
                continue;

            var client = qbitManager.GetClient(instanceName);
            if (client == null) continue;

            List<TorrentInfo> torrents;
            try
            {
                torrents = await client.GetTorrentsAsync(cancellationToken: ct);
            }
            catch
            {
                continue;
            }

            var monitored = new HashSet<string>(qbit.ManagedCategories, StringComparer.OrdinalIgnoreCase);
            monitored.UnionWith(arrCategories);

            foreach (var catName in monitored.OrderBy(c => c, StringComparer.OrdinalIgnoreCase))
            {
                var inCat = torrents.Where(t =>
                    CategoryPathHelper.MatchesConfigured(t.Category, new[] { catName }, prefix: qbit.MatchSubcategories)
                        == CategoryPathHelper.NormalizeCategory(catName)).ToList();
                var truncated = inCat.Count > MaxTorrentsPerCategory;
                var slice = inCat.Take(MaxTorrentsPerCategory).Select(SerializeTorrent).ToList();
                var seeding = inCat.Count(t => t.IsSeeding);

                categories.Add(new
                {
                    category = catName,
                    instance = instanceName,
                    qbitInstance = instanceName,
                    arrName = cfg.ArrInstances.FirstOrDefault(a => CategoryPathHelper.CategoryEquals(a.Value.Category, catName)).Key,
                    totalSize = inCat.Sum(t => t.Size),
                    avgRatio = inCat.Count > 0 ? inCat.Average(t => t.Ratio) : 0.0,
                    avgSeedingTime = inCat.Count > 0 ? inCat.Average(t => (double)t.SeedingTime) : 0.0,
                    seedingConfig = CategorySeedingApiResolver.ResolveAggregate(cfg, instanceName, qbit, catName, inCat),
                    managedBy = arrCategories.Contains(catName) ? "arr" : "qbit",
                    torrentCount = inCat.Count,
                    seedingCount = seeding,
                    truncated,
                    torrentsTruncated = truncated,
                    torrents = slice
                });
            }
        }

        return new
        {
            instances = instanceNames,
            categories,
            ready = true
        };
    }

    private static object SerializeTorrent(TorrentInfo t) => new
    {
        hash = t.Hash,
        name = t.Name,
        size = t.Size,
        progress = t.Progress,
        state = t.State,
        category = t.Category,
        ratio = t.Ratio,
        seedingTime = t.SeedingTime,
        addedOn = t.AddedOn,
        completionOn = t.CompletionOn,
        tags = t.Tags,
        eta = t.Eta,
        amountLeft = t.AmountLeft,
        priority = t.Priority,
        totalSize = t.TotalSize > 0 ? t.TotalSize : t.Size,
        downloaded = t.Downloaded,
        uploaded = t.Uploaded,
        dlspeed = t.DownloadSpeed,
        upspeed = t.UploadSpeed,
        numSeeds = t.NumSeeds,
        numLeechs = t.NumLeechs,
        numComplete = t.CompletePeers,
        numIncomplete = t.IncompletePeers,
        availability = t.Availability,
        timeActive = t.TimeActive,
        lastActivity = t.LastActivity,
        ratioLimit = t.RatioLimit,
        seedingTimeLimit = t.SeedingTimeLimit,
        dlLimit = t.DownloadLimit,
        upLimit = t.UploadLimit
    };
}

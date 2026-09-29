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

        var instanceNames = cfg.QBitInstances.Keys
            .Where(name => includeAll || name.Equals(filter, StringComparison.OrdinalIgnoreCase))
            .ToList();

        var arrCategories = cfg.ArrInstances.Values
            .Select(a => a.Category)
            .Where(c => !string.IsNullOrWhiteSpace(c))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var categories = new List<object>();
        foreach (var instanceName in instanceNames)
        {
            if (!cfg.QBitInstances.TryGetValue(instanceName, out var qbit))
                continue;
            if (qbit.Host == "CHANGE_ME" || string.IsNullOrEmpty(qbit.Host))
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
                    CategoryPathHelper.MatchesConfigured(t.Category, new[] { catName }, prefix: true)
                        == CategoryPathHelper.NormalizeCategory(catName)).ToList();
                var truncated = inCat.Count > MaxTorrentsPerCategory;
                var slice = inCat.Take(MaxTorrentsPerCategory).Select(SerializeTorrent).ToList();
                var seeding = inCat.Count(t =>
                    t.State.Contains("seeding", StringComparison.OrdinalIgnoreCase) ||
                    t.State.Equals("uploading", StringComparison.OrdinalIgnoreCase) ||
                    t.State.Equals("stalledUP", StringComparison.OrdinalIgnoreCase) ||
                    t.State.Equals("forcedUP", StringComparison.OrdinalIgnoreCase) ||
                    t.State.Equals("queuedUP", StringComparison.OrdinalIgnoreCase));

                categories.Add(new
                {
                    category = catName,
                    instance = instanceName,
                    managedBy = arrCategories.Contains(catName) ? "arr" : "qbit",
                    torrentCount = inCat.Count,
                    seedingCount = seeding,
                    truncated,
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
        amountLeft = t.AmountLeft
    };
}

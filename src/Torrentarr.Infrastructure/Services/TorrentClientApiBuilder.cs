using Torrentarr.Core.Configuration;
using Torrentarr.Core.Interfaces;
using Torrentarr.Core.Models;

namespace Torrentarr.Infrastructure.Services;

public static class TorrentClientApiBuilder
{
    public static async Task<Dictionary<string, object>> StatusAsync(TorrentarrConfig config, ITorrentClientRegistry registry, CancellationToken ct = default)
    {
        var instances = new Dictionary<string, object>();
        foreach (var (id, cfg) in config.GetAllTorrentClients())
        {
            var client = registry.GetClient(id);
            var alive = !cfg.Disabled && !string.IsNullOrWhiteSpace(cfg.Host) && cfg.Host != "CHANGE_ME" && client != null;
            string? version = null;
            if (alive)
            {
                try { version = await client!.GetVersionAsync(ct); }
                catch (Exception ex) when (ex is not OperationCanceledException) { alive = false; }
            }
            instances[id] = new { alive, host = cfg.Host, port = cfg.Port, version };
        }
        return instances;
    }

    public static async Task<List<object>> CategoriesAsync(TorrentarrConfig config, ITorrentClientRegistry registry, bool extendedPolicy = false, CancellationToken ct = default)
    {
        var categories = new List<object>();
        var arrCategories = config.ArrInstances.Values.Select(a => a.Category)
            .Where(c => !string.IsNullOrWhiteSpace(c)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var (id, cfg) in config.GetAllTorrentClients())
        {
            if (cfg.Disabled || string.IsNullOrWhiteSpace(cfg.Host) || cfg.Host == "CHANGE_ME") continue;
            var client = registry.GetClient(id);
            if (client == null) continue;
            List<TorrentInfo> torrents;
            try { torrents = await client.GetTorrentsAsync(cancellationToken: ct); }
            catch (Exception ex) when (ex is not OperationCanceledException) { continue; }
            foreach (var category in cfg.ManagedCategories.Union(arrCategories, StringComparer.OrdinalIgnoreCase))
            {
                var inCategory = CategorySeedingApiResolver.SelectAggregateTorrents(torrents, category, cfg.MatchSubcategories);
                var seeding = inCategory.Where(t => t.IsSeeding).ToList();
                var row = new Dictionary<string, object?>
                {
                    ["category"] = category,
                    ["instance"] = id,
                    ["managedBy"] = arrCategories.Contains(category) ? "arr" : "qbit",
                    ["torrentCount"] = inCategory.Count,
                    ["seedingCount"] = seeding.Count,
                    ["totalSize"] = inCategory.Sum(t => t.Size),
                    ["avgRatio"] = inCategory.Count > 0 ? inCategory.Average(t => t.Ratio) : 0.0,
                    ["seedingConfig"] = extendedPolicy
                        ? CategorySeedingApiResolver.ResolveAggregatePolicy(config, id, cfg, category, inCategory)
                        : (object?)CategorySeedingApiResolver.ResolveAggregate(config, id, cfg, category, inCategory)
                };
                if (extendedPolicy) row["avgSeedingTimeDays"] = seeding.Count > 0 ? seeding.Average(t => t.SeedingTime) / 86400.0 : 0.0;
                else row["avgSeedingTime"] = inCategory.Count > 0 ? inCategory.Average(t => (double)t.SeedingTime) : 0.0;
                categories.Add(row);
            }
        }
        return categories;
    }
}

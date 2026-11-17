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
                try
                {
                    using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    timeout.CancelAfter(TimeSpan.FromSeconds(5));
                    version = await client!.GetVersionAsync(timeout.Token);
                    alive = !string.IsNullOrWhiteSpace(version);
                }
                catch (Exception) when (!ct.IsCancellationRequested) { alive = false; }
            }
            instances[id] = new { alive, host = cfg.Host, port = cfg.Port, version, scanIntervalSeconds = cfg.TorrentScanIntervalSeconds };
        }
        return instances;
    }

    public static async Task<List<object>> CategoriesAsync(TorrentarrConfig config, ITorrentClientRegistry registry, bool extendedPolicy = false, CancellationToken ct = default, TorrentStatisticsSnapshots? snapshots = null)
    {
        var categories = new List<object>();
        Exception? failure = null;
        var inspected = 0;
        var arrCategories = config.ArrInstances.Values.Select(a => a.Category)
            .Where(c => !string.IsNullOrWhiteSpace(c)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var (id, cfg) in config.GetAllTorrentClients())
        {
            if (cfg.Disabled || string.IsNullOrWhiteSpace(cfg.Host) || cfg.Host == "CHANGE_ME") continue;
            var client = registry.GetClient(id);
            if (client == null && snapshots == null) continue;
            List<TorrentInfo> torrents;
            TorrentStatisticsSnapshots.Snapshot? snapshot = null;
            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeout.CancelAfter(TimeSpan.FromSeconds(5));
                if (snapshots != null)
                {
                    snapshot = client == null ? snapshots.Unavailable(id, config) : await snapshots.GetAsync(id, config, client, ct);
                    if (snapshot.ObservedAt == null) { failure = new IOException(snapshot.Error); continue; }
                    torrents = snapshot.Torrents.ToList();
                }
                else torrents = await client!.GetTorrentsAsync(cancellationToken: timeout.Token);
                inspected++;
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            { failure = new TimeoutException("Torrent-client category inspection timed out."); continue; }
            catch (Exception ex) when (ex is not OperationCanceledException)
            { failure = ex; continue; }
            var byOwner = torrents.ToLookup(t => CategoryOwnershipHelper.ResolveOwningCategory(config, t.Category, id) ?? "", StringComparer.OrdinalIgnoreCase);
            foreach (var category in cfg.ManagedCategories.Union(arrCategories, StringComparer.OrdinalIgnoreCase))
            {
                var inCategory = byOwner[CategoryPathHelper.NormalizeCategory(category)].ToList();
                var seeding = inCategory.Where(t => t.IsSeeding).ToList();
                var row = new Dictionary<string, object?>
                {
                    ["category"] = category,
                    ["instance"] = id,
                    ["managedBy"] = arrCategories.Contains(category) ? "arr" : "qbit",
                    ["torrentCount"] = inCategory.Count,
                    ["downloadingCount"] = inCategory.Count(t => TorrentPolicyHelper.IsActiveDownloadingStateForFreeSpace(t.State)),
                    ["observedAt"] = snapshot?.ObservedAt,
                    ["observationError"] = snapshot?.Error,
                    ["scanIntervalSeconds"] = cfg.TorrentScanIntervalSeconds,
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
        if (inspected == 0 && failure != null && snapshots == null) throw failure;
        return categories;
    }
}

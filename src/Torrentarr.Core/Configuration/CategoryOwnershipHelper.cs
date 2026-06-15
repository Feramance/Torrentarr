using Torrentarr.Core.Models;

namespace Torrentarr.Core.Configuration;

/// <summary>
/// qBitrr <c>ArrManager.resolve_owning_category</c> and managed-object registry parity.
/// </summary>
public static class CategoryOwnershipHelper
{
    /// <summary>All category keys with active torrent processing (Arr categories + qBit-only managed).</summary>
    public static HashSet<string> BuildManagedObjectKeys(TorrentarrConfig config)
    {
        var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var a in config.ArrInstances.Values)
        {
            var cat = CategoryPathHelper.NormalizeCategory(a.Category);
            if (!string.IsNullOrEmpty(cat))
                keys.Add(cat);
        }

        foreach (var cat in GetQBitOnlyManagedCategories(config))
            keys.Add(cat);

        var failed = CategoryPathHelper.NormalizeCategory(config.Settings.FailedCategory);
        var recheck = CategoryPathHelper.NormalizeCategory(config.Settings.RecheckCategory);
        if (!string.IsNullOrEmpty(failed)) keys.Add(failed);
        if (!string.IsNullOrEmpty(recheck)) keys.Add(recheck);

        return keys;
    }

    /// <summary>ManagedCategories not already owned by an Arr instance category.</summary>
    public static List<string> GetQBitOnlyManagedCategories(TorrentarrConfig config)
    {
        var arrCategories = config.ArrInstances.Values
            .Select(a => CategoryPathHelper.NormalizeCategory(a.Category))
            .Where(c => !string.IsNullOrEmpty(c))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var result = new List<string>();
        foreach (var (_, qbit) in config.QBitInstances)
        {
            foreach (var raw in qbit.ManagedCategories)
            {
                var norm = CategoryPathHelper.NormalizeCategory(raw);
                if (string.IsNullOrEmpty(norm))
                    continue;
                if (!arrCategories.Contains(norm))
                    result.Add(norm);
            }
        }

        return result.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>Find Arr section name for a category owner key.</summary>
    public static string? FindArrSectionForCategory(TorrentarrConfig config, string ownerCategory)
    {
        foreach (var (name, arr) in config.ArrInstances)
        {
            if (CategoryPathHelper.CategoryEquals(arr.Category, ownerCategory))
                return name;
        }
        return null;
    }

  public static bool QBitMatchSubcategories(TorrentarrConfig config, string qbitSection)
    {
        if (config.QBitInstances.TryGetValue(qbitSection, out var qbit))
            return qbit.MatchSubcategories;
        return false;
    }

    /// <summary>
    /// Per-Arr override when set; otherwise inherits from the qBit instance default.
    /// </summary>
    public static bool ArrMatchSubcategoriesEffective(
        TorrentarrConfig config,
        string arrSectionName,
        string qbitSection)
    {
        if (config.ArrInstances.TryGetValue(arrSectionName, out var arr)
            && arr.MatchSubcategories.HasValue)
            return arr.MatchSubcategories.Value;

        return QBitMatchSubcategories(config, qbitSection);
    }

    /// <summary>
    /// Whether prefix/subcategory matching is enabled for <paramref name="ownerCategory"/>.
    /// </summary>
    public static bool PrefixMatchAllowedForOwner(
        TorrentarrConfig config,
        string ownerCategory,
        string? qbitSection = null)
    {
        var norm = CategoryPathHelper.NormalizeCategory(ownerCategory);
        if (string.IsNullOrEmpty(norm))
            return false;

        foreach (var (arrName, arr) in config.ArrInstances)
        {
            if (!CategoryPathHelper.CategoryEquals(arr.Category, norm))
                continue;
            var section = qbitSection ?? "qBit";
            return ArrMatchSubcategoriesEffective(config, arrName, section);
        }

        if (config.ArrInstances.Values.Any(a =>
                CategoryPathHelper.CategoryEquals(a.Category, norm)))
            return false;

        if (qbitSection != null)
            return QBitMatchSubcategories(config, qbitSection);

        return config.QBitInstances.Values.Any(q => q.MatchSubcategories);
    }

    /// <summary>
    /// Return the managed-object key that owns <paramref name="torrentCategory"/> (or null).
    /// </summary>
    public static string? ResolveOwningCategory(
        TorrentarrConfig config,
        string? torrentCategory,
        string? qbitSection = null)
    {
        if (string.IsNullOrWhiteSpace(torrentCategory))
            return null;

        var norm = CategoryPathHelper.NormalizeCategory(torrentCategory);
        if (string.IsNullOrEmpty(norm))
            return null;

        var managed = BuildManagedObjectKeys(config);
        if (managed.Contains(norm))
            return norm;

        var eligible = managed
            .Where(k => PrefixMatchAllowedForOwner(config, k, qbitSection))
            .ToList();

        if (eligible.Count == 0)
            return null;

        var match = CategoryPathHelper.MatchesConfigured(norm, eligible, prefix: true);
        return match != null && managed.Contains(match) ? match : null;
    }

    /// <summary>
    /// Gather torrents for an owner category across all qBit clients (MatchSubcategories-aware).
    /// </summary>
    public static async Task<List<TorrentInfo>> GatherTorrentsForOwnerAsync(
        TorrentarrConfig config,
        string ownerCategory,
        IReadOnlyDictionary<string, Func<CancellationToken, Task<List<TorrentInfo>>>> fetchAllByInstance,
        IReadOnlyDictionary<string, Func<string, CancellationToken, Task<List<TorrentInfo>>>> fetchByCategory,
        CancellationToken ct = default)
    {
        var target = CategoryPathHelper.NormalizeCategory(ownerCategory);
        var results = new List<TorrentInfo>();

        foreach (var (instanceName, fetchAll) in fetchAllByInstance)
        {
            var usePrefix = PrefixMatchAllowedForOwner(config, target, instanceName);
            List<TorrentInfo> torrents;
            if (usePrefix)
            {
                torrents = await fetchAll(ct);
                torrents = torrents
                    .Where(t => ResolveOwningCategory(config, t.Category, instanceName) == target)
                    .ToList();
            }
            else
            {
                torrents = await fetchByCategory[instanceName](target, ct);
            }

            foreach (var t in torrents)
                t.QBitInstanceName = instanceName;
            results.AddRange(torrents);
        }

        return results;
    }
}

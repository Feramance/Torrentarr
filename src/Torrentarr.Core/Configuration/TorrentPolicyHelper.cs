using System;
using System.Collections.Generic;
using System.Linq;
using Torrentarr.Core.Models;

namespace Torrentarr.Core.Configuration;

/// <summary>
/// qBitrr <c>TorrentPolicyManager</c> gating and queue-order helpers (arss.py).
/// </summary>
public static class TorrentPolicyHelper
{
    public static bool HasAnyQBitSection(TorrentarrConfig config) =>
        config.QBitInstances.Keys.Any(k =>
            string.Equals(k, "qBit", StringComparison.OrdinalIgnoreCase)
            || k.StartsWith("qBit-", StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Mirrors qBitrr <c>get_effective_qbit_disabled()</c> (no qBit sections, or primary <c>[qBit].Disabled</c>).
    /// </summary>
    public static bool IsEffectiveQBitDisabled(TorrentarrConfig config)
    {
        if (!HasAnyQBitSection(config))
            return true;
        return config.QBitInstances.TryGetValue("qBit", out var primary) && primary.Disabled;
    }

    /// <summary>
    /// <c>Arr.global_sort_torrents_enabled()</c> — any merged tracker has <c>SortTorrents</c>.
    /// </summary>
    public static bool GlobalSortTorrentsEnabled(TorrentarrConfig config)
    {
        foreach (var q in config.QBitInstances.Values)
        {
            if (q.Trackers.Any(t => t.SortTorrents))
                return true;
        }

        foreach (var a in config.ArrInstances.Values)
        {
            if (a.Torrent.Trackers.Any(t => t.SortTorrents))
                return true;
        }

        return false;
    }

    public static bool EnableTrackerSort(TorrentarrConfig config) =>
        GlobalSortTorrentsEnabled(config) && !IsEffectiveQBitDisabled(config);

    /// <param name="freeSpaceGuardActive"><c>Settings.FreeSpace</c> resolved to a non-disabled threshold and <c>_minFreeSpaceBytes &gt; 0</c>.</param>
    public static bool EnableFreeSpace(TorrentarrConfig config, bool freeSpaceGuardActive) =>
        config.Settings.AutoPauseResume
        && freeSpaceGuardActive
        && !IsEffectiveQBitDisabled(config);

    /// <summary>
    /// When true, Host runs pre-sort tracker sync; workers should skip duplicate <c>ApplyTrackerActionsForTorrentAsync</c>.
    /// </summary>
    public static bool PolicyManagerOwnsTrackerSync(TorrentarrConfig config) => EnableTrackerSort(config);

    /// <summary>
    /// <c>Arr.merge_global_tracker_tag_to_priority_max()</c> — tag label → max Priority among merged tracker rows.
    /// </summary>
    public static Dictionary<string, int> MergeGlobalTrackerTagToPriorityMax(TorrentarrConfig config)
    {
        var dict = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        void Accumulate(IEnumerable<TrackerConfig> rows)
        {
            foreach (var row in rows)
            {
                var pri = row.Priority;
                foreach (var tag in row.AddTags)
                {
                    if (string.IsNullOrWhiteSpace(tag)) continue;
                    var t = tag.Trim();
                    dict[t] = Math.Max(dict.GetValueOrDefault(t, -100), pri);
                }
            }
        }

        foreach (var q in config.QBitInstances.Values)
            Accumulate(q.Trackers);
        foreach (var a in config.ArrInstances.Values)
            Accumulate(a.Torrent.Trackers);

        return dict;
    }

    /// <summary>
    /// qBittorrent Web API <c>priority</c> (queue position; <c>-1</c> when queuing disabled).
    /// </summary>
    public static int NormalizeTorrentQueuePriorityValue(int raw) => raw;

    /// <summary>
    /// Sort key matching qBitrr <c>Arr._torrent_queue_position_sort_key</c>: active queue first, then position.
    /// </summary>
    public static (bool InactiveQueueGroup, int Nq) TorrentQueuePositionSortKey(TorrentInfo torrent)
    {
        var nq = NormalizeTorrentQueuePriorityValue(torrent.Priority);
        return (!(nq > 0), nq);
    }

    /// <summary>
    /// Seeding / upload side of qBittorrent queue for <c>SortTorrents</c> (arss.py <c>is_queue_seeding_for_sort</c>).
    /// Includes <c>stoppedUP</c> (qBittorrent v5+; replaces <c>pausedUP</c> in the API).
    /// </summary>
    public static bool IsQueueSeedingForSort(string? state)
    {
        if (string.IsNullOrWhiteSpace(state)) return false;
        var lower = state.ToLowerInvariant();
        return lower.Contains("upload", StringComparison.Ordinal)
               || lower.Contains("stalledup", StringComparison.Ordinal)
               || lower.Contains("queuedup", StringComparison.Ordinal)
               || lower.Contains("pausedup", StringComparison.Ordinal)
               || lower.Contains("stoppedup", StringComparison.Ordinal)
               || lower.Contains("forcedup", StringComparison.Ordinal)
               || lower.Contains("checkingup", StringComparison.Ordinal);
    }

    /// <summary>
    /// Call after the same <see cref="TorrentarrConfig"/> instance is mutated in place (e.g. API config apply)
    /// so the next <see cref="GetAllMonitoredPolicyCategories"/> rebuilds from current Arr / qBit category lists.
    /// </summary>
    public static void InvalidateMonitoredPolicyCategoriesCache(TorrentarrConfig config) =>
        config.MonitoredPolicyCategoriesCache = null;

    /// <summary>
    /// Categories monitored by the global policy worker (Arr categories + qBit <c>ManagedCategories</c>).
    /// Result is cached on <paramref name="config"/> for the lifetime of that instance (one allocation per reload
    /// until <see cref="InvalidateMonitoredPolicyCategoriesCache"/> is called or a new <paramref name="config"/> is used).
    /// </summary>
    public static HashSet<string> GetAllMonitoredPolicyCategories(TorrentarrConfig config)
    {
        if (config.MonitoredPolicyCategoriesCache != null)
            return config.MonitoredPolicyCategoriesCache;

        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var a in config.ArrInstances.Values)
        {
            if (!string.IsNullOrEmpty(a.Category))
                set.Add(a.Category);
        }

        foreach (var q in config.QBitInstances.Values)
        {
            if (q.ManagedCategories == null) continue;
            foreach (var c in q.ManagedCategories)
            {
                if (!string.IsNullOrEmpty(c))
                    set.Add(c);
            }
        }

        config.MonitoredPolicyCategoriesCache = set;
        return set;
    }

    public static bool IsMonitoredPolicyCategory(TorrentarrConfig config, string? category)
    {
        if (string.IsNullOrEmpty(category)) return false;
        return GetAllMonitoredPolicyCategories(config).Contains(category);
    }
}

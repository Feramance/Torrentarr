using Torrentarr.Core.Configuration;
using Torrentarr.Core.Models;
using Torrentarr.Core.Services;
using Torrentarr.Infrastructure.ApiClients.QBittorrent;
using Microsoft.Extensions.Logging;

namespace Torrentarr.Infrastructure.Services;

/// <summary>
/// Global tracker-priority queue ordering (single Host worker; no cross-process lock).
/// </summary>
public class TrackerQueueSortService : ITrackerQueueSortService
{
    private readonly ILogger<TrackerQueueSortService> _logger;
    private readonly TorrentarrConfig _config;
    private readonly QBittorrentConnectionManager _qbitManager;
    private readonly ISeedingService _seedingService;

    public TrackerQueueSortService(
        ILogger<TrackerQueueSortService> logger,
        TorrentarrConfig config,
        QBittorrentConnectionManager qbitManager,
        ISeedingService seedingService)
    {
        _logger = logger;
        _config = config;
        _qbitManager = qbitManager;
        _seedingService = seedingService;
    }

    /// <inheritdoc />
    public async Task SortTorrentQueuesByTrackerPriorityAsync(CancellationToken ct = default)
    {
        var hasSortTorrentsEnabled = _config.QBitInstances.Values.Any(q =>
            q.Trackers.Any(t => t.SortTorrents))
            || _config.ArrInstances.Values.Any(a =>
                a.Torrent.Trackers.Any(t => t.SortTorrents));

        if (!hasSortTorrentsEnabled)
            return;

        var managedCategories = BuildManagedCategoriesSet();
        var allTorrents = new List<TorrentInfo>();
        foreach (var (instanceName, client) in _qbitManager.GetAllClients())
        {
            foreach (var category in managedCategories)
            {
                var torrents = await client.GetTorrentsAsync(category, ct);
                foreach (var t in torrents)
                    t.QBitInstanceName = instanceName;
                allTorrents.AddRange(torrents);
            }
        }

        if (allTorrents.Count == 0)
            return;

        var sortableByInstance = new Dictionary<string, List<(TorrentInfo Torrent, int Priority)>>(StringComparer.OrdinalIgnoreCase);

        foreach (var torrent in allTorrents)
        {
            try
            {
                var trackerConfig = await _seedingService.GetTrackerConfigAsync(torrent, ct);
                if (trackerConfig?.SortTorrents != true)
                    continue;

                if (!sortableByInstance.TryGetValue(torrent.QBitInstanceName, out var list))
                {
                    list = new List<(TorrentInfo Torrent, int Priority)>();
                    sortableByInstance[torrent.QBitInstanceName] = list;
                }

                list.Add((torrent, trackerConfig.Priority));
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Skipping sort evaluation for torrent {Hash}", torrent.Hash);
            }
        }

        foreach (var (instanceName, sortable) in sortableByInstance)
        {
            if (sortable.Count == 0)
                continue;

            var client = _qbitManager.GetClient(instanceName);
            if (client == null)
                continue;

            try
            {
                var ordered = TrackerQueueSortOrdering.BuildOrderedHashesForTopPriorityCalls(sortable);

                foreach (var hash in ordered)
                    await client.TopPriorityAsync(new List<string> { hash }, ct);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to sort torrent queue for qBit instance {Instance}", instanceName);
            }
        }
    }

    private HashSet<string> BuildManagedCategoriesSet()
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var arrInstance in _config.ArrInstances.Where(x => !string.IsNullOrEmpty(x.Value.Category)))
            set.Add(arrInstance.Value.Category!);
        foreach (var qbit in _config.QBitInstances.Values)
        {
            if (qbit.ManagedCategories != null)
            {
                foreach (var cat in qbit.ManagedCategories)
                    set.Add(cat);
            }
        }
        return set;
    }
}

/// <summary>
/// Pure ordering for qBit <c>topPrio</c> calls: last hash in the returned list ends up at the front of the queue.
/// </summary>
internal static class TrackerQueueSortOrdering
{
    public static List<string> BuildOrderedHashesForTopPriorityCalls(
        IEnumerable<(TorrentInfo Torrent, int Priority)> sortable)
    {
        return sortable
            .OrderByDescending(t => t.Priority)
            .ThenBy(t => t.Torrent.AddedOn)
            .Select(t => t.Torrent.Hash)
            .Reverse()
            .ToList();
    }
}

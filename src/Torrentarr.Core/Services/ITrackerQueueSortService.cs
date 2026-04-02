namespace Torrentarr.Core.Services;

/// <summary>
/// Reorders qBittorrent queue by tracker priority when SortTorrents is enabled (global per qBit instance).
/// </summary>
public interface ITrackerQueueSortService
{
    /// <summary>
    /// Applies tracker-priority ordering for all torrents across all qBit instances.
    /// </summary>
    Task SortTorrentQueuesByTrackerPriorityAsync(CancellationToken cancellationToken = default);
}

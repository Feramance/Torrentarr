using Commandarr.Core.Services;
using Commandarr.Infrastructure.ApiClients.QBittorrent;
using Commandarr.Infrastructure.Database;
using Commandarr.Infrastructure.Database.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Commandarr.Infrastructure.Services;

/// <summary>
/// Implements torrent processing logic
/// </summary>
public class TorrentProcessor : ITorrentProcessor
{
    private readonly ILogger<TorrentProcessor> _logger;
    private readonly QBittorrentConnectionManager _qbitManager;
    private readonly CommandarrDbContext _dbContext;

    public TorrentProcessor(
        ILogger<TorrentProcessor> logger,
        QBittorrentConnectionManager qbitManager,
        CommandarrDbContext dbContext)
    {
        _logger = logger;
        _qbitManager = qbitManager;
        _dbContext = dbContext;
    }

    public async Task ProcessTorrentsAsync(string category, CancellationToken cancellationToken = default)
    {
        var client = _qbitManager.GetDefaultClient();
        if (client == null)
        {
            _logger.LogWarning("No qBittorrent client available");
            return;
        }

        try
        {
            // Get all torrents for this category
            var torrents = await client.GetTorrentsAsync(category, cancellationToken);
            _logger.LogDebug("Found {Count} torrents in category {Category}", torrents.Count, category);

            var stats = new TorrentProcessingStats
            {
                TotalTorrents = torrents.Count
            };

            foreach (var torrent in torrents)
            {
                try
                {
                    await ProcessSingleTorrentAsync(torrent, category, stats, cancellationToken);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error processing torrent {Hash} ({Name})",
                        torrent.Hash, torrent.Name);
                }
            }

            _logger.LogInformation(
                "Processed {Total} torrents in {Category}: {Downloading} downloading, {Completed} completed, {Seeding} seeding, {Failed} failed",
                stats.TotalTorrents, category, stats.Downloading, stats.Completed, stats.Seeding, stats.Failed);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error processing torrents for category {Category}", category);
        }
    }

    public async Task ProcessTorrentAsync(string hash, CancellationToken cancellationToken = default)
    {
        var client = _qbitManager.GetDefaultClient();
        if (client == null)
        {
            _logger.LogWarning("No qBittorrent client available");
            return;
        }

        var torrents = await client.GetTorrentsAsync(ct: cancellationToken);
        var torrent = torrents.FirstOrDefault(t => t.Hash == hash);

        if (torrent != null)
        {
            await ProcessSingleTorrentAsync(torrent, torrent.Category, new TorrentProcessingStats(), cancellationToken);
        }
    }

    public async Task<bool> IsReadyForImportAsync(string hash, CancellationToken cancellationToken = default)
    {
        var client = _qbitManager.GetDefaultClient();
        if (client == null) return false;

        var torrents = await client.GetTorrentsAsync(ct: cancellationToken);
        var torrent = torrents.FirstOrDefault(t => t.Hash == hash);

        if (torrent == null) return false;

        // Torrent is ready for import if:
        // 1. Progress is 100%
        // 2. State is completed/seeding
        // 3. Not already imported
        var isComplete = torrent.Progress >= 1.0;
        var isSeeding = torrent.State.Contains("uploading", StringComparison.OrdinalIgnoreCase) ||
                       torrent.State.Contains("seeding", StringComparison.OrdinalIgnoreCase);

        var libraryEntry = await _dbContext.TorrentLibrary
            .FirstOrDefaultAsync(t => t.Hash == hash, cancellationToken);

        var notImported = libraryEntry == null || !libraryEntry.Imported;

        return isComplete && isSeeding && notImported;
    }

    public async Task ImportTorrentAsync(string hash, CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("Importing torrent {Hash}", hash);

        // TODO: Implement actual import to Arr
        // 1. Trigger manual import in Arr
        // 2. Wait for import to complete
        // 3. Mark as imported in database

        var libraryEntry = await _dbContext.TorrentLibrary
            .FirstOrDefaultAsync(t => t.Hash == hash, cancellationToken);

        if (libraryEntry != null)
        {
            libraryEntry.Imported = true;
            await _dbContext.SaveChangesAsync(cancellationToken);
            _logger.LogInformation("Marked torrent {Hash} as imported", hash);
        }
    }

    private async Task ProcessSingleTorrentAsync(
        TorrentInfo torrent,
        string category,
        TorrentProcessingStats stats,
        CancellationToken cancellationToken)
    {
        var state = ParseTorrentState(torrent.State);

        // Update statistics
        switch (state)
        {
            case TorrentState.Downloading:
            case TorrentState.StalledDownloading:
            case TorrentState.ForcedDownloading:
                stats.Downloading++;
                break;
            case TorrentState.Uploading:
            case TorrentState.ForcedUploading:
                stats.Seeding++;
                break;
            case TorrentState.Error:
            case TorrentState.MissingFiles:
                stats.Failed++;
                break;
            case TorrentState.PausedUploading:
            case TorrentState.PausedDownloading:
                stats.Paused++;
                break;
        }

        // Ensure torrent exists in database
        await EnsureTorrentInDatabaseAsync(torrent, category, cancellationToken);

        // Process based on state
        if (torrent.Progress >= 1.0 && !torrent.State.Contains("paused", StringComparison.OrdinalIgnoreCase))
        {
            // Torrent is complete
            stats.Completed++;

            // Check if ready for import
            if (await IsReadyForImportAsync(torrent.Hash, cancellationToken))
            {
                _logger.LogDebug("Torrent {Name} is ready for import", torrent.Name);
                // Import will be handled by the Arr-specific worker
            }
        }

        _logger.LogTrace("Processed torrent {Name} in state {State} (Progress: {Progress:P0})",
            torrent.Name, torrent.State, torrent.Progress);
    }

    private async Task EnsureTorrentInDatabaseAsync(
        TorrentInfo torrent,
        string category,
        CancellationToken cancellationToken)
    {
        var exists = await _dbContext.TorrentLibrary
            .AnyAsync(t => t.Hash == torrent.Hash && t.QbitInstance == "default", cancellationToken);

        if (!exists)
        {
            var entry = new TorrentLibrary
            {
                Hash = torrent.Hash,
                Category = category,
                QbitInstance = "default",
                AllowedSeeding = false,
                Imported = false,
                AllowedStalled = false,
                FreeSpacePaused = false
            };

            _dbContext.TorrentLibrary.Add(entry);
            await _dbContext.SaveChangesAsync(cancellationToken);

            _logger.LogDebug("Added torrent {Hash} to database", torrent.Hash);
        }
    }

    private TorrentState ParseTorrentState(string stateString)
    {
        return stateString.ToLower() switch
        {
            var s when s.Contains("downloading") => TorrentState.Downloading,
            var s when s.Contains("uploading") => TorrentState.Uploading,
            var s when s.Contains("stalleddownload") => TorrentState.StalledDownloading,
            var s when s.Contains("stalledupload") => TorrentState.StalledUploading,
            var s when s.Contains("pauseddownload") => TorrentState.PausedDownloading,
            var s when s.Contains("pausedupload") => TorrentState.PausedUploading,
            var s when s.Contains("queueddownload") => TorrentState.QueuedDownloading,
            var s when s.Contains("queuedupload") => TorrentState.QueuedUploading,
            var s when s.Contains("error") => TorrentState.Error,
            var s when s.Contains("missingfiles") => TorrentState.MissingFiles,
            var s when s.Contains("checking") => TorrentState.CheckingUploading,
            _ => TorrentState.Unknown
        };
    }
}

using Torrentarr.Core.Configuration;
using Torrentarr.Core.Models;
using Torrentarr.Core.Services;
using Torrentarr.Infrastructure.ApiClients.QBittorrent;
using Torrentarr.Infrastructure.Database;
using Torrentarr.Infrastructure.Database.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Torrentarr.Infrastructure.Services;

/// <summary>
/// Implements torrent processing logic with tag-based filtering.
/// Skips torrents tagged with qBitrr-ignored and integrates with seeding/space services.
/// Handles special categories (failed, recheck) with bypass logic.
/// </summary>
public class TorrentProcessor : ITorrentProcessor
{
    private const string IgnoredTag = "qBitrr-ignored";
    private const string AllowedSeedingTag = "qBitrr-allowed_seeding";
    private const string FreeSpacePausedTag = "qBitrr-free_space_paused";
    private const string HnrActiveTag = "qBitrr-hnr_active";

    private readonly ILogger<TorrentProcessor> _logger;
    private readonly QBittorrentConnectionManager _qbitManager;
    private readonly TorrentarrDbContext _dbContext;
    private readonly TorrentarrConfig _config;
    private readonly IArrImportService? _importService;
    private readonly ISeedingService? _seedingService;
    private readonly IFreeSpaceService? _freeSpaceService;

    private readonly HashSet<string> _specialCategories;

    public TorrentProcessor(
        ILogger<TorrentProcessor> logger,
        QBittorrentConnectionManager qbitManager,
        TorrentarrDbContext dbContext,
        TorrentarrConfig config,
        IArrImportService? importService = null,
        ISeedingService? seedingService = null,
        IFreeSpaceService? freeSpaceService = null)
    {
        _logger = logger;
        _qbitManager = qbitManager;
        _dbContext = dbContext;
        _config = config;
        _importService = importService;
        _seedingService = seedingService;
        _freeSpaceService = freeSpaceService;

        _specialCategories = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            config.Settings.FailedCategory,
            config.Settings.RecheckCategory
        };
    }

    public async Task ProcessTorrentsAsync(string category, CancellationToken cancellationToken = default)
    {
        var client = _qbitManager.GetAllClients().Values.FirstOrDefault();
        if (client == null)
        {
            _logger.LogWarning("No qBittorrent client available");
            return;
        }

        // Skip special categories - they are handled globally by the Host orchestrator
        if (_specialCategories.Contains(category))
        {
            _logger.LogDebug("Skipping special category {Category} - handled by Host orchestrator", category);
            return;
        }

        try
        {
            // Ensure tags exist
            await EnsureTagsExistAsync(client, cancellationToken);

            // NOTE: Free space management is handled GLOBALLY by the Host orchestrator
            // This matches qBitrr's design where FreeSpaceManager runs ONCE per qBittorrent instance

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

            // Update seeding tags for completed torrents
            if (_seedingService != null)
            {
                await _seedingService.UpdateSeedingTagsAsync(category, cancellationToken);
            }

            _logger.LogInformation(
                "Processed {Total} torrents in {Category}: {Downloading} downloading, {Completed} completed, {Seeding} seeding, {Failed} failed, {Ignored} ignored",
                stats.TotalTorrents, category, stats.Downloading, stats.Completed, stats.Seeding, stats.Failed, stats.Ignored);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error processing torrents for category {Category}", category);
        }
    }

    /// <summary>
    /// Process special categories (failed, recheck).
    /// NOTE: This is now handled globally by the Host orchestrator to match qBitrr's design.
    /// </summary>
    [Obsolete("Special categories are now handled by the Host orchestrator. This method is kept for backwards compatibility.")]
    public async Task ProcessSpecialCategoriesAsync(CancellationToken cancellationToken = default)
    {
        // This is now handled by the Host orchestrator's ProcessSpecialCategoriesAsync
        // Keeping for backwards compatibility
        _logger.LogDebug("ProcessSpecialCategoriesAsync called - this is now handled by Host orchestrator");
        await Task.CompletedTask;
    }

    public async Task ProcessTorrentAsync(string hash, CancellationToken cancellationToken = default)
    {
        var client = _qbitManager.GetAllClients().Values.FirstOrDefault();
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
        var client = _qbitManager.GetAllClients().Values.FirstOrDefault();
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

        // Get torrent info
        var client = _qbitManager.GetAllClients().Values.FirstOrDefault();
        if (client == null)
        {
            _logger.LogWarning("No qBittorrent client available for import");
            return;
        }

        var torrents = await client.GetTorrentsAsync(ct: cancellationToken);
        var torrent = torrents.FirstOrDefault(t => t.Hash == hash);

        if (torrent == null)
        {
            _logger.LogWarning("Torrent {Hash} not found in qBittorrent", hash);
            return;
        }

        var libraryEntry = await _dbContext.TorrentLibrary
            .FirstOrDefaultAsync(t => t.Hash == hash, cancellationToken);

        if (libraryEntry == null)
        {
            _logger.LogWarning("Torrent {Hash} not found in database", hash);
            return;
        }

        // Trigger import using the ArrImportService
        if (_importService != null)
        {
            var result = await _importService.TriggerImportAsync(
                hash, torrent.ContentPath, libraryEntry.Category, cancellationToken);

            if (result.Success)
            {
                libraryEntry.Imported = true;
                await _dbContext.SaveChangesAsync(cancellationToken);
                _logger.LogInformation("Successfully triggered import for torrent {Hash}: {Message}",
                    hash, result.Message);
            }
            else
            {
                _logger.LogWarning("Failed to trigger import for torrent {Hash}: {Message}",
                    hash, result.Message);
            }
        }
        else
        {
            _logger.LogWarning("ArrImportService not available, marking as imported without triggering");
            libraryEntry.Imported = true;
            await _dbContext.SaveChangesAsync(cancellationToken);
        }
    }

    private async Task ProcessSingleTorrentAsync(
        TorrentInfo torrent,
        string category,
        TorrentProcessingStats stats,
        CancellationToken cancellationToken)
    {
        // Check if torrent is in a special category (failed, recheck)
        if (_specialCategories.Contains(torrent.Category))
        {
            await ProcessSpecialCategoryTorrentAsync(torrent, stats, cancellationToken);
            return;
        }

        // Check if torrent is ignored via tag
        if (HasTag(torrent, IgnoredTag))
        {
            _logger.LogTrace("Skipping ignored torrent {Name}", torrent.Name);
            stats.Ignored++;
            return;
        }

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

        // Auto-resume stopped torrents that are not ignored or free-space-paused
        if (torrent.IsStopped && !HasTag(torrent, FreeSpacePausedTag) && !HasTag(torrent, IgnoredTag))
        {
            var client = _qbitManager.GetAllClients().Values.FirstOrDefault();
            if (client != null)
            {
                _logger.LogDebug("Resuming stopped torrent: {Name} ({Hash}) - State[{State}]",
                    torrent.Name, torrent.Hash, torrent.State);
                await client.ResumeTorrentsAsync(new List<string> { torrent.Hash }, cancellationToken);
            }
        }

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

    private async Task ProcessSpecialCategoryTorrentAsync(
        TorrentInfo torrent,
        TorrentProcessingStats stats,
        CancellationToken cancellationToken)
    {
        var client = _qbitManager.GetAllClients().Values.FirstOrDefault();
        if (client == null) return;

        var failedCategory = _config.Settings.FailedCategory;
        var recheckCategory = _config.Settings.RecheckCategory;

        if (torrent.Category.Equals(failedCategory, StringComparison.OrdinalIgnoreCase))
        {
            // Check HnR protection before deleting
            if (_seedingService != null)
            {
                var hnrAllowsDelete = await _seedingService.HnrAllowsDeleteAsync(torrent, "failed category deletion", cancellationToken);
                if (!hnrAllowsDelete)
                {
                    _logger.LogInformation("HnR protection: keeping failed torrent {Name}", torrent.Name);
                    stats.Ignored++;
                    return;
                }
            }

            _logger.LogWarning(
                "Deleting manually failed torrent: {Name} | Progress: {Progress:P1}% | State: {State} | Hash: {Hash}",
                torrent.Name, torrent.Progress * 100, torrent.State, torrent.Hash);

            await client.DeleteTorrentsAsync(new List<string> { torrent.Hash }, deleteFiles: true, cancellationToken);
            stats.Failed++;
        }
        else if (torrent.Category.Equals(recheckCategory, StringComparison.OrdinalIgnoreCase))
        {
            _logger.LogInformation(
                "Re-checking manually set torrent: {Name} | Progress: {Progress:P1}% | State: {State} | Hash: {Hash}",
                torrent.Name, torrent.Progress * 100, torrent.State, torrent.Hash);

            await client.RecheckTorrentsAsync(new List<string> { torrent.Hash }, cancellationToken);
        }
    }

    private bool HasTag(TorrentInfo torrent, string tag)
    {
        if (string.IsNullOrEmpty(torrent.Tags))
            return false;

        var tags = torrent.Tags.Split(',', StringSplitOptions.RemoveEmptyEntries)
            .Select(t => t.Trim())
            .ToList();

        return tags.Contains(tag, StringComparer.OrdinalIgnoreCase);
    }

    private async Task EnsureTagsExistAsync(QBittorrentClient client, CancellationToken cancellationToken)
    {
        try
        {
            var existingTags = await client.GetTagsAsync(cancellationToken);
            var tagsToCreate = new List<string>();

            if (!existingTags.Contains(IgnoredTag, StringComparer.OrdinalIgnoreCase))
                tagsToCreate.Add(IgnoredTag);

            if (!existingTags.Contains(AllowedSeedingTag, StringComparer.OrdinalIgnoreCase))
                tagsToCreate.Add(AllowedSeedingTag);

            if (!existingTags.Contains(FreeSpacePausedTag, StringComparer.OrdinalIgnoreCase))
                tagsToCreate.Add(FreeSpacePausedTag);

            if (tagsToCreate.Count > 0)
            {
                await client.CreateTagsAsync(tagsToCreate, cancellationToken);
                _logger.LogDebug("Created tags: {Tags}", string.Join(", ", tagsToCreate));
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to ensure tags exist");
        }
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

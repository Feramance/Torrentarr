using System.Text.RegularExpressions;
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
    private readonly ITorrentCacheService _cache;
    private readonly IArrImportService? _importService;
    private readonly ISeedingService? _seedingService;

    private readonly HashSet<string> _specialCategories;

    public TorrentProcessor(
        ILogger<TorrentProcessor> logger,
        QBittorrentConnectionManager qbitManager,
        TorrentarrDbContext dbContext,
        TorrentarrConfig config,
        ITorrentCacheService cache,
        IArrImportService? importService = null,
        ISeedingService? seedingService = null)
    {
        _logger = logger;
        _qbitManager = qbitManager;
        _dbContext = dbContext;
        _config = config;
        _cache = cache;
        _importService = importService;
        _seedingService = seedingService;

        _specialCategories = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            config.Settings.FailedCategory,
            config.Settings.RecheckCategory
        };
    }

    public async Task ProcessTorrentsAsync(string category, CancellationToken cancellationToken = default)
    {
        _logger.LogTrace("Starting torrent processing for category {Category}", category);
        
        var client = _qbitManager.GetAllClients().Values.FirstOrDefault();
        if (client == null)
        {
            _logger.LogWarning("No qBittorrent client available");
            _logger.LogTrace("Abort processing - no qBittorrent client");
            return;
        }

        // Skip special categories - they are handled globally by the Host orchestrator
        if (_specialCategories.Contains(category))
        {
            _logger.LogTrace("Skipping special category {Category} - handled by Host orchestrator", category);
            _logger.LogTrace("Abort processing - special category {Category} excluded", category);
            return;
        }

        try
        {
            // Ensure tags exist (skip in Tagless mode — §1.6)
            if (!_config.Settings.Tagless)
            {
                _logger.LogTrace("Ensuring required tags exist in qBittorrent");
                await EnsureTagsExistAsync(client, cancellationToken);
            }
            _logger.LogTrace("Tags verified/created successfully");

            // NOTE: Free space management is handled GLOBALLY by the Host orchestrator
            // This matches qBitrr's design where FreeSpaceManager runs ONCE per qBittorrent instance

            // Get all torrents for this category
            _logger.LogTrace("Fetching torrents from qBittorrent for category {Category}", category);
            var torrents = await client.GetTorrentsAsync(category, cancellationToken);
            _logger.LogDebug("Found {Count} torrents in category {Category}", torrents.Count, category);
            _logger.LogTrace("Torrent fetch complete - {Count} torrents retrieved", torrents.Count);

            var stats = new TorrentProcessingStats
            {
                TotalTorrents = torrents.Count
            };

            _logger.LogTrace("Starting iteration through {Count} torrents", torrents.Count);
            foreach (var torrent in torrents)
            {
                try
                {
                    _logger.LogTrace("Processing torrent: {Name} ({Hash})", torrent.Name, torrent.Hash);
                    await ProcessSingleTorrentAsync(torrent, category, stats, cancellationToken);
                    _logger.LogTrace("Completed processing torrent: {Name}", torrent.Name);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error processing torrent {Hash} ({Name})",
                        torrent.Hash, torrent.Name);
                }
            }
            _logger.LogTrace("Finished iterating through all torrents");

            // Update seeding tags for completed torrents
            if (_seedingService != null)
            {
                _logger.LogTrace("Updating seeding tags for category {Category}", category);
                await _seedingService.UpdateSeedingTagsAsync(category, cancellationToken);
                _logger.LogTrace("Seeding tags updated");
            }

            _logger.LogInformation(
                "Processed {Total} torrents in {Category}: {Downloading} downloading, {Completed} completed, {Seeding} seeding, {Failed} failed, {Ignored} ignored",
                stats.TotalTorrents, category, stats.Downloading, stats.Completed, stats.Seeding, stats.Failed, stats.Ignored);
            _logger.LogTrace("Torrent processing completed for category {Category}", category);
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
        _logger.LogTrace("ProcessSpecialCategoriesAsync called - this is now handled by Host orchestrator");
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
        // 4. §2.2: completed at least 60 seconds ago (files fully flushed to disk)
        var isComplete = torrent.Progress >= 1.0;
        var isSeeding = torrent.State.Contains("uploading", StringComparison.OrdinalIgnoreCase) ||
                       torrent.State.Contains("seeding", StringComparison.OrdinalIgnoreCase);

        // Grace period: completion_on=0 means qBit hasn't set it yet; wait until 60s after completion
        if (torrent.CompletionOn <= 0 ||
            DateTimeOffset.FromUnixTimeSeconds(torrent.CompletionOn).UtcDateTime > DateTime.UtcNow.AddSeconds(-60))
        {
            return false;
        }

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

                // AutoDelete: remove torrent from qBittorrent after successful import
                var arrCfgForDelete = _config.ArrInstances.Values.FirstOrDefault(a =>
                    string.Equals(a.Category, libraryEntry.Category, StringComparison.OrdinalIgnoreCase));
                if (arrCfgForDelete?.Torrent.AutoDelete == true)
                {
                    _logger.LogInformation("AutoDelete: removing torrent {Hash} after successful import", hash);
                    await client.DeleteTorrentsAsync(new List<string> { hash }, deleteFiles: false, cancellationToken);
                }
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
        _logger.LogTrace("Torrent [{Name}]: State[{State}] | Progress[{Progress:P1}] | Ratio[{Ratio:F2}] | AddedOn[{AddedOn}] | Availability[{Availability:P1}] | Size[{Size}] | Hash[{Hash}]",
            torrent.Name, torrent.State, torrent.Progress, torrent.Ratio, 
            DateTimeOffset.FromUnixTimeSeconds(torrent.AddedOn).DateTime,
            torrent.Availability, torrent.Size, torrent.Hash);

        _logger.LogTrace("Begin processing torrent {Name} | Hash: {Hash} | State: {State} | Progress: {Progress:P1} | Category: {Category}",
            torrent.Name, torrent.Hash, torrent.State, torrent.Progress, torrent.Category);

        // §2.13: Per-Arr IgnoreTorrentsYoungerThan — skip non-special torrents added too recently
        var arrCfg = _config.ArrInstances.Values.FirstOrDefault(a =>
            string.Equals(a.Category, category, StringComparison.OrdinalIgnoreCase));
        var ignoreYoungerThan = arrCfg?.Torrent.IgnoreTorrentsYoungerThan
            ?? _config.Settings.IgnoreTorrentsYoungerThan;

        if (!_specialCategories.Contains(torrent.Category) && torrent.AddedOn > 0)
        {
            var addedAt = DateTimeOffset.FromUnixTimeSeconds(torrent.AddedOn).UtcDateTime;
            if ((DateTime.UtcNow - addedAt).TotalSeconds < ignoreYoungerThan)
            {
                _logger.LogTrace("Skipping torrent too young to process: [{Name}] | AddedOn[{AddedOn}] | Age[{Age:F0}s] | Threshold[{Threshold}s]",
                    torrent.Name, addedAt, (DateTime.UtcNow - addedAt).TotalSeconds, ignoreYoungerThan);
                return;
            }
        }

        // Check if torrent is in a special category (failed, recheck)
        if (_specialCategories.Contains(torrent.Category))
        {
            _logger.LogTrace("Torrent {Name} is in special category {Category} - routing to special category handler",
                torrent.Name, torrent.Category);
            await ProcessSpecialCategoryTorrentAsync(torrent, stats, cancellationToken);
            _logger.LogTrace("Special category processing complete for {Name}", torrent.Name);
            return;
        }

        // Check if torrent is ignored via tag
        var hasIgnoredTag = HasTag(torrent, IgnoredTag);
        _logger.LogTrace("[{Category}] Checking ignored tag for {Name}: {HasTag}", category, torrent.Name, hasIgnoredTag);
        if (hasIgnoredTag)
        {
            _logger.LogTrace("[{Category}] Skipping ignored torrent: [{Name}] | Tag[{Tag}] | State[{State}] | Progress[{Progress:P1}] | Hash[{Hash}]",
                category, torrent.Name, IgnoredTag, torrent.State, torrent.Progress, torrent.Hash);
            stats.Ignored++;
            return;
        }

        var state = ParseTorrentState(torrent.State);
        _logger.LogTrace("Parsed torrent state: {OriginalState} -> {ParsedState}", torrent.State, state);

        // Skip processing for transient states
        if (state == TorrentState.Allocating ||
            state == TorrentState.Moving ||
            state == TorrentState.ForcedMetaDL ||
            state == TorrentState.CheckingResumeData)
        {
            _logger.LogTrace("Skipping torrent in transient state: [{Name}] | State[{State}] | Progress[{Progress:P1}] | Hash[{Hash}]",
                torrent.Name, state, torrent.Progress, torrent.Hash);
            return;
        }

        // Update statistics
        _logger.LogTrace("Updating stats for state {State}", state);
        switch (state)
        {
            case TorrentState.Downloading:
            case TorrentState.StalledDownloading:
            case TorrentState.ForcedDownloading:
                stats.Downloading++;
                break;
            case TorrentState.Uploading:
            case TorrentState.ForcedUploading:
            case TorrentState.StalledUploading:
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
        _logger.LogTrace("Stats updated: Downloading={Downloading}, Completed={Completed}, Seeding={Seeding}, Failed={Failed}, Paused={Paused}",
            stats.Downloading, stats.Completed, stats.Seeding, stats.Failed, stats.Paused);

        // Ensure torrent exists in database
        _logger.LogTrace("Ensuring torrent {Hash} exists in database", torrent.Hash);
        await EnsureTorrentInDatabaseAsync(torrent, category, cancellationToken);
        _logger.LogTrace("Database check complete for {Hash}", torrent.Hash);

        // Auto-resume stopped torrents that are not ignored or free-space-paused
        var hasFreeSpaceTag = HasTag(torrent, FreeSpacePausedTag);
        _logger.LogTrace("Checking auto-resume conditions for {Name}: IsStopped={IsStopped}, HasFreeSpaceTag={FreeSpaceTag}, HasIgnoredTag={IgnoredTag}",
            torrent.Name, torrent.IsStopped, hasFreeSpaceTag, hasIgnoredTag);
        
        if (torrent.IsStopped && !hasFreeSpaceTag && !hasIgnoredTag)
        {
            var client = _qbitManager.GetAllClients().Values.FirstOrDefault();
            if (client != null)
            {
                _logger.LogDebug("Resuming stopped torrent: [{Name}] | Progress[{Progress:P1}] | State[{State}] | Hash[{Hash}]",
                    torrent.Name, torrent.Progress, torrent.State, torrent.Hash);
                _logger.LogTrace("Executing resume for torrent {Hash}", torrent.Hash);
                await client.ResumeTorrentsAsync(new List<string> { torrent.Hash }, cancellationToken);
                _logger.LogTrace("Resume command sent for {Hash}", torrent.Hash);
            }
        }

        // §2.1: File filtering — apply once per torrent (tracked via singleton cache so it persists across loop iterations)
        if (!_cache.IsFileFiltered(torrent.Hash) &&
            (state == TorrentState.Downloading || state == TorrentState.StalledDownloading ||
             state == TorrentState.ForcedDownloading))
        {
            var filterClient = _qbitManager.GetAllClients().Values.FirstOrDefault();
            if (filterClient != null && arrCfg != null)
            {
                _logger.LogTrace("Applying file filter to {Name} ({Hash}) for first time", torrent.Name, torrent.Hash);
                var wasDeleted = await ApplyFileFilterAsync(torrent, arrCfg, filterClient, cancellationToken);
                if (wasDeleted)
                {
                    _cache.MarkFileFiltered(torrent.Hash);
                    return; // Torrent was deleted — stop processing
                }
            }
            _cache.MarkFileFiltered(torrent.Hash);
        }

        // §1.4: ReSearchStalled — delete stalled downloads that exceed StalledDelay
        if (arrCfg != null && arrCfg.Torrent.ReSearchStalled &&
            state == TorrentState.StalledDownloading &&
            torrent.LastActivity > 0)
        {
            var lastActivityTime = DateTimeOffset.FromUnixTimeSeconds(torrent.LastActivity).UtcDateTime;
            var stalledMinutes = (DateTime.UtcNow - lastActivityTime).TotalMinutes;
            if (stalledMinutes > arrCfg.Torrent.StalledDelay)
            {
                var stalledClient = _qbitManager.GetAllClients().Values.FirstOrDefault();
                if (stalledClient != null)
                {
                    _logger.LogWarning(
                        "Stalled torrent exceeded StalledDelay ({Delay} min, actual {Actual:F0} min) — deleting and re-queuing search: [{Name}] ({Hash})",
                        arrCfg.Torrent.StalledDelay, stalledMinutes, torrent.Name, torrent.Hash);
                    await stalledClient.DeleteTorrentsAsync(new List<string> { torrent.Hash }, deleteFiles: true, cancellationToken);
                    stats.Failed++;
                    return;
                }
            }
        }

        // §1.5 / §3.6: MaximumETA — delete downloads whose last activity exceeds MaximumETA seconds.
        // Per-tracker MaxETA overrides the global Torrent.MaximumETA (qBitrr §3.6 parity).
        if (arrCfg != null &&
            (state == TorrentState.Downloading || state == TorrentState.StalledDownloading ||
             state == TorrentState.ForcedDownloading) &&
            torrent.LastActivity > 0 &&
            torrent.Progress < arrCfg.Torrent.MaximumDeletablePercentage)
        {
            // §3.6: resolve per-tracker MaxETA override from merged qBit + Arr tracker config
            var effectiveMaxETA = arrCfg.Torrent.MaximumETA;
            if (!string.IsNullOrEmpty(torrent.Tracker))
            {
                var torrentTrackerHost = SeedingService.ExtractTrackerHost(torrent.Tracker);
                var allTrackerCfgs = _config.QBitInstances.Values
                    .SelectMany(q => q.Trackers)
                    .Concat(arrCfg.Torrent.Trackers);
                var matchedCfg = allTrackerCfgs.FirstOrDefault(t =>
                {
                    var h = SeedingService.ExtractTrackerHost(t.Uri ?? "");
                    return string.Equals(h, torrentTrackerHost, StringComparison.OrdinalIgnoreCase)
                        || torrentTrackerHost.EndsWith("." + h, StringComparison.OrdinalIgnoreCase);
                });
                if (matchedCfg?.MaxETA != null)
                    effectiveMaxETA = matchedCfg.MaxETA.Value;
            }

            if (effectiveMaxETA > 0)
            {
                var lastActivityTime = DateTimeOffset.FromUnixTimeSeconds(torrent.LastActivity).UtcDateTime;
                var inactiveSeconds = (DateTime.UtcNow - lastActivityTime).TotalSeconds;
                if (inactiveSeconds > effectiveMaxETA)
                {
                    // DoNotRemoveSlow: skip deletion if torrent has a finite ETA (still making progress, just slow)
                    var isSlow = arrCfg.Torrent.DoNotRemoveSlow && torrent.Eta > 0 && torrent.Eta < 8640000; // < 100 days
                    if (!isSlow)
                    {
                        var etaClient = _qbitManager.GetAllClients().Values.FirstOrDefault();
                        if (etaClient != null)
                        {
                            _logger.LogWarning(
                                "Torrent inactive for {Inactive:F0}s (MaximumETA={Max}s) — deleting: [{Name}] ({Hash})",
                                inactiveSeconds, effectiveMaxETA, torrent.Name, torrent.Hash);
                            await etaClient.DeleteTorrentsAsync(new List<string> { torrent.Hash }, deleteFiles: true, cancellationToken);
                            stats.Failed++;
                            return;
                        }
                    }
                    else
                    {
                        _logger.LogTrace(
                            "DoNotRemoveSlow: skipping deletion of slow torrent [{Name}] (ETA={Eta}s, Inactive={Inactive:F0}s)",
                            torrent.Name, torrent.Eta, inactiveSeconds);
                    }
                }
            }
        }

        // Process based on state
        var isComplete = torrent.Progress >= 1.0;
        var isPaused = torrent.State.Contains("paused", StringComparison.OrdinalIgnoreCase);
        _logger.LogTrace("Completion check: Progress={Progress}, IsPaused={IsPaused}", torrent.Progress, isPaused);
        
        if (isComplete && !isPaused)
        {
            // Torrent is complete
            _logger.LogTrace("Torrent {Name} is complete (Progress: {Progress:P1})", torrent.Name, torrent.Progress);
            stats.Completed++;

            // Check if ready for import
            _logger.LogTrace("Checking if torrent {Hash} is ready for import", torrent.Hash);
            var isReadyForImport = await IsReadyForImportAsync(torrent.Hash, cancellationToken);
            _logger.LogTrace("Import readiness check result for {Hash}: {IsReady}", torrent.Hash, isReadyForImport);
            
            if (isReadyForImport)
            {
                _logger.LogDebug("Importing Completed torrent: [{Name}] | Progress[{Progress:P1}] | AddedOn[{AddedOn}] | Availability[{Availability:P1}] | Hash[{Hash}]",
                    torrent.Name, torrent.Progress, 
                    DateTimeOffset.FromUnixTimeSeconds(torrent.AddedOn).DateTime,
                    torrent.Availability, torrent.Hash);
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

        // §2.13: Settings-level IgnoreTorrentsYoungerThan applies to special categories (failed/recheck)
        if (torrent.AddedOn > 0)
        {
            var addedAt = DateTimeOffset.FromUnixTimeSeconds(torrent.AddedOn).UtcDateTime;
            var age = (DateTime.UtcNow - addedAt).TotalSeconds;
            if (age < _config.Settings.IgnoreTorrentsYoungerThan)
            {
                _logger.LogTrace(
                    "Skipping special-category torrent too young to act on: [{Name}] age={Age:F0}s < threshold={Threshold}s",
                    torrent.Name, age, _config.Settings.IgnoreTorrentsYoungerThan);
                return;
            }
        }

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
        // §1.6 Tagless mode: map tags to TorrentLibrary DB columns; IgnoredTag has no DB equivalent → false
        if (_config.Settings.Tagless)
        {
            if (tag == IgnoredTag) return false;
            var dbEntry = _dbContext.TorrentLibrary.AsNoTracking()
                .FirstOrDefault(t => t.Hash == torrent.Hash);
            if (dbEntry == null) return false;
            return tag switch
            {
                FreeSpacePausedTag => dbEntry.FreeSpacePaused,
                AllowedSeedingTag  => dbEntry.AllowedSeeding,
                _ => false
            };
        }

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
                _logger.LogTrace("Created tags: {Tags}", string.Join(", ", tagsToCreate));
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

            _logger.LogTrace("Added torrent {Hash} to database", torrent.Hash);
        }
    }

    private TorrentState ParseTorrentState(string stateString)
    {
        return stateString.ToLower() switch
        {
            var s when s.Contains("forcedMetaDL") || s.Contains("forcedmetadl") => TorrentState.ForcedMetaDL,
            var s when s.Contains("checkingResumeData") || s.Contains("checkingresumedata") => TorrentState.CheckingResumeData,
            var s when s.Contains("allocating") => TorrentState.Allocating,
            var s when s.Contains("moving") => TorrentState.Moving,
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

    /// <summary>
    /// §2.1: Apply file filtering to a downloading torrent.
    /// Sets excluded files to priority 0; deletes torrent if all files are excluded.
    /// Returns true if the torrent was deleted.
    /// </summary>
    private async Task<bool> ApplyFileFilterAsync(
        TorrentInfo torrent,
        ArrInstanceConfig arrCfg,
        QBittorrentClient client,
        CancellationToken ct)
    {
        var cfg = arrCfg.Torrent;

        // No filtering configured — fast exit
        if (cfg.FolderExclusionRegex.Count == 0 &&
            cfg.FileNameExclusionRegex.Count == 0 &&
            cfg.FileExtensionAllowlist.Count == 0)
            return false;

        var files = await client.GetTorrentFilesAsync(torrent.Hash, ct);
        if (files.Count == 0)
            return false;

        var regexOptions = cfg.CaseSensitiveMatches ? RegexOptions.None : RegexOptions.IgnoreCase;

        var excludedIds = files
            .Where(f => ShouldExcludeFile(f.Name, cfg, regexOptions))
            .Select(f => f.Index)
            .ToArray();

        if (excludedIds.Length == 0)
            return false;

        // If ALL files are excluded, delete the torrent entirely
        if (excludedIds.Length >= files.Count)
        {
            _logger.LogWarning(
                "All {Total} files excluded in [{Name}] ({Hash}) — deleting torrent",
                files.Count, torrent.Name, torrent.Hash);
            await client.DeleteTorrentsAsync(new List<string> { torrent.Hash }, deleteFiles: true, ct);
            return true;
        }

        // Set excluded files to priority 0 (do not download)
        _logger.LogDebug(
            "File filter: setting {Excluded}/{Total} files to priority 0 in [{Name}]: {Files}",
            excludedIds.Length, files.Count, torrent.Name,
            string.Join(", ", files.Where(f => excludedIds.Contains(f.Index)).Select(f => f.Name)));
        await client.SetFilePriorityAsync(torrent.Hash, excludedIds, 0, ct);
        return false;
    }

    /// <summary>
    /// Returns true if a file should be excluded based on folder/filename regex and extension allowlist.
    /// </summary>
    private static bool ShouldExcludeFile(string filePath, TorrentConfig cfg, RegexOptions regexOptions)
    {
        // Normalize to forward slashes
        filePath = filePath.Replace('\\', '/');

        var parts = filePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var fileName = parts.Last();
        var folderParts = parts.Take(parts.Length - 1);

        // Check folder exclusion regex against each path component
        foreach (var folder in folderParts)
        {
            foreach (var pattern in cfg.FolderExclusionRegex)
            {
                try
                {
                    if (Regex.IsMatch(folder, pattern, regexOptions))
                        return true;
                }
                catch (ArgumentException) { /* skip bad regex patterns */ }
            }
        }

        // Check file name exclusion regex
        foreach (var pattern in cfg.FileNameExclusionRegex)
        {
            try
            {
                if (Regex.IsMatch(fileName, pattern, regexOptions))
                    return true;
            }
            catch (ArgumentException) { /* skip bad regex patterns */ }
        }

        // Check file extension allowlist — if allowlist is non-empty, extension must be in it
        if (cfg.FileExtensionAllowlist.Count > 0)
        {
            var ext = Path.GetExtension(fileName);
            if (!cfg.FileExtensionAllowlist.Any(allowed =>
                    string.Equals(allowed, ext, StringComparison.OrdinalIgnoreCase)))
                return true;
        }

        return false;
    }
}

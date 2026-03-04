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
/// Implements torrent processing logic matching qBitrr's _process_single_torrent state machine.
/// Handles tag-based filtering, seeding rules, special categories, and import triggering.
/// </summary>
public class TorrentProcessor : ITorrentProcessor
{
    private const string IgnoredTag = "qBitrr-ignored";
    private const string AllowedSeedingTag = "qBitrr-allowed_seeding";
    private const string AllowedStalledTag = "qBitrr-allowed_stalled";
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

        var allClients = _qbitManager.GetAllClients();
        if (allClients.Count == 0)
        {
            _logger.LogWarning("No qBittorrent client available");
            return;
        }

        // Skip special categories - they are handled globally by the Host orchestrator
        if (_specialCategories.Contains(category))
        {
            _logger.LogTrace("Skipping special category {Category} - handled by Host orchestrator", category);
            return;
        }

        try
        {
            // Ensure tags exist on all clients (skip in Tagless mode — §1.6)
            if (!_config.Settings.Tagless)
            {
                foreach (var (_, c) in allClients)
                    await EnsureTagsExistAsync(c, cancellationToken);
            }

            // Get all torrents for this category from ALL qBit instances, stamping instance name
            var torrents = new List<TorrentInfo>();
            foreach (var (instanceName, c) in allClients)
            {
                var instanceTorrents = await c.GetTorrentsAsync(category, cancellationToken);
                foreach (var t in instanceTorrents)
                    t.QBitInstanceName = instanceName;
                torrents.AddRange(instanceTorrents);
            }
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

    [Obsolete("Special categories are now handled by the Host orchestrator. This method is kept for backwards compatibility.")]
    public async Task ProcessSpecialCategoriesAsync(CancellationToken cancellationToken = default)
    {
        _logger.LogTrace("ProcessSpecialCategoriesAsync called - this is now handled by Host orchestrator");
        await Task.CompletedTask;
    }

    public async Task ProcessTorrentAsync(string hash, CancellationToken cancellationToken = default)
    {
        TorrentInfo? torrent = null;
        foreach (var (instanceName, c) in _qbitManager.GetAllClients())
        {
            var torrents = await c.GetTorrentsAsync(ct: cancellationToken);
            var found = torrents.FirstOrDefault(t => t.Hash == hash);
            if (found != null)
            {
                found.QBitInstanceName = instanceName;
                torrent = found;
                break;
            }
        }

        if (torrent != null)
        {
            await ProcessSingleTorrentAsync(torrent, torrent.Category, new TorrentProcessingStats(), cancellationToken);
        }
    }

    public async Task<bool> IsReadyForImportAsync(string hash, CancellationToken cancellationToken = default)
    {
        TorrentInfo? torrent = null;
        foreach (var (_, c) in _qbitManager.GetAllClients())
        {
            var torrents = await c.GetTorrentsAsync(ct: cancellationToken);
            var found = torrents.FirstOrDefault(t => t.Hash == hash);
            if (found != null) { torrent = found; break; }
        }

        if (torrent == null) return false;

        var isComplete = torrent.Progress >= 1.0;
        var isSeeding = torrent.State.Contains("uploading", StringComparison.OrdinalIgnoreCase) ||
                       torrent.State.Contains("seeding", StringComparison.OrdinalIgnoreCase);

        // §2.2: Grace period: completion_on=0 means qBit hasn't set it yet; wait until 60s after completion
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

        TorrentInfo? torrent = null;
        string? torrentInstanceName = null;
        foreach (var (instanceName, c) in _qbitManager.GetAllClients())
        {
            var torrents = await c.GetTorrentsAsync(ct: cancellationToken);
            var found = torrents.FirstOrDefault(t => t.Hash == hash);
            if (found != null)
            {
                found.QBitInstanceName = instanceName;
                torrent = found;
                torrentInstanceName = instanceName;
                break;
            }
        }

        if (torrent == null)
        {
            _logger.LogWarning("Torrent {Hash} not found in qBittorrent", hash);
            return;
        }

        var client = _qbitManager.GetClient(torrentInstanceName!);
        if (client == null)
        {
            _logger.LogWarning("No qBittorrent client available for import");
            return;
        }

        var libraryEntry = await _dbContext.TorrentLibrary
            .FirstOrDefaultAsync(t => t.Hash == hash, cancellationToken);

        if (libraryEntry == null)
        {
            _logger.LogWarning("Torrent {Hash} not found in database", hash);
            return;
        }

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

    // ========================================================================================
    // CORE STATE MACHINE — matches qBitrr's _process_single_torrent (arss.py:6065-6253)
    // ========================================================================================

    private async Task ProcessSingleTorrentAsync(
        TorrentInfo torrent,
        string category,
        TorrentProcessingStats stats,
        CancellationToken ct)
    {
        _logger.LogTrace(
            "Torrent [{Name}]: State[{State}] | Progress[{Progress:P1}] | Ratio[{Ratio:F2}] | " +
            "AddedOn[{AddedOn}] | Availability[{Availability:P1}] | Size[{Size}] | Hash[{Hash}]",
            torrent.Name, torrent.State, torrent.Progress, torrent.Ratio,
            DateTimeOffset.FromUnixTimeSeconds(torrent.AddedOn).DateTime,
            torrent.Availability, torrent.Size, torrent.Hash);

        var state = ParseTorrentState(torrent.State);
        var arrCfg = _config.ArrInstances.Values.FirstOrDefault(a =>
            string.Equals(a.Category, category, StringComparison.OrdinalIgnoreCase));
        var ignoreYoungerThan = arrCfg?.Torrent.IgnoreTorrentsYoungerThan
            ?? _config.Settings.IgnoreTorrentsYoungerThan;
        var timeNow = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        // Cache torrent metadata
        _cache.SetCategory(torrent.Hash, torrent.Category);
        _cache.SetName(torrent.Hash, torrent.Name);

        // Ensure torrent exists in database
        await EnsureTorrentInDatabaseAsync(torrent, category, ct);

        // PRE-STEP 0: Tracker actions — runs for EVERY torrent BEFORE the state machine
        // (qBitrr: _process_single_torrent_trackers — arss.py:6070)
        if (_seedingService != null)
        {
            await _seedingService.ApplyTrackerActionsForTorrentAsync(torrent, ct);
        }

        // PRE-STEP 1: Resolve leave_alone / remove_torrent / maxEta (qBitrr: _should_leave_alone)
        var (leaveAlone, maxEta, removeTorrent) = await ResolveLeaveAloneAsync(torrent, state, arrCfg, ct);
        _logger.LogTrace("Torrent [{Name}]: LeaveAlone={LeaveAlone}, MaxETA={MaxEta}, RemoveTorrent={Remove}",
            torrent.Name, leaveAlone, maxEta, removeTorrent);

        // PRE-STEP 2: Stalled check for MetaDL / StalledDL / Downloading states
        // (qBitrr: _stalled_check — runs before the state machine)
        var stalledIgnore = false;
        if (state is TorrentState.MetadataDownloading or TorrentState.StalledDownloading or TorrentState.Downloading)
        {
            stalledIgnore = await StalledCheckAsync(torrent, state, arrCfg, timeNow, ct);
        }

        // If ignored via tag: clean up seeding/free-space tags and skip (qBitrr: lines 6094-6098)
        if (HasTag(torrent, IgnoredTag))
        {
            var ignClient = _qbitManager.GetClient(torrent.QBitInstanceName);
            if (ignClient != null && !_config.Settings.Tagless)
                await ignClient.RemoveTagsAsync(new List<string> { torrent.Hash },
                    new List<string> { AllowedSeedingTag, FreeSpacePausedTag }, ct);
            stats.Ignored++;
            return;
        }

        // Update statistics based on state
        UpdateStats(state, stats);

        var client = _qbitManager.GetClient(torrent.QBitInstanceName);
        if (client == null) return;

        // ================================================================================
        // STATE MACHINE — if/elif chain matching qBitrr's _process_single_torrent ordering
        // ================================================================================

        // Branch 1: Custom format unmet → delete (qBitrr line 6099-6105)
        if (_importService != null
            && arrCfg?.Search.CustomFormatUnmetSearch == true
            && !HasTag(torrent, IgnoredTag)
            && !HasTag(torrent, FreeSpacePausedTag)
            && await _importService.IsCustomFormatUnmetAsync(torrent.Hash, category, ct))
        {
            _logger.LogWarning("Deleting torrent (custom format unmet): [{Name}] | Hash[{Hash}]",
                torrent.Name, torrent.Hash);
            await client.DeleteTorrentsAsync(new List<string> { torrent.Hash }, deleteFiles: true, ct);
            stats.Failed++;
        }
        // Branch 2: Ratio/seed limit met AND not leave_alone AND fully downloaded → delete
        // (qBitrr line 6106-6109: remove_torrent and not leave_alone and amount_left==0)
        else if (removeTorrent && !leaveAlone && torrent.AmountLeft == 0)
        {
            var hnrAllows = _seedingService == null || await _seedingService.HnrAllowsDeleteAsync(torrent, "ratio/seed limit", ct);
            if (hnrAllows)
            {
                _logger.LogWarning("Deleting torrent (ratio/seed limit reached): [{Name}] | Ratio[{Ratio:F2}] | SeedingTime[{SeedTime}s] | Hash[{Hash}]",
                    torrent.Name, torrent.Ratio, torrent.SeedingTime, torrent.Hash);
                await client.DeleteTorrentsAsync(new List<string> { torrent.Hash }, deleteFiles: true, ct);
                stats.Failed++;
            }
        }
        // Branch 3: Failed category → delete (qBitrr line 6110-6112)
        // No HnR check — manually failed torrents are always deleted immediately (qBitrr parity)
        else if (torrent.Category.Equals(_config.Settings.FailedCategory, StringComparison.OrdinalIgnoreCase))
        {
            _logger.LogWarning("Deleting manually failed torrent: [{Name}] | Hash[{Hash}]", torrent.Name, torrent.Hash);
            await client.DeleteTorrentsAsync(new List<string> { torrent.Hash }, deleteFiles: true, ct);
            stats.Failed++;
        }
        // Branch 4: Recheck category → recheck (qBitrr line 6113-6115)
        else if (torrent.Category.Equals(_config.Settings.RecheckCategory, StringComparison.OrdinalIgnoreCase))
        {
            _logger.LogInformation("Re-checking manually set torrent: [{Name}] | Hash[{Hash}]", torrent.Name, torrent.Hash);
            await client.RecheckTorrentsAsync(new List<string> { torrent.Hash }, ct);
        }
        // Branch 5: Missing files → delete from client, no blacklist (qBitrr line 6116-6118)
        else if (IsMissingFilesTorrent(torrent, state))
        {
            _logger.LogInformation("Deleting torrent with missing files: [{Name}] | State[{State}] | Hash[{Hash}]",
                torrent.Name, torrent.State, torrent.Hash);
            await client.DeleteTorrentsAsync(new List<string> { torrent.Hash }, deleteFiles: false, ct);
            stats.Failed++;
        }
        // Branch 6: Ignored states → skip (qBitrr line 6119-6120: is_ignored_state)
        else if (IsIgnoredState(state))
        {
            _logger.LogTrace("Skipping torrent in ignored state: [{Name}] | State[{State}]", torrent.Name, state);
        }
        // Branch 7: Stopped + leave_alone → resume (qBitrr line 6121-6133)
        else if (torrent.IsStopped
            && leaveAlone
            && !HasTag(torrent, FreeSpacePausedTag)
            && !HasTag(torrent, IgnoredTag))
        {
            _logger.LogDebug("Resuming stopped torrent (leave_alone): [{Name}] | State[{State}] | Hash[{Hash}]",
                torrent.Name, torrent.State, torrent.Hash);
            await client.ResumeTorrentsAsync(new List<string> { torrent.Hash }, ct);
        }
        // Branch 8: Stalled DL / MetaDL + not stalled_ignore → stalled processing (qBitrr line 6134-6140)
        else if (state is TorrentState.MetadataDownloading or TorrentState.StalledDownloading
            && !HasTag(torrent, IgnoredTag)
            && !HasTag(torrent, FreeSpacePausedTag)
            && !stalledIgnore)
        {
            await ProcessStalledTorrentAsync(torrent, "Stalled State", client, arrCfg, stats, timeNow, ct);
        }
        // Branch 9: Downloading + not yet file-filtered → file filter (qBitrr line 6141-6147)
        else if (IsActiveDownloadingState(state)
            && state != TorrentState.MetadataDownloading
            && !_cache.IsFileFiltered(torrent.Hash))
        {
            if (arrCfg != null)
            {
                var wasDeleted = await ApplyFileFilterAsync(torrent, arrCfg, client, ct);
                _cache.MarkFileFiltered(torrent.Hash);
                if (wasDeleted) return;
            }
            else
            {
                _cache.MarkFileFiltered(torrent.Hash);
            }
        }
        // Branch 10: In timed ignore cache → resume if stopped, else skip (qBitrr line 6148-6163)
        else if (_cache.IsInIgnoreCache(torrent.Hash))
        {
            if (torrent.IsStopped
                && !HasTag(torrent, FreeSpacePausedTag)
                && !HasTag(torrent, IgnoredTag))
            {
                _logger.LogDebug("Resuming stopped torrent (in ignore cache): [{Name}]", torrent.Name);
                await client.ResumeTorrentsAsync(new List<string> { torrent.Hash }, ct);
            }
            else
            {
                _logger.LogTrace("Skipping torrent in ignore cache: [{Name}]", torrent.Name);
            }
        }
        // Branch 11: Queued upload → pause if !leave_alone (qBitrr line 6164-6165)
        else if (state == TorrentState.QueuedUploading)
        {
            if (leaveAlone || state == TorrentState.ForcedUploading)
            {
                _logger.LogTrace("Queued upload, allowing seeding: [{Name}]", torrent.Name);
            }
            else
            {
                _logger.LogTrace("Pausing queued upload torrent: [{Name}]", torrent.Name);
                await client.PauseTorrentsAsync(new List<string> { torrent.Hash }, ct);
            }
        }
        // Branch 12: Paused download with amount_left → resume (qBitrr line 6167-6173)
        else if (state == TorrentState.PausedDownloading
            && torrent.AmountLeft != 0
            && !HasTag(torrent, FreeSpacePausedTag)
            && !HasTag(torrent, IgnoredTag))
        {
            _logger.LogDebug("Resuming incomplete paused download: [{Name}] | Progress[{Progress:P1}] | Hash[{Hash}]",
                torrent.Name, torrent.Progress, torrent.Hash);
            _cache.AddToIgnoreCache(torrent.Hash, TimeSpan.FromSeconds(ignoreYoungerThan));
            await client.ResumeTorrentsAsync(new List<string> { torrent.Hash }, ct);
        }
        // Branch 13: Percentage threshold — high progress, not complete, already filtered (qBitrr line 6174-6181)
        else if (torrent.Progress <= (arrCfg?.Torrent.MaximumDeletablePercentage ?? 0.99)
            && !IsCompleteState(state)
            && !HasTag(torrent, IgnoredTag)
            && !HasTag(torrent, FreeSpacePausedTag)
            && !stalledIgnore
            && _cache.IsFileFiltered(torrent.Hash))
        {
            await ProcessPercentageThresholdAsync(torrent, maxEta, client, stats, ct);
        }
        // Branch 14: Already imported → skip (qBitrr line 6184-6187)
        else if (await IsImportedInDatabaseAsync(torrent.Hash, ct)
            && _cache.IsFileFiltered(torrent.Hash))
        {
            _logger.LogTrace("Skipping already-imported torrent: [{Name}]", torrent.Name);
        }
        // Branch 15: Error state → recheck (qBitrr line 6191-6192)
        else if (state == TorrentState.Error)
        {
            _logger.LogInformation("Re-checking errored torrent: [{Name}] | Hash[{Hash}]", torrent.Name, torrent.Hash);
            await client.RecheckTorrentsAsync(new List<string> { torrent.Hash }, ct);
        }
        // Branch 16: Fully completed (60s grace period) → trigger import (qBitrr line 6196-6205)
        // Python: if leave_alone → allow seeding; elif not tagged qBitrr-imported → dispatch to import_torrents
        else if (torrent.AddedOn > 0
            && torrent.CompletionOn > 0
            && torrent.AmountLeft == 0
            && state != TorrentState.PausedUploading
            && IsCompleteState(state)
            && !string.IsNullOrEmpty(torrent.ContentPath)
            && torrent.CompletionOn < timeNow - 60)
        {
            stats.Completed++;
            if (leaveAlone || state == TorrentState.ForcedUploading)
            {
                _logger.LogTrace("Completed torrent — allowing seeding: [{Name}]", torrent.Name);
            }
            else if (await IsReadyForImportAsync(torrent.Hash, ct))
            {
                _logger.LogDebug("Completed torrent ready for import: [{Name}] | Progress[{Progress:P1}] | Hash[{Hash}]",
                    torrent.Name, torrent.Progress, torrent.Hash);
                await ImportTorrentAsync(torrent.Hash, ct);
            }
        }
        // Branch 17: Uploading + seeding limits configured → pause if !leave_alone (qBitrr line 6207-6215)
        else if (IsUploadingState(state)
            && torrent.SeedingTime > 1
            && torrent.AmountLeft == 0
            && torrent.AddedOn > 0
            && !string.IsNullOrEmpty(torrent.ContentPath)
            && GetRemoveMode(torrent, arrCfg) != -1
            && _cache.IsFileFiltered(torrent.Hash))
        {
            if (leaveAlone || state == TorrentState.ForcedUploading)
            {
                _logger.LogTrace("Uploading, allowing seeding: [{Name}]", torrent.Name);
            }
            else
            {
                _logger.LogInformation("Pausing uploading torrent (seeding limits configured): [{Name}] | Hash[{Hash}]",
                    torrent.Name, torrent.Hash);
                await client.PauseTorrentsAsync(new List<string> { torrent.Hash }, ct);
            }
        }
        // Branch 18: Slow download — 0 < maxEta < torrent.Eta + DoNotRemoveSlow=false (qBitrr line 6217-6227)
        else if (state != TorrentState.PausedDownloading
            && IsActiveDownloadingState(state)
            && timeNow > torrent.AddedOn + ignoreYoungerThan
            && maxEta > 0 && torrent.Eta > 0 && maxEta < torrent.Eta
            && !(arrCfg?.Torrent.DoNotRemoveSlow ?? true)
            && !HasTag(torrent, IgnoredTag)
            && !HasTag(torrent, FreeSpacePausedTag)
            && !stalledIgnore)
        {
            _logger.LogWarning("Deleting slow torrent (ETA {Eta}s > MaxETA {Max}s): [{Name}] ({Hash})",
                torrent.Eta, maxEta, torrent.Name, torrent.Hash);
            var hnrAllows = _seedingService == null || await _seedingService.HnrAllowsDeleteAsync(torrent, "slow torrent deletion", ct);
            if (hnrAllows)
            {
                await client.DeleteTorrentsAsync(new List<string> { torrent.Hash }, deleteFiles: true, ct);
                stats.Failed++;
            }
        }
        // Branch 19: Downloading — availability-based deletion or file processing (qBitrr line 6229-6249)
        else if (IsActiveDownloadingState(state))
        {
            if (timeNow > torrent.AddedOn + ignoreYoungerThan
                && torrent.Availability < 1
                && _cache.IsFileFiltered(torrent.Hash)
                && !HasTag(torrent, IgnoredTag)
                && !HasTag(torrent, FreeSpacePausedTag)
                && !stalledIgnore)
            {
                // Unavailable torrent past age gate → mark for deletion
                await ProcessStalledTorrentAsync(torrent, "Unavailable", client, arrCfg, stats, timeNow, ct);
            }
            else if (_cache.IsFileFiltered(torrent.Hash))
            {
                // Already filtered, skip
                _logger.LogTrace("Already cleaned up: [{Name}]", torrent.Name);
            }
            else
            {
                // Not yet filtered — apply file filter
                if (arrCfg != null)
                {
                    var wasDeleted = await ApplyFileFilterAsync(torrent, arrCfg, client, ct);
                    _cache.MarkFileFiltered(torrent.Hash);
                    if (wasDeleted) return;
                }
            }
        }
        // Branch 20: Complete + leave_alone → resume if not already running (qBitrr line 6250-6251)
        // Python: self.resume.add(torrent.hash) — ensures paused-upload complete torrents resume seeding
        else if (IsCompleteState(state) && leaveAlone)
        {
            _logger.LogTrace("Allowing seeding for complete torrent: [{Name}]", torrent.Name);
            await client.ResumeTorrentsAsync(new List<string> { torrent.Hash }, ct);
        }
        // Branch 21: Default — unprocessed
        else
        {
            _logger.LogTrace("Unprocessed torrent: [{Name}] | State[{State}] | Progress[{Progress:P1}]",
                torrent.Name, state, torrent.Progress);
        }
    }

    // ========================================================================================
    // PRE-STEP: leave_alone resolution (qBitrr: _should_leave_alone — arss.py:5804-5892)
    // ========================================================================================

    /// <summary>
    /// Resolves whether a torrent should be left alone (allowed to seed),
    /// the effective MaxETA, and whether removal conditions are met.
    /// Manages qBitrr-allowed_seeding and qBitrr-hnr_active tags.
    /// </summary>
    private async Task<(bool LeaveAlone, int MaxEta, bool RemoveTorrent)> ResolveLeaveAloneAsync(
        TorrentInfo torrent, TorrentState state, ArrInstanceConfig? arrCfg, CancellationToken ct)
    {
        var defaultMaxEta = arrCfg?.Torrent.MaximumETA ?? -1;

        if (_seedingService == null)
            return (true, defaultMaxEta, false);

        // Super seeding or forced upload → always leave alone (qBitrr: lines 5809-5810)
        if (torrent.SuperSeeding || state == TorrentState.ForcedUploading)
            return (true, -1, false);

        var isUploading = IsUploadingState(state);

        // Get tracker config for MaxETA override (qBitrr: data_settings.get("max_eta"))
        var trackerCfg = await _seedingService.GetTrackerConfigAsync(torrent, ct);
        var maxEta = trackerCfg?.MaxETA ?? defaultMaxEta;

        // Check if torrent should be removed (ratio/time limits met)
        // ShouldRemoveTorrentAsync already handles HnR protection for downloading torrents
        var shouldRemove = await _seedingService.ShouldRemoveTorrentAsync(torrent, ct);

        // leave_alone = NOT (isUploading AND shouldRemove) — qBitrr line 5862-5864
        var leaveAlone = !(isUploading && shouldRemove);

        // Free space paused → leave alone — qBitrr line 5867-5868
        if (HasTag(torrent, FreeSpacePausedTag))
            leaveAlone = true;

        // Tag management: qBitrr-allowed_seeding (qBitrr lines 5869-5878)
        var client = _qbitManager.GetClient(torrent.QBitInstanceName);
        if (client != null)
        {
            var hasAllowedSeeding = HasTag(torrent, AllowedSeedingTag);
            var hasFreeSpacePaused = HasTag(torrent, FreeSpacePausedTag);

            if (leaveAlone && !hasAllowedSeeding && !hasFreeSpacePaused)
            {
                await AddTagAsync(torrent, client, AllowedSeedingTag, ct);
            }
            else if ((!leaveAlone && hasAllowedSeeding) || hasFreeSpacePaused)
            {
                await RemoveTagAsync(torrent, client, AllowedSeedingTag, ct);
            }
        }

        return (leaveAlone, maxEta, shouldRemove);
    }

    // ========================================================================================
    // PRE-STEP: Stalled check (qBitrr: _stalled_check — arss.py:5973-6063)
    // ========================================================================================

    /// <summary>
    /// Checks if a torrent should be ignored during stalled processing.
    /// Returns true (stalled_ignore) if the torrent is too young or within the stalled delay window.
    /// </summary>
    private async Task<bool> StalledCheckAsync(
        TorrentInfo torrent,
        TorrentState state,
        ArrInstanceConfig? arrCfg,
        long timeNow,
        CancellationToken ct)
    {
        var stalledDelay = arrCfg?.Torrent.StalledDelay ?? 15;
        var ignoreYoungerThan = arrCfg?.Torrent.IgnoreTorrentsYoungerThan
            ?? _config.Settings.IgnoreTorrentsYoungerThan;

        // If stalled delay is disabled (< 0): stalled_ignore = False (process immediately)
        if (stalledDelay < 0)
            return false;

        var stalledDelaySeconds = stalledDelay * 60;

        // Too young → stalled_ignore = True (qBitrr line 5984)
        if (timeNow < torrent.AddedOn + ignoreYoungerThan)
        {
            _logger.LogTrace("Stalled check: torrent too young [{Name}]", torrent.Name);
            return true;
        }

        // Check stalled condition:
        // - MetaDL/StalledDL AND not ignored AND not free_space_paused
        // - OR: Downloading AND availability < 1 AND already file-filtered AND not ignored AND not free_space_paused
        var isIgnored = HasTag(torrent, IgnoredTag);
        var isFreeSpacePaused = HasTag(torrent, FreeSpacePausedTag);

        var isStalledState = (state is TorrentState.MetadataDownloading or TorrentState.StalledDownloading)
            && !isIgnored && !isFreeSpacePaused;
        var isUnavailableDownloading = torrent.Availability < 1
            && _cache.IsFileFiltered(torrent.Hash)
            && state == TorrentState.Downloading
            && !isIgnored && !isFreeSpacePaused;

        if ((isStalledState || isUnavailableDownloading) && stalledDelay >= 0)
        {
            // Stalled delay expired → stalled_ignore = False (let the state machine handle deletion)
            if (stalledDelay > 0 && timeNow >= torrent.LastActivity + stalledDelaySeconds)
            {
                _logger.LogTrace("Stalled delay expired for [{Name}]", torrent.Name);
                return false;
            }

            // Within delay: add allowed_stalled tag (qBitrr line 6029-6046)
            if (!HasTag(torrent, AllowedStalledTag))
            {
                var client = _qbitManager.GetClient(torrent.QBitInstanceName);
                if (client != null)
                    await AddStalledTagAsync(torrent, client, ct);

                // If ReSearchStalled is enabled, blocklist + re-search via Arr API
                // (qBitrr: process_entries([torrent.hash]) + _process_failed_individual)
                if (arrCfg?.Torrent.ReSearchStalled == true && _importService != null)
                {
                    _logger.LogDebug("Stalled torrent [{Name}] — ReSearchStalled enabled; blocklisting + re-search",
                        torrent.Name);
                    await _importService.BlocklistAndReSearchAsync(torrent.Hash, torrent.Category, ct);
                }
            }
            return true; // within delay, stalled_ignore = True
        }

        // Not stalled: remove allowed_stalled tag if present (qBitrr line 6056-6059)
        if (HasTag(torrent, AllowedStalledTag))
        {
            var client = _qbitManager.GetClient(torrent.QBitInstanceName);
            if (client != null)
                await RemoveStalledTagAsync(torrent, client, ct);
            return false;
        }

        return false;
    }

    // ========================================================================================
    // STATE MACHINE HELPER: Stalled torrent processing (qBitrr: _process_single_torrent_stalled_torrent)
    // ========================================================================================

    /// <summary>
    /// Process stalled or unavailable torrents: check age gate + last_activity + HnR before deletion.
    /// </summary>
    private async Task ProcessStalledTorrentAsync(
        TorrentInfo torrent, string reason,
        QBittorrentClient client, ArrInstanceConfig? arrCfg,
        TorrentProcessingStats stats, long timeNow, CancellationToken ct)
    {
        var ignoreYoungerThan = arrCfg?.Torrent.IgnoreTorrentsYoungerThan
            ?? _config.Settings.IgnoreTorrentsYoungerThan;

        // qBitrr line 5247-5252: only delete if added AND last_activity are both past the age threshold
        if (torrent.AddedOn < timeNow - ignoreYoungerThan
            && torrent.LastActivity < timeNow - ignoreYoungerThan)
        {
            var hnrAllows = _seedingService == null || await _seedingService.HnrAllowsDeleteAsync(torrent, reason, ct);
            if (hnrAllows)
            {
                _logger.LogWarning("Deleting stalled torrent ({Reason}): [{Name}] | Availability[{Avail:P1}] | Hash[{Hash}]",
                    reason, torrent.Name, torrent.Availability, torrent.Hash);
                await client.DeleteTorrentsAsync(new List<string> { torrent.Hash }, deleteFiles: true, ct);
                stats.Failed++;
            }
        }
        else
        {
            _logger.LogTrace("Ignoring stale torrent ({Reason}): [{Name}] (too young or recently active)", reason, torrent.Name);
        }
    }

    // ========================================================================================
    // STATE MACHINE HELPER: Percentage threshold (qBitrr: _process_single_torrent_percentage_threshold)
    // ========================================================================================

    /// <summary>
    /// Process torrents at high progress that are stale: delete if last_activity exceeds MaxETA.
    /// If MaxETA not set or torrent is still active, skip.
    /// </summary>
    private async Task ProcessPercentageThresholdAsync(
        TorrentInfo torrent, int maxEta,
        QBittorrentClient client, TorrentProcessingStats stats, CancellationToken ct)
    {
        // qBitrr line 5284: only delete if maxEta > 0 AND last_activity is stale
        if (maxEta > 0 && torrent.LastActivity < DateTimeOffset.UtcNow.ToUnixTimeSeconds() - maxEta)
        {
            var hnrAllows = _seedingService == null || await _seedingService.HnrAllowsDeleteAsync(torrent, "stale high-percentage deletion", ct);
            if (hnrAllows)
            {
                _logger.LogWarning("Deleting stale high-percentage torrent: [{Name}] | Progress[{Progress:P1}] | Hash[{Hash}]",
                    torrent.Name, torrent.Progress, torrent.Hash);
                await client.DeleteTorrentsAsync(new List<string> { torrent.Hash }, deleteFiles: true, ct);
                stats.Failed++;
            }
        }
        else
        {
            _logger.LogTrace("Skipping high-percentage torrent (still active): [{Name}] | Progress[{Progress:P1}]",
                torrent.Name, torrent.Progress);
        }
    }

    // ========================================================================================
    // STATE HELPERS
    // ========================================================================================

    /// <summary>
    /// Check for missing files state (qBitrr: _is_missing_files_torrent — arss.py:931-941).
    /// Returns true for MissingFiles state or Error state with "missing" in the raw state string.
    /// </summary>
    private static bool IsMissingFilesTorrent(TorrentInfo torrent, TorrentState state)
    {
        if (state == TorrentState.MissingFiles)
            return true;
        if (state == TorrentState.Error)
        {
            var raw = torrent.State;
            if (!string.IsNullOrEmpty(raw) &&
                (raw.Equals("missingFiles", StringComparison.OrdinalIgnoreCase) ||
                 raw.Contains("missing", StringComparison.OrdinalIgnoreCase)))
                return true;
        }
        return false;
    }

    /// <summary>
    /// Check for ignored states (qBitrr: is_ignored_state — arss.py:918-928).
    /// These states are skipped entirely during torrent processing.
    /// </summary>
    private static bool IsIgnoredState(TorrentState state)
    {
        return state is TorrentState.ForcedDownloading
            or TorrentState.ForcedUploading
            or TorrentState.CheckingUploading
            or TorrentState.CheckingDownloading
            or TorrentState.CheckingResumeData
            or TorrentState.Allocating
            or TorrentState.Moving
            or TorrentState.QueuedDownloading;
    }

    /// <summary>
    /// Returns true if the state is an active downloading state
    /// (Downloading, StalledDownloading, ForcedDownloading, MetadataDownloading).
    /// </summary>
    private static bool IsActiveDownloadingState(TorrentState state)
    {
        return state is TorrentState.Downloading
            or TorrentState.StalledDownloading
            or TorrentState.ForcedDownloading
            or TorrentState.MetadataDownloading;
    }

    /// <summary>
    /// Returns true if the state is an uploading/seeding state.
    /// </summary>
    private static bool IsUploadingState(TorrentState state)
    {
        return state is TorrentState.Uploading
            or TorrentState.StalledUploading
            or TorrentState.QueuedUploading
            or TorrentState.PausedUploading
            or TorrentState.ForcedUploading;
    }

    /// <summary>
    /// Returns true if the torrent is in a complete (upload/seeding) state.
    /// Matches qBitrr's is_complete_state: UPLOADING, STALLED_UPLOAD, PAUSED_UPLOAD, QUEUED_UPLOAD, FORCED_UPLOAD, CHECKING_UPLOAD.
    /// </summary>
    private static bool IsCompleteState(TorrentState state)
    {
        return state is TorrentState.Uploading
            or TorrentState.StalledUploading
            or TorrentState.PausedUploading
            or TorrentState.QueuedUploading
            or TorrentState.ForcedUploading
            or TorrentState.CheckingUploading;
    }

    /// <summary>
    /// Gets the removal mode for a torrent from its category seeding config.
    /// -1 = Never, 1 = Ratio, 2 = Time, 3 = OR, 4 = AND.
    /// </summary>
    private int GetRemoveMode(TorrentInfo torrent, ArrInstanceConfig? arrCfg)
    {
        // Check qBit instance's CategorySeeding config first
        var qbitCfg = _config.QBitInstances.GetValueOrDefault(torrent.QBitInstanceName);
        if (qbitCfg?.CategorySeeding?.RemoveTorrent != null)
            return qbitCfg.CategorySeeding.RemoveTorrent;

        // Then check Arr instance's SeedingMode
        if (arrCfg?.Torrent.SeedingMode?.RemoveTorrent != null)
            return arrCfg.Torrent.SeedingMode.RemoveTorrent;

        return -1; // Default: never remove
    }

    /// <summary>
    /// Check if a torrent is marked as imported in the database.
    /// </summary>
    private async Task<bool> IsImportedInDatabaseAsync(string hash, CancellationToken ct)
    {
        var entry = await _dbContext.TorrentLibrary
            .AsNoTracking()
            .FirstOrDefaultAsync(t => t.Hash == hash, ct);
        return entry?.Imported == true;
    }

    /// <summary>
    /// Update statistics based on torrent state.
    /// </summary>
    private static void UpdateStats(TorrentState state, TorrentProcessingStats stats)
    {
        switch (state)
        {
            case TorrentState.Downloading:
            case TorrentState.StalledDownloading:
            case TorrentState.ForcedDownloading:
            case TorrentState.MetadataDownloading:
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
    }

    // ========================================================================================
    // TAG HELPERS
    // ========================================================================================

    /// <summary>
    /// Add a tag to a torrent (respects tagless mode).
    /// </summary>
    private async Task AddTagAsync(TorrentInfo torrent, QBittorrentClient client, string tag, CancellationToken ct)
    {
        if (_config.Settings.Tagless)
        {
            var entry = await _dbContext.TorrentLibrary
                .FirstOrDefaultAsync(t => t.Hash == torrent.Hash && t.QbitInstance == torrent.QBitInstanceName, ct);
            if (entry != null)
            {
                switch (tag)
                {
                    case AllowedSeedingTag: entry.AllowedSeeding = true; break;
                    case AllowedStalledTag: entry.AllowedStalled = true; break;
                    case FreeSpacePausedTag: entry.FreeSpacePaused = true; break;
                }
                await _dbContext.SaveChangesAsync(ct);
            }
        }
        else
        {
            await client.AddTagsAsync(new List<string> { torrent.Hash }, new List<string> { tag }, ct);
        }
    }

    /// <summary>
    /// Remove a tag from a torrent (respects tagless mode).
    /// </summary>
    private async Task RemoveTagAsync(TorrentInfo torrent, QBittorrentClient client, string tag, CancellationToken ct)
    {
        if (_config.Settings.Tagless)
        {
            var entry = await _dbContext.TorrentLibrary
                .FirstOrDefaultAsync(t => t.Hash == torrent.Hash && t.QbitInstance == torrent.QBitInstanceName, ct);
            if (entry != null)
            {
                switch (tag)
                {
                    case AllowedSeedingTag: entry.AllowedSeeding = false; break;
                    case AllowedStalledTag: entry.AllowedStalled = false; break;
                    case FreeSpacePausedTag: entry.FreeSpacePaused = false; break;
                }
                await _dbContext.SaveChangesAsync(ct);
            }
        }
        else
        {
            await client.RemoveTagsAsync(new List<string> { torrent.Hash }, new List<string> { tag }, ct);
        }
    }

    // ========================================================================================
    // EXISTING HELPERS (preserved)
    // ========================================================================================

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
                AllowedSeedingTag => dbEntry.AllowedSeeding,
                AllowedStalledTag => dbEntry.AllowedStalled,
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

            if (!existingTags.Contains(AllowedStalledTag, StringComparer.OrdinalIgnoreCase))
                tagsToCreate.Add(AllowedStalledTag);

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
            .AnyAsync(t => t.Hash == torrent.Hash && t.QbitInstance == torrent.QBitInstanceName, cancellationToken);

        if (!exists)
        {
            var entry = new TorrentLibrary
            {
                Hash = torrent.Hash,
                Category = category,
                QbitInstance = torrent.QBitInstanceName,
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
            var s when s.Contains("forcedmetadl") => TorrentState.ForcedMetaDL,
            var s when s == "metadl" || s.Contains("metadl") && !s.Contains("forced") => TorrentState.MetadataDownloading,
            var s when s.Contains("checkingresumedata") => TorrentState.CheckingResumeData,
            var s when s.Contains("allocating") => TorrentState.Allocating,
            var s when s.Contains("moving") => TorrentState.Moving,
            var s when s.Contains("stalleddownload") || s == "stalleddl" => TorrentState.StalledDownloading,
            var s when s.Contains("stalledupload") || s == "stalledup" => TorrentState.StalledUploading,
            var s when s.Contains("forceddownload") || s == "forceddl" => TorrentState.ForcedDownloading,
            var s when s.Contains("forcedup") || s == "forcedup" => TorrentState.ForcedUploading,
            var s when s.Contains("pauseddownload") || s == "pauseddl" || s.Contains("stoppeddownload") || s == "stoppeddl" => TorrentState.PausedDownloading,
            var s when s.Contains("pausedupload") || s == "pausedup" || s.Contains("stoppedupload") || s == "stoppedup" => TorrentState.PausedUploading,
            var s when s.Contains("queueddownload") || s == "queueddl" => TorrentState.QueuedDownloading,
            var s when s.Contains("queuedupload") || s == "queuedup" => TorrentState.QueuedUploading,
            var s when s.Contains("checkingupload") || s == "checkingup" => TorrentState.CheckingUploading,
            var s when s.Contains("checkingdownload") || s == "checkingdl" => TorrentState.CheckingDownloading,
            var s when s.Contains("downloading") => TorrentState.Downloading,
            var s when s.Contains("uploading") => TorrentState.Uploading,
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

    /// <summary>
    /// Add qBitrr-allowed_stalled tag (or set DB column in tagless mode).
    /// </summary>
    private async Task AddStalledTagAsync(TorrentInfo torrent, QBittorrentClient client, CancellationToken ct)
    {
        if (_config.Settings.Tagless)
        {
            var entry = await _dbContext.TorrentLibrary
                .FirstOrDefaultAsync(t => t.Hash == torrent.Hash && t.QbitInstance == torrent.QBitInstanceName, ct);
            if (entry != null)
            {
                entry.AllowedStalled = true;
                await _dbContext.SaveChangesAsync(ct);
            }
        }
        else
        {
            await client.AddTagsAsync(new List<string> { torrent.Hash }, new List<string> { AllowedStalledTag }, ct);
        }
    }

    /// <summary>
    /// Remove qBitrr-allowed_stalled tag (or clear DB column in tagless mode).
    /// </summary>
    private async Task RemoveStalledTagAsync(TorrentInfo torrent, QBittorrentClient client, CancellationToken ct)
    {
        if (_config.Settings.Tagless)
        {
            var entry = await _dbContext.TorrentLibrary
                .FirstOrDefaultAsync(t => t.Hash == torrent.Hash && t.QbitInstance == torrent.QBitInstanceName, ct);
            if (entry != null)
            {
                entry.AllowedStalled = false;
                await _dbContext.SaveChangesAsync(ct);
            }
        }
        else
        {
            await client.RemoveTagsAsync(new List<string> { torrent.Hash }, new List<string> { AllowedStalledTag }, ct);
        }
    }
}

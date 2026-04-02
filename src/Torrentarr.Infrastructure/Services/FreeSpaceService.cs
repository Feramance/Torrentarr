using System.Linq;
using Torrentarr.Core.Configuration;
using Torrentarr.Core.Models;
using Torrentarr.Core.Services;
using Torrentarr.Infrastructure.ApiClients.QBittorrent;
using Torrentarr.Infrastructure.Database;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Torrentarr.Infrastructure.Services;

/// <summary>
/// Service for managing disk space and preventing download issues.
/// Checks ALL qBit instances and handles torrents by added date across all instances at once.
/// Each qBit instance's seeding/download-path settings are applied only to that instance's torrents.
/// </summary>
public class FreeSpaceService : IFreeSpaceService
{
    private const string FreeSpacePausedTag = "qBitrr-free_space_paused";
    private const string AllowedSeedingTag = "qBitrr-allowed_seeding";

    private readonly ILogger<FreeSpaceService> _logger;
    private readonly TorrentarrConfig _config;
    private readonly QBittorrentConnectionManager _qbitManager;
    private readonly TorrentarrDbContext _dbContext;
    private long _currentFreeSpace;
    private long _minFreeSpaceBytes;

    public FreeSpaceService(
        ILogger<FreeSpaceService> logger,
        TorrentarrConfig config,
        QBittorrentConnectionManager qbitManager,
        TorrentarrDbContext dbContext)
    {
        _logger = logger;
        _config = config;
        _qbitManager = qbitManager;
        _dbContext = dbContext;
        // §FreeSpace parity: prefer Settings.FreeSpace (qBitrr string format) over FreeSpaceThresholdGB
        var freeSpaceBytes = ParseFreeSpaceString(config.Settings.FreeSpace);
        _minFreeSpaceBytes = freeSpaceBytes > 0
            ? freeSpaceBytes
            : (long)(_config.Settings.FreeSpaceThresholdGB ?? 10) * 1024L * 1024L * 1024L;
    }

    /// <summary>
    /// Parse qBitrr FreeSpace config string: "-1" → disabled (-1), "10G" → 10 GiB, "500M" → 500 MiB, "1024K" → 1 KiB, raw number → bytes.
    /// </summary>
    private static long ParseFreeSpaceString(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Trim() == "-1") return -1;
        var v = value.Trim().ToUpperInvariant();
        try
        {
            if (v.EndsWith("G")) return long.Parse(v[..^1]) * 1024L * 1024L * 1024L;
            if (v.EndsWith("M")) return long.Parse(v[..^1]) * 1024L * 1024L;
            if (v.EndsWith("K")) return long.Parse(v[..^1]) * 1024L;
            return long.Parse(v);
        }
        catch { return -1; }
    }

    public async Task<bool> HasEnoughFreeSpaceAsync(long requiredBytes, CancellationToken cancellationToken = default)
    {
        var stats = await GetFreeSpaceStatsAsync(cancellationToken);

        if (stats.FreeBytes < requiredBytes)
        {
            _logger.LogWarning("Insufficient free space: {Free} bytes available, {Required} bytes required",
                stats.FreeBytes, requiredBytes);
            return false;
        }

        return true;
    }

    /// <summary>
    /// Returns stats for the most constrained drive across all qBit instances.
    /// </summary>
    public async Task<FreeSpaceStats> GetFreeSpaceStatsAsync(CancellationToken cancellationToken = default)
    {
        _logger.LogTrace("Getting free space stats for all qBit instances");

        // Use _minFreeSpaceBytes (parsed from Settings.FreeSpace or FreeSpaceThresholdGB in constructor)
        var thresholdBytes = _minFreeSpaceBytes > 0 ? _minFreeSpaceBytes
            : (long)(_config.Settings.FreeSpaceThresholdGB ?? 10) * 1024L * 1024L * 1024L;
        FreeSpaceStats? mostConstrained = null;

        // §FreeSpaceFolder: if configured, add it to the set of paths to check
        var pathsToCheck = new List<(string instanceName, string path)>();
        if (!string.IsNullOrWhiteSpace(_config.Settings.FreeSpaceFolder))
            pathsToCheck.Add(("FreeSpaceFolder", _config.Settings.FreeSpaceFolder));

        _logger.LogTrace("Checking {Count} qBit instances for free space", _config.QBitInstances.Count);

        foreach (var (instanceName, qbitConfig) in _config.QBitInstances)
        {
            if (qbitConfig.Disabled)
            {
                _logger.LogTrace("FreeSpace: [{Instance}] Skipping disabled instance", instanceName);
                continue;
            }

            var savePath = qbitConfig.DownloadPath ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            pathsToCheck.Add((instanceName, savePath));
        }

        foreach (var (instanceName, savePath) in pathsToCheck)
        {
            var stats = new FreeSpaceStats { Path = savePath };

            _logger.LogTrace("FreeSpace: [{Instance}] Checking path: {Path}", instanceName, savePath);

            DriveInfo? drive = null;
            if (OperatingSystem.IsWindows())
            {
                var driveLetter = Path.GetPathRoot(savePath);
                if (!string.IsNullOrEmpty(driveLetter))
                    drive = DriveInfo.GetDrives().FirstOrDefault(d => d.Name == driveLetter);
            }
            else
            {
                drive = DriveInfo.GetDrives()
                    .Where(d => d.IsReady && d.DriveType == DriveType.Fixed)
                    .OrderByDescending(d => d.Name.Length)
                    .FirstOrDefault(d => savePath.StartsWith(d.Name));
            }

            if (drive != null && drive.IsReady)
            {
                stats.TotalBytes = drive.TotalSize;
                stats.FreeBytes = drive.AvailableFreeSpace;
                stats.UsedBytes = stats.TotalBytes - stats.FreeBytes;
                stats.FreePercentage = (double)stats.FreeBytes / stats.TotalBytes * 100;
                stats.ThresholdBytes = thresholdBytes;
                stats.BelowThreshold = stats.FreeBytes < thresholdBytes;

                _logger.LogTrace("FreeSpace: [{Instance}] Drive info: Total={Total}, Free={Free}, Used={Used}",
                    instanceName, FormatBytes(stats.TotalBytes), FormatBytes(stats.FreeBytes), FormatBytes(stats.UsedBytes));

                _logger.LogTrace("FreeSpace: [{Instance}] Free space: {Free}GB / {Total}GB ({Percent:F1}%)",
                    instanceName,
                    stats.FreeBytes / 1024.0 / 1024.0 / 1024.0,
                    stats.TotalBytes / 1024.0 / 1024.0 / 1024.0,
                    stats.FreePercentage);

                // Track the most constrained (lowest free bytes)
                if (mostConstrained == null || stats.FreeBytes < mostConstrained.FreeBytes)
                {
                    _logger.LogTrace("FreeSpace: [{Instance}] New most constrained drive: {Free}GB", instanceName, FormatBytes(stats.FreeBytes));
                    mostConstrained = stats;
                }
            }
            else
            {
                _logger.LogWarning("FreeSpace: [{Instance}] Unable to determine drive info for path: {Path}", instanceName, savePath);
            }
        }

        await Task.CompletedTask;

        if (mostConstrained != null)
        {
            _logger.LogTrace("Most constrained: {Path} with {Free}GB free ({Percent:F1}%)",
                mostConstrained.Path, FormatBytes(mostConstrained.FreeBytes), mostConstrained.FreePercentage);
        }

        return mostConstrained ?? new FreeSpaceStats { ThresholdBytes = thresholdBytes };
    }

    public async Task<bool> PauseDownloadsIfLowSpaceAsync(CancellationToken cancellationToken = default)
    {
        var stats = await GetFreeSpaceStatsAsync(cancellationToken);
        if (!stats.BelowThreshold) return false;

        var paused = false;
        foreach (var (instanceName, client) in _qbitManager.GetAllClients())
        {
            try
            {
                var torrents = await client.GetTorrentsAsync(ct: cancellationToken);
                var downloading = torrents.Where(t =>
                    t.State.Contains("downloading", StringComparison.OrdinalIgnoreCase) ||
                    t.State.Contains("stalledDL", StringComparison.OrdinalIgnoreCase)
                ).ToList();

                foreach (var torrent in downloading)
                {
                    try
                    {
                        await SetFreeSpacePausedTagAsync(client, torrent.Hash, true, cancellationToken);
                        await client.PauseTorrentAsync(torrent.Hash, cancellationToken);
                        _logger.LogInformation("FreeSpace: [{Instance}] Paused torrent due to low space: {Name}", instanceName, torrent.Name);
                        paused = true;
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "FreeSpace: [{Instance}] Failed to pause torrent {Hash}", instanceName, torrent.Hash);
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "FreeSpace: [{Instance}] Error pausing downloads due to low space", instanceName);
            }
        }

        return paused;
    }

    public async Task<bool> ResumeDownloadsIfSpaceAvailableAsync(CancellationToken cancellationToken = default)
    {
        var stats = await GetFreeSpaceStatsAsync(cancellationToken);
        if (stats.BelowThreshold) return false;

        var resumed = false;
        foreach (var (instanceName, client) in _qbitManager.GetAllClients())
        {
            try
            {
                var torrents = await client.GetTorrentsAsync(ct: cancellationToken);
                var paused = torrents.Where(t =>
                    t.State.Contains("pausedDL", StringComparison.OrdinalIgnoreCase)
                ).ToList();

                foreach (var torrent in paused)
                {
                    try
                    {
                        await SetFreeSpacePausedTagAsync(client, torrent.Hash, false, cancellationToken);
                        await client.ResumeTorrentAsync(torrent.Hash, cancellationToken);
                        _logger.LogInformation("FreeSpace: [{Instance}] Resumed torrent: {Name}", instanceName, torrent.Name);
                        resumed = true;
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "FreeSpace: [{Instance}] Failed to resume torrent {Hash}", instanceName, torrent.Hash);
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "FreeSpace: [{Instance}] Error resuming downloads", instanceName);
            }
        }

        return resumed;
    }

    /// <summary>
    /// Process torrents from ALL qBit instances for the given category, sorted by added date globally.
    /// Each torrent uses its own qBit instance's client for pause/resume/tag operations.
    /// </summary>
    public async Task ProcessTorrentsForSpaceAsync(string category, CancellationToken cancellationToken = default)
    {
        _logger.LogTrace("Starting free space processing for category {Category}", category);

        // Gather torrents from all qBit instances, stamping instance name
        var allTorrents = new List<(string instanceName, QBittorrentClient client, TorrentInfo torrent)>();
        var clientCount = _qbitManager.GetAllClients().Count;
        _logger.LogTrace("Fetching torrents from {Count} qBit instances", clientCount);

        foreach (var (instanceName, client) in _qbitManager.GetAllClients())
        {
            try
            {
                _logger.LogTrace("FreeSpace: [{Instance}] Ensuring tags exist", instanceName);
                await EnsureTagsExistAsync(client, cancellationToken);

                _logger.LogTrace("FreeSpace: [{Instance}] Fetching torrents for category {Category}", instanceName, category);
                var torrents = await client.GetTorrentsAsync(category, cancellationToken);
                _logger.LogTrace("FreeSpace: [{Instance}] Found {Count} torrents", instanceName, torrents.Count);

                foreach (var t in torrents)
                {
                    t.QBitInstanceName = instanceName;
                    allTorrents.Add((instanceName, client, t));
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "FreeSpace: [{Instance}] Error fetching torrents for space processing", instanceName);
            }
        }

        _logger.LogTrace("Total torrents gathered: {Count}", allTorrents.Count);

        if (allTorrents.Count == 0)
        {
            _logger.LogTrace("No torrents to process for free space");
            return;
        }

        // Get current free space (most constrained drive across all instances)
        _logger.LogTrace("Getting free space stats");
        var stats = await GetFreeSpaceStatsAsync(cancellationToken);
        _currentFreeSpace = stats.FreeBytes - _minFreeSpaceBytes;

        _logger.LogTrace(
            "Processing torrents for space across {Count} qBit instance(s) | Available: {Available} | Threshold: {Threshold} | Usable: {Usable}",
            _qbitManager.GetAllClients().Count,
            FormatBytes(stats.FreeBytes),
            FormatBytes(_minFreeSpaceBytes),
            FormatBytes(_currentFreeSpace));

        _logger.LogTrace("Free space: Available={Available}, Threshold={Threshold}, Usable={Usable}",
            FormatBytes(stats.FreeBytes), FormatBytes(_minFreeSpaceBytes), FormatBytes(_currentFreeSpace));

        // Sort globally by added date — older torrents get priority to keep downloading
        _logger.LogTrace("Sorting {Count} torrents by added date", allTorrents.Count);
        var sorted = allTorrents.OrderBy(x => x.torrent.AddedOn).ToList();

        _logger.LogTrace("Processing {Count} torrents in order of oldest first", sorted.Count);
        var processedCount = 0;
        foreach (var (instanceName, client, torrent) in sorted)
        {
            try
            {
                _logger.LogTrace("FreeSpace: [{Instance}] Processing torrent {Name} (Added: {Added})",
                    instanceName, torrent.Name, torrent.AddedOn);
                await ProcessSingleTorrentSpaceAsync(instanceName, client, torrent, cancellationToken);
                processedCount++;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "FreeSpace: [{Instance}] Error processing torrent {Hash} for space", instanceName, torrent.Hash);
            }
        }

        _logger.LogTrace("Free space processing complete. Processed {Processed} of {Total} torrents", processedCount, sorted.Count);
    }

    private async Task ProcessSingleTorrentSpaceAsync(
        string instanceName,
        QBittorrentClient client,
        TorrentInfo torrent,
        CancellationToken cancellationToken)
    {
        var isDownloading = IsDownloadingState(torrent.State);
        var isPausedDownload = torrent.State.Contains("pausedDL", StringComparison.OrdinalIgnoreCase);
        var hasFreeSpaceTag = HasTag(torrent, FreeSpacePausedTag);

        _logger.LogTrace("FreeSpace: [{Name}] | State[{State}] | Progress[{Progress:P1}] | Size[{Size}] | AmountLeft[{AmountLeft}] | HasTag[{HasTag}] | Hash[{Hash}]",
            torrent.Name, torrent.State, torrent.Progress, FormatBytes(torrent.Size), FormatBytes(torrent.AmountLeft), hasFreeSpaceTag, torrent.Hash);

        _logger.LogTrace("FreeSpace: [{Instance}] Torrent {Name}: State={State}, IsDownloading={IsDl}, IsPausedDownload={IsPausedDl}, HasFreeSpaceTag={HasTag}",
            instanceName, torrent.Name, torrent.State, isDownloading, isPausedDownload, hasFreeSpaceTag);

        if (isDownloading || (isPausedDownload && hasFreeSpaceTag))
        {
            var freeSpaceTest = _currentFreeSpace - torrent.AmountLeft;

            _logger.LogTrace(
                "FreeSpace: [{Instance}] Evaluating torrent {Name}: Current space: {Current} | Space after: {After} | Remaining: {Remaining} | Would be: {WouldBe}",
                instanceName, torrent.Name,
                FormatBytes(_currentFreeSpace + _minFreeSpaceBytes),
                FormatBytes(freeSpaceTest + _minFreeSpaceBytes),
                FormatBytes(torrent.AmountLeft),
                freeSpaceTest >= 0 ? "positive" : "negative");

            if (!isPausedDownload && freeSpaceTest < 0)
            {
                _logger.LogInformation(
                    "FreeSpace: [{Instance}] Pausing torrent [{Name}] | Available[{Available}] | Needed[{Needed}] | Deficit[{Deficit}] | Progress[{Progress:P1}] | Hash[{Hash}]",
                    instanceName, torrent.Name,
                    FormatBytes(_currentFreeSpace + _minFreeSpaceBytes),
                    FormatBytes(torrent.AmountLeft),
                    FormatBytes(-freeSpaceTest),
                    torrent.Progress,
                    torrent.Hash);

                _logger.LogTrace("FreeSpace: [{Instance}] Setting FreeSpacePaused on torrent {Hash}", instanceName, torrent.Hash);
                await SetFreeSpacePausedTagAsync(client, torrent.Hash, true, cancellationToken);
                if (!_config.Settings.Tagless)
                    await client.RemoveTagsAsync(new List<string> { torrent.Hash }, new List<string> { AllowedSeedingTag }, cancellationToken);
                _logger.LogTrace("FreeSpace: [{Instance}] Pausing torrent {Hash}", instanceName, torrent.Hash);
                await client.PauseTorrentAsync(torrent.Hash, cancellationToken);
            }
            else if (isPausedDownload && freeSpaceTest < 0)
            {
                _logger.LogInformation(
                    "FreeSpace: [{Instance}] Keeping paused [{Name}] | Available[{Available}] | Needed[{Needed}] | Deficit[{Deficit}] | Hash[{Hash}]",
                    instanceName, torrent.Name,
                    FormatBytes(_currentFreeSpace + _minFreeSpaceBytes),
                    FormatBytes(torrent.AmountLeft),
                    FormatBytes(-freeSpaceTest),
                    torrent.Hash);

                _logger.LogTrace("FreeSpace: [{Instance}] Maintaining FreeSpacePaused on torrent {Hash}", instanceName, torrent.Hash);
                await SetFreeSpacePausedTagAsync(client, torrent.Hash, true, cancellationToken);
                if (!_config.Settings.Tagless)
                    await client.RemoveTagsAsync(new List<string> { torrent.Hash }, new List<string> { AllowedSeedingTag }, cancellationToken);
            }
            else if (!isPausedDownload && freeSpaceTest >= 0)
            {
                _logger.LogTrace(
                    "FreeSpace: [{Instance}] Continuing download [{Name}] | Available[{Available}] | SpaceAfter[{SpaceAfter}] | Hash[{Hash}]",
                    instanceName, torrent.Name,
                    FormatBytes(_currentFreeSpace + _minFreeSpaceBytes),
                    FormatBytes(freeSpaceTest + _minFreeSpaceBytes),
                    torrent.Hash);

                _currentFreeSpace = freeSpaceTest;
                _logger.LogTrace("FreeSpace: [{Instance}] Clearing FreeSpacePaused on torrent {Hash}", instanceName, torrent.Hash);
                await SetFreeSpacePausedTagAsync(client, torrent.Hash, false, cancellationToken);
            }
            else if (isPausedDownload && freeSpaceTest >= 0)
            {
                _logger.LogInformation(
                    "FreeSpace: [{Instance}] Resuming download [{Name}] | Available[{Available}] | SpaceAfter[{SpaceAfter}] | Hash[{Hash}]",
                    instanceName, torrent.Name,
                    FormatBytes(_currentFreeSpace + _minFreeSpaceBytes),
                    FormatBytes(freeSpaceTest + _minFreeSpaceBytes),
                    torrent.Hash);

                _currentFreeSpace = freeSpaceTest;
                _logger.LogTrace("FreeSpace: [{Instance}] Clearing FreeSpacePaused on torrent {Hash}", instanceName, torrent.Hash);
                await SetFreeSpacePausedTagAsync(client, torrent.Hash, false, cancellationToken);
                if (!_config.Settings.Tagless)
                    await client.AddTagsAsync(new List<string> { torrent.Hash }, new List<string> { AllowedSeedingTag }, cancellationToken);
                _logger.LogTrace("FreeSpace: [{Instance}] Resuming torrent {Hash}", instanceName, torrent.Hash);
                await client.ResumeTorrentAsync(torrent.Hash, cancellationToken);
            }
        }
        else if (!isDownloading && hasFreeSpaceTag)
        {
            _logger.LogInformation(
                "FreeSpace: [{Instance}] Completed, removing tag [{Name}] | Available[{Available}] | Hash[{Hash}]",
                instanceName, torrent.Name,
                FormatBytes(_currentFreeSpace + _minFreeSpaceBytes),
                torrent.Hash);

            _logger.LogTrace("FreeSpace: [{Instance}] Clearing FreeSpacePaused on completed torrent {Hash}", instanceName, torrent.Hash);
            await SetFreeSpacePausedTagAsync(client, torrent.Hash, false, cancellationToken);
        }
        else
        {
            _logger.LogTrace("FreeSpace: [{Instance}] No action needed for torrent {Name}", instanceName, torrent.Name);
        }
    }

    private bool IsDownloadingState(string state)
    {
        return state.Contains("downloading", StringComparison.OrdinalIgnoreCase) ||
               state.Contains("stalledDL", StringComparison.OrdinalIgnoreCase) ||
               state.Contains("metaDL", StringComparison.OrdinalIgnoreCase);
    }

    private bool HasTag(TorrentInfo torrent, string tag)
    {
        // §1.6 Tagless: FreeSpacePaused → DB column
        if (_config.Settings.Tagless)
        {
            var dbEntry = _dbContext.TorrentLibrary.AsNoTracking()
                .FirstOrDefault(t => t.Hash == torrent.Hash);
            return dbEntry != null && tag == FreeSpacePausedTag && dbEntry.FreeSpacePaused;
        }

        if (string.IsNullOrEmpty(torrent.Tags)) return false;
        return torrent.Tags.Split(',', StringSplitOptions.RemoveEmptyEntries)
            .Select(t => t.Trim())
            .Contains(tag, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>§1.6: Set or clear FreeSpacePaused — uses qBit tag or DB column based on Tagless setting.</summary>
    private async Task SetFreeSpacePausedTagAsync(QBittorrentClient client, string hash, bool paused, CancellationToken ct)
    {
        if (_config.Settings.Tagless)
        {
            await _dbContext.TorrentLibrary
                .Where(t => t.Hash == hash)
                .ExecuteUpdateAsync(s => s.SetProperty(t => t.FreeSpacePaused, paused), ct);
        }
        else if (paused)
        {
            await client.AddTagsAsync(new List<string> { hash }, new List<string> { FreeSpacePausedTag }, ct);
        }
        else
        {
            await client.RemoveTagsAsync(new List<string> { hash }, new List<string> { FreeSpacePausedTag }, ct);
        }
    }

    private async Task EnsureTagsExistAsync(QBittorrentClient client, CancellationToken cancellationToken)
    {
        if (_config.Settings.Tagless) return; // §1.6: no tags in Tagless mode
        try
        {
            var existingTags = await client.GetTagsAsync(cancellationToken);
            var tagsToCreate = new List<string>();

            if (!existingTags.Contains(FreeSpacePausedTag, StringComparer.OrdinalIgnoreCase))
                tagsToCreate.Add(FreeSpacePausedTag);
            if (!existingTags.Contains(AllowedSeedingTag, StringComparer.OrdinalIgnoreCase))
                tagsToCreate.Add(AllowedSeedingTag);

            if (tagsToCreate.Count > 0)
                await client.CreateTagsAsync(tagsToCreate, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to ensure tags exist");
        }
    }

    private static string FormatBytes(long bytes)
    {
        string[] sizes = { "B", "KB", "MB", "GB", "TB" };
        int order = 0;
        double size = bytes;
        while (size >= 1024 && order < sizes.Length - 1) { order++; size /= 1024; }
        return $"{size:0.##} {sizes[order]}";
    }

    /// <inheritdoc />
    public async Task<GlobalFreeSpacePassResult> ProcessGlobalManagedCategoriesHostPassAsync(CancellationToken cancellationToken = default)
    {
        var freeSpaceCfg = ParseFreeSpaceString(_config.Settings.FreeSpace);
        if (freeSpaceCfg < 0 || !_config.Settings.AutoPauseResume)
            return new GlobalFreeSpacePassResult(0, false);

        var minBytes = freeSpaceCfg;
        var folder = GetResolvedFreeSpaceFolderPath();
        if (string.IsNullOrEmpty(folder))
        {
            _logger.LogWarning("FreeSpace: No free space folder configured or folder doesn't exist");
            return new GlobalFreeSpacePassResult(0, false);
        }

        _logger.LogInformation("FreeSpace: Starting FreeSpace manager check");
        _logger.LogInformation("FreeSpace: Using folder {Folder} for space monitoring", folder);

        var managedCategories = _config.BuildManagedCategoriesSet();
        long currentFreeSpace;
        try
        {
            var driveInfo = new DriveInfo(folder);
            currentFreeSpace = driveInfo.AvailableFreeSpace - minBytes;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "FreeSpace: Failed to read drive for folder {Folder}", folder);
            return new GlobalFreeSpacePassResult(0, false);
        }

        var allTorrents = new List<(QBittorrentClient client, TorrentInfo torrent)>();
        foreach (var (_, client) in _qbitManager.GetAllClients())
        {
            foreach (var category in managedCategories)
            {
                var torrents = await client.GetTorrentsAsync(category, cancellationToken);
                allTorrents.AddRange(torrents.Select(t => (client, t)));
            }
        }

        int pausedCount;
        int[]? pausedCountRef = null;
        if (!_config.Settings.Tagless)
        {
            pausedCount = allTorrents.Count(t => t.torrent.Tags?.Contains(FreeSpacePausedTag) == true);
            pausedCountRef = new int[] { pausedCount };
        }
        else
            pausedCount = 0;

        foreach (var (client, torrent) in allTorrents.OrderBy(x => x.torrent.AddedOn))
        {
            currentFreeSpace = await ProcessSingleTorrentSpaceHostOrchestratorStyleAsync(
                client, torrent, currentFreeSpace, minBytes, pausedCountRef, cancellationToken);
        }

        if (_config.Settings.Tagless)
            pausedCount = await _dbContext.TorrentLibrary.CountAsync(t => t.FreeSpacePaused, cancellationToken);
        else if (pausedCountRef != null)
            pausedCount = pausedCountRef[0];

        return new GlobalFreeSpacePassResult(pausedCount, minBytes > 0 && !string.IsNullOrEmpty(folder));
    }

    private string? GetResolvedFreeSpaceFolderPath()
    {
        if (!string.IsNullOrEmpty(_config.Settings.FreeSpaceFolder) && _config.Settings.FreeSpaceFolder != "CHANGE_ME")
        {
            if (Directory.Exists(_config.Settings.FreeSpaceFolder))
                return _config.Settings.FreeSpaceFolder;
        }
        if (!string.IsNullOrEmpty(_config.Settings.CompletedDownloadFolder) && _config.Settings.CompletedDownloadFolder != "CHANGE_ME")
        {
            if (Directory.Exists(_config.Settings.CompletedDownloadFolder))
                return _config.Settings.CompletedDownloadFolder;
        }
        return "/config";
    }

    /// <summary>Matches former Host <c>ProcessSingleTorrentSpaceAsync</c> (downloading = DL + stalledDL only, not metaDL).</summary>
    private async Task<long> ProcessSingleTorrentSpaceHostOrchestratorStyleAsync(
        QBittorrentClient client,
        TorrentInfo torrent,
        long currentFreeSpace,
        long minFreeSpaceBytes,
        int[]? pausedCountRef,
        CancellationToken cancellationToken)
    {
        var tagless = _config.Settings.Tagless;

        var isDownloading = torrent.State.Contains("downloading", StringComparison.OrdinalIgnoreCase) ||
                           torrent.State.Contains("stalledDL", StringComparison.OrdinalIgnoreCase);
        var isPausedDownload = torrent.State.Contains("pausedDL", StringComparison.OrdinalIgnoreCase);

        bool hasFreeSpaceTag;
        if (tagless)
        {
            var dbEntry = await _dbContext.TorrentLibrary.AsNoTracking()
                .FirstOrDefaultAsync(t => t.Hash == torrent.Hash, cancellationToken);
            hasFreeSpaceTag = dbEntry?.FreeSpacePaused == true;
        }
        else
            hasFreeSpaceTag = torrent.Tags?.Contains(FreeSpacePausedTag, StringComparison.OrdinalIgnoreCase) == true;

        if (isDownloading || (isPausedDownload && hasFreeSpaceTag))
        {
            var freeSpaceTest = currentFreeSpace - torrent.AmountLeft;

            _logger.LogInformation(
                "FreeSpace: Evaluating torrent: {Name} | Current space: {Available} | Space after: {SpaceAfter} | Remaining: {Needed}",
                torrent.Name, FormatBytes(currentFreeSpace), FormatBytes(freeSpaceTest), FormatBytes(torrent.AmountLeft));

            if (!isPausedDownload && freeSpaceTest < 0)
            {
                _logger.LogInformation(
                    "FreeSpace: Pausing download (insufficient space) | Torrent: {Name} | Available: {Available} | Needed: {Needed} | Deficit: {Deficit}",
                    torrent.Name, FormatBytes(currentFreeSpace), FormatBytes(torrent.AmountLeft), FormatBytes(-freeSpaceTest));
                if (tagless)
                    await _dbContext.TorrentLibrary.Where(t => t.Hash == torrent.Hash)
                        .ExecuteUpdateAsync(s => s.SetProperty(t => t.FreeSpacePaused, true), cancellationToken);
                else
                    await client.AddTagsAsync(new List<string> { torrent.Hash }, new List<string> { FreeSpacePausedTag }, cancellationToken);
                if (pausedCountRef != null) pausedCountRef[0]++;
                await client.PauseTorrentAsync(torrent.Hash, cancellationToken);
            }
            else if (isPausedDownload && freeSpaceTest >= 0)
            {
                _logger.LogInformation(
                    "FreeSpace: Resuming download (space available) | Torrent: {Name} | Available: {Available} | Space after: {SpaceAfter}",
                    torrent.Name, FormatBytes(currentFreeSpace), FormatBytes(freeSpaceTest));
                currentFreeSpace = freeSpaceTest;
                if (tagless)
                    await _dbContext.TorrentLibrary.Where(t => t.Hash == torrent.Hash)
                        .ExecuteUpdateAsync(s => s.SetProperty(t => t.FreeSpacePaused, false), cancellationToken);
                else
                    await client.RemoveTagsAsync(new List<string> { torrent.Hash }, new List<string> { FreeSpacePausedTag }, cancellationToken);
                if (pausedCountRef != null) pausedCountRef[0]--;
                await client.ResumeTorrentAsync(torrent.Hash, cancellationToken);
            }
            else if (isPausedDownload && freeSpaceTest < 0)
            {
                _logger.LogInformation(
                    "FreeSpace: Keeping paused (insufficient space) | Torrent: {Name} | Available: {Available} | Needed: {Needed} | Deficit: {Deficit}",
                    torrent.Name, FormatBytes(currentFreeSpace), FormatBytes(torrent.AmountLeft), FormatBytes(-freeSpaceTest));
            }
            else if (!isPausedDownload && freeSpaceTest >= 0)
            {
                _logger.LogInformation(
                    "FreeSpace: Continuing download (sufficient space) | Torrent: {Name} | Available: {Available} | Space after: {SpaceAfter}",
                    torrent.Name, FormatBytes(currentFreeSpace), FormatBytes(freeSpaceTest));
                currentFreeSpace = freeSpaceTest;
            }
        }
        else if (!isDownloading && hasFreeSpaceTag)
        {
            _logger.LogInformation(
                "FreeSpace: Torrent completed, removing free space tag | Torrent: {Name} | Available: {Available}",
                torrent.Name, FormatBytes(currentFreeSpace + minFreeSpaceBytes));
            if (tagless)
                await _dbContext.TorrentLibrary.Where(t => t.Hash == torrent.Hash)
                    .ExecuteUpdateAsync(s => s.SetProperty(t => t.FreeSpacePaused, false), cancellationToken);
            else
                await client.RemoveTagsAsync(new List<string> { torrent.Hash }, new List<string> { FreeSpacePausedTag }, cancellationToken);
            if (pausedCountRef != null) pausedCountRef[0]--;
        }

        return currentFreeSpace;
    }
}

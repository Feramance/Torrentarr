using Torrentarr.Core.Configuration;
using Torrentarr.Core.Interfaces;
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
    private readonly ITorrentClientRegistry _qbitManager;
    private readonly TorrentarrDbContext _dbContext;
    private long _currentFreeSpace;
    private long _minFreeSpaceBytes => FreeSpaceThreshold.Parse(_config.Settings.FreeSpace) is var bytes && bytes > 0
        ? bytes : (long)(_config.Settings.FreeSpaceThresholdGB ?? 10) * 1024L * 1024L * 1024L;

    public FreeSpaceService(
        ILogger<FreeSpaceService> logger,
        TorrentarrConfig config,
        ITorrentClientRegistry qbitManager,
        TorrentarrDbContext dbContext)
    {
        _logger = logger;
        _config = config;
        _qbitManager = qbitManager;
        _dbContext = dbContext;

    }

    /// <summary>
    /// Parse qBitrr FreeSpace config string: "-1" → disabled (-1), "10G" → 10 GiB, "500M" → 500 MiB, "1024K" → 1 KiB, raw number → bytes.
    /// </summary>
    private static long ParseFreeSpaceString(string? value) => FreeSpaceThreshold.Parse(value);

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

        _logger.LogTrace("Checking {Count} torrent clients for free space", _config.GetAllTorrentClients().Count);

        foreach (var (instanceName, qbitConfig) in _config.GetAllTorrentClients())
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
        if (!(await GetFreeSpaceStatsAsync(cancellationToken)).BelowThreshold) return false;
        var inventory = await FreeSpaceInventory.ReadAsync(_config, _qbitManager, _logger, cancellationToken);
        var paused = false;
        await inventory.ApplyAsync(inventory.Torrents, async (item, _) =>
        {
            if (!TorrentPolicyHelper.IsFreeSpaceDownloadState(item.Torrent.State)
                || TorrentPolicyHelper.IsPausedDownloadStateForFreeSpace(item.Torrent.State)) return;
            await SetFreeSpacePausedTagAsync(item.Client, item.Torrent, item.InstanceName, true, cancellationToken);
            await FreeSpaceInventory.PauseAndRollbackMarkerAsync(item.Client, item.Torrent.Hash,
                () => SetFreeSpacePausedTagAsync(item.Client, item.Torrent, item.InstanceName, false, CancellationToken.None), cancellationToken);
            paused = true;
        }, _logger, cancellationToken);
        return paused;
    }

    public async Task<bool> ResumeDownloadsIfSpaceAvailableAsync(CancellationToken cancellationToken = default)
    {
        var stats = await GetFreeSpaceStatsAsync(cancellationToken);
        if (stats.BelowThreshold) return false;
        var inventory = await FreeSpaceInventory.ReadAsync(_config, _qbitManager, _logger, cancellationToken);
        _currentFreeSpace = stats.FreeBytes - _minFreeSpaceBytes;
        var resumed = false;
        await inventory.ApplyAsync(inventory.Torrents.OrderBy(t => t.Torrent.AddedOn), async (item, resumes) =>
        {
            var before = resumes?.Count ?? 0;
            await ProcessSingleTorrentSpaceAsync(item.InstanceName, item.Client, item.Torrent, resumes, cancellationToken);
            if (resumes != null && resumes.Count > before)
            {
                var resume = resumes[^1];
                resumes[^1] = async () => { await resume(); resumed = true; };
            }
        }, _logger, cancellationToken);
        return resumed;
    }

    /// <summary>
    /// Process torrents from ALL qBit instances for the given category, sorted by added date globally.
    /// Each torrent uses its own qBit instance's client for pause/resume/tag operations.
    /// </summary>
    public async Task ProcessTorrentsForSpaceAsync(string category, CancellationToken cancellationToken = default)
    {
        _logger.LogTrace("Starting free space processing for category {Category}", category);

        var inventory = await FreeSpaceInventory.ReadAsync(_config, _qbitManager, _logger, cancellationToken);
        var allTorrents = inventory.Torrents;

        // Get current free space (most constrained drive across all instances)
        _logger.LogTrace("Getting free space stats");
        var stats = await GetFreeSpaceStatsAsync(cancellationToken);
        _currentFreeSpace = stats.FreeBytes - _minFreeSpaceBytes
            - inventory.UnmanagedRemaining(t => t.Category == category);

        _logger.LogTrace(
            "Processing torrents for space across {Count} qBit instance(s) | Available: {Available} | Threshold: {Threshold} | Usable: {Usable}",
            _qbitManager.GetAllClients().Count,
            FormatBytes(stats.FreeBytes),
            FormatBytes(_minFreeSpaceBytes),
            FormatBytes(_currentFreeSpace));

        _logger.LogTrace("Free space: Available={Available}, Threshold={Threshold}, Usable={Usable}",
            FormatBytes(stats.FreeBytes), FormatBytes(_minFreeSpaceBytes), FormatBytes(_currentFreeSpace));

        var sorted = allTorrents.Where(t => t.Torrent.Category == category).OrderBy(t => t.Torrent.AddedOn);
        await inventory.ApplyAsync(sorted, async (item, resumes) =>
        {
            await EnsureTagsExistAsync(item.Client, cancellationToken);
            await ProcessSingleTorrentSpaceAsync(item.InstanceName, item.Client, item.Torrent, resumes, cancellationToken);
        }, _logger, cancellationToken);
    }

    private async Task ProcessSingleTorrentSpaceAsync(
        string instanceName,
        ITorrentClient client,
        TorrentInfo torrent,
        List<Func<Task>>? resumes,
        CancellationToken cancellationToken)
    {
        var isFreeSpaceDownload = TorrentPolicyHelper.IsFreeSpaceDownloadState(torrent.State);
        var isPausedDownload = TorrentPolicyHelper.IsPausedDownloadStateForFreeSpace(torrent.State);
        var hasFreeSpaceTag = HasTag(torrent, FreeSpacePausedTag, client, instanceName);

        _logger.LogTrace("FreeSpace: [{Name}] | State[{State}] | Progress[{Progress:P1}] | Size[{Size}] | AmountLeft[{AmountLeft}] | HasTag[{HasTag}] | Hash[{Hash}]",
            torrent.Name, torrent.State, torrent.Progress, FormatBytes(torrent.Size), FormatBytes(torrent.AmountLeft), hasFreeSpaceTag, torrent.Hash);

        _logger.LogTrace("FreeSpace: [{Instance}] Torrent {Name}: State={State}, IsFreeSpaceDownload={IsDl}, IsPausedDownload={IsPausedDl}, HasFreeSpaceTag={HasTag}",
            instanceName, torrent.Name, torrent.State, isFreeSpaceDownload, isPausedDownload, hasFreeSpaceTag);

        if (isFreeSpaceDownload && !isPausedDownload && hasFreeSpaceTag)
            await SetFreeSpacePausedTagAsync(client, torrent, instanceName, false, cancellationToken);

        if (isFreeSpaceDownload)
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
                await SetFreeSpacePausedTagAsync(client, torrent, instanceName, true, cancellationToken);
                if (!_config.Settings.Tagless && client.Capabilities.Tags)
                    await client.RemoveTagsAsync(new List<string> { torrent.Hash }, new List<string> { AllowedSeedingTag }, cancellationToken);
                _logger.LogTrace("FreeSpace: [{Instance}] Pausing torrent {Hash}", instanceName, torrent.Hash);
                await FreeSpaceInventory.PauseAndRollbackMarkerAsync(client, torrent.Hash,
                    () => SetFreeSpacePausedTagAsync(client, torrent, instanceName, false, CancellationToken.None), cancellationToken);
            }
            else if (isPausedDownload && freeSpaceTest < 0 && hasFreeSpaceTag)
            {
                _logger.LogInformation(
                    "FreeSpace: [{Instance}] Keeping paused [{Name}] | Available[{Available}] | Needed[{Needed}] | Deficit[{Deficit}] | Hash[{Hash}]",
                    instanceName, torrent.Name,
                    FormatBytes(_currentFreeSpace + _minFreeSpaceBytes),
                    FormatBytes(torrent.AmountLeft),
                    FormatBytes(-freeSpaceTest),
                    torrent.Hash);

                _logger.LogTrace("FreeSpace: [{Instance}] Maintaining FreeSpacePaused on torrent {Hash}", instanceName, torrent.Hash);
                await SetFreeSpacePausedTagAsync(client, torrent, instanceName, true, cancellationToken);
                if (!_config.Settings.Tagless && client.Capabilities.Tags)
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
            }
            else if (isPausedDownload && freeSpaceTest >= 0 && hasFreeSpaceTag && resumes != null)
            {
                _logger.LogInformation(
                    "FreeSpace: [{Instance}] Resuming download [{Name}] | Available[{Available}] | SpaceAfter[{SpaceAfter}] | Hash[{Hash}]",
                    instanceName, torrent.Name,
                    FormatBytes(_currentFreeSpace + _minFreeSpaceBytes),
                    FormatBytes(freeSpaceTest + _minFreeSpaceBytes),
                    torrent.Hash);

                _currentFreeSpace = freeSpaceTest;
                resumes.Add(async () =>
                {
                    _logger.LogTrace("FreeSpace: [{Instance}] Clearing FreeSpacePaused on torrent {Hash}", instanceName, torrent.Hash);
                    if (!_config.Settings.Tagless && client.Capabilities.Tags)
                        await client.AddTagsAsync(new List<string> { torrent.Hash }, new List<string> { AllowedSeedingTag }, cancellationToken);
                    _logger.LogTrace("FreeSpace: [{Instance}] Resuming torrent {Hash}", instanceName, torrent.Hash);
                    await FreeSpaceInventory.ResumeAndClearMarkerAsync(client, torrent.Hash,
                        () => SetFreeSpacePausedTagAsync(client, torrent, instanceName, false, CancellationToken.None), cancellationToken);
                });
            }
        }
        else if (!isFreeSpaceDownload && hasFreeSpaceTag)
        {
            _logger.LogInformation(
                "FreeSpace: [{Instance}] Completed, removing tag [{Name}] | Available[{Available}] | Hash[{Hash}]",
                instanceName, torrent.Name,
                FormatBytes(_currentFreeSpace + _minFreeSpaceBytes),
                torrent.Hash);

            _logger.LogTrace("FreeSpace: [{Instance}] Clearing FreeSpacePaused on completed torrent {Hash}", instanceName, torrent.Hash);
            await SetFreeSpacePausedTagAsync(client, torrent, instanceName, false, cancellationToken);
        }
        else
        {
            _logger.LogTrace("FreeSpace: [{Instance}] No action needed for torrent {Name}", instanceName, torrent.Name);
        }
    }

    private bool HasTag(TorrentInfo torrent, string tag, ITorrentClient client, string instanceName)
    {
        // §1.6 Tagless: FreeSpacePaused → DB column
        if (_config.Settings.Tagless || !client.Capabilities.Tags)
        {
            var dbEntry = _dbContext.TorrentLibrary.AsNoTracking()
                .FirstOrDefault(t => t.Hash == torrent.Hash && t.QbitInstance == instanceName);
            return dbEntry != null && tag == FreeSpacePausedTag && dbEntry.FreeSpacePaused;
        }

        if (string.IsNullOrEmpty(torrent.Tags)) return false;
        return torrent.Tags.Split(',', StringSplitOptions.RemoveEmptyEntries)
            .Select(t => t.Trim())
            .Contains(tag, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>§1.6: Set or clear FreeSpacePaused — uses qBit tag or DB column based on Tagless setting.</summary>
    private async Task SetFreeSpacePausedTagAsync(
        ITorrentClient client, TorrentInfo torrent, string instanceName, bool paused, CancellationToken ct)
    {
        if (_config.Settings.Tagless || !client.Capabilities.Tags)
        {
            await TaglessTorrentLibraryHelper.SetFreeSpacePausedAsync(
                _dbContext, torrent.Hash, torrent.Category, instanceName, paused, ct);
        }
        else
        {
            var accepted = paused
                ? await client.AddTagsAsync([torrent.Hash], [FreeSpacePausedTag], ct)
                : await client.RemoveTagsAsync([torrent.Hash], [FreeSpacePausedTag], ct);
            if (!accepted) throw new IOException("Torrent client rejected free-space marker update.");
        }
    }

    private async Task EnsureTagsExistAsync(ITorrentClient client, CancellationToken cancellationToken)
    {
        if (_config.Settings.Tagless || !client.Capabilities.Tags) return; // §1.6: no tags in Tagless mode
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
        catch (Exception ex) when (ex is not OperationCanceledException)
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
}

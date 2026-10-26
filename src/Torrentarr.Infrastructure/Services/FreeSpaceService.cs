using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Torrentarr.Core.Configuration;
using Torrentarr.Core.Interfaces;
using Torrentarr.Core.Services;
using Torrentarr.Infrastructure.Database;

namespace Torrentarr.Infrastructure.Services;

/// <summary>One coordinated allocation pass across every client's shared storage.</summary>
public class FreeSpaceService(
    ILogger<FreeSpaceService> logger, TorrentarrConfig config,
    ITorrentClientRegistry registry, TorrentarrDbContext db) : IFreeSpaceService
{
    private const string PausedTag = "qBitrr-free_space_paused";
    private readonly PathMappingService _paths = new();
    public record StorageSpace(string Identity, long TotalBytes, long FreeBytes);
    private sealed record Monitor(string Client, FreeSpaceMonitorConfig Config, StorageSpace? Space, string? Error);

    protected virtual string IdentifyStorage(string path)
    {
        if (!Directory.Exists(path)) throw new IOException("Monitor directory is unavailable.");
        using (var entries = Directory.EnumerateFileSystemEntries(path).GetEnumerator()) entries.MoveNext();
        return StorageIdentity(path);
    }

    // Virtual so allocation tests can supply disk budgets without depending on the test machine.
    protected virtual StorageSpace ReadSpace(string path)
    {
        if (OperatingSystem.IsWindows())
        {
            if (!GetDiskFreeSpaceEx(path, out var available, out var total, out _))
                throw new IOException("Cannot measure monitor volume.", new System.ComponentModel.Win32Exception());
            return new(IdentifyStorage(path), checked((long)total), checked((long)available));
        }
        var drive = new DriveInfo(path);
        return new(IdentifyStorage(path), drive.TotalSize, drive.AvailableFreeSpace);
    }

    private static string StorageIdentity(string path)
    {
        path = Path.GetFullPath(path);
        if (OperatingSystem.IsLinux())
        {
            var resolved = RealPath(path, IntPtr.Zero);
            if (resolved == IntPtr.Zero) throw new IOException("Cannot resolve monitor path.", new System.ComponentModel.Win32Exception());
            try { path = System.Runtime.InteropServices.Marshal.PtrToStringUTF8(resolved)!; }
            finally { Free(resolved); }
            // Device numbers identify bind mounts of the same filesystem as one budget.
            var mount = File.ReadLines("/proc/self/mountinfo")
                .Select(line => line.Split(' '))
                .Where(parts => parts.Length > 4 && StorageConfigMigration.Contains(UnescapeMount(parts[4]), path))
                .OrderByDescending(parts => UnescapeMount(parts[4]).Length).FirstOrDefault();
            if (mount == null) throw new IOException("Cannot identify monitor filesystem.");
            return mount[2];
        }
        if (OperatingSystem.IsWindows()) return path.StartsWith(@"\\", StringComparison.Ordinal)
            ? Path.GetPathRoot(path)!.ToUpperInvariant() : WindowsVolume(path);
        var volume = DriveInfo.GetDrives().Where(d => StorageConfigMigration.Contains(d.Name, path))
            .OrderByDescending(d => d.Name.Length).FirstOrDefault();
        return volume?.Name ?? throw new IOException("Cannot identify monitor filesystem.");
    }

    [System.Runtime.InteropServices.DllImport("libc", EntryPoint = "realpath", SetLastError = true)]
    private static extern IntPtr RealPath([System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.LPUTF8Str)] string path, IntPtr buffer);
    [System.Runtime.InteropServices.DllImport("libc", EntryPoint = "free")]
    private static extern void Free(IntPtr pointer);

    private static string UnescapeMount(string value) => value.Replace("\\040", " ").Replace("\\011", "\t").Replace("\\012", "\n").Replace("\\134", "\\");
    private static string WindowsVolume(string path)
    {
        var mount = new System.Text.StringBuilder(32768);
        var volume = new System.Text.StringBuilder(1024);
        if (!GetVolumePathName(path, mount, mount.Capacity) || !GetVolumeNameForVolumeMountPoint(mount.ToString(), volume, volume.Capacity))
            throw new IOException("Cannot identify monitor volume.", new System.ComponentModel.Win32Exception());
        return volume.ToString();
    }
    [System.Runtime.InteropServices.DllImport("kernel32.dll", EntryPoint = "GetDiskFreeSpaceExW", CharSet = System.Runtime.InteropServices.CharSet.Unicode, SetLastError = true)]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    private static extern bool GetDiskFreeSpaceEx(string path, out ulong available, out ulong total, out ulong free);
    [System.Runtime.InteropServices.DllImport("kernel32.dll", EntryPoint = "GetVolumePathNameW", CharSet = System.Runtime.InteropServices.CharSet.Unicode, SetLastError = true)]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    private static extern bool GetVolumePathName(string path, System.Text.StringBuilder volumePath, int length);
    [System.Runtime.InteropServices.DllImport("kernel32.dll", EntryPoint = "GetVolumeNameForVolumeMountPointW", CharSet = System.Runtime.InteropServices.CharSet.Unicode, SetLastError = true)]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    private static extern bool GetVolumeNameForVolumeMountPoint(string mount, System.Text.StringBuilder volume, int length);

    private List<Monitor> ReadMonitors()
    {
        var result = new List<Monitor>();
        var measured = new Dictionary<string, StorageSpace>(StringComparer.Ordinal);
        foreach (var (id, client) in config.GetAllTorrentClients().Where(c => !c.Value.Disabled))
            foreach (var monitor in client.FreeSpace.Where(m => m.Enabled))
            {
                try
                {
                    var identity = IdentifyStorage(monitor.Path);
                    if (!measured.TryGetValue(identity, out var shared)) measured[identity] = shared = ReadSpace(monitor.Path);
                    result.Add(new(id, monitor, shared, null));
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
                {
                    logger.LogWarning(ex, "FreeSpace: [{Client}] Cannot read {Path}; affected downloads will be paused", id, monitor.Path);
                    result.Add(new(id, monitor, null, ex.Message));
                }
            }
        return result;
    }

    public Task<IReadOnlyList<FreeSpaceStats>> GetFreeSpaceStatsAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var monitors = ReadMonitors();
        return Task.FromResult<IReadOnlyList<FreeSpaceStats>>(monitors.Select(m =>
        {
            var reserve = m.Space == null ? FreeSpaceThreshold.Parse(m.Config.Limit)
                : monitors.Where(other => other.Space?.Identity == m.Space.Identity).Max(other => FreeSpaceThreshold.Parse(other.Config.Limit));
            return new FreeSpaceStats
            {
                ClientInstanceId = m.Client,
                Path = m.Config.Path,
                Error = m.Error,
                TotalBytes = m.Space?.TotalBytes ?? 0,
                FreeBytes = m.Space?.FreeBytes ?? 0,
                ThresholdBytes = reserve,
                BelowThreshold = m.Space == null || m.Space.FreeBytes < reserve
            };
        }).ToList());
    }

    private Monitor? Match(FreeSpaceTorrent item, List<Monitor> monitors)
    {
        var client = config.GetTorrentClient(item.InstanceName);
        if (client == null || client.Disabled) return null;
        var local = client.Maintenance.PathMappings.Count > 0
            ? _paths.MapToLocal(item.Torrent.SavePath, client.Maintenance.PathMappings)
            : Path.IsPathFullyQualified(item.Torrent.SavePath) ? item.Torrent.SavePath : null;
        var own = monitors.Where(m => m.Client.Equals(item.InstanceName, StringComparison.OrdinalIgnoreCase)).ToList();
        if (local != null)
        {
            var prefix = own.Where(m => StorageConfigMigration.Contains(m.Config.Path, local))
                .OrderByDescending(m => m.Config.Path.Length).FirstOrDefault();
            if (prefix is { Space: null }) return prefix;
            try
            {
                var identity = IdentifyStorage(local);
                if (prefix?.Space?.Identity == identity) return prefix;
                var sameDisk = own.FirstOrDefault(m => m.Space?.Identity == identity);
                if (sameDisk != null) return sameDisk;
                // Clients without enabled monitors can still consume storage protected by another client.
                if (own.Count == 0) return monitors.FirstOrDefault(m => m.Space?.Identity == identity);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
            {
                // A not-yet-created save directory inherits its configured monitor's filesystem.
                if (prefix != null) return prefix;
            }
        }
        if (own.Count > 0) logger.LogWarning("FreeSpace: [{Client}] No monitor matches torrent {Hash} save path {Path}", item.InstanceName, item.Torrent.Hash, item.Torrent.SavePath);
        return null;
    }

    public async Task<FreeSpacePassResult> ProcessAsync(IReadOnlySet<string> managedCategories, CancellationToken cancellationToken = default)
    {
        var monitors = ReadMonitors();
        if (monitors.Count == 0) return new(0, 0, 0, false);
        var inventory = await FreeSpaceInventory.ReadAsync(config, registry, logger, cancellationToken);
        var assignments = inventory.Torrents.Select(item => (Item: item, Monitor: Match(item, monitors))).Where(a => a.Monitor != null).ToList();
        static string Key(Monitor m) => m.Space?.Identity ?? $"unavailable:{m.Client}:{m.Config.Path}";
        int paused = 0, resumed = 0;
        foreach (var group in monitors.GroupBy(Key))
        {
            var rows = group.ToList();
            var items = assignments.Where(a => Key(a.Monitor!) == group.Key).Select(a => a.Item).ToList();
            var available = rows[0].Space?.FreeBytes ?? 0;
            var reserve = rows.Max(m => FreeSpaceThreshold.Parse(m.Config.Limit));
            // Saturate subtraction so very large torrent inventories cannot overflow into free space.
            long remaining = available >= reserve ? available - reserve : -1;
            void Reserve(long bytes) => remaining = bytes > remaining ? -1 : remaining - Math.Max(0, bytes);
            bool Managed(FreeSpaceTorrent item) => managedCategories.Contains(item.Torrent.Category)
                && monitors.Any(m => m.Client.Equals(item.InstanceName, StringComparison.OrdinalIgnoreCase));
            foreach (var item in items.Where(i => !Managed(i) && TorrentPolicyHelper.IsFreeSpaceDownloadState(i.Torrent.State)
                && !TorrentPolicyHelper.IsPausedDownloadStateForFreeSpace(i.Torrent.State))) Reserve(Math.Max(0, item.Torrent.AmountLeft));

            var safe = rows.All(m => m.Space != null) && !inventory.FailedClients.Any(id =>
            {
                var failedRows = monitors.Where(m => m.Client.Equals(id, StringComparison.OrdinalIgnoreCase)).ToList();
                return failedRows.Count == 0 || failedRows.Any(m => Key(m) == group.Key);
            });
            var resumes = new List<Func<Task>>();
            var ordered = items.Where(Managed)
                .OrderBy(i => TorrentPolicyHelper.TorrentQueuePositionSortKey(i.Torrent).InactiveQueueGroup)
                .ThenBy(i => TorrentPolicyHelper.TorrentQueuePositionSortKey(i.Torrent).Nq).ThenBy(i => i.Torrent.AddedOn);
            foreach (var item in ordered)
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    var torrent = item.Torrent;
                    var marked = await HasMarker(item, cancellationToken);
                    var download = TorrentPolicyHelper.IsFreeSpaceDownloadState(torrent.State);
                    var stopped = TorrentPolicyHelper.IsPausedDownloadStateForFreeSpace(torrent.State);
                    if (!download) { if (marked) await SetMarker(item, false, cancellationToken); continue; }
                    if (stopped && !marked) continue;
                    if (!stopped && marked) await SetMarker(item, false, cancellationToken);
                    var needed = Math.Max(0, torrent.AmountLeft);
                    var fits = rows[0].Space != null && remaining >= needed;
                    if (!fits && !stopped)
                    {
                        await SetMarker(item, true, cancellationToken);
                        await FreeSpaceInventory.PauseAndRollbackMarkerAsync(item.Client, torrent.Hash,
                            () => SetMarker(item, false, CancellationToken.None), cancellationToken);
                        paused++;
                    }
                    else if (fits && (!stopped || safe))
                    {
                        Reserve(needed);
                        if (stopped && safe) resumes.Add(async () =>
                        {
                            await FreeSpaceInventory.ResumeAndClearMarkerAsync(item.Client, torrent.Hash,
                                () => SetMarker(item, false, CancellationToken.None), cancellationToken);
                            resumed++;
                        });
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    safe = false;
                    if (TorrentPolicyHelper.IsFreeSpaceDownloadState(item.Torrent.State)
                        && !TorrentPolicyHelper.IsPausedDownloadStateForFreeSpace(item.Torrent.State))
                        Reserve(Math.Max(0, item.Torrent.AmountLeft));
                    logger.LogWarning(ex, "FreeSpace: [{Client}] Operation failed; shared-storage resumes withheld", item.InstanceName);
                }
            }
            if (safe)
                foreach (var resume in resumes)
                {
                    try { await resume(); }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    { logger.LogWarning(ex, "FreeSpace: resume failed; remaining shared-storage resumes withheld"); break; }
                }
        }
        var markedCount = 0;
        foreach (var item in inventory.Torrents)
            if (await HasMarker(item, cancellationToken)) markedCount++;
        return new(paused, resumed, markedCount, monitors.Any(m => m.Space != null));
    }

    private bool Tagless(FreeSpaceTorrent item) => config.Settings.Tagless || !item.Client.Capabilities.Tags;
    private async Task<bool> HasMarker(FreeSpaceTorrent item, CancellationToken ct)
        => Tagless(item)
            ? await db.TorrentLibrary.AsNoTracking().AnyAsync(t => t.QbitInstance == item.InstanceName && t.Hash == item.Torrent.Hash && t.FreeSpacePaused, ct)
            : item.Torrent.Tags.Split(',', StringSplitOptions.RemoveEmptyEntries).Any(t => t.Trim().Equals(PausedTag, StringComparison.OrdinalIgnoreCase));

    private async Task SetMarker(FreeSpaceTorrent item, bool paused, CancellationToken ct)
    {
        if (Tagless(item))
            await TaglessTorrentLibraryHelper.SetFreeSpacePausedAsync(db, item.Torrent.Hash, item.Torrent.Category, item.InstanceName, paused, ct);
        else
        {
            if (paused)
            {
                var existingTags = await item.Client.GetTagsAsync(ct);
                if (!existingTags.Contains(PausedTag, StringComparer.OrdinalIgnoreCase) && !await item.Client.CreateTagsAsync([PausedTag], ct))
                    throw new IOException("Torrent client rejected free-space tag creation.");
            }
            var accepted = paused ? await item.Client.AddTagsAsync([item.Torrent.Hash], [PausedTag], ct)
                : await item.Client.RemoveTagsAsync([item.Torrent.Hash], [PausedTag], ct);
            if (!accepted) throw new IOException("Torrent client rejected free-space marker update.");
            var tags = item.Torrent.Tags.Split(',', StringSplitOptions.RemoveEmptyEntries).Select(t => t.Trim())
                .Where(t => !t.Equals(PausedTag, StringComparison.OrdinalIgnoreCase)).ToList();
            if (paused) tags.Add(PausedTag);
            item.Torrent.Tags = string.Join(",", tags);
        }
    }
}

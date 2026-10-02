using Torrentarr.Core.Configuration;
using Microsoft.Extensions.Logging;
using Torrentarr.Core.Interfaces;
using Torrentarr.Core.Services;

namespace Torrentarr.Infrastructure.Services;

public sealed class SafeDeletionService : ISafeDeletionService
{
    private readonly ITorrentClientRegistry _registry;
    private readonly ITorrentInventoryService _inventory;
    private readonly IPathMappingService _paths;
    private readonly TorrentarrConfig _config;
    private readonly ILogger<SafeDeletionService> _logger;

    public SafeDeletionService(
        ITorrentClientRegistry registry,
        ITorrentInventoryService inventory,
        IPathMappingService paths,
        TorrentarrConfig config,
        ILogger<SafeDeletionService> logger)
    {
        _registry = registry;
        _inventory = inventory;
        _paths = paths;
        _config = config;
        _logger = logger;
    }

    public async Task<SafeDeletionResult> RemoveTorrentAsync(
        string clientInstanceId,
        string hash,
        bool removeContent,
        bool permanent = false,
        CancellationToken ct = default)
    {
        var client = _registry.GetClient(clientInstanceId);
        var cfg = _config.GetTorrentClient(clientInstanceId);
        if (client == null || cfg == null)
            return new(false, true, 0, "Torrent client is disconnected or unconfigured.");

        if (!removeContent)
            return await client.DeleteTorrentsAsync([hash], false, ct)
                ? new(true, true, 0)
                : new(false, true, 0, "Torrent client rejected removal.");

        // Preserve legacy failed-torrent cleanup for installations that have no
        // optional maintenance mappings, but only through the client's explicit
        // content-deletion capability and with recycling disabled.
        if (cfg.Maintenance.PathMappings.Count == 0 && !cfg.Maintenance.RecycleBin.Enabled)
        {
            if (!client.Capabilities.ContentDeletion)
                return new(false, true, 0, "No path mapping is configured and the client cannot delete content safely.");
            return await client.DeleteTorrentsAsync([hash], true, ct)
                ? new(true, false, 0)
                : new(false, true, 0, "Torrent client rejected legacy content deletion.");
        }

        var inventory = await _inventory.BuildAsync(ct: ct);
        if (inventory.Warnings.Count > 0)
            return new(false, true, 0, $"Content deletion blocked because inventory is incomplete: {string.Join("; ", inventory.Warnings)}");
        var item = inventory.Torrents.FirstOrDefault(t =>
            t.Torrent.ClientInstanceId.Equals(clientInstanceId, StringComparison.OrdinalIgnoreCase)
            && t.Torrent.Hash.Equals(hash, StringComparison.OrdinalIgnoreCase));
        if (item == null) return new(false, true, 0, "Torrent no longer exists.");

        var localFiles = item.Files.Select(f => f.LocalPath).Where(p => p != null).Cast<string>().ToList();
        if (localFiles.Count == 0 || item.Files.Any(f => f.LocalPath == null))
            return new(false, true, 0, "No validated local file paths are available.");
        foreach (var source in CollapseRoots(localFiles))
        {
            var validation = ValidateSource(source, cfg);
            if (validation != null) return new(false, true, 0, validation);
        }

        var shared = localFiles.Any(path =>
            inventory.LocalPathReferences.TryGetValue(Path.GetFullPath(path), out var owners)
            && owners.Any(owner => owner.Torrent.Hash != hash
                || !owner.Torrent.ClientInstanceId.Equals(clientInstanceId, StringComparison.OrdinalIgnoreCase)));
        if (shared)
        {
            var removed = await client.DeleteTorrentsAsync([hash], false, ct);
            return removed
                ? new(true, true, 0)
                : new(false, true, 0, "Torrent client rejected cross-seed-safe removal.");
        }

        if (!cfg.Maintenance.RecycleBin.Enabled)
            return new(false, true, 0, "Recycle-bin content removal is disabled; refusing destructive deletion.");

        if (permanent)
        {
            if (!client.Capabilities.ContentDeletion)
                return new(false, true, 0, "Torrent client does not support content deletion.");
            var removed = await client.DeleteTorrentsAsync([hash], true, ct);
            return removed
                ? new(true, false, 0)
                : new(false, true, 0, "Torrent client rejected content deletion.");
        }

        await BackupResumeDataAsync(client, hash, clientInstanceId, cfg, ct);

        var moved = 0L;
        var movedPaths = new List<(string Source, string Destination)>();
        try
        {
            foreach (var source in CollapseRoots(localFiles))
            {
                var validation = ValidateSource(source, cfg);
                if (validation != null) return new(false, true, moved, validation);
                var destination = BuildRecycleDestination(source, clientInstanceId, item.Torrent.Category, cfg);
                moved += await MoveToRecycleAsync(source, destination, ct);
                movedPaths.Add((source, destination));
                RemoveEmptyParents(Path.GetDirectoryName(source), cfg);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            foreach (var (source, destination) in movedPaths.AsEnumerable().Reverse())
            {
                try
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(source)!);
                    if (Directory.Exists(destination)) Directory.Move(destination, source);
                    else if (File.Exists(destination)) File.Move(destination, source);
                }
                catch (Exception rollbackEx)
                {
                    _logger.LogError(rollbackEx, "Unable to roll back recycle move {Destination} to {Source}", destination, source);
                }
            }
            return new(false, true, 0, $"Recycle operation rolled back: {ex.Message}");
        }

        var deleted = await client.DeleteTorrentsAsync([hash], false, ct);
        return deleted
            ? new(true, false, moved)
            : new(false, false, moved, "Content was recycled, but the torrent client rejected removal.");
    }

    public async Task<SafeDeletionResult> QuarantineFileAsync(string clientInstanceId, string localPath, CancellationToken ct = default)
    {
        var cfg = _config.GetTorrentClient(clientInstanceId);
        if (cfg == null) return new(false, true, 0, "Torrent client is unconfigured.");
        var validation = ValidateSource(localPath, cfg);
        if (validation != null) return new(false, true, 0, validation);
        var destination = BuildRecycleDestination(localPath, clientInstanceId, "orphans", cfg);
        var bytes = await MoveToRecycleAsync(localPath, destination, ct);
        return new(true, false, bytes);
    }

    public Task<int> CleanupExpiredAsync(string clientInstanceId, CancellationToken ct = default)
    {
        var cfg = _config.GetTorrentClient(clientInstanceId);
        if (cfg == null || !cfg.Maintenance.RecycleBin.Enabled) return Task.FromResult(0);
        var root = ResolveRecycleRoot(cfg);
        if (!Directory.Exists(root)) return Task.FromResult(0);
        if ((File.GetAttributes(root) & FileAttributes.ReparsePoint) != 0) return Task.FromResult(0);

        var cutoff = DateTime.UtcNow.AddDays(-cfg.Maintenance.RecycleBin.RetentionDays);
        var deleted = 0;
        var options = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            AttributesToSkip = FileAttributes.ReparsePoint,
            IgnoreInaccessible = true
        };
        foreach (var path in Directory.EnumerateFileSystemEntries(root, "*", options))
        {
            ct.ThrowIfCancellationRequested();
            if (Directory.Exists(path)) continue;
            var modified = File.GetLastWriteTimeUtc(path);
            if (modified > cutoff) continue;
            if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) continue;
            File.Delete(path);
            deleted++;
        }
        return Task.FromResult(deleted);
    }

    private string? ValidateSource(string source, TorrentClientInstanceConfig cfg)
    {
        var full = Path.GetFullPath(source);
        if (!File.Exists(full) && !Directory.Exists(full)) return $"Path no longer exists: {full}";
        if (!IsConfiguredRoot(full, cfg)) return $"Path is outside configured roots: {full}";
        if ((File.GetAttributes(full) & FileAttributes.ReparsePoint) != 0)
            return $"Refusing symlink or reparse point: {full}";
        var root = cfg.Maintenance.PathMappings.Select(m => m.LocalPath)
            .FirstOrDefault(candidate => !string.IsNullOrWhiteSpace(candidate) && _paths.IsSafeDescendant(full, candidate));
        if (root == null || HasReparsePointInPath(full, Path.GetFullPath(root)))
            return $"Refusing path with a symlink or reparse-point ancestor: {full}";
        return null;
    }

    private bool IsConfiguredRoot(string path, TorrentClientInstanceConfig cfg)
        => cfg.Maintenance.PathMappings.Any(m =>
            !string.IsNullOrWhiteSpace(m.LocalPath) && _paths.IsSafeDescendant(path, m.LocalPath));

    private string BuildRecycleDestination(string source, string instanceId, string category, TorrentClientInstanceConfig cfg)
    {
        var root = ResolveRecycleRoot(cfg);
        if (cfg.Maintenance.RecycleBin.SplitByClient) root = Path.Combine(root, Sanitize(instanceId));
        if (cfg.Maintenance.RecycleBin.SplitByCategory) root = Path.Combine(root, Sanitize(category));
        Directory.CreateDirectory(root);
        var timestamp = DateTime.UtcNow.ToString("yyyyMMddHHmmssfff");
        var baseName = $"{timestamp}-{Path.GetFileName(source)}";
        var destination = Path.Combine(root, baseName);
        var suffix = 0;
        while (File.Exists(destination) || Directory.Exists(destination))
            destination = Path.Combine(root, $"{timestamp}-{++suffix}-{Path.GetFileName(source)}");
        return destination;
    }

    private static string ResolveRecycleRoot(TorrentClientInstanceConfig cfg)
    {
        if (!string.IsNullOrWhiteSpace(cfg.Maintenance.RecycleBin.Path))
            return Path.GetFullPath(cfg.Maintenance.RecycleBin.Path);
        var mapping = cfg.Maintenance.PathMappings.FirstOrDefault()
            ?? throw new InvalidOperationException("A path mapping is required for recycle-bin operations.");
        return Path.Combine(Path.GetFullPath(mapping.LocalPath), ".torrentarr-recycle");
    }

    private async Task<long> MoveToRecycleAsync(string source, string destination, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        try
        {
            if (Directory.Exists(source)) Directory.Move(source, destination); else File.Move(source, destination);
        }
        catch (IOException)
        {
            if (Directory.Exists(source))
            {
                await CopyDirectoryAsync(source, destination, ct);
                Directory.Delete(source, true);
            }
            else
            {
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                await using var input = File.OpenRead(source);
                await using var output = File.Create(destination);
                await input.CopyToAsync(output, ct);
                await output.FlushAsync(ct);
                if (new FileInfo(source).Length != new FileInfo(destination).Length)
                    throw new IOException("Recycle copy verification failed.");
                File.Delete(source);
            }
        }
        var bytes = Directory.Exists(destination)
            ? Directory.EnumerateFiles(destination, "*", SearchOption.AllDirectories).Sum(f => new FileInfo(f).Length)
            : new FileInfo(destination).Length;
        _logger.LogInformation("Moved {Source} to recycle bin {Destination}", source, destination);
        return bytes;
    }

    private static async Task CopyDirectoryAsync(string source, string destination, CancellationToken ct)
    {
        Directory.CreateDirectory(destination);
        foreach (var directory in Directory.EnumerateDirectories(source, "*", SearchOption.AllDirectories))
        {
            ct.ThrowIfCancellationRequested();
            var relative = Path.GetRelativePath(source, directory);
            Directory.CreateDirectory(Path.Combine(destination, relative));
        }
        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            ct.ThrowIfCancellationRequested();
            if ((File.GetAttributes(file) & FileAttributes.ReparsePoint) != 0)
                throw new IOException($"Refusing to copy reparse point: {file}");
            var target = Path.Combine(destination, Path.GetRelativePath(source, file));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            await using var input = File.OpenRead(file);
            await using var output = File.Create(target);
            await input.CopyToAsync(output, ct);
        }
    }

    private static IEnumerable<string> CollapseRoots(IEnumerable<string> paths)
    {
        var comparer = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
        return paths.Select(Path.GetFullPath).Distinct(comparer)
            .Where(candidate => !paths.Any(other =>
                !candidate.Equals(Path.GetFullPath(other), OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal)
                && candidate.StartsWith(Path.TrimEndingDirectorySeparator(Path.GetFullPath(other)) + Path.DirectorySeparatorChar,
                    OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal)));
    }

    private static string Sanitize(string value)
        => string.Concat(value.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));

    private static async Task BackupResumeDataAsync(
        ITorrentClient client, string hash, string instanceId, TorrentClientInstanceConfig cfg, CancellationToken ct)
    {
        if (!client.Capabilities.ResumeDataExport || string.IsNullOrWhiteSpace(cfg.Maintenance.RecycleBin.ResumeDataPath)) return;
        var root = Path.GetFullPath(cfg.Maintenance.RecycleBin.ResumeDataPath);
        Directory.CreateDirectory(root);
        var data = await client.ExportResumeDataAsync(hash, ct);
        foreach (var (name, bytes) in data)
        {
            var safeName = Sanitize(Path.GetFileName(name));
            await File.WriteAllBytesAsync(Path.Combine(root, $"{Sanitize(instanceId)}-{hash}-{safeName}"), bytes, ct);
        }
    }

    private void RemoveEmptyParents(string? directory, TorrentClientInstanceConfig cfg)
    {
        while (!string.IsNullOrWhiteSpace(directory) && IsConfiguredRoot(directory, cfg))
        {
            if (cfg.Maintenance.PathMappings.Any(m => Path.GetFullPath(m.LocalPath).Equals(
                    Path.GetFullPath(directory), OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))) break;
            if (!Directory.Exists(directory) || Directory.EnumerateFileSystemEntries(directory).Any()) break;
            var parent = Directory.GetParent(directory)?.FullName;
            Directory.Delete(directory);
            directory = parent;
        }
    }

    private static bool HasReparsePointInPath(string path, string root)
    {
        var current = new DirectoryInfo(Directory.Exists(path) ? path : Path.GetDirectoryName(path)!);
        var rootFull = Path.TrimEndingDirectorySeparator(root);
        while (current != null && current.FullName.Length >= rootFull.Length)
        {
            if ((current.Attributes & FileAttributes.ReparsePoint) != 0) return true;
            if (Path.TrimEndingDirectorySeparator(current.FullName).Equals(rootFull,
                    OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal)) break;
            current = current.Parent;
        }
        return false;
    }
}

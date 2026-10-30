using Torrentarr.Core.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Torrentarr.Core.Interfaces;
using Torrentarr.Core.Models;
using Torrentarr.Core.Services;

namespace Torrentarr.Infrastructure.Services;

public sealed class SafeDeletionService : ISafeDeletionService
{
    private readonly ITorrentClientRegistry _registry;
    private readonly ITorrentInventoryService _inventory;
    private readonly IPathMappingService _paths;
    private readonly TorrentarrConfig _config;
    private readonly ILogger<SafeDeletionService> _logger;
    private readonly IServiceScopeFactory? _scopes;
    private RecycleLedger Ledger => new(_scopes);

    public SafeDeletionService(
        ITorrentClientRegistry registry,
        ITorrentInventoryService inventory,
        IPathMappingService paths,
        TorrentarrConfig config,
        ILogger<SafeDeletionService> logger, IServiceScopeFactory? scopes = null)
    {
        _registry = registry;
        _inventory = inventory;
        _paths = paths;
        _config = config;
        _logger = logger;
        _scopes = scopes;
    }

    public Task<SafeDeletionResult> RemoveTorrentAsync(string clientInstanceId, string hash, bool removeContent, CancellationToken ct = default)
        => RemoveAsync(clientInstanceId, hash, removeContent, null, ct);

    public Task<SafeDeletionResult> RemoveTorrentForIntentAsync(string clientInstanceId, string hash, bool removeContent,
        TorrentRemovalIntent intent, CancellationToken ct = default)
        => RemoveAsync(clientInstanceId, hash, removeContent, null, ct, intent);

    public Task<SafeDeletionResult> RemoveMaintenanceTorrentAsync(string clientInstanceId, string hash, bool removeContent, TorrentInventory inventory, CancellationToken ct = default)
        => RemoveAsync(clientInstanceId, hash, removeContent, inventory, ct);

    private async Task<SafeDeletionResult> RemoveAsync(string clientInstanceId, string hash, bool removeContent,
        TorrentInventory? validatedInventory, CancellationToken ct, TorrentRemovalIntent intent = TorrentRemovalIntent.SeedingLimit)
    {
        using var configuration = _config.BeginOperation();
        using var processing = ConfigMutationGuard.EnterProcessing();
        var client = _registry.GetClient(clientInstanceId);
        var cfg = _config.GetTorrentClient(clientInstanceId);
        if (client == null || cfg == null)
            return new(false, true, 0, "Torrent client is disconnected or unconfigured.");

        var expected = validatedInventory?.Torrents.FirstOrDefault(t =>
            t.Torrent.ClientInstanceId.Equals(clientInstanceId, StringComparison.OrdinalIgnoreCase)
            && t.Torrent.Hash.Equals(hash, StringComparison.OrdinalIgnoreCase))?.Torrent
            ?? (await client.GetTorrentsAsync(cancellationToken: ct))
                .FirstOrDefault(t => t.Hash.Equals(hash, StringComparison.OrdinalIgnoreCase));
        if (expected == null) return new(false, true, 0, "Torrent no longer exists in the client.");
        if (!await ProtectionAllowsAsync(clientInstanceId, hash, ct, expected, intent: intent))
            return new(false, true, 0, $"Final {intent} protection clearance rejected or changed (HnR, age, tags, torrent identity or configuration); torrent and content preserved.");

        if (!removeContent)
            return await client.DeleteTorrentsAsync([hash], false, ct)
                ? new(true, true, 0)
                : new(false, true, 0, "Torrent client rejected removal.");

        // Preserve legacy failed-torrent cleanup for installations that have no
        // optional maintenance mappings, but only through the client's explicit
        // content-deletion capability and with recycling disabled.
        if (validatedInventory == null && _config.GetAllTorrentClients().Count == 1 && cfg.Maintenance.PathMappings.Count == 0 && !cfg.Maintenance.RecycleBin.Enabled)
        {
            if (!client.Capabilities.ContentDeletion)
                return new(false, true, 0, "No path mapping is configured and the client cannot delete content safely.");
            return await client.DeleteTorrentsAsync([hash], true, ct)
                ? new(true, false, 0)
                : new(false, true, 0, "Torrent client rejected legacy content deletion.");
        }

        var inventory = validatedInventory ?? await _inventory.BuildAsync(ct: ct);
        if (!inventory.IsComplete)
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
            if (!await ProtectionAllowsAsync(clientInstanceId, hash, ct, expected, intent: intent)) return new(false, true, 0, "Removal clearance changed.");
            var removed = await client.DeleteTorrentsAsync([hash], false, ct);
            return removed
                ? new(true, true, 0)
                : new(false, true, 0, "Torrent client rejected cross-seed-safe removal.");
        }

        if (!cfg.Maintenance.RecycleBin.Enabled)
            return new(false, true, 0, "Recycle-bin content removal is disabled; refusing destructive deletion.");

        long moved = 0;
        var movedPaths = new List<(string Source, string Destination)>();
        var entries = new List<RecycleEntry>();
        var removalAttempted = false;
        var removalRejected = false;
        var pausedForRecycle = false;
        string? stateBeforeOwnedPause = null;
        try
        {
            if (!await ProtectionAllowsAsync(clientInstanceId, hash, ct, expected, intent: intent))
                throw new IOException("Removal clearance changed before pause.");
            var current = (await client.GetTorrentsAsync(cancellationToken: ct))
                .FirstOrDefault(t => t.Hash.Equals(hash, StringComparison.OrdinalIgnoreCase))
                ?? throw new IOException("Torrent no longer exists.");
            if (!current.IsStopped && !current.State.Contains("paused", StringComparison.OrdinalIgnoreCase))
            {
                if (!await client.PauseTorrentsAsync([hash], ct)) throw new IOException("Torrent client rejected pause.");
                pausedForRecycle = true;
                for (var attempt = 0; ; attempt++)
                {
                    current = (await client.GetTorrentsAsync(cancellationToken: ct))
                        .FirstOrDefault(t => t.Hash.Equals(hash, StringComparison.OrdinalIgnoreCase))
                        ?? throw new IOException("Torrent disappeared while pausing.");
                    if (current.IsStopped || current.State.Contains("paused", StringComparison.OrdinalIgnoreCase)) break;
                    if (attempt >= 49) throw new IOException("Torrent did not stop; refusing to recycle active files.");
                    await Task.Delay(100, ct);
                }
                stateBeforeOwnedPause = expected.State;
                expected.State = current.State;
            }
            if (!await ProtectionAllowsAsync(clientInstanceId, hash, ct, expected, stateBeforeOwnedPause, intent))
                throw new IOException("Removal clearance changed before recycling.");
            foreach (var source in CollapseRoots(localFiles))
            {
                var validation = ValidateSource(source, cfg);
                if (validation != null) throw new IOException(validation);
                var destination = BuildRecycleDestination(source, clientInstanceId, item.Torrent.Category, cfg);
                entries.Add(Ledger.Prepare(clientInstanceId, source, destination, hash));
                try { moved += await MoveToRecycleAsync(source, destination, ct); }
                catch (RecyclePublishedException)
                {
                    movedPaths.Add((source, destination));
                    throw;
                }
                movedPaths.Add((source, destination));
                RemoveEmptyParents(Path.GetDirectoryName(source), cfg);
            }
            ct.ThrowIfCancellationRequested();
            if (!await ProtectionAllowsAsync(clientInstanceId, hash, ct, expected, stateBeforeOwnedPause, intent))
                throw new IOException("Removal clearance changed before client removal.");
            removalAttempted = true;
            if (!await client.DeleteTorrentsAsync([hash], false, ct))
            {
                removalRejected = true;
                throw new IOException("Torrent client rejected removal.");
            }
            foreach (var entry in entries) Ledger.Write(entry with { State = "committed", RecycledAt = DateTimeOffset.UtcNow });
            return new(true, false, moved);
        }
        catch (Exception ex)
        {
            var errors = new List<string>();
            foreach (var (source, destination) in movedPaths.AsEnumerable().Reverse())
            {
                try
                {
                    if (File.Exists(source) || Directory.Exists(source)) throw new IOException("Source path was recreated; refusing to overwrite it.");
                    Directory.CreateDirectory(Path.GetDirectoryName(source)!);
                    await MoveToRecycleAsync(destination, source, CancellationToken.None);
                }
                catch (Exception rollbackEx)
                {
                    errors.Add($"Unable to restore {destination} to {source}: {rollbackEx.Message}");
                    _logger.LogError(rollbackEx, "Unable to restore recycled content {Destination} to {Source}", destination, source);
                }
            }
            var restored = errors.Count == 0;
            foreach (var entry in entries)
                Ledger.Write(entry with { State = restored && (!removalAttempted || removalRejected) ? "restored" : "needs-recovery" });
            if (pausedForRecycle && restored && (!removalAttempted || removalRejected))
            {
                try
                {
                    if (!await client.ResumeTorrentsAsync([hash], CancellationToken.None))
                        errors.Add("Content restored, but torrent client rejected resume.");
                }
                catch (Exception resumeEx) { errors.Add($"Content restored, but resume failed: {resumeEx.Message}"); }
            }
            var uncertain = removalAttempted && !removalRejected ? " Remote removal outcome is uncertain." : "";
            return new(false, restored, restored ? 0 : moved,
                $"Recycle failed: {ex.Message}.{uncertain}" + (errors.Count == 0 ? " Moved content restored." : " " + string.Join("; ", errors)));
        }
    }

    private async Task<bool> ProtectionAllowsAsync(string instanceId, string hash, CancellationToken ct, TorrentInfo? expected = null,
        string? stateBeforeOwnedPause = null, TorrentRemovalIntent intent = TorrentRemovalIntent.SeedingLimit)
    {
        if (_scopes == null || _config.Revision != _config.LatestRevision || _config.GetTorrentClient(instanceId)?.Disabled != false) return false;
        try
        {
            var client = _registry.GetClient(instanceId);
            if (client == null) return false;
            using var scope = _scopes.CreateScope();
            var seeding = scope.ServiceProvider.GetRequiredService<ISeedingService>();
            var current = (await client.GetTorrentsAsync(cancellationToken: ct))
                .FirstOrDefault(t => t.Hash.Equals(hash, StringComparison.OrdinalIgnoreCase));
            if (current == null || (expected != null && !SeedingService.SameRemovalCandidate(expected, current))) return false;
            current.ClientInstanceId = instanceId;
            // An owned pause preserves validated stalled-idle evidence; all other fields and protection stay fresh.
            if (stateBeforeOwnedPause != null
                && (SeedingService.IsStalledUploadState(stateBeforeOwnedPause)
                    || intent == TorrentRemovalIntent.StalledDownload))
                current.State = stateBeforeOwnedPause;
            var cleared = intent == TorrentRemovalIntent.SeedingLimit
                ? await seeding.CanDeleteTorrentAsync(current, "final safe-removal boundary", ct)
                : await seeding.CanDeleteTorrentForIntentAsync(current, intent, "final safe-removal boundary", ct);
            if (!cleared) _logger.LogDebug("Protection rejected {Intent} deletion for {Hash} on {Client}", intent, hash, instanceId);
            return cleared;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "HnR clearance unavailable for {Hash} on {Client}; preserving torrent", hash, instanceId);
            return false;
        }
    }

    public async Task<bool> DeleteOwnedFileAsync(string instanceId, string hash, string path, CancellationToken ct = default)
    {
        using var configuration = _config.BeginOperation();
        using var processing = ConfigMutationGuard.EnterProcessing();
        var cfg = _config.GetTorrentClient(instanceId);
        if (cfg == null || ValidateSource(path, cfg) != null) return false;
        var inventory = await _inventory.BuildAsync(ct: ct);
        if (!inventory.IsComplete || !inventory.LocalPathReferences.TryGetValue(Path.GetFullPath(path), out var owners)
            || !owners.Any(o => o.Torrent.ClientInstanceId.Equals(instanceId, StringComparison.OrdinalIgnoreCase)
                && o.Torrent.Hash.Equals(hash, StringComparison.OrdinalIgnoreCase))) return false;
        foreach (var owner in owners)
            if (!await ProtectionAllowsAsync(owner.Torrent.ClientInstanceId, owner.Torrent.Hash, ct, owner.Torrent)) return false;
        if (ValidateSource(path, cfg) != null) return false;
        ct.ThrowIfCancellationRequested();
        File.Delete(path);
        return true;
    }

    public async Task<SafeDeletionResult> QuarantineFileAsync(string clientInstanceId, string localPath, CancellationToken ct = default)
    {
        var cfg = _config.GetTorrentClient(clientInstanceId);
        if (cfg == null) return new(false, true, 0, "Torrent client is unconfigured.");
        var validation = ValidateSource(localPath, cfg);
        if (validation != null) return new(false, true, 0, validation);
        var destination = BuildRecycleDestination(localPath, clientInstanceId, "orphans", cfg);
        var entry = Ledger.Prepare(clientInstanceId, localPath, destination);
        try
        {
            var bytes = await MoveToRecycleAsync(localPath, destination, ct);
            Ledger.Write(entry with { State = "committed", RecycledAt = DateTimeOffset.UtcNow });
            return new(true, false, bytes);
        }
        catch { Ledger.Write(entry with { State = "needs-recovery" }); throw; }
    }

    public IReadOnlyList<string> GetRecoveryPaths() => Ledger.Read().Where(e => e.State is "preparing" or "needs-recovery")
        .SelectMany(e => new[] { e.Source, e.Destination }).Where(Path.IsPathFullyQualified).Distinct().ToList();

    public IReadOnlyList<RecycleEntry> InspectRecycleEntries(string clientInstanceId)
    {
        var cfg = _config.GetTorrentClient(clientInstanceId) ?? throw new InvalidOperationException("Unknown client.");
        var baseRoot = ResolveRecycleRoot(cfg);
        var root = cfg.Maintenance.RecycleBin.SplitByClient ? Path.Combine(baseRoot, Sanitize(clientInstanceId)) : baseRoot;
        var entries = Ledger.Read();
        var owned = entries.Where(e => e.ClientInstanceId.Equals(clientInstanceId, StringComparison.OrdinalIgnoreCase)
            && e.State is not ("restored" or "purged")).ToList();
        if (!Directory.Exists(root))
        {
            if (owned.Count > 0) throw new IOException("Recorded recycle storage is missing; verify the mount before cleanup.");
            var parent = Directory.Exists(baseRoot) ? baseRoot : Path.GetDirectoryName(baseRoot);
            if (parent == null || _paths.ValidateLocalPath(parent, parent) != null) throw new IOException("Recycle root is inaccessible.");
            return owned;
        }
        if (_paths.ValidateLocalPath(root, root) != null) throw new IOException("Recycle root is inaccessible or unsafe.");
        var sharedLegacy = !cfg.Maintenance.RecycleBin.SplitByClient && _config.GetAllTorrentClients().Any(p =>
        {
            if (p.Key.Equals(clientInstanceId, StringComparison.OrdinalIgnoreCase)) return false;
            try { var other = ResolveRecycleRoot(p.Value); return _paths.IsSafeDescendant(other, root) || _paths.IsSafeDescendant(root, other); }
            catch (InvalidOperationException) { return false; }
        });
        var options = new EnumerationOptions { AttributesToSkip = FileAttributes.ReparsePoint, IgnoreInaccessible = false };
        var folders = cfg.Maintenance.RecycleBin.SplitByCategory ? Directory.EnumerateDirectories(root, "*", options) : new[] { root };
        foreach (var folder in folders)
            foreach (var path in Directory.EnumerateFileSystemEntries(folder, "*", options))
            {
                if (entries.Any(e => string.Equals(e.Destination, path, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))) continue;
                owned.Add(new(RecycleLedger.Key(clientInstanceId, path), clientInstanceId, path, path,
                    DateTimeOffset.UtcNow, path.EndsWith(".partial", StringComparison.OrdinalIgnoreCase) ? "needs-recovery" : sharedLegacy ? "ambiguous" : "legacy"));
            }
        return owned;
    }

    public IReadOnlyList<RecycleFileSnapshot> InspectRecycleFiles(RecycleEntry entry)
    {
        var cfg = _config.GetTorrentClient(entry.ClientInstanceId) ?? throw new InvalidOperationException("Unknown recycle owner.");
        var root = ClientRecycleDirectory(cfg, entry.ClientInstanceId) ?? ResolveRecycleRoot(cfg);
        if (!_paths.IsSafeDescendant(entry.Destination, root) || _paths.ValidateLocalPath(entry.Destination, root) != null)
            throw new IOException("Recycle entry is inaccessible or outside its configured root.");
        if (!File.Exists(entry.Destination) && !Directory.Exists(entry.Destination)) throw new IOException("Recorded recycled content is missing; verify storage.");
        var files = new List<RecycleFileSnapshot>();
        var pending = new Stack<string>(); pending.Push(entry.Destination);
        while (pending.TryPop(out var path))
        {
            if (_paths.ValidateLocalPath(path, root) != null) throw new IOException("Unsafe recycle path.");
            FileAttributes attributes;
            try { attributes = File.GetAttributes(path); }
            catch (FileNotFoundException) { continue; }
            catch (DirectoryNotFoundException) { continue; }
            if ((attributes & FileAttributes.ReparsePoint) != 0) throw new IOException("Recycle links require manual investigation.");
            if ((attributes & FileAttributes.Directory) != 0)
            {
                foreach (var child in Directory.EnumerateFileSystemEntries(path)) pending.Push(child);
            }
            else
            {
                var info = new FileInfo(path); files.Add(new(path, info.Length, info.LastWriteTimeUtc.Ticks));
            }
        }
        return files;
    }

    public void RegisterLegacyRecycleEntry(string clientInstanceId, string path)
    {
        var entry = InspectRecycleEntries(clientInstanceId).FirstOrDefault(e => e.Destination == path && e.State == "legacy")
            ?? throw new InvalidOperationException("Legacy recycle entry changed or ownership is ambiguous.");
        _ = InspectRecycleFiles(entry);
        Ledger.Write(entry with { State = "committed", RecycledAt = DateTimeOffset.UtcNow });
    }

    public Task<long> PurgeRecycleEntryAsync(string clientInstanceId, string key, IReadOnlyList<RecycleFileSnapshot> files, CancellationToken ct = default)
    {
        var cfg = _config.GetTorrentClient(clientInstanceId) ?? throw new InvalidOperationException("Unknown client.");
        var entry = Ledger.Read().SingleOrDefault(e => e.Key == key && e.ClientInstanceId.Equals(clientInstanceId, StringComparison.OrdinalIgnoreCase));
        if (entry == null || entry.State != "committed" || !cfg.Maintenance.RecycleBin.Enabled || !cfg.Maintenance.RecycleBin.RetentionEnabled
            || (DateTimeOffset.UtcNow - entry.RecycledAt).TotalDays < cfg.Maintenance.RecycleBin.RetentionDays)
            throw new InvalidOperationException("Recycle entry is not eligible for retention.");
        var current = InspectRecycleFiles(entry).ToDictionary(f => f.Path);
        long bytes = 0;
        foreach (var file in files.Take(cfg.Maintenance.RecycleBin.MaxFilesDeletedPerRun))
        {
            ct.ThrowIfCancellationRequested();
            if (!current.TryGetValue(file.Path, out var fresh) || fresh != file) throw new IOException("Recycle file changed after preview; check again.");
            if (_paths.ValidateLocalPath(file.Path, ClientRecycleDirectory(cfg, clientInstanceId) ?? ResolveRecycleRoot(cfg), true) != null) throw new IOException("Recycle path changed before deletion.");
            var info = new FileInfo(file.Path);
            if (info.Length != file.Bytes || info.LastWriteTimeUtc.Ticks != file.LastWriteTicks) throw new IOException("Recycle file changed before deletion.");
            File.Delete(file.Path); bytes += file.Bytes;
        }
        if ((!File.Exists(entry.Destination) && !Directory.Exists(entry.Destination)) || InspectRecycleFiles(entry).Count == 0)
        {
            if (Directory.Exists(entry.Destination))
            {
                // Never recursively remove an uninspected file or link added during purge.
                var options = new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = FileAttributes.ReparsePoint, IgnoreInaccessible = false };
                foreach (var directory in Directory.EnumerateDirectories(entry.Destination, "*", options).OrderByDescending(p => p.Length))
                    if (_paths.ValidateLocalPath(directory, root: ClientRecycleDirectory(cfg, clientInstanceId) ?? ResolveRecycleRoot(cfg)) == null && !Directory.EnumerateFileSystemEntries(directory).Any()) Directory.Delete(directory);
                if (!Directory.EnumerateFileSystemEntries(entry.Destination).Any()) Directory.Delete(entry.Destination);
            }
            Ledger.Write(entry with { State = "purged" });
        }
        return Task.FromResult(bytes);
    }

    public async Task<int> CleanupExpiredAsync(string clientInstanceId, CancellationToken ct = default)
    {
        var cfg = _config.GetTorrentClient(clientInstanceId);
        if (cfg == null || !cfg.Maintenance.RecycleBin.Enabled || !cfg.Maintenance.RecycleBin.RetentionEnabled) return 0;
        var remaining = cfg.Maintenance.RecycleBin.MaxFilesDeletedPerRun;
        var deleted = 0;
        foreach (var entry in InspectRecycleEntries(clientInstanceId))
        {
            if (entry.State == "legacy") { RegisterLegacyRecycleEntry(clientInstanceId, entry.Destination); continue; }
            if (entry.State != "committed" || (DateTimeOffset.UtcNow - entry.RecycledAt).TotalDays < cfg.Maintenance.RecycleBin.RetentionDays) continue;
            var files = InspectRecycleFiles(entry).Take(remaining).ToList();
            await PurgeRecycleEntryAsync(clientInstanceId, entry.Key, files, ct);
            remaining -= files.Count; deleted += files.Count;
            if (remaining == 0) break;
        }
        return deleted;
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
        if (root == null || _paths.ValidateLocalPath(full, root) != null || HasReparsePointInPath(full, Path.GetFullPath(root)))
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

    public static string? ClientRecycleDirectory(TorrentClientInstanceConfig cfg, string instanceId)
        => !cfg.Maintenance.RecycleBin.SplitByClient
            || string.IsNullOrWhiteSpace(cfg.Maintenance.RecycleBin.Path) && cfg.Maintenance.PathMappings.Count == 0
            ? null : Path.Combine(ResolveRecycleRoot(cfg), Sanitize(instanceId));

    private static string ResolveRecycleRoot(TorrentClientInstanceConfig cfg)
    {
        if (!string.IsNullOrWhiteSpace(cfg.Maintenance.RecycleBin.Path))
            return Path.GetFullPath(cfg.Maintenance.RecycleBin.Path);
        var mapping = cfg.Maintenance.PathMappings.FirstOrDefault()
            ?? throw new InvalidOperationException("A path mapping is required for recycle-bin operations.");
        return Path.Combine(Path.GetFullPath(mapping.LocalPath), ".torrentarr-recycle");
    }

    private sealed class RecyclePublishedException(string destination)
        : IOException($"Source removal failed; copied content preserved at {destination}.");

    private async Task<long> MoveToRecycleAsync(string source, string destination, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        try
        {
            if (Directory.Exists(source)) Directory.Move(source, destination); else File.Move(source, destination);
        }
        catch (IOException)
        {
            // Publish a private copy with a non-overwriting move; collisions never own the destination.
            var temporary = destination + "." + Guid.NewGuid().ToString("N") + ".partial";
            var copied = false;
            try
            {
                if (Directory.Exists(source))
                {
                    await CopyDirectoryAsync(source, temporary, ct);
                    foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
                        if (new FileInfo(file).Length != new FileInfo(Path.Combine(temporary, Path.GetRelativePath(source, file))).Length)
                            throw new IOException("Recycle directory copy verification failed.");
                    ct.ThrowIfCancellationRequested();
                    Directory.Move(temporary, destination);
                    copied = true;
                    Directory.Delete(source, true);
                }
                else
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                    await using (var input = File.OpenRead(source))
                    await using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                    {
                        await input.CopyToAsync(output, ct);
                        await output.FlushAsync(ct);
                    }
                    if (new FileInfo(source).Length != new FileInfo(temporary).Length)
                        throw new IOException("Recycle copy verification failed.");
                    ct.ThrowIfCancellationRequested();
                    File.Move(temporary, destination);
                    copied = true;
                    File.Delete(source);
                }
            }
            catch
            {
                if (copied) throw new RecyclePublishedException(destination);
                throw;
            }
            finally
            {
                if (File.Exists(temporary)) File.Delete(temporary);
                else if (Directory.Exists(temporary)) Directory.Delete(temporary, true);
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

using Torrentarr.Core.Configuration;
using Microsoft.Extensions.Logging;
using Torrentarr.Core.Interfaces;
using Torrentarr.Core.Models;
using Torrentarr.Core.Services;

namespace Torrentarr.Infrastructure.Services;

public sealed class TorrentInventoryService(ITorrentClientRegistry registry, TorrentarrConfig config,
    IPathMappingService paths, ILogger<TorrentInventoryService> logger) : ITorrentInventoryService
{
    public Task<TorrentInventory> BuildAsync(IEnumerable<string>? instanceIds = null, CancellationToken ct = default)
        => CollectAsync((instanceIds ?? config.GetAllTorrentClients().Keys).Distinct(StringComparer.OrdinalIgnoreCase)
            .ToDictionary(id => id, _ => InventoryRequirements.All, StringComparer.OrdinalIgnoreCase), ct);
    public Task<TorrentInventory> RefreshAsync(TorrentInventory previous, string? candidateInstance = null, string? candidateHash = null, CancellationToken ct = default)
        => BuildAsync(ct: ct);
    public Task<TorrentInventory> RefreshCandidateAsync(string instanceId, string hash, CancellationToken ct = default)
        => CollectAsync(new Dictionary<string, InventoryRequirements> { [instanceId] = InventoryRequirements.All & ~InventoryRequirements.GlobalOwnership }, ct, hash);

    public async Task<TorrentInventory> CollectAsync(IReadOnlyDictionary<string, InventoryRequirements> requirements, CancellationToken ct = default, string? candidateHash = null)
    {
        var ownership = requirements.Values.Any(r => r.HasFlag(InventoryRequirements.GlobalOwnership));
        var result = new TorrentInventory { OwnershipRequested = ownership && candidateHash == null };
        var configured = config.GetAllTorrentClients();
        if (ownership)
        {
            foreach (var (id, cfg) in configured.Where(p => HasEndpoint(p.Value)))
            {
                var files = InventoryRequirements.Files | InventoryRequirements.LocalFiles;
                if (requirements.TryGetValue(id, out var required) && (required & files) == files) continue;
                result.OwnershipComplete = false;
                MaintenanceDiagnostic.Add(result.Diagnostics, "ownership-not-inspected", id,
                    "Global file ownership was not requested for this client.", "Collect files and mapped paths from every configured owner before changing content.");
            }
        }
        foreach (var (id, required) in requirements)
        {
            ct.ThrowIfCancellationRequested();
            if (required == InventoryRequirements.None) continue;
            var cfg = configured.GetValueOrDefault(id);
            if (cfg == null || !HasEndpoint(cfg)) { Issue("client-unavailable", "Torrent client has no usable endpoint."); continue; }
            if (cfg.Disabled) { Issue("client-disabled", "Disabled client ownership is unknown."); continue; }
            var client = registry.GetClient(id);
            if (client == null) { Issue("client-unavailable", "Torrent client is unavailable."); continue; }
            if (required.HasFlag(InventoryRequirements.Files) && !client.Capabilities.FileEnumeration)
                Issue("file-enumeration-unsupported", "Client cannot enumerate file ownership.");
            var local = required.HasFlag(InventoryRequirements.LocalFiles);
            if (local)
                foreach (var error in paths.Validate(cfg.Maintenance.PathMappings)) Issue("invalid-mapping", error);
            List<TorrentInfo> torrents;
            try { torrents = await client.GetTorrentsAsync(cancellationToken: ct); }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning("Unable to list torrents for client {InstanceId}", id);
                Issue("torrent-list-failed", "Torrent listing failed; ownership is unknown."); continue;
            }
            result.InspectedClients.Add(id);
            foreach (var torrent in torrents)
            {
                ct.ThrowIfCancellationRequested();
                if (candidateHash != null && !torrent.Hash.Equals(candidateHash, StringComparison.OrdinalIgnoreCase)) continue;
                torrent.ClientInstanceId = id;
                var item = new TorrentInventoryItem { Torrent = torrent, Client = new(client.Identity, client.Capabilities) };
                result.Torrents.Add(item);
                if (required.HasFlag(InventoryRequirements.Properties) && !torrent.IsPrivate.HasValue)
                {
                    try { torrent.IsPrivate = (await client.GetTorrentPropertiesAsync(torrent.Hash, ct))?.IsPrivate; }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    { Issue("properties-failed", "Torrent privacy inspection failed.", torrent, blocksOwnership: false); }
                }
                if (required.HasFlag(InventoryRequirements.Files))
                {
                    if (!client.Capabilities.FileEnumeration) Issue("file-enumeration-unsupported", "Client cannot enumerate file ownership.", torrent);
                    else
                    {
                        try
                        {
                            var files = await client.GetTorrentFilesAsync(torrent.Hash, ct);
                            item.FilesComplete = files.Count > 0 || torrent.Size == 0;
                            item.LocalFilesComplete = local && item.FilesComplete;
                            if (!item.FilesComplete) Issue("empty-file-list", "File ownership is unavailable for a nonempty torrent.", torrent);
                            foreach (var file in files)
                            {
                                // Adapter names are relative to SavePath, including the top directory for multi-file torrents.
                                var fileName = file.Name.Replace('\\', '/');
                                var clientPath = torrent.SavePath.Replace('\\', '/').TrimEnd('/') + "/" + fileName;
                                var invalidName = fileName.StartsWith('/') || fileName.Length > 1 && fileName[1] == ':';
                                var mapping = local && !invalidName ? paths.Resolve(clientPath, cfg.Maintenance.PathMappings) : null;
                                var localPath = mapping?.LocalPath;
                                if (local)
                                {
                                    var code = invalidName ? "unsafe-path" : mapping?.Code;
                                    code ??= localPath != null ? paths.ValidateLocalPath(localPath, mapping!.LocalRoot!, true) : "unmatched-prefix";
                                    if (code != null)
                                    {
                                        item.LocalFilesComplete = false;
                                        Issue(code, mapping?.Error ?? LocalError(code), torrent, clientPath, localPath ?? mapping?.LocalRoot, true, code != "missing-file");
                                        if (code != "missing-file") localPath = null;
                                    }
                                }
                                item.Files.Add(new() { File = file, ClientPath = clientPath, LocalPath = localPath });
                            }
                        }
                        catch (Exception ex) when (ex is not OperationCanceledException)
                        {
                            item.FilesComplete = false; item.LocalFilesComplete = false;
                            Issue("file-list-failed", "Torrent file enumeration failed.", torrent);
                        }
                    }
                }
                if (required.HasFlag(InventoryRequirements.Trackers))
                {
                    try { item.Trackers.AddRange(await client.GetTorrentTrackersAsync(torrent.Hash, ct)); item.TrackersComplete = true; }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    { Issue("tracker-list-failed", "Tracker inspection failed.", torrent, blocksOwnership: false); }
                }
            }
            void Issue(string code, string message, TorrentInfo? torrent = null, string? clientPath = null,
                string? localPath = null, bool file = false, bool blocksOwnership = true)
            {
                if (blocksOwnership) result.OwnershipComplete = false;
                MaintenanceDiagnostic.Add(result.Diagnostics, code, id, message,
                    code is "client-unavailable" or "client-disabled" or "torrent-list-failed" or "tracker-list-failed" or "file-list-failed"
                        ? "Check the client's connection and permissions, then preview again."
                        : "Check Maintenance.PathMappings and the local filesystem mount in Config, then preview again.",
                    torrent != null || clientPath != null || localPath != null ? new(torrent?.Hash, torrent?.Name, clientPath, localPath) : null, file);
            }
        }
        foreach (var item in result.Torrents)
            foreach (var path in item.Files.Select(f => f.LocalPath).OfType<string>().Distinct())
            {
                var full = Path.GetFullPath(path);
                if (!result.LocalPathReferences.TryGetValue(full, out var owners)) result.LocalPathReferences[full] = owners = [];
                owners.Add(item);
            }
        result.Warnings.AddRange(result.Diagnostics.Select(d => $"[{d.ClientInstanceId}] {d.Message} ({d.AffectedFiles} files, {d.AffectedTorrents} torrents)"));
        return result;
    }
    private static bool HasEndpoint(TorrentClientInstanceConfig cfg) => !string.IsNullOrWhiteSpace(cfg.Host) && cfg.Host != "CHANGE_ME";
    private static string LocalError(string code) => code switch
    {
        "missing-file" => "Mapped torrent file does not exist locally.",
        "unsafe-path" => "Torrent path contains traversal or a symlink/reparse-point ancestor.",
        "inaccessible-local-root" => "Mapped local root is missing or inaccessible.",
        _ => "Torrent file has no usable local mapping."
    };
}

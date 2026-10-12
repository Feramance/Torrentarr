using Torrentarr.Core.Configuration;
using Microsoft.Extensions.Logging;
using Torrentarr.Core.Interfaces;
using Torrentarr.Core.Models;
using Torrentarr.Core.Services;

namespace Torrentarr.Infrastructure.Services;

public sealed class TorrentInventoryService(
    ITorrentClientRegistry registry,
    TorrentarrConfig config,
    IPathMappingService paths,
    ILogger<TorrentInventoryService> logger) : ITorrentInventoryService
{
    public Task<TorrentInventory> BuildAsync(IEnumerable<string>? instanceIds = null, CancellationToken ct = default)
        => ReadAsync(null, instanceIds?.ToHashSet(StringComparer.OrdinalIgnoreCase), null, null, ct);

    public Task<TorrentInventory> RefreshAsync(TorrentInventory previous, string? candidateInstance = null, string? candidateHash = null, CancellationToken ct = default)
        => ReadAsync(previous.IsComplete ? previous : null, null, candidateInstance, candidateHash, ct);

    private async Task<TorrentInventory> ReadAsync(TorrentInventory? previous, HashSet<string>? selected,
        string? candidateInstance, string? candidateHash, CancellationToken ct)
    {
        var result = new TorrentInventory();
        var old = previous?.Torrents.ToDictionary(i => (i.Torrent.ClientInstanceId.ToUpperInvariant(), i.Torrent.Hash.ToUpperInvariant()));
        foreach (var (id, cfg) in config.GetAllTorrentClients())
        {
            if (cfg.Disabled || cfg.Host == "CHANGE_ME" || (selected is { Count: > 0 } && !selected.Contains(id))) continue;
            var client = registry.GetClient(id);
            if (client == null)
            {
                result.Warnings.Add($"[{id}] Torrent client is unavailable.");
                continue;
            }
            result.Warnings.AddRange(paths.Validate(cfg.Maintenance.PathMappings).Select(e => $"[{id}] {e}"));
            if (!client.Capabilities.FileEnumeration)
            {
                result.Warnings.Add($"[{id}] Client cannot enumerate file ownership.");
            }
            try
            {
                var torrents = await client.GetTorrentsAsync(cancellationToken: ct);
                foreach (var torrent in torrents)
                {
                    torrent.ClientInstanceId = id;
                    var item = new TorrentInventoryItem { Torrent = torrent, Client = new(client.Identity, client.Capabilities) };
                    var cached = old?.GetValueOrDefault((id.ToUpperInvariant(), torrent.Hash.ToUpperInvariant()));
                    var candidate = id.Equals(candidateInstance, StringComparison.OrdinalIgnoreCase)
                        && torrent.Hash.Equals(candidateHash, StringComparison.OrdinalIgnoreCase);
                    // shortcut: reuse files only while exposed ownership metadata is unchanged; adapters with hidden renames must force refresh.
                    if (!candidate && cached != null && cached.Client.Identity == client.Identity
                        && cached.Torrent.SavePath == torrent.SavePath && cached.Torrent.ContentPath == torrent.ContentPath
                        && cached.Torrent.Name == torrent.Name && cached.Torrent.Size == torrent.Size)
                    {
                        item.Files.AddRange(cached.Files);
                        item.Trackers.AddRange(cached.Trackers);
                    }
                    else
                    {
                        var files = client.Capabilities.FileEnumeration ? await client.GetTorrentFilesAsync(torrent.Hash, ct) : [];
                        if (files.Count == 0 && torrent.Size > 0)
                            result.Warnings.Add($"[{id}] File ownership is unavailable for torrent {torrent.Hash}.");
                        foreach (var file in files)
                        {
                            var clientPath = torrent.SavePath.TrimEnd('/', '\\') + "/" + file.Name.TrimStart('/', '\\');
                            var localPath = paths.MapToLocal(clientPath, cfg.Maintenance.PathMappings);
                            if (localPath == null) result.Warnings.Add($"[{id}] A torrent file could not be mapped locally.");
                            item.Files.Add(new() { File = file, ClientPath = clientPath, LocalPath = localPath });
                        }
                        item.Trackers.AddRange(await client.GetTorrentTrackersAsync(torrent.Hash, ct));
                    }
                    result.Torrents.Add(item);
                }
                result.InspectedClients.Add(id);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning("Unable to inventory torrent client {InstanceId}", id);
                result.Warnings.Add($"[{id}] Inventory request failed.");
            }
        }
        foreach (var item in result.Torrents)
            foreach (var path in item.Files.Select(f => f.LocalPath).OfType<string>())
            {
                var full = Path.GetFullPath(path);
                if (!result.LocalPathReferences.TryGetValue(full, out var owners)) result.LocalPathReferences[full] = owners = [];
                owners.Add(item);
            }
        return result;
    }
}

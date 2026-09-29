using Torrentarr.Core.Configuration;
using Microsoft.Extensions.Logging;
using Torrentarr.Core.Interfaces;
using Torrentarr.Core.Models;
using Torrentarr.Core.Services;

namespace Torrentarr.Infrastructure.Services;

public sealed class TorrentInventoryService : ITorrentInventoryService
{
    private readonly ITorrentClientRegistry _registry;
    private readonly TorrentarrConfig _config;
    private readonly IPathMappingService _paths;
    private readonly ILogger<TorrentInventoryService> _logger;

    public TorrentInventoryService(
        ITorrentClientRegistry registry,
        TorrentarrConfig config,
        IPathMappingService paths,
        ILogger<TorrentInventoryService> logger)
    {
        _registry = registry;
        _config = config;
        _paths = paths;
        _logger = logger;
    }

    public async Task<TorrentInventory> BuildAsync(IEnumerable<string>? instanceIds = null, CancellationToken ct = default)
    {
        var selected = instanceIds?.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var result = new TorrentInventory();
        var comparer = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
        var references = new Dictionary<string, List<TorrentInventoryItem>>(comparer);

        foreach (var (instanceId, client) in _registry.GetAllClients())
        {
            if (selected is { Count: > 0 } && !selected.Contains(instanceId)) continue;
            var clientConfig = _config.GetTorrentClient(instanceId);
            if (clientConfig == null)
            {
                result.Warnings.Add($"No configuration found for connected torrent client '{instanceId}'.");
                continue;
            }

            var mappingErrors = _paths.Validate(clientConfig.Maintenance.PathMappings);
            result.Warnings.AddRange(mappingErrors.Select(e => $"[{instanceId}] {e}"));

            try
            {
                var torrents = await client.GetTorrentsAsync(cancellationToken: ct);
                foreach (var torrent in torrents)
                {
                    torrent.ClientInstanceId = instanceId;
                    var item = new TorrentInventoryItem
                    {
                        Torrent = torrent,
                        Client = new TorrentClientView(client.Identity, client.Capabilities)
                    };

                    if (client.Capabilities.FileEnumeration)
                    {
                        foreach (var file in await client.GetTorrentFilesAsync(torrent.Hash, ct))
                        {
                            var clientPath = CombineClientPath(torrent.SavePath, file.Name);
                            var localPath = _paths.MapToLocal(clientPath, clientConfig.Maintenance.PathMappings);
                            item.Files.Add(new TorrentFileInventoryItem
                            {
                                File = file,
                                ClientPath = clientPath,
                                LocalPath = localPath
                            });
                        }
                    }

                    item.Trackers.AddRange(await client.GetTorrentTrackersAsync(torrent.Hash, ct));
                    result.Torrents.Add(item);
                    foreach (var localPath in item.Files.Select(f => f.LocalPath).Where(p => p != null).Cast<string>())
                    {
                        var normalized = Path.GetFullPath(localPath);
                        if (!references.TryGetValue(normalized, out var owners))
                            references[normalized] = owners = new List<TorrentInventoryItem>();
                        owners.Add(item);
                    }
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "Unable to inventory torrent client {InstanceId}", instanceId);
                result.Warnings.Add($"[{instanceId}] Inventory failed: {ex.Message}");
            }
        }

        foreach (var (path, owners) in references) result.LocalPathReferences[path] = owners;
        return result;
    }

    private static string CombineClientPath(string root, string relative)
    {
        var separator = root.Contains('\\') && !root.Contains('/') ? '\\' : '/';
        return root.TrimEnd('/', '\\') + separator + relative.TrimStart('/', '\\');
    }
}

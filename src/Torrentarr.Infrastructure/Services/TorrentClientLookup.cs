using Microsoft.Extensions.Logging;
using Torrentarr.Core.Interfaces;
using Torrentarr.Core.Models;

namespace Torrentarr.Infrastructure.Services;

internal static class TorrentClientLookup
{
    public static async Task<TorrentInfo?> FindAsync(ITorrentClientRegistry registry, string hash, string? instanceId, ILogger logger, CancellationToken ct)
    {
        TorrentInfo? result = null;
        var incomplete = false;
        IReadOnlyDictionary<string, ITorrentClient> clients;
        if (instanceId == null) clients = registry.GetAllClients();
        else
            clients = registry.GetClient(instanceId) is { } named
                ? new Dictionary<string, ITorrentClient> { [instanceId] = named }
                : new Dictionary<string, ITorrentClient>();
        foreach (var (id, client) in clients)
        {
            ct.ThrowIfCancellationRequested();
            List<TorrentInfo> torrents;
            try { torrents = await client.GetTorrentsAsync(cancellationToken: ct); }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "Unable to read torrent client {Instance}", id);
                incomplete = true;
                continue;
            }
            var found = torrents.FirstOrDefault(t => t.Hash.Equals(hash, StringComparison.OrdinalIgnoreCase));
            if (found == null) continue;
            if (result != null) throw new InvalidOperationException("Torrent hash belongs to multiple clients; specify an instance.");
            found.ClientInstanceId = id;
            result = found;
        }
        if (instanceId == null && incomplete)
            throw new HttpRequestException("Torrent lookup is incomplete; specify a reachable instance.");
        return result;
    }
}

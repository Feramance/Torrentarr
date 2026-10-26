using Microsoft.Extensions.Logging;
using Torrentarr.Core.Configuration;
using Torrentarr.Core.Interfaces;
using Torrentarr.Core.Models;

namespace Torrentarr.Infrastructure.Services;

public sealed record FreeSpaceTorrent(string InstanceName, ITorrentClient Client, TorrentInfo Torrent);

public sealed class FreeSpaceInventory
{
    public List<FreeSpaceTorrent> Torrents { get; } = [];
    public HashSet<string> FailedClients { get; } = new(StringComparer.OrdinalIgnoreCase);
    public bool Complete => FailedClients.Count == 0;

    public static async Task PauseAndRollbackMarkerAsync(ITorrentClient client, string hash, Func<Task> clearMarker, CancellationToken ct)
    {
        if (await client.PauseTorrentAsync(hash, ct)) return;
        try { await clearMarker(); }
        catch (Exception ex) { throw new IOException("Free-space pause was rejected and marker rollback failed.", ex); }
        throw new IOException("Torrent client rejected free-space pause; pause marker rolled back.");
    }

    public static async Task ResumeAndClearMarkerAsync(ITorrentClient client, string hash, Func<Task> clearMarker, CancellationToken ct)
    {
        if (!await client.ResumeTorrentAsync(hash, ct))
            throw new IOException("Torrent client rejected free-space resume.");
        try { await clearMarker(); }
        catch (Exception ex)
        {
            if (!await client.PauseTorrentAsync(hash, CancellationToken.None))
                throw new IOException("Free-space marker cleanup failed and restoring the paused state was rejected.", ex);
            throw;
        }
    }

    public static async Task<FreeSpaceInventory> ReadAsync(TorrentarrConfig config, ITorrentClientRegistry registry,
        ILogger logger, CancellationToken ct)
    {
        var result = new FreeSpaceInventory();
        var clients = registry.GetAllClients();
        foreach (var (id, cfg) in config.GetAllTorrentClients())
            if (!string.IsNullOrWhiteSpace(cfg.Host) && cfg.Host != "CHANGE_ME" && !cfg.Disabled && !clients.ContainsKey(id))
                result.FailedClients.Add(id);
        foreach (var (id, client) in clients)
        {
            if (config.GetTorrentClient(id) is not { Disabled: false }) continue;
            try
            {
                // One complete response avoids publishing a client's partially read categories.
                var torrents = await client.GetTorrentsAsync(cancellationToken: ct);
                foreach (var torrent in torrents) torrent.QBitInstanceName = id;
                result.Torrents.AddRange(torrents.Select(t => new FreeSpaceTorrent(id, client, t)));
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                result.FailedClients.Add(id);
                logger.LogWarning(ex, "[{Instance}] FreeSpace: unable to enumerate torrents", id);
            }
        }
        if (!result.Complete) logger.LogWarning("FreeSpace: incomplete download inventory; resumes withheld for all storage");
        return result;
    }

}

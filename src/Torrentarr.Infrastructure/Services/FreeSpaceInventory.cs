using Microsoft.Extensions.Logging;
using Torrentarr.Core.Configuration;
using Torrentarr.Core.Interfaces;
using Torrentarr.Core.Models;

namespace Torrentarr.Infrastructure.Services;

public sealed record FreeSpaceTorrent(string InstanceName, ITorrentClient Client, TorrentInfo Torrent);

public sealed class FreeSpaceInventory
{
    public List<FreeSpaceTorrent> Torrents { get; } = [];
    public bool Complete { get; private set; } = true;

    public static async Task<FreeSpaceInventory> ReadAsync(TorrentarrConfig config, ITorrentClientRegistry registry,
        ILogger logger, CancellationToken ct)
    {
        var result = new FreeSpaceInventory();
        var clients = registry.GetAllClients();
        foreach (var (id, cfg) in config.GetAllTorrentClients())
            if (!string.IsNullOrWhiteSpace(cfg.Host) && cfg.Host != "CHANGE_ME" && (cfg.Disabled || !clients.ContainsKey(id)))
                result.Complete = false;
        foreach (var (id, client) in clients)
        {
            try
            {
                // One complete response avoids publishing a client's partially read categories.
                var torrents = await client.GetTorrentsAsync(cancellationToken: ct);
                foreach (var torrent in torrents) torrent.QBitInstanceName = id;
                result.Torrents.AddRange(torrents.Select(t => new FreeSpaceTorrent(id, client, t)));
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                result.Complete = false;
                logger.LogWarning(ex, "[{Instance}] FreeSpace: unable to enumerate torrents", id);
            }
        }
        if (!result.Complete) logger.LogWarning("FreeSpace: incomplete download inventory; resumes withheld for this pass");
        return result;
    }

    public long UnmanagedRemaining(Func<TorrentInfo, bool> managed) => Torrents
        .Where(t => !managed(t.Torrent) && TorrentPolicyHelper.IsFreeSpaceDownloadState(t.Torrent.State)
            && !TorrentPolicyHelper.IsPausedDownloadStateForFreeSpace(t.Torrent.State))
        .Sum(t => Math.Max(0, t.Torrent.AmountLeft));

    public async Task ApplyAsync(IEnumerable<FreeSpaceTorrent> ordered,
        Func<FreeSpaceTorrent, List<Func<Task>>?, Task> process, ILogger logger, CancellationToken ct)
    {
        List<Func<Task>>? resumes = Complete ? [] : null;
        var safe = Complete;
        foreach (var item in ordered)
        {
            ct.ThrowIfCancellationRequested();
            try { await process(item, resumes); }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                safe = false;
                logger.LogWarning(ex, "[{Instance}] FreeSpace: operation failed; resumes withheld", item.InstanceName);
            }
        }
        // Protective pauses must succeed before any newly allocated download resumes.
        if (safe && resumes != null)
            foreach (var resume in resumes)
            {
                ct.ThrowIfCancellationRequested();
                try { await resume(); }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    logger.LogWarning(ex, "FreeSpace: resume failed; remaining resumes withheld");
                    break;
                }
            }
    }
}

using System.Text.RegularExpressions;
using Torrentarr.Core.Configuration;
using Torrentarr.Core.Interfaces;
using Torrentarr.Core.Models;
using Torrentarr.Infrastructure.Database;

namespace Torrentarr.Infrastructure.Services;

internal static class SharedFileFilterPriorities
{
    internal sealed class State
    {
        public long AddedOn { get; set; }
        public List<Entry> Entries { get; set; } = [];
    }
    internal sealed record Entry(int Index, string Name, int Priority);

    internal static State Read(TorrentarrDbContext db, TorrentInfo torrent)
    {
        var state = MaintenancePersistence.Read<State>(db, Key(torrent));
        return state.AddedOn == torrent.AddedOn && torrent.AddedOn > 0 ? state : new() { AddedOn = torrent.AddedOn };
    }

    private static string Key(TorrentInfo torrent)
        => MaintenancePersistence.Key(torrent.ClientInstanceId, torrent.Hash, "shared-filter-priorities");

    internal static async Task<bool> ReconcileAsync(TorrentarrDbContext db, ITorrentClient client, TorrentInfo torrent,
        TorrentConfig cfg, IReadOnlyList<TorrentFileRecord> files, IReadOnlyList<TorrentFileRecord> excluded, CancellationToken ct)
    {
        var state = Read(db, torrent);
        var options = cfg.CaseSensitiveMatches ? RegexOptions.None : RegexOptions.IgnoreCase;
        var excludedIds = excluded.Select(file => file.Index).ToHashSet();
        foreach (var owned in state.Entries.ToArray())
        {
            var file = files.SingleOrDefault(file => file.Index == owned.Index && file.Name == owned.Name);
            if (file != null && excludedIds.Contains(file.Index)) continue;
            if (file?.Priority == 0
                && !await client.SetFilePriorityAsync(torrent.Hash, [file.Index], owned.Priority, ct))
                return false;
            state.Entries.Remove(owned);
            MaintenancePersistence.Write(db, Key(torrent), state);
        }

        var newEntries = excluded.Where(file => file.Priority > 0
            && FileFilterMatcher.LocalReason(file.Name, cfg, options) == null
            && !state.Entries.Any(owned => owned.Index == file.Index && owned.Name == file.Name)).ToArray();
        if (newEntries.Length > 0 && torrent.AddedOn <= 0) return false;
        state.Entries.AddRange(newEntries.Select(file => new Entry(file.Index, file.Name, file.Priority)));
        if (state.Entries.Count > 0) MaintenancePersistence.Write(db, Key(torrent), state);
        else await MaintenancePersistence.DeleteAsync(db, Key(torrent), ct);
        return true;
    }
}

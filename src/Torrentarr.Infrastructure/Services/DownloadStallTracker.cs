using System.Collections.Concurrent;
using Torrentarr.Core.Configuration;
using Torrentarr.Core.Models;

namespace Torrentarr.Infrastructure.Services;

public sealed class DownloadStallTracker
{
    private sealed record Observation(string Category, long AddedOn, string State, long Downloaded,
        long AmountLeft, long Seen, long Since, long ConfigRevision);
    private readonly ConcurrentDictionary<(string Client, string Hash), Observation> _observations = new();

    internal (long Seconds, bool Progress) Observe(TorrentInfo torrent, bool stalled, long now,
        int gapSeconds, long configRevision)
    {
        var key = (torrent.ClientInstanceId.ToUpperInvariant(), torrent.Hash.ToUpperInvariant());
        if (!stalled || torrent.AddedOn <= 0)
        {
            _observations.TryRemove(key, out _);
            return (0, false);
        }

        var next = new Observation(torrent.Category, torrent.AddedOn, torrent.State, torrent.Downloaded,
            torrent.AmountLeft, now, now, configRevision);
        var progress = false;
        var observation = _observations.AddOrUpdate(key, next, (_, previous) =>
        {
            var continuous = previous.ConfigRevision == configRevision && previous.Category == next.Category
                && previous.AddedOn == next.AddedOn && previous.State == next.State
                && now >= previous.Seen && now - previous.Seen <= gapSeconds;
            progress = continuous && next.Downloaded > previous.Downloaded
                && next.AmountLeft >= 0 && next.AmountLeft < previous.AmountLeft;
            if (!continuous || progress || next.Downloaded < previous.Downloaded || next.AmountLeft > previous.AmountLeft)
                return next;
            return next with { Since = previous.Since };
        });
        return (now - observation.Since, progress);
    }

    internal void InvalidateClient(string clientId)
    {
        var normalized = clientId.ToUpperInvariant();
        foreach (var entry in _observations)
            if (entry.Key.Client == normalized)
                _observations.TryRemove(entry);
    }

    internal void ForgetMissing(string clientId, string? category, IReadOnlyList<TorrentInfo> torrents)
    {
        if (_observations.IsEmpty) return;
        var normalized = clientId.ToUpperInvariant();
        var present = torrents.Select(t => t.Hash).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in _observations)
            if (entry.Key.Client == normalized && !present.Contains(entry.Key.Hash)
                && (category == null || CategoryPathHelper.CategoryEquals(entry.Value.Category, category)))
                _observations.TryRemove(entry);
    }

    internal void Prune(long now)
    {
        foreach (var entry in _observations)
            if (now - entry.Value.Seen > 240)
                _observations.TryRemove(entry);
    }
}

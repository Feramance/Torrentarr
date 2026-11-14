using System.Collections.Concurrent;
using Torrentarr.Core.Configuration;
using Torrentarr.Core.Interfaces;
using Torrentarr.Core.Models;

namespace Torrentarr.Infrastructure.Services;

/// <summary>Complete client observations, refreshed at each client's configured scan cadence.</summary>
public sealed class TorrentStatisticsSnapshots
{
    public sealed record Snapshot(IReadOnlyList<TorrentInfo> Torrents, DateTimeOffset? ObservedAt, string? Error, int ScanIntervalSeconds);
    private sealed class Entry
    {
        public readonly SemaphoreSlim Gate = new(1);
        public Snapshot? Value;
        public DateTimeOffset NextAttempt;
        public long Revision = -1;
        public ITorrentClient? Client;
    }
    private readonly ConcurrentDictionary<string, Entry> _entries = new(StringComparer.OrdinalIgnoreCase);

    public sealed record Observation(DateTimeOffset? ObservedAt, string? Error, int ScanIntervalSeconds);
    public Snapshot Unavailable(string id, TorrentarrConfig config)
    {
        var previous = _entries.GetValueOrDefault(id)?.Value;
        return new(previous?.Torrents ?? [], previous?.ObservedAt, "Client is disconnected.", config.GetTorrentClient(id)!.TorrentScanIntervalSeconds);
    }
    public Dictionary<string, Observation> Describe(TorrentarrConfig config, ITorrentClientRegistry registry)
        => config.GetAllTorrentClients().Where(pair => !pair.Value.Disabled && !string.IsNullOrWhiteSpace(pair.Value.Host) && pair.Value.Host != "CHANGE_ME")
            .ToDictionary(pair => pair.Key, pair =>
            {
                var snapshot = _entries.GetValueOrDefault(pair.Key)?.Value;
                return new Observation(snapshot?.ObservedAt, registry.GetClient(pair.Key) == null ? "Client is disconnected." : snapshot?.Error,
                    pair.Value.TorrentScanIntervalSeconds);
            });

    public async Task<Snapshot> GetAsync(string id, TorrentarrConfig config, ITorrentClient client, CancellationToken ct)
    {
        var entry = _entries.GetOrAdd(id, _ => new());
        await entry.Gate.WaitAsync(ct);
        try
        {
            var interval = config.GetTorrentClient(id)!.TorrentScanIntervalSeconds;
            if (entry.Value != null && entry.NextAttempt > DateTimeOffset.UtcNow && entry.Revision == config.Revision
                && entry.Value.ScanIntervalSeconds == interval && ReferenceEquals(entry.Client, client)) return entry.Value;
            if (!ReferenceEquals(entry.Client, client)) entry.Value = null;
            entry.Client = client;
            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeout.CancelAfter(TimeSpan.FromSeconds(10));
                var torrents = await client.GetTorrentsAsync(cancellationToken: timeout.Token);
                foreach (var torrent in torrents) torrent.QBitInstanceName = id;
                entry.Value = new(torrents, DateTimeOffset.UtcNow, null, interval);
            }
            catch (Exception) when (!ct.IsCancellationRequested)
            {
                entry.Value = new(entry.Value?.Torrents ?? [], entry.Value?.ObservedAt,
                    "Client statistics could not be refreshed.", interval);
            }
            entry.Revision = config.Revision;
            entry.NextAttempt = DateTimeOffset.UtcNow.AddSeconds(interval);
            return entry.Value;
        }
        finally { entry.Gate.Release(); }
    }
}

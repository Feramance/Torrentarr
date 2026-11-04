using Torrentarr.Core.Configuration;

namespace Torrentarr.Core.Services;

/// <summary>Per-client (or shared-storage) deadlines owned by one processing loop.</summary>
public sealed class TorrentScanSchedule(TimeProvider clock, IEqualityComparer<string>? comparer = null)
{
    private readonly Dictionary<string, DateTimeOffset> _next = new(comparer ?? StringComparer.OrdinalIgnoreCase);
    private long _revision = -1;

    public static Dictionary<string, int> Intervals(TorrentarrConfig config) => config.GetAllTorrentClients()
        .Where(pair => !pair.Value.Disabled).ToDictionary(pair => pair.Key, pair => pair.Value.TorrentScanIntervalSeconds, StringComparer.OrdinalIgnoreCase);

    public void Reset() => _next.Clear();

    public HashSet<string> TakeDue(IReadOnlyDictionary<string, int> intervals, long revision)
    {
        if (_revision != revision) { Reset(); _revision = revision; }
        foreach (var key in _next.Keys.Where(key => !intervals.ContainsKey(key)).ToList()) _next.Remove(key);
        var now = clock.GetUtcNow();
        var due = intervals.Keys.Where(key => !_next.TryGetValue(key, out var at) || at <= now).ToHashSet(_next.Comparer);
        foreach (var key in due) _next[key] = now.AddSeconds(intervals[key]);
        return due;
    }

    public void Complete(IEnumerable<string> keys, IReadOnlyDictionary<string, int> intervals)
    {
        var now = clock.GetUtcNow();
        foreach (var key in keys)
        {
            if (!_next.TryGetValue(key, out var at) || at > now) continue;
            var interval = TimeSpan.FromSeconds(intervals[key]);
            // Skip missed ticks instead of starting overlapping or catch-up scans.
            _next[key] = at + interval * ((now - at).Ticks / interval.Ticks + 1);
        }
    }

    public TimeSpan Delay
    {
        get
        {
            if (_next.Count == 0) return TimeSpan.FromSeconds(30);
            var delay = _next.Values.Min() - clock.GetUtcNow();
            return delay > TimeSpan.Zero ? delay : TimeSpan.Zero;
        }
    }
}

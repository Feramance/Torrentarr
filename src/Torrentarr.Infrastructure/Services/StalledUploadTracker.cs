using System.Collections.Concurrent;

namespace Torrentarr.Infrastructure.Services;

/// <summary>
/// Process-lifetime clock of when each torrent hash was first seen in qBittorrent
/// <c>stalledUP</c>. Matches qBitrr <c>_stalled_up_since</c> (5.14.4).
/// Must be a singleton — a scoped <see cref="SeedingService"/> would reset every cycle.
/// </summary>
public sealed class StalledUploadTracker
{
    private readonly ConcurrentDictionary<string, DateTimeOffset> _since = new(StringComparer.OrdinalIgnoreCase);
    private readonly TimeProvider _time;

    public StalledUploadTracker()
        : this(TimeProvider.System)
    {
    }

    public StalledUploadTracker(TimeProvider time)
    {
        _time = time;
    }

    /// <summary>
    /// Record or clear the first time this hash was seen in stalled upload.
    /// Returns the stamp, or null when the torrent is not currently stalled.
    /// Queued, paused, stopped, forced, and actively uploading states reset the clock.
    /// </summary>
    public DateTimeOffset? Touch(string? hash, bool isStalledUpload)
    {
        if (string.IsNullOrEmpty(hash))
            return null;

        if (!isStalledUpload)
        {
            _since.TryRemove(hash, out _);
            return null;
        }

        return _since.GetOrAdd(hash, _ => _time.GetUtcNow());
    }

    /// <summary>
    /// True when qBitrr-style observed stalledUP duration is at least <paramref name="limitSeconds"/>.
    /// The first stalled loop never meets the limit (elapsed is ~0).
    /// </summary>
    public bool IdleMeets(string? hash, int limitSeconds)
    {
        if (limitSeconds <= 0 || string.IsNullOrEmpty(hash))
            return false;
        if (!_since.TryGetValue(hash, out var since))
            return false;
        return (_time.GetUtcNow() - since).TotalSeconds >= limitSeconds;
    }

    public void Evict(params string?[] hashes)
    {
        foreach (var hash in hashes)
        {
            if (!string.IsNullOrEmpty(hash))
                _since.TryRemove(hash, out _);
        }
    }

    public void Evict(IEnumerable<string> hashes)
    {
        foreach (var hash in hashes)
            Evict(hash);
    }
}

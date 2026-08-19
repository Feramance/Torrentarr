using System.Collections.Concurrent;

namespace Torrentarr.Infrastructure.Services;

/// <summary>
/// Process-lifetime clock of when each torrent was first seen in qBittorrent
/// <c>stalledUP</c>. Matches qBitrr <c>_stalled_up_since</c> (5.14.4), keyed by
/// qBit instance + hash so two clients cannot share or reset each other's clock.
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
    /// Record or clear the first time this instance+hash was seen in stalled upload.
    /// Returns the stamp, or null when the torrent is not currently stalled.
    /// Queued, paused, stopped, forced, and actively uploading states reset the clock.
    /// </summary>
    public DateTimeOffset? Touch(string? instanceName, string? hash, bool isStalledUpload)
    {
        var key = Key(instanceName, hash);
        if (key == null)
            return null;

        if (!isStalledUpload)
        {
            _since.TryRemove(key, out _);
            return null;
        }

        return _since.GetOrAdd(key, _ => _time.GetUtcNow());
    }

    /// <summary>
    /// True when qBitrr-style observed stalledUP duration is at least <paramref name="limitSeconds"/>.
    /// The first stalled loop never meets the limit (elapsed is ~0).
    /// </summary>
    public bool IdleMeets(string? instanceName, string? hash, int limitSeconds)
    {
        if (limitSeconds <= 0)
            return false;
        var key = Key(instanceName, hash);
        if (key == null || !_since.TryGetValue(key, out var since))
            return false;
        return (_time.GetUtcNow() - since).TotalSeconds >= limitSeconds;
    }

    public void Evict(string? instanceName, params string?[] hashes)
    {
        foreach (var hash in hashes)
        {
            var key = Key(instanceName, hash);
            if (key != null)
                _since.TryRemove(key, out _);
        }
    }

    public void Evict(string? instanceName, IEnumerable<string> hashes)
    {
        foreach (var hash in hashes)
            Evict(instanceName, hash);
    }

    private static string? Key(string? instanceName, string? hash)
    {
        if (string.IsNullOrEmpty(hash))
            return null;
        return $"{instanceName ?? ""}\0{hash}";
    }
}

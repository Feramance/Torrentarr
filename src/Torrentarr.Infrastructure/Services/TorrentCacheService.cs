using Torrentarr.Core.Services;
using Microsoft.Extensions.Logging;

namespace Torrentarr.Infrastructure.Services;

/// <summary>
/// In-memory cache service for torrent-related data.
/// Reduces repeated database and API calls.
/// </summary>
public class TorrentCacheService : ITorrentCacheService
{
    private readonly ILogger<TorrentCacheService> _logger;
    private readonly Dictionary<string, string> _categoryCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _nameCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, DateTime> _ignoreCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _filePrioritiesAppliedHashes = new(StringComparer.Ordinal);
    private readonly HashSet<string> _fileFilteredHashes = new(StringComparer.Ordinal);
    private readonly Dictionary<string, DateTimeOffset> _allowlistWarningCache = new(StringComparer.Ordinal);
    private readonly Dictionary<string, (string Reason, DateTimeOffset At)> _removalBlocks = new(StringComparer.Ordinal);
    private readonly object _lock = new();
    private DateTime _lastFullClearUtc = DateTime.MinValue;
    private static readonly TimeSpan FullClearInterval = TimeSpan.FromHours(24);

    public TorrentCacheService(ILogger<TorrentCacheService> logger)
    {
        _logger = logger;
    }

    public string? GetCategory(string instanceId, string hash)
    {
        lock (_lock)
        {
            return _categoryCache.TryGetValue(FileStateKey(instanceId, hash), out var category) ? category : null;
        }
    }

    public void SetCategory(string instanceId, string hash, string category)
    {
        lock (_lock)
        {
            _categoryCache[FileStateKey(instanceId, hash)] = category;
        }
    }

    public string? GetName(string instanceId, string hash)
    {
        lock (_lock)
        {
            return _nameCache.TryGetValue(FileStateKey(instanceId, hash), out var name) ? name : null;
        }
    }

    public void SetName(string instanceId, string hash, string name)
    {
        lock (_lock)
        {
            _nameCache[FileStateKey(instanceId, hash)] = name;
        }
    }

    public bool IsInIgnoreCache(string instanceId, string hash)
    {
        lock (_lock)
        {
            if (!_ignoreCache.TryGetValue(FileStateKey(instanceId, hash), out var expiry))
            {
                return false;
            }

            if (DateTime.UtcNow > expiry)
            {
                _ignoreCache.Remove(FileStateKey(instanceId, hash));
                return false;
            }

            return true;
        }
    }

    public void AddToIgnoreCache(string instanceId, string hash, TimeSpan duration)
    {
        lock (_lock)
        {
            _ignoreCache[FileStateKey(instanceId, hash)] = DateTime.UtcNow.Add(duration);
            _logger.LogTrace("Added {Hash} to ignore cache for {Duration}", hash, duration);
        }
    }

    public void RemoveFromIgnoreCache(string instanceId, string hash)
    {
        lock (_lock)
        {
            _ignoreCache.Remove(FileStateKey(instanceId, hash));
        }
    }

    public bool AreFilePrioritiesApplied(string qBitInstanceName, string hash)
    {
        lock (_lock)
        {
            return _filePrioritiesAppliedHashes.Contains(FileStateKey(qBitInstanceName, hash));
        }
    }

    public void MarkFilePrioritiesApplied(string qBitInstanceName, string hash)
    {
        lock (_lock)
        {
            _filePrioritiesAppliedHashes.Add(FileStateKey(qBitInstanceName, hash));
        }
    }

    public bool IsFileFiltered(string qBitInstanceName, string hash)
    {
        lock (_lock)
        {
            return _fileFilteredHashes.Contains(FileStateKey(qBitInstanceName, hash));
        }
    }

    public void MarkFileFiltered(string qBitInstanceName, string hash)
    {
        lock (_lock)
        {
            _fileFilteredHashes.Add(FileStateKey(qBitInstanceName, hash));
        }
    }

    public bool ShouldLogAllowlistWarning(
        string qBitInstanceName,
        string hash,
        DateTimeOffset timestamp,
        TimeSpan throttlePeriod)
    {
        lock (_lock)
        {
            var key = FileStateKey(qBitInstanceName, hash);
            if (_allowlistWarningCache.TryGetValue(key, out var last)
                && timestamp - last < throttlePeriod)
            {
                return false;
            }

            _allowlistWarningCache[key] = timestamp;
            return true;
        }
    }

    public bool ShouldLogRemovalBlock(string instanceId, string hash, string reason,
        DateTimeOffset timestamp, TimeSpan throttlePeriod)
    {
        lock (_lock)
        {
            var key = FileStateKey(instanceId, hash);
            if (_removalBlocks.TryGetValue(key, out var previous)
                && previous.Reason == reason && timestamp - previous.At < throttlePeriod)
                return false;
            _removalBlocks[key] = (reason, timestamp);
            return true;
        }
    }

    public void InvalidateTorrent(string instanceId, string hash)
    {
        lock (_lock)
        {
            var key = FileStateKey(instanceId, hash);
            _categoryCache.Remove(key); _nameCache.Remove(key); _ignoreCache.Remove(key);
            _filePrioritiesAppliedHashes.Remove(key); _fileFilteredHashes.Remove(key); _allowlistWarningCache.Remove(key); _removalBlocks.Remove(key);
        }
    }

    public void Clear()
    {
        lock (_lock)
        {
            _categoryCache.Clear();
            _nameCache.Clear();
            _ignoreCache.Clear();
            _filePrioritiesAppliedHashes.Clear();
            _fileFilteredHashes.Clear();
            _allowlistWarningCache.Clear();
            _removalBlocks.Clear();
            _lastFullClearUtc = DateTime.UtcNow;
            _logger.LogTrace("All caches cleared");
        }
    }

    public void CleanExpired()
    {
        lock (_lock)
        {
            var now = DateTime.UtcNow;
            if (_lastFullClearUtc != DateTime.MinValue && now - _lastFullClearUtc >= FullClearInterval)
            {
                _categoryCache.Clear();
                _nameCache.Clear();
                _ignoreCache.Clear();
                _filePrioritiesAppliedHashes.Clear();
                _fileFilteredHashes.Clear();
                _allowlistWarningCache.Clear();
            _removalBlocks.Clear();
                _lastFullClearUtc = now;
                _logger.LogInformation("Periodic full cache clear (every {Hours}h) to bound memory growth", (int)FullClearInterval.TotalHours);
            }
            else
            {
                if (_lastFullClearUtc == DateTime.MinValue)
                    _lastFullClearUtc = now;

                var expiredKeys = _ignoreCache
                    .Where(kvp => kvp.Value < now)
                    .Select(kvp => kvp.Key)
                    .ToList();

                foreach (var key in expiredKeys)
                {
                    _ignoreCache.Remove(key);
                }

                if (expiredKeys.Count > 0)
                {
                    _logger.LogTrace("Cleaned {Count} expired entries from ignore cache", expiredKeys.Count);
                }
            }
        }
    }

    private static string FileStateKey(string qBitInstanceName, string hash) =>
        string.Concat(qBitInstanceName.ToUpperInvariant(), "\0", hash.ToUpperInvariant());

    public TorrentCacheStats GetStats()
    {
        lock (_lock)
        {
            return new TorrentCacheStats
            {
                CategoryCacheSize = _categoryCache.Count,
                NameCacheSize = _nameCache.Count,
                IgnoreCacheSize = _ignoreCache.Count,
                LastCleaned = DateTime.UtcNow
            };
        }
    }
}

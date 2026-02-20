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
    private readonly object _lock = new();

    public TorrentCacheService(ILogger<TorrentCacheService> logger)
    {
        _logger = logger;
    }

    public string? GetCategory(string hash)
    {
        lock (_lock)
        {
            return _categoryCache.TryGetValue(hash, out var category) ? category : null;
        }
    }

    public void SetCategory(string hash, string category)
    {
        lock (_lock)
        {
            _categoryCache[hash] = category;
        }
    }

    public string? GetName(string hash)
    {
        lock (_lock)
        {
            return _nameCache.TryGetValue(hash, out var name) ? name : null;
        }
    }

    public void SetName(string hash, string name)
    {
        lock (_lock)
        {
            _nameCache[hash] = name;
        }
    }

    public bool IsInIgnoreCache(string hash)
    {
        lock (_lock)
        {
            if (!_ignoreCache.TryGetValue(hash, out var expiry))
            {
                return false;
            }

            if (DateTime.UtcNow > expiry)
            {
                _ignoreCache.Remove(hash);
                return false;
            }

            return true;
        }
    }

    public void AddToIgnoreCache(string hash, TimeSpan duration)
    {
        lock (_lock)
        {
            _ignoreCache[hash] = DateTime.UtcNow.Add(duration);
            _logger.LogDebug("Added {Hash} to ignore cache for {Duration}", hash, duration);
        }
    }

    public void RemoveFromIgnoreCache(string hash)
    {
        lock (_lock)
        {
            _ignoreCache.Remove(hash);
        }
    }

    public void Clear()
    {
        lock (_lock)
        {
            _categoryCache.Clear();
            _nameCache.Clear();
            _ignoreCache.Clear();
            _logger.LogDebug("All caches cleared");
        }
    }

    public void CleanExpired()
    {
        lock (_lock)
        {
            var now = DateTime.UtcNow;
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
                _logger.LogDebug("Cleaned {Count} expired entries from ignore cache", expiredKeys.Count);
            }
        }
    }

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

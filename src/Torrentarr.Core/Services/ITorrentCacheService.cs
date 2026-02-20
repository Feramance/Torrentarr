namespace Torrentarr.Core.Services;

/// <summary>
/// Service for caching torrent-related data to reduce API calls
/// </summary>
public interface ITorrentCacheService
{
    /// <summary>
    /// Get cached category for a torrent hash
    /// </summary>
    string? GetCategory(string hash);

    /// <summary>
    /// Set cached category for a torrent hash
    /// </summary>
    void SetCategory(string hash, string category);

    /// <summary>
    /// Get cached name for a torrent hash
    /// </summary>
    string? GetName(string hash);

    /// <summary>
    /// Set cached name for a torrent hash
    /// </summary>
    void SetName(string hash, string name);

    /// <summary>
    /// Check if hash is in the timed ignore cache
    /// </summary>
    bool IsInIgnoreCache(string hash);

    /// <summary>
    /// Add hash to timed ignore cache
    /// </summary>
    void AddToIgnoreCache(string hash, TimeSpan duration);

    /// <summary>
    /// Remove hash from ignore cache
    /// </summary>
    void RemoveFromIgnoreCache(string hash);

    /// <summary>
    /// Clear all caches
    /// </summary>
    void Clear();

    /// <summary>
    /// Clean expired entries from all caches
    /// </summary>
    void CleanExpired();
}

public class TorrentCacheStats
{
    public int CategoryCacheSize { get; set; }
    public int NameCacheSize { get; set; }
    public int IgnoreCacheSize { get; set; }
    public DateTime LastCleaned { get; set; }
}

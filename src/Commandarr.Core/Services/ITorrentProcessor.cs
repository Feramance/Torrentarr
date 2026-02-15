namespace Commandarr.Core.Services;

/// <summary>
/// Interface for torrent processing operations
/// </summary>
public interface ITorrentProcessor
{
    /// <summary>
    /// Process all torrents for a specific category
    /// </summary>
    Task ProcessTorrentsAsync(string category, CancellationToken cancellationToken = default);

    /// <summary>
    /// Process a single torrent by hash
    /// </summary>
    Task ProcessTorrentAsync(string hash, CancellationToken cancellationToken = default);

    /// <summary>
    /// Check if torrent is ready for import to Arr
    /// </summary>
    Task<bool> IsReadyForImportAsync(string hash, CancellationToken cancellationToken = default);

    /// <summary>
    /// Import completed torrent to Arr
    /// </summary>
    Task ImportTorrentAsync(string hash, CancellationToken cancellationToken = default);
}

/// <summary>
/// Torrent processing statistics
/// </summary>
public class TorrentProcessingStats
{
    public int TotalTorrents { get; set; }
    public int Downloading { get; set; }
    public int Completed { get; set; }
    public int Seeding { get; set; }
    public int Failed { get; set; }
    public int Paused { get; set; }
    public int Imported { get; set; }
    public DateTime LastProcessed { get; set; } = DateTime.UtcNow;
}

/// <summary>
/// Torrent state enumeration matching qBittorrent states
/// </summary>
public enum TorrentState
{
    Unknown,
    Downloading,
    Uploading,
    StalledDownloading,
    StalledUploading,
    CheckingUploading,
    CheckingDownloading,
    PausedUploading,
    PausedDownloading,
    QueuedUploading,
    QueuedDownloading,
    ForcedUploading,
    ForcedDownloading,
    Allocating,
    Error,
    MissingFiles,
    Moving
}

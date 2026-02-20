namespace Torrentarr.Core.Services;

/// <summary>
/// Service for checking internet connectivity
/// </summary>
public interface IConnectivityService
{
    /// <summary>
    /// Check if internet connectivity is available
    /// </summary>
    Task<bool> IsConnectedAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Check if qBittorrent is reachable
    /// </summary>
    Task<bool> IsQBittorrentReachableAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Get the last known connectivity status
    /// </summary>
    bool IsConnected { get; }

    /// <summary>
    /// Get the last connectivity check time
    /// </summary>
    DateTime? LastChecked { get; }
}

public class ConnectivityStatus
{
    public bool IsConnected { get; set; }
    public bool IsQBittorrentReachable { get; set; }
    public DateTime CheckedAt { get; set; } = DateTime.UtcNow;
    public string? ErrorMessage { get; set; }
}

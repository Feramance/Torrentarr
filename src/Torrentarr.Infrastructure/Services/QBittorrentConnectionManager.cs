using Torrentarr.Core.Configuration;
using Torrentarr.Infrastructure.ApiClients.QBittorrent;
using Microsoft.Extensions.Logging;

namespace Torrentarr.Infrastructure.Services;

/// <summary>
/// Manages connections to one or more qBittorrent instances.
/// Instances are keyed by their config section name ("qBit", "qBit-seedbox", …).
/// </summary>
public class QBittorrentConnectionManager
{
    private readonly ILogger<QBittorrentConnectionManager> _logger;
    private readonly Dictionary<string, QBittorrentClient> _clients = new();
    private readonly Dictionary<string, DateTime> _lastConnected = new();

    public QBittorrentConnectionManager(ILogger<QBittorrentConnectionManager> logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// Connect to a named qBittorrent instance (e.g. "qBit", "qBit-seedbox").
    /// </summary>
    public async Task<bool> InitializeAsync(string name, QBitConfig config, CancellationToken cancellationToken = default)
    {
        if (config.Disabled)
        {
            _logger.LogInformation("qBittorrent instance '{Name}' is disabled in configuration", name);
            return false;
        }

        var client = new QBittorrentClient(config.Host, config.Port, config.UserName, config.Password);

        try
        {
            var loginSuccess = await client.LoginAsync(cancellationToken);
            if (!loginSuccess)
            {
                _logger.LogError("Failed to login to qBittorrent instance '{Name}' at {Host}:{Port}", name, config.Host, config.Port);
                return false;
            }

            var version = await client.GetVersionAsync(cancellationToken);
            _logger.LogInformation("Connected to qBittorrent instance '{Name}' {Version} at {Host}:{Port}",
                name, version, config.Host, config.Port);

            _clients[name] = client;
            _lastConnected[name] = DateTime.UtcNow;

            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error connecting to qBittorrent instance '{Name}' at {Host}:{Port}", name, config.Host, config.Port);
            return false;
        }
    }

    /// <summary>
    /// Get the client for a named qBit instance.
    /// </summary>
    public QBittorrentClient? GetClient(string instanceName)
    {
        return _clients.TryGetValue(instanceName, out var client) ? client : null;
    }

    /// <summary>
    /// Get all connected (name, client) pairs.
    /// </summary>
    public IReadOnlyDictionary<string, QBittorrentClient> GetAllClients()
    {
        return _clients;
    }

    /// <summary>
    /// Returns true if any qBit instance is connected.
    /// </summary>
    public bool IsConnected()
    {
        return _clients.Count > 0;
    }

    /// <summary>
    /// Returns true if the named qBit instance is connected.
    /// </summary>
    public bool IsConnected(string instanceName)
    {
        return _clients.ContainsKey(instanceName);
    }

    /// <summary>
    /// Get connection statistics for all instances.
    /// </summary>
    public Dictionary<string, ConnectionInfo> GetConnectionInfo()
    {
        var info = new Dictionary<string, ConnectionInfo>();

        foreach (var (name, _) in _clients)
        {
            info[name] = new ConnectionInfo
            {
                InstanceName = name,
                IsConnected = true,
                LastConnected = _lastConnected.TryGetValue(name, out var time) ? time : null
            };
        }

        return info;
    }
}

public class ConnectionInfo
{
    public string InstanceName { get; set; } = "";
    public bool IsConnected { get; set; }
    public DateTime? LastConnected { get; set; }
}

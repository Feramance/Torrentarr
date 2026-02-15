using Commandarr.Core.Configuration;
using Commandarr.Infrastructure.ApiClients.QBittorrent;
using Microsoft.Extensions.Logging;

namespace Commandarr.Infrastructure.Services;

/// <summary>
/// Manages connections to one or more qBittorrent instances
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
    /// Initialize connection to default qBittorrent instance
    /// </summary>
    public async Task<bool> InitializeAsync(QBitConfig config, CancellationToken cancellationToken = default)
    {
        if (config.Disabled)
        {
            _logger.LogInformation("qBittorrent is disabled in configuration");
            return false;
        }

        var client = new QBittorrentClient(config.Host, config.Port, config.UserName, config.Password);

        try
        {
            var loginSuccess = await client.LoginAsync(cancellationToken);
            if (!loginSuccess)
            {
                _logger.LogError("Failed to login to qBittorrent at {Host}:{Port}", config.Host, config.Port);
                return false;
            }

            var version = await client.GetVersionAsync(cancellationToken);
            _logger.LogInformation("Connected to qBittorrent {Version} at {Host}:{Port}",
                version, config.Host, config.Port);

            _clients["default"] = client;
            _lastConnected["default"] = DateTime.UtcNow;

            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error connecting to qBittorrent at {Host}:{Port}", config.Host, config.Port);
            return false;
        }
    }

    /// <summary>
    /// Get the default qBittorrent client
    /// </summary>
    public QBittorrentClient? GetDefaultClient()
    {
        return _clients.TryGetValue("default", out var client) ? client : null;
    }

    /// <summary>
    /// Get client by instance name
    /// </summary>
    public QBittorrentClient? GetClient(string instanceName)
    {
        return _clients.TryGetValue(instanceName, out var client) ? client : null;
    }

    /// <summary>
    /// Check if connected to qBittorrent
    /// </summary>
    public bool IsConnected(string instanceName = "default")
    {
        return _clients.ContainsKey(instanceName);
    }

    /// <summary>
    /// Get all connected instances
    /// </summary>
    public IEnumerable<string> GetConnectedInstances()
    {
        return _clients.Keys;
    }

    /// <summary>
    /// Get connection statistics
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

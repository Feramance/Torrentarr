using System.Collections.Concurrent;
using Torrentarr.Core.Configuration;
using Torrentarr.Core.Interfaces;
using Torrentarr.Infrastructure.ApiClients.QBittorrent;
using Microsoft.Extensions.Logging;

namespace Torrentarr.Infrastructure.Services;

/// <summary>
/// Manages connections to one or more qBittorrent instances.
/// Instances are keyed by their config section name ("qBit", "qBit-seedbox", …).
/// </summary>
public class QBittorrentConnectionManager : ITorrentClientRegistry
{
    private readonly ILogger<QBittorrentConnectionManager> _logger;
    // A regular dictionary is kept behind a lock so existing reflection-based
    // integrations remain compatible while snapshots stay race-free.
    private readonly Dictionary<string, QBittorrentClient> _clients = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _clientsLock = new();
    private readonly ConcurrentDictionary<string, DateTime> _lastConnected = new(StringComparer.OrdinalIgnoreCase);
    private readonly IReadOnlyDictionary<string, ITorrentClientFactory> _factories;

    public QBittorrentConnectionManager(
        ILogger<QBittorrentConnectionManager> logger,
        IEnumerable<ITorrentClientFactory>? factories = null)
    {
        _logger = logger;
        _factories = (factories ?? [new QBittorrentTorrentClientFactory()])
            .ToDictionary(factory => factory.Type, StringComparer.OrdinalIgnoreCase);
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

        lock (_clientsLock) if (_clients.ContainsKey(name)) return true;

        var client = (QBittorrentClient)_factories["qbittorrent"].Create(name, config);

        try
        {
            var loginSuccess = await client.LoginAsync(cancellationToken);
            if (!loginSuccess)
            {
                _logger.LogError(
                    "Failed to login to qBittorrent instance '{Name}' at {Host}:{Port}. {Detail}",
                    name, config.Host, config.Port, client.LastLoginFailure ?? "unknown error");
                return false;
            }

            var version = await client.GetVersionAsync(cancellationToken);
            _logger.LogInformation("Connected to qBittorrent instance '{Name}' {Version} at {Host}:{Port}",
                name, version, config.Host, config.Port);

            lock (_clientsLock) _clients[name] = client;
            _lastConnected[name] = DateTime.UtcNow;

            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error connecting to qBittorrent instance '{Name}' at {Host}:{Port}", name, config.Host, config.Port);
            return false;
        }
    }

    public async Task<bool> InitializeAsync(string name, TorrentClientInstanceConfig config, CancellationToken cancellationToken = default)
    {
        if (!_factories.TryGetValue(config.Type, out var factory))
        {
            _logger.LogError("Torrent client type '{Type}' is not registered for instance '{Name}'", config.Type, name);
            return false;
        }

        var compatible = config as QBitConfig ?? new QBitConfig
        {
            Type = config.Type,
            Disabled = config.Disabled,
            Host = config.Host,
            Port = config.Port,
            UserName = config.UserName,
            Password = config.Password,
            SkipTLSVerify = config.SkipTLSVerify,
            DownloadPath = config.DownloadPath,
            ManagedCategories = config.ManagedCategories,
            MatchSubcategories = config.MatchSubcategories,
            Trackers = config.Trackers,
            CategorySeeding = config.CategorySeeding,
            Maintenance = config.Maintenance
        };
        if (compatible.Disabled) return false;
        lock (_clientsLock) if (_clients.ContainsKey(name)) return true;
        var client = factory.Create(name, compatible);
        if (client is not QBittorrentClient qbitClient)
        {
            _logger.LogError("Torrent client adapter '{Type}' must currently derive from QBittorrentClient", config.Type);
            return false;
        }
        try
        {
            if (!await client.LoginAsync(cancellationToken)) return false;
            lock (_clientsLock) _clients[name] = qbitClient;
            _lastConnected[name] = DateTime.UtcNow;
            _logger.LogInformation("Connected torrent client '{Name}' ({Type}) {Version}",
                name, config.Type, await client.GetVersionAsync(cancellationToken));
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error connecting torrent client '{Name}' ({Type})", name, config.Type);
            return false;
        }
    }

    /// <summary>
    /// Connect any configured instances that are not yet connected (startup recovery).
    /// </summary>
    public async Task<int> EnsureAllConnectedAsync(
        IEnumerable<KeyValuePair<string, QBitConfig>> instances,
        CancellationToken cancellationToken = default)
    {
        var connected = 0;
        foreach (var (name, config) in instances)
        {
            if (config.Disabled || config.Host == "CHANGE_ME")
                continue;
            if (await InitializeAsync(name, config, cancellationToken))
                connected++;
        }

        return connected;
    }

    public async Task<int> EnsureAllConnectedAsync(
        IEnumerable<KeyValuePair<string, TorrentClientInstanceConfig>> instances,
        CancellationToken cancellationToken = default)
    {
        var connected = 0;
        foreach (var (name, config) in instances)
        {
            if (config.Disabled || config.Host == "CHANGE_ME")
                continue;
            if (await InitializeAsync(name, config, cancellationToken))
                connected++;
        }
        return connected;
    }

    /// <summary>
    /// Get the client for a named qBit instance.
    /// </summary>
    public QBittorrentClient? GetClient(string instanceName)
    {
        lock (_clientsLock) return _clients.TryGetValue(instanceName, out var client) ? client : null;
    }

    /// <summary>
    /// Get all connected (name, client) pairs.
    /// </summary>
    public IReadOnlyDictionary<string, QBittorrentClient> GetAllClients()
    {
        lock (_clientsLock) return new Dictionary<string, QBittorrentClient>(_clients, StringComparer.OrdinalIgnoreCase);
    }

    ITorrentClient? ITorrentClientRegistry.GetClient(string instanceId) => GetClient(instanceId);

    IReadOnlyDictionary<string, ITorrentClient> ITorrentClientRegistry.GetAllClients()
        => GetAllClients().ToDictionary(pair => pair.Key, pair => (ITorrentClient)pair.Value, StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Returns true if any qBit instance is connected.
    /// </summary>
    public bool IsConnected()
    {
        lock (_clientsLock) return _clients.Count > 0;
    }

    /// <summary>
    /// Returns true if the named qBit instance is connected.
    /// </summary>
    public bool IsConnected(string instanceName)
    {
        lock (_clientsLock) return _clients.ContainsKey(instanceName);
    }

    /// <summary>
    /// Get connection statistics for all instances.
    /// </summary>
    public Dictionary<string, ConnectionInfo> GetConnectionInfo()
    {
        var info = new Dictionary<string, ConnectionInfo>();

        foreach (var (name, _) in GetAllClients())
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

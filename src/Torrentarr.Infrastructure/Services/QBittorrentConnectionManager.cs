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
    private readonly Dictionary<string, QBittorrentClient> _clients = new();
    private readonly Dictionary<string, DateTime> _lastConnected = new();
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

        if (_clients.ContainsKey(name))
            return true;

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
        if (_clients.ContainsKey(name)) return true;
        var client = factory.Create(name, compatible);
        try
        {
            if (!await client.LoginAsync(cancellationToken)) return false;
            _clients[name] = (QBittorrentClient)client;
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
        return _clients.TryGetValue(instanceName, out var client) ? client : null;
    }

    /// <summary>
    /// Get all connected (name, client) pairs.
    /// </summary>
    public IReadOnlyDictionary<string, QBittorrentClient> GetAllClients()
    {
        return _clients;
    }

    ITorrentClient? ITorrentClientRegistry.GetClient(string instanceId) => GetClient(instanceId);

    IReadOnlyDictionary<string, ITorrentClient> ITorrentClientRegistry.GetAllClients()
        => _clients.ToDictionary(pair => pair.Key, pair => (ITorrentClient)pair.Value);

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


/// <summary>Client-neutral registry. Adapters are selected exclusively by their declared type.</summary>
public sealed class TorrentClientRegistry : ITorrentClientRegistry
{
    private readonly ILogger<TorrentClientRegistry> _logger;
    private readonly IReadOnlyDictionary<string, ITorrentClientFactory> _factories;
    private readonly Dictionary<string, ITorrentClient> _clients = new(StringComparer.OrdinalIgnoreCase);

    public TorrentClientRegistry(
        ILogger<TorrentClientRegistry> logger,
        IEnumerable<ITorrentClientFactory> factories)
    {
        _logger = logger;
        _factories = factories.ToDictionary(factory => factory.Type, StringComparer.OrdinalIgnoreCase);
    }

    public async Task<bool> InitializeAsync(string name, TorrentClientInstanceConfig config, CancellationToken ct = default)
    {
        if (config.Disabled) return false;
        if (_clients.ContainsKey(name)) return true;
        if (!_factories.TryGetValue(config.Type, out var factory))
        {
            _logger.LogError("No adapter is registered for torrent client type '{Type}'", config.Type);
            return false;
        }
        var client = factory.Create(name, config);
        try
        {
            if (!await client.LoginAsync(ct)) return false;
            _clients[name] = client;
            _logger.LogInformation("Connected torrent client '{Name}' ({Type}) {Version}",
                name, config.Type, await client.GetVersionAsync(ct));
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error connecting torrent client '{Name}' ({Type})", name, config.Type);
            return false;
        }
    }

    public async Task<int> EnsureAllConnectedAsync(IEnumerable<KeyValuePair<string, TorrentClientInstanceConfig>> instances, CancellationToken ct = default)
    {
        var connected = 0;
        foreach (var (name, config) in instances)
            if (!config.Disabled && config.Host != "CHANGE_ME" && await InitializeAsync(name, config, ct)) connected++;
        return connected;
    }

    public ITorrentClient? GetClient(string instanceId) => _clients.GetValueOrDefault(instanceId);
    public IReadOnlyDictionary<string, ITorrentClient> GetAllClients() => _clients;
    public bool IsConnected() => _clients.Count > 0;
    public bool IsConnected(string instanceId) => _clients.ContainsKey(instanceId);
}

public class ConnectionInfo
{
    public string InstanceName { get; set; } = "";
    public bool IsConnected { get; set; }
    public DateTime? LastConnected { get; set; }
}

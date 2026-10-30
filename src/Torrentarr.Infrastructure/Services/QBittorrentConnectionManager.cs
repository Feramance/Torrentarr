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
    private readonly Dictionary<string, ITorrentClient> _clients = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _clientsLock = new();
    private readonly SemaphoreSlim _reconcile = new(1, 1);
    private readonly Dictionary<string, ConnectionSettings> _settings = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, AuthenticationFailure> _authenticationFailures = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _connectionErrors = new(StringComparer.OrdinalIgnoreCase);
    private sealed record ConnectionSettings(string Type, string Host, int Port, string UserName, string Password, bool SkipTLSVerify);
    private sealed record AuthenticationFailure(ConnectionSettings Settings, long Revision);
    private readonly ConcurrentDictionary<string, DateTime> _lastConnected = new(StringComparer.OrdinalIgnoreCase);
    private readonly IReadOnlyDictionary<string, ITorrentClientFactory> _factories;
    private readonly TorrentarrConfig? _config;

    public QBittorrentConnectionManager(
        ILogger<QBittorrentConnectionManager> logger,
        IEnumerable<ITorrentClientFactory>? factories = null,
        TorrentarrConfig? config = null)
    {
        _logger = logger;
        _config = config;
        _factories = (factories ?? [new QBittorrentTorrentClientFactory()])
            .ToDictionary(factory => factory.Type, StringComparer.OrdinalIgnoreCase);
    }

    public Task<bool> InitializeAsync(string name, QBitConfig config, CancellationToken cancellationToken = default)
        => InitializeAsync(name, (TorrentClientInstanceConfig)config, cancellationToken);

    public async Task<bool> InitializeAsync(string name, TorrentClientInstanceConfig config, CancellationToken cancellationToken = default)
    {
        await _reconcile.WaitAsync(cancellationToken);
        try
        {
            lock (_clientsLock) _authenticationFailures.Remove(name); // Explicit reconnect bypasses cached rejection.
            return await ConnectAsync(name, config, cancellationToken);
        }
        finally { _reconcile.Release(); }
    }

    private async Task<bool> ConnectAsync(string name, TorrentClientInstanceConfig config, CancellationToken ct)
    {
        var settings = new ConnectionSettings(config.Type, config.Host, config.Port, config.UserName, config.Password, config.SkipTLSVerify);
        var revision = _config?.Revision ?? 0;
        lock (_clientsLock)
        {
            if (!config.Disabled && config.Host != "CHANGE_ME" && _settings.GetValueOrDefault(name) == settings && _clients.ContainsKey(name)) return true;
            if (_authenticationFailures.TryGetValue(name, out var failure))
            {
                if (failure.Settings == settings && failure.Revision == revision) return false;
                _authenticationFailures.Remove(name);
            }
            RemoveClient(name);
            _connectionErrors.Remove(name);
        }
        if (config.Disabled || config.Host == "CHANGE_ME") return false;
        if (!_factories.TryGetValue(config.Type, out var factory))
        {
            lock (_clientsLock) _connectionErrors[name] = $"Unsupported client type: {config.Type}.";
            _logger.LogError("Torrent client type {Type} is not registered for {Name}", config.Type, name);
            return false;
        }
        ITorrentClient? client = null;
        try
        {
            client = factory.Create(name, config);
            if (!await client.LoginAsync(ct))
            {
                var rejectedCredentials = client.LastConnectionFailure?.Contains("rejected credentials", StringComparison.OrdinalIgnoreCase) == true;
                lock (_clientsLock) _connectionErrors[name] = rejectedCredentials
                    ? "Authentication rejected. Check credentials or reconnect after restoring access."
                    : $"Connection/login failed: {client.LastConnectionFailure ?? "No reason supplied"}.";
                (client as IDisposable)?.Dispose();
                if (rejectedCredentials)
                    lock (_clientsLock) _authenticationFailures[name] = new AuthenticationFailure(settings, revision);
                return false;
            }
            var version = await client.GetVersionAsync(ct);
            lock (_clientsLock)
            {
                _clients[name] = new RegisteredTorrentClient(client);
                _settings[name] = settings;
                _authenticationFailures.Remove(name);
                _connectionErrors.Remove(name);
                _lastConnected[name] = DateTime.UtcNow;
            }
            _logger.LogInformation("Connected torrent client {Name} ({Type}) {Version}", name, config.Type, version);
            return true;
        }
        catch (OperationCanceledException) { (client as IDisposable)?.Dispose(); throw; }
        catch (Exception ex)
        {
            lock (_clientsLock) _connectionErrors[name] = $"{ex.GetType().Name}: {ex.Message}";
            (client as IDisposable)?.Dispose();
            _logger.LogWarning("Could not connect torrent client {Name} ({Type})", name, config.Type);
            return false;
        }
    }

    private void RemoveClient(string name)
    {
        if (_clients.Remove(name, out var client)) (client as IDisposable)?.Dispose();
        _settings.Remove(name);
        _lastConnected.TryRemove(name, out _);
    }

    public Task<int> EnsureAllConnectedAsync(IEnumerable<KeyValuePair<string, QBitConfig>> instances, CancellationToken cancellationToken = default)
        => EnsureAllConnectedAsync(instances.Select(p => new KeyValuePair<string, TorrentClientInstanceConfig>(p.Key, p.Value)), cancellationToken);

    public Task<int> EnsureAllConnectedAsync(IEnumerable<KeyValuePair<string, TorrentClientInstanceConfig>> instances, CancellationToken cancellationToken = default)
        => ReconcileAsync(instances, ct: cancellationToken);

    public async Task<int> ReconcileAsync(IEnumerable<KeyValuePair<string, TorrentClientInstanceConfig>> instances, bool connect = true, CancellationToken ct = default)
    {
        var desired = instances.ToDictionary(p => p.Key, p => p.Value, StringComparer.OrdinalIgnoreCase);
        await _reconcile.WaitAsync(ct);
        try
        {
            lock (_clientsLock)
            {
                foreach (var name in _clients.Keys.Where(id => !desired.ContainsKey(id)).ToList())
                {
                    RemoveClient(name);
                    _authenticationFailures.Remove(name);
                }
                foreach (var name in _authenticationFailures.Keys.Where(id => !desired.ContainsKey(id)).ToList())
                    _authenticationFailures.Remove(name);
            }
            var count = 0;
            foreach (var (name, config) in desired)
            {
                if (connect)
                {
                    if (await ConnectAsync(name, config, ct)) count++;
                }
                else
                {
                    var settings = new ConnectionSettings(config.Type, config.Host, config.Port, config.UserName, config.Password, config.SkipTLSVerify);
                    lock (_clientsLock)
                        if (config.Disabled || config.Host == "CHANGE_ME" || _settings.GetValueOrDefault(name) != settings) RemoveClient(name);
                }
            }
            return count;
        }
        finally { _reconcile.Release(); }
    }

    /// <summary>Connection errors for runtime diagnostics.</summary>
    public IReadOnlyDictionary<string, string> GetConnectionErrors()
    {
        lock (_clientsLock) return new Dictionary<string, string>(_connectionErrors, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Get the client for a named qBit instance.
    /// </summary>
    public ITorrentClient? GetClient(string instanceName)
    {
        lock (_clientsLock) return _clients.TryGetValue(instanceName, out var client) ? client : null;
    }

    /// <summary>
    /// Get all connected (name, client) pairs.
    /// </summary>
    public IReadOnlyDictionary<string, ITorrentClient> GetAllClients()
    {
        lock (_clientsLock) return new Dictionary<string, ITorrentClient>(_clients, StringComparer.OrdinalIgnoreCase);
    }

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

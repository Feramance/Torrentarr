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
        try { return await ConnectAsync(name, config, cancellationToken); }
        finally { _reconcile.Release(); }
    }

    /// <summary>Explicit operator-initiated reconnection, including after credentials are restored externally.</summary>
    public async Task<bool> ForceReconnectAsync(string name, TorrentClientInstanceConfig config, CancellationToken ct = default)
    {
        await _reconcile.WaitAsync(ct);
        try
        {
            lock (_clientsLock)
            {
                _authenticationFailures.Remove(name);
                _connectionErrors.Remove(name);
                RemoveClient(name);
            }
            return await ConnectAsync(name, config, ct);
        }
        finally { _reconcile.Release(); }
    }

    private async Task<bool> ConnectAsync(string name, TorrentClientInstanceConfig config, CancellationToken ct)
    {
        var settings = new ConnectionSettings(config.Type, config.Host, config.Port, config.UserName, config.Password, config.SkipTLSVerify);
        var revision = _config?.Revision ?? 0;
        ITorrentClient? existing = null;
        lock (_clientsLock)
        {
            if (!config.Disabled && config.Host != "CHANGE_ME" && _settings.GetValueOrDefault(name) == settings)
                _clients.TryGetValue(name, out existing);
        }
        if (existing != null)
        {
            try
            {
                // A registered object is not proof the remote service is still available.
                if (string.IsNullOrWhiteSpace(await existing.GetVersionAsync(ct)))
                    throw new IOException("Torrent client returned an empty version response.");
                return true;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex)
            {
                lock (_clientsLock)
                {
                    RemoveClient(name);
                    _connectionErrors[name] = "The existing torrent-client session is unavailable; reconnection will be retried.";
                }
                // HTTP exceptions can include credential-bearing URLs: log only safe diagnostic context.
                _logger.LogWarning("Torrent client {Name} failed its connection health check ({ErrorType})",
                    name, ex.GetType().Name);
                return false;
            }
        }
        if (config.Disabled || config.Host == "CHANGE_ME")
        {
            lock (_clientsLock)
            {
                RemoveClient(name);
                _authenticationFailures.Remove(name);
                _connectionErrors.Remove(name);
            }
            return false;
        }
        lock (_clientsLock)
        {
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
                var rejectedCredentials = client.LastConnectionFailureKind == TorrentClientConnectionFailureKind.AuthenticationRejected;
                lock (_clientsLock) _connectionErrors[name] = rejectedCredentials
                    ? "Authentication rejected. Check credentials or reconnect after restoring access."
                    : "Connection/login failed. Check the torrent-client service and server logs.";
                (client as IDisposable)?.Dispose();
                if (rejectedCredentials)
                    lock (_clientsLock) _authenticationFailures[name] = new AuthenticationFailure(settings, revision);
                return false;
            }
            var version = await client.GetVersionAsync(ct);
            if (string.IsNullOrWhiteSpace(version))
                throw new IOException("Torrent client returned an empty version response.");
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
            lock (_clientsLock) _connectionErrors[name] = $"Connection failed ({ex.GetType().Name}). Check server logs.";
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
                foreach (var name in _connectionErrors.Keys.Where(id => !desired.ContainsKey(id)).ToList())
                    _connectionErrors.Remove(name);
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
                    {
                        var inactive = config.Disabled || config.Host == "CHANGE_ME";
                        if (inactive || _settings.GetValueOrDefault(name) != settings)
                            RemoveClient(name);
                        // A failed login has no registered client settings. Do not clear its
                        // rejection cache during a disconnect-only reconciliation unless the
                        // credentials actually changed or the client was disabled.
                        if (inactive || _authenticationFailures.TryGetValue(name, out var failure)
                            && failure.Settings != settings)
                            _authenticationFailures.Remove(name);
                        if (inactive || !_authenticationFailures.ContainsKey(name))
                            _connectionErrors.Remove(name);
                    }
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

using System.Net;
using System.Net.NetworkInformation;
using Torrentarr.Core.Services;
using Microsoft.Extensions.Logging;

namespace Torrentarr.Infrastructure.Services;

/// <summary>
/// Service for checking internet and network connectivity.
/// Used to delay processing during network outages.
/// </summary>
public class ConnectivityService : IConnectivityService
{
    private readonly ILogger<ConnectivityService> _logger;
    private readonly QBittorrentConnectionManager _qbitManager;
    private readonly HashSet<string> _testHosts;

    private volatile bool _isConnected = true;
    private volatile bool _lastCheckedSet = false;
    private DateTime _lastChecked;
    private readonly object _stateLock = new();

    public bool IsConnected => _isConnected;
    public DateTime? LastChecked
    {
        get
        {
            lock (_stateLock) return _lastCheckedSet ? _lastChecked : null;
        }
    }

    public ConnectivityService(
        ILogger<ConnectivityService> logger,
        QBittorrentConnectionManager qbitManager)
    {
        _logger = logger;
        _qbitManager = qbitManager;
        _testHosts = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "8.8.8.8",
            "1.1.1.1",
            "9.9.9.9"
        };
    }

    private void SetState(bool connected)
    {
        _isConnected = connected;
        lock (_stateLock)
        {
            _lastChecked = DateTime.UtcNow;
            _lastCheckedSet = true;
        }
    }

    public async Task<bool> IsConnectedAsync(CancellationToken cancellationToken = default)
    {
        _logger.LogTrace("Checking connectivity status");

        try
        {
            _logger.LogTrace("Checking qBittorrent reachability");
            var qbitReachable = await IsQBittorrentReachableAsync(cancellationToken);
            if (qbitReachable)
            {
                _logger.LogTrace("qBittorrent is reachable - connectivity confirmed");
                SetState(true);
                return true;
            }

            _logger.LogTrace("qBittorrent not reachable, checking internet hosts");
            var hostsChecked = 0;
            foreach (var host in _testHosts)
            {
                hostsChecked++;
                _logger.LogTrace("Pinging host {Host} ({Current}/{Total})", host, hostsChecked, _testHosts.Count);

                if (await PingHostAsync(host, cancellationToken))
                {
                    _logger.LogTrace("Ping successful to {Host} - connectivity confirmed", host);
                    SetState(true);
                    _logger.LogTrace("Internet connectivity confirmed via {Host}", host);
                    return true;
                }

                _logger.LogTrace("Ping failed to {Host}", host);
            }

            _logger.LogTrace("All {Count} hosts unreachable - no connectivity", _testHosts.Count);
            SetState(false);
            _logger.LogWarning("No internet connectivity detected");
            return false;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error checking internet connectivity");
            SetState(false);
            return false;
        }
    }

    public async Task<bool> IsQBittorrentReachableAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var client = _qbitManager.GetAllClients().Values.FirstOrDefault();
            if (client == null)
            {
                return false;
            }

            var version = await client.GetVersionAsync(cancellationToken);
            return !string.IsNullOrEmpty(version);
        }
        catch (Exception ex)
        {
            _logger.LogTrace(ex, "qBittorrent not reachable");
            return false;
        }
    }

    private async Task<bool> PingHostAsync(string host, CancellationToken cancellationToken)
    {
        try
        {
            if (!IPAddress.TryParse(host, out var ipAddress))
            {
                var addresses = await Dns.GetHostAddressesAsync(host, cancellationToken);
                ipAddress = addresses.FirstOrDefault();

                if (ipAddress == null)
                {
                    return false;
                }
            }

            using var ping = new Ping();
            var reply = await ping.SendPingAsync(ipAddress, 5000);

            return reply.Status == IPStatus.Success;
        }
        catch (Exception ex)
        {
            _logger.LogTrace(ex, "Ping failed for host {Host}", host);
            return false;
        }
    }
}

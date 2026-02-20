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
    
    private bool _isConnected = true;
    private DateTime? _lastChecked;

    public bool IsConnected => _isConnected;
    public DateTime? LastChecked => _lastChecked;

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

    public async Task<bool> IsConnectedAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var qbitReachable = await IsQBittorrentReachableAsync(cancellationToken);
            if (qbitReachable)
            {
                _isConnected = true;
                _lastChecked = DateTime.UtcNow;
                return true;
            }

            foreach (var host in _testHosts)
            {
                if (await PingHostAsync(host, cancellationToken))
                {
                    _isConnected = true;
                    _lastChecked = DateTime.UtcNow;
                    _logger.LogDebug("Internet connectivity confirmed via {Host}", host);
                    return true;
                }
            }

            _isConnected = false;
            _lastChecked = DateTime.UtcNow;
            _logger.LogWarning("No internet connectivity detected");
            return false;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error checking internet connectivity");
            _isConnected = false;
            _lastChecked = DateTime.UtcNow;
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
            _logger.LogDebug(ex, "qBittorrent not reachable");
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

using System.Net.Sockets;
using Torrentarr.Core.Configuration;
using Torrentarr.Core.Services;
using Microsoft.Extensions.Logging;

namespace Torrentarr.Infrastructure.Services;

/// <summary>
/// Service for checking internet and network connectivity.
/// Used to delay processing during network outages.
/// </summary>
public class ConnectivityService : IConnectivityService
{
    private static readonly HttpClient ProbeHttpClient = CreateProbeClient();

    private readonly ILogger<ConnectivityService> _logger;
    private readonly QBittorrentConnectionManager _qbitManager;
    private readonly TorrentarrConfig _config;
    private readonly Func<string, CancellationToken, Task<bool>> _probeAsync;

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
        QBittorrentConnectionManager qbitManager,
        TorrentarrConfig config)
        : this(logger, qbitManager, config, probe: null)
    {
    }

    internal ConnectivityService(
        ILogger<ConnectivityService> logger,
        QBittorrentConnectionManager qbitManager,
        TorrentarrConfig config,
        Func<string, CancellationToken, Task<bool>>? probe)
    {
        _logger = logger;
        _qbitManager = qbitManager;
        _config = config;
        _probeAsync = probe ?? ProbeHostAsync;
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

            var pingUrls = GetPingUrls();
            _logger.LogTrace("qBittorrent not reachable, probing {Count} PingURLS via HTTP/TCP", pingUrls.Count);
            var failed = new List<string>();
            foreach (var host in pingUrls)
            {
                if (await _probeAsync(host, cancellationToken))
                {
                    _logger.LogTrace("Probe successful to {Host} - connectivity confirmed", host);
                    SetState(true);
                    return true;
                }

                failed.Add(host);
                _logger.LogTrace("Probe failed to {Host}", host);
            }

            SetState(false);
            _logger.LogWarning(
                "No internet connectivity detected (qBittorrent unreachable; failed probes: {Hosts})",
                failed.Count == 0 ? "(no PingURLS configured)" : string.Join(", ", failed));
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

    internal IReadOnlyList<string> GetPingUrls()
    {
        var urls = _config.Settings.PingURLS?
            .Where(u => !string.IsNullOrWhiteSpace(u))
            .Select(u => u.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList() ?? new List<string>();
        if (urls.Count == 0)
        {
            urls.Add("one.one.one.one");
            urls.Add("dns.google.com");
        }
        return urls;
    }

    private async Task<bool> ProbeHostAsync(string host, CancellationToken cancellationToken)
    {
        try
        {
            if (Uri.TryCreate(host, UriKind.Absolute, out var absolute) &&
                (absolute.Scheme == Uri.UriSchemeHttp || absolute.Scheme == Uri.UriSchemeHttps))
            {
                return await HttpProbeAsync(absolute, cancellationToken);
            }

            if (await HttpProbeAsync(new Uri($"https://{host}"), cancellationToken))
                return true;

            if (await HttpProbeAsync(new Uri($"http://{host}"), cancellationToken))
                return true;

            var hostname = host;
            if (Uri.TryCreate($"http://{host}", UriKind.Absolute, out var parsed))
                hostname = parsed.Host;

            return await TcpProbeAsync(hostname, 443, cancellationToken)
                || await TcpProbeAsync(hostname, 80, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogTrace(ex, "Probe failed for host {Host}", host);
            return false;
        }
    }

    private static async Task<bool> HttpProbeAsync(Uri uri, CancellationToken cancellationToken)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Head, uri);
            using var response = await ProbeHttpClient.SendAsync(
                request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            return true;
        }
        catch (HttpRequestException)
        {
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, uri);
                using var response = await ProbeHttpClient.SendAsync(
                    request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
                return true;
            }
            catch (Exception)
            {
                return false;
            }
        }
        catch (TaskCanceledException)
        {
            return false;
        }
    }

    private static async Task<bool> TcpProbeAsync(string host, int port, CancellationToken cancellationToken)
    {
        try
        {
            using var client = new TcpClient();
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(5));
            await client.ConnectAsync(host, port, timeoutCts.Token);
            return client.Connected;
        }
        catch
        {
            return false;
        }
    }

    private static HttpClient CreateProbeClient()
    {
        var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = true,
            ConnectTimeout = TimeSpan.FromSeconds(5)
        };
        return new HttpClient(handler)
        {
            Timeout = TimeSpan.FromSeconds(5)
        };
    }
}

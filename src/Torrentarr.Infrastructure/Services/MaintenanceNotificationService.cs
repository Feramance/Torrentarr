using System.Net.Http.Json;
using System.Threading.Channels;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Torrentarr.Core.Configuration;
using Torrentarr.Core.Models;
using Torrentarr.Core.Services;

namespace Torrentarr.Infrastructure.Services;

public sealed class MaintenanceNotificationService : BackgroundService, IMaintenanceNotificationService
{
    private readonly Channel<MaintenanceEvent> _events = Channel.CreateBounded<MaintenanceEvent>(256);
    private readonly IHttpClientFactory _clients;
    private readonly TorrentarrConfig _config;
    private readonly ILogger<MaintenanceNotificationService> _logger;

    public MaintenanceNotificationService(IHttpClientFactory clients, TorrentarrConfig config, ILogger<MaintenanceNotificationService> logger)
    {
        _clients = clients;
        _config = config;
        _logger = logger;
    }

    public void Publish(MaintenanceEvent maintenanceEvent)
    {
        if (!_events.Writer.TryWrite(maintenanceEvent))
            _logger.LogWarning("Maintenance notification queue is full; event {Type} was dropped", maintenanceEvent.Type);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await foreach (var item in _events.Reader.ReadAllAsync(stoppingToken))
        {
            var sinks = _config.GetAllTorrentClients()
                .Where(pair => item.ClientInstanceId == null || pair.Key.Equals(item.ClientInstanceId, StringComparison.OrdinalIgnoreCase))
                .SelectMany(pair => pair.Value.Maintenance.Notifications)
                .Where(s => s.Enabled && Uri.TryCreate(s.Url, UriKind.Absolute, out _)).ToList();
            foreach (var sink in sinks)
                await DeliverAsync(sink, item, stoppingToken);
        }
    }

    private async Task DeliverAsync(MaintenanceNotificationConfig sink, MaintenanceEvent item, CancellationToken ct)
    {
        var client = _clients.CreateClient();
        for (var attempt = 1; attempt <= Math.Clamp(sink.MaxAttempts, 1, 5); attempt++)
        {
            try
            {
                using var request = BuildRequest(sink, item);
                using var response = await client.SendAsync(request, ct);
                response.EnsureSuccessStatusCode();
                return;
            }
            catch (Exception ex) when (attempt < Math.Clamp(sink.MaxAttempts, 1, 5) && ex is not OperationCanceledException)
            {
                await Task.Delay(TimeSpan.FromSeconds(Math.Pow(2, attempt - 1)), ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "Maintenance notification delivery to {Type} failed", sink.Type);
            }
        }
    }

    private static HttpRequestMessage BuildRequest(MaintenanceNotificationConfig sink, MaintenanceEvent item)
    {
        var uri = sink.Type.ToLowerInvariant() switch
        {
            "apprise" => sink.Url.TrimEnd('/') + "/notify",
            "notifiarr" when !string.IsNullOrWhiteSpace(sink.Token) => sink.Url.TrimEnd('/') + "/api/v1/notification/torrentarr?apikey=" + Uri.EscapeDataString(sink.Token),
            _ => sink.Url
        };
        var request = new HttpRequestMessage(HttpMethod.Post, uri) { Content = JsonContent.Create(item) };
        if (!string.IsNullOrWhiteSpace(sink.Token) && !sink.Type.Equals("notifiarr", StringComparison.OrdinalIgnoreCase))
            request.Headers.Authorization = new("Bearer", sink.Token);
        return request;
    }
}

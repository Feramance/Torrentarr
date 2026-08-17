using Torrentarr.Core.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Torrentarr.Infrastructure.Services;

/// <summary>
/// Periodic WAL checkpoint + health/repair (qBitrr 5.12.9 <c>maintain_database</c>, 5-minute interval).
/// </summary>
public class PeriodicWalCheckpointService : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromMinutes(5);
    private readonly ILogger<PeriodicWalCheckpointService> _logger;
    private readonly IServiceScopeFactory _scopeFactory;

    public PeriodicWalCheckpointService(
        ILogger<PeriodicWalCheckpointService> logger,
        IServiceScopeFactory scopeFactory)
    {
        _logger = logger;
        _scopeFactory = scopeFactory;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation(
            "Starting periodic database maintenance service (checkpoint + health check, interval: {Minutes} minutes)",
            Interval.TotalMinutes);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(Interval, stoppingToken);
                using var scope = _scopeFactory.CreateScope();
                var dbHealth = scope.ServiceProvider.GetRequiredService<IDatabaseHealthService>();
                if (!await dbHealth.MaintainAsync(repairIfUnhealthy: true, stoppingToken))
                    _logger.LogCritical("Periodic database maintenance detected corruption and repair failed");
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Periodic database maintenance failed");
            }
        }
    }
}

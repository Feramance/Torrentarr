using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Torrentarr.Core.Interfaces;

namespace Torrentarr.Infrastructure.Services;

/// <summary>
/// Restarts all workers when <see cref="DatabaseRestartCoordinator"/> requests coordinated recovery.
/// </summary>
public class DatabaseRestartWatchdogService : BackgroundService
{
    private readonly ILogger<DatabaseRestartWatchdogService> _logger;
    private readonly DatabaseRestartCoordinator _coordinator;
    private readonly IProcessOrchestrator _arrWorkers;
    private readonly QBitCategoryWorkerManager _qbitCategoryWorkers;

    public DatabaseRestartWatchdogService(
        ILogger<DatabaseRestartWatchdogService> logger,
        DatabaseRestartCoordinator coordinator,
        IProcessOrchestrator arrWorkers,
        QBitCategoryWorkerManager qbitCategoryWorkers)
    {
        _logger = logger;
        _coordinator = coordinator;
        _arrWorkers = arrWorkers;
        _qbitCategoryWorkers = qbitCategoryWorkers;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            var hostRestartRequested = _coordinator.RestartRequested;
            try
            {
                var statuses = await _arrWorkers.GetProcessStatusAsync();
                if (hostRestartRequested)
                {
                    _logger.LogCritical("Database restart signal detected — restarting all workers for coordinated recovery");
                    _coordinator.ClearRestartRequest();
                    foreach (var status in statuses.Values.Where(s => s.IsAlive || s.RestartRequested))
                        await _arrWorkers.RestartProcessAsync(status.Name);
                    await _qbitCategoryWorkers.RestartAllCategoriesAsync();
                }
                else
                {
                    foreach (var status in statuses.Values.Where(s => s.RestartRequested))
                        await _arrWorkers.RestartProcessAsync(status.Name);
                }

            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Coordinated database restart failed");
            }

            try
            {
                await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }
}

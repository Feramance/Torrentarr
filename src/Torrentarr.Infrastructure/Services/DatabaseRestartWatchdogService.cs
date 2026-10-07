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
            if (_coordinator.RestartRequested)
            {
                _logger.LogCritical(
                    "Database restart signal detected — restarting all workers for coordinated recovery");
                _coordinator.ClearRestartRequest();

                try
                {
                    foreach (var name in (await _arrWorkers.GetProcessStatusAsync()).Keys)
                        await _arrWorkers.RestartProcessAsync(name);
                    await _qbitCategoryWorkers.RestartAllCategoriesAsync();
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Coordinated database restart failed");
                }
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

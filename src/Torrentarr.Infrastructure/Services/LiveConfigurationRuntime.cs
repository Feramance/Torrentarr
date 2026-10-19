using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Serilog.Core;
using Serilog.Events;
using Torrentarr.Core.Configuration;
using Torrentarr.Core.Interfaces;
using Torrentarr.Core.Services;

namespace Torrentarr.Infrastructure.Services;

public sealed class LiveConfigurationRuntime(TorrentarrConfig config, IConfigReloader reloader,
    ITorrentClientRegistry clients, IServiceProvider services, LoggingLevelSwitch levelSwitch,
    ILogger<LiveConfigurationRuntime> logger) : BackgroundService
{
    public static LogEventLevel ParseLevel(string? value) => value?.Trim().ToUpperInvariant() switch
    {
        "TRACE" or "VERBOSE" => LogEventLevel.Verbose,
        "DEBUG" => LogEventLevel.Debug,
        "WARN" or "WARNING" => LogEventLevel.Warning,
        "ERROR" => LogEventLevel.Error,
        "CRITICAL" or "FATAL" => LogEventLevel.Fatal,
        _ => LogEventLevel.Information
    };

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        long applied = -1;
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                var revision = reloader.Revision;
                if (revision != applied)
                {
                    try
                    {
                        using var operation = config.BeginOperation();
                        levelSwitch.MinimumLevel = ParseLevel(config.Settings.ConsoleLevel);
                        await clients.ReconcileAsync(config.GetAllTorrentClients(), ct: stoppingToken);
                        if (services.GetService<WorkerProcessSupervisor>() is { } workers)
                            await workers.ReconcileConfigurationAsync(stoppingToken);
                        reloader.Acknowledge("runtime", revision);
                        applied = revision;
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        logger.LogWarning("Configuration runtime update failed ({ErrorType})", ex.GetType().Name);
                        reloader.Acknowledge("runtime", revision, "Runtime configuration could not be applied.");
                        await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
                        continue;
                    }
                }
                await reloader.WaitForChangeAsync(applied, TimeSpan.FromSeconds(5), stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
    }
}

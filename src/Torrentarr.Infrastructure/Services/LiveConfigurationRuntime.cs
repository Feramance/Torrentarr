using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Serilog.Core;
using Serilog.Events;
using Torrentarr.Core.Configuration;
using Torrentarr.Core.Interfaces;
using Torrentarr.Core.Services;

namespace Torrentarr.Infrastructure.Services;

/// <summary>Reconciles live resources without restarting their owning processes.</summary>
public sealed class LiveConfigurationRuntime(IServiceProvider services, TorrentarrConfig config,
    IConfigReloader reloader, ILogger<LiveConfigurationRuntime> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        long applied = -1;
        string? policy = null;
        var ffprobeEnabled = config.Settings.FFprobeAutoUpdate;
        reloader.Acknowledge("runtime", 0);
        while (!stoppingToken.IsCancellationRequested)
        {
            var revision = reloader.Revision;
            if (revision != applied)
            {
                try
                {
                    using var mutation = ConfigMutationGuard.EnterMutation(reloader.ConfigPath);
                    var clients = config.GetAllTorrentClients();
                    var connected = await services.GetRequiredService<ITorrentClientRegistry>().ReconcileAsync(clients, ct: stoppingToken);
                    var expected = clients.Values.Count(c => !c.Disabled && !string.IsNullOrWhiteSpace(c.Host) && c.Host != "CHANGE_ME");
                    if (connected < expected) throw new IOException("Torrent client connection settings could not be activated.");
                    var nextPolicy = Newtonsoft.Json.JsonConvert.SerializeObject(new { config.Settings, Clients = config.GetAllTorrentClients(), config.ArrInstances });
                    if (policy != nextPolicy)
                    {
                        services.GetService<ITorrentCacheService>()?.Clear();
                        services.GetService<IImportPathTracker>()?.Clear();
                        services.GetService<StalledUploadTracker>()?.Clear();
                        policy = nextPolicy;
                    }
                    if (services.GetService<WorkerProcessSupervisor>() is { } supervisor)
                        await supervisor.ReconcileConfigurationAsync(stoppingToken);
                    if (services.GetService<QBitCategoryWorkerManager>() is { } categories)
                        await categories.SyncWorkersWithConfigAsync();
                    if (services.GetService<LoggingLevelSwitch>() is { } level)
                        level.MinimumLevel = ParseLevel(config.Settings.ConsoleLevel);
                    mutation.Dispose();
                    if (config.Settings.FFprobeAutoUpdate && !ffprobeEnabled)
                        await services.GetRequiredService<IMediaValidationService>().UpdateFFprobeAsync(stoppingToken);
                    ffprobeEnabled = config.Settings.FFprobeAutoUpdate;
                    applied = revision;
                    reloader.Acknowledge("runtime", applied);
                }
                catch (ConfigurationBusyException) { }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    logger.LogWarning("Live configuration resource reconciliation failed ({Type}); retrying", ex.GetType().Name);
                    reloader.Acknowledge("runtime", applied, "Resource reconciliation failed; retrying.");
                }
            }
            if (services.GetService<WorkerProcessSupervisor>() is { } workers)
            {
                await workers.ReconcileConfigurationAsync(stoppingToken);
                foreach (var (name, status) in await workers.GetProcessStatusAsync())
                    if (config.ArrInstances.ContainsKey(name)) reloader.Acknowledge("worker:" + name, status.ConfigRevision,
                        status.IsAlive ? null : "Worker is unavailable.");
            }
            await reloader.WaitForChangeAsync(revision, TimeSpan.FromSeconds(1), stoppingToken);
        }
    }
    public static LogEventLevel ParseLevel(string? value) => value?.ToUpperInvariant() switch
    {
        "TRACE" or "VERBOSE" => LogEventLevel.Verbose,
        "DEBUG" => LogEventLevel.Debug,
        "WARN" or "WARNING" => LogEventLevel.Warning,
        "ERROR" => LogEventLevel.Error,
        "CRITICAL" or "FATAL" => LogEventLevel.Fatal,
        _ => LogEventLevel.Information
    };
}

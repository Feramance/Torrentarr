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
    private sealed class ClientActivationException(string message) : IOException(message) { }

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
        (long Revision, string ErrorType, string Message)? lastFailure = null;
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
                        var desired = config.GetAllTorrentClients();
                        var connected = await clients.ReconcileAsync(desired, ct: stoppingToken);
                        var expected = desired.Values.Count(client => !client.Disabled && client.Host != "CHANGE_ME");
                        if (services.GetService<WorkerProcessSupervisor>() is { } workers)
                            await workers.ReconcileConfigurationAsync(stoppingToken);
                        if (services.GetService<QBitCategoryWorkerManager>() is { } categories)
                            await categories.SyncWorkersWithConfigAsync();
                        if (connected < expected)
                        {
                            var unavailable = desired.Where(pair => !pair.Value.Disabled && pair.Value.Host != "CHANGE_ME"
                                && !clients.IsConnected(pair.Key)).Select(pair => pair.Key).ToArray();
                            var failures = (clients as QBittorrentConnectionManager)?.GetConnectionErrors();
                            var detail = string.Join("; ", unavailable.Select(id =>
                                $"{id}: {(failures != null && failures.TryGetValue(id, out var error) ? error : "client not connected")}"));
                            throw new ClientActivationException($"Configuration revision {revision}: {expected - connected} of {expected} enabled torrent clients unavailable. {detail}");
                        }
                        reloader.Acknowledge("runtime", revision);
                        applied = revision;
                        lastFailure = null;
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        var failure = (Revision: revision, ErrorType: ex.GetType().Name, Message: ex.Message);
                        if (lastFailure != failure)
                        {
                            // Do not log raw HTTP exceptions; they may contain URL query credentials.
                            logger.LogWarning("Configuration runtime update failed at revision {Revision} ({ErrorType}); inspect component status",
                                revision, ex.GetType().Name);
                            lastFailure = failure;
                        }
                        reloader.Acknowledge("runtime", revision, ex is ClientActivationException
                            ? ex.Message
                            : $"Configuration revision {revision}: runtime update failed ({ex.GetType().Name}). Inspect server logs.");
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

using System.Collections.Concurrent;
using Torrentarr.Core.Configuration;
using Torrentarr.Core.Services;
using Torrentarr.Infrastructure.ApiClients.Arr;
using Torrentarr.Infrastructure.ApiClients.QBittorrent;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Torrentarr.Infrastructure.Services;

/// <summary>
/// Background service that manages one async worker per configured Arr instance.
/// Each worker: syncs DB from Arr API → processes torrents → searches missing media → sleeps.
/// Workers can be restarted individually via RestartWorkerAsync (called by restart endpoints).
/// </summary>
public class ArrWorkerManager : BackgroundService
{
    private readonly ILogger<ArrWorkerManager> _logger;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly TorrentarrConfig _config;
    private readonly ProcessStateManager _stateManager;

    // Per-instance worker tracking: instanceName → (Task, CancellationTokenSource)
    private readonly ConcurrentDictionary<string, (Task Task, CancellationTokenSource Cts)> _workers =
        new(StringComparer.OrdinalIgnoreCase);

    private CancellationToken _appStopping;

    public ArrWorkerManager(
        ILogger<ArrWorkerManager> logger,
        IServiceScopeFactory scopeFactory,
        TorrentarrConfig config,
        ProcessStateManager stateManager)
    {
        _logger = logger;
        _scopeFactory = scopeFactory;
        _config = config;
        _stateManager = stateManager;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _appStopping = stoppingToken;

        // Initialise state for every configured instance (alive = false until worker starts)
        // Create 2 process states per Arr instance: "search" and "torrent"
        foreach (var (name, arrCfg) in _config.ArrInstances)
        {
            // Search process state
            _stateManager.Initialize(name + "-search", new ArrProcessState
            {
                Name = name + "-search",
                Category = name,
                Kind = "search",
                Alive = false,
                Rebuilding = false
            });

            // Torrent process state
            _stateManager.Initialize(name + "-torrent", new ArrProcessState
            {
                Name = name + "-torrent",
                Category = arrCfg.Category ?? "",
                Kind = "torrent",
                Alive = false,
                Rebuilding = false
            });
        }

        // Start a worker for every managed instance that has a real URI
        foreach (var (name, arrCfg) in _config.ArrInstances)
        {
            if (arrCfg.Managed && !string.IsNullOrEmpty(arrCfg.URI) && arrCfg.URI != "CHANGE_ME")
                StartWorker(name, stoppingToken);
        }

        // Block until the host signals shutdown
        try { await Task.Delay(Timeout.Infinite, stoppingToken); }
        catch (OperationCanceledException) { }

        await StopAllWorkersAsync();
    }

    /// <summary>Restart a single instance worker (called from restart endpoint).</summary>
    public async Task RestartWorkerAsync(string instanceName)
    {
        _logger.LogInformation("Restarting worker for {Instance}", instanceName);

        var searchStateName = instanceName + "-search";
        var torrentStateName = instanceName + "-torrent";

        _stateManager.Update(searchStateName, s => { s.Alive = false; s.Rebuilding = true; });
        _stateManager.Update(torrentStateName, s => { s.Alive = false; s.Rebuilding = true; });

        if (_workers.TryRemove(instanceName, out var old))
        {
            old.Cts.Cancel();
            try { await old.Task.WaitAsync(TimeSpan.FromSeconds(10)); } catch { }
            old.Cts.Dispose();
        }

        StartWorker(instanceName, _appStopping);
    }

    /// <summary>Restart all workers.</summary>
    public async Task RestartAllWorkersAsync()
    {
        foreach (var name in _workers.Keys.ToList())
            await RestartWorkerAsync(name);
    }

    // ── Internal ─────────────────────────────────────────────────────────────

    private void StartWorker(string instanceName, CancellationToken appStopping)
    {
        if (!_config.ArrInstances.TryGetValue(instanceName, out var arrCfg))
            return;

        var cts = CancellationTokenSource.CreateLinkedTokenSource(appStopping);
        var task = Task.Run(() => RunWorkerAsync(instanceName, arrCfg, cts.Token), CancellationToken.None);
        _workers[instanceName] = (task, cts);
    }

    private async Task RunWorkerAsync(string instanceName, ArrInstanceConfig arrCfg, CancellationToken ct)
    {
        var searchStateName = instanceName + "-search";
        var torrentStateName = instanceName + "-torrent";

        _logger.LogInformation("Worker starting: {Instance} ({Type}/{Category})",
            instanceName, arrCfg.Type, arrCfg.Category);

        _stateManager.Update(searchStateName, s => { s.Alive = true; s.Rebuilding = false; s.Status = "Starting..."; });
        _stateManager.Update(torrentStateName, s => { s.Alive = true; s.Rebuilding = false; });

        try
        {
            // Initial sync on startup
            _stateManager.Update(searchStateName, s => s.Status = "Syncing database...");
            await RunSyncAsync(instanceName, ct);

            while (!ct.IsCancellationRequested)
            {
                var loopStart = DateTime.UtcNow;

                try
                {
                    // 1. Process torrents (unless SearchOnly mode)
                    if (!arrCfg.SearchOnly)
                    {
                        _stateManager.Update(searchStateName, s => s.Status = "Processing torrents...");
                        await RunTorrentProcessingAsync(instanceName, arrCfg, ct);
                    }

                    // 2. Search missing media (unless ProcessingOnly mode)
                    if (!arrCfg.ProcessingOnly && arrCfg.Search.SearchMissing)
                    {
                        _stateManager.Update(searchStateName, s => s.Status = "Searching...");
                        var result = await RunSearchAsync(instanceName, arrCfg, ct);
                        if (result != null)
                        {
                            _stateManager.Update(searchStateName, s =>
                            {
                                s.SearchSummary = $"{result.SearchesTriggered} searches triggered ({result.ItemsSearched} items)";
                                s.SearchTimestamp = DateTime.UtcNow.ToString("o");
                                s.MetricType = "search";
                            });
                        }
                    }

                    // 3. Re-sync DB after processing
                    _stateManager.Update(searchStateName, s => s.Status = "Syncing database...");
                    await RunSyncAsync(instanceName, ct);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Worker loop error for {Instance}", instanceName);
                }

                // Sleep for remainder of the configured interval
                var elapsed = (int)(DateTime.UtcNow - loopStart).TotalMilliseconds;
                var sleepMs = Math.Max(0, _config.Settings.LoopSleepTimer * 1000 - elapsed);
                if (sleepMs > 0)
                {
                    _stateManager.Update(searchStateName, s => s.Status = "Waiting for next cycle...");
                    try { await Task.Delay(sleepMs, ct); }
                    catch (OperationCanceledException) { break; }
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Fatal error in worker for {Instance}", instanceName);
        }
        finally
        {
            _stateManager.Update(searchStateName, s => { s.Alive = false; s.Rebuilding = false; s.Status = null; });
            _stateManager.Update(torrentStateName, s => { s.Alive = false; s.Rebuilding = false; });
            _logger.LogInformation("Worker stopped: {Instance}", instanceName);
        }
    }

    private async Task RunSyncAsync(string instanceName, CancellationToken ct)
    {
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var svc = scope.ServiceProvider.GetRequiredService<ArrSyncService>();
            await svc.SyncAsync(instanceName, ct);
            await svc.MarkRequestsAsync(instanceName, ct);

            await UpdateCountsAsync(instanceName, ct);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Sync failed for {Instance}", instanceName);
        }
    }

    private async Task UpdateCountsAsync(string instanceName, CancellationToken ct)
    {
        if (!_config.ArrInstances.TryGetValue(instanceName, out var arrCfg))
            return;

        var torrentStateName = instanceName + "-torrent";

        try
        {
            int? queueCount = null;
            int? categoryCount = null;

            object? client = arrCfg.Type.ToLowerInvariant() switch
            {
                "radarr" => new RadarrClient(arrCfg.URI, arrCfg.APIKey),
                "sonarr" => new SonarrClient(arrCfg.URI, arrCfg.APIKey),
                "lidarr" => new LidarrClient(arrCfg.URI, arrCfg.APIKey),
                _ => null
            };

            if (client is RadarrClient radarr)
            {
                var queue = await radarr.GetQueueAsync(ct: ct);
                queueCount = queue?.TotalRecords ?? 0;
            }
            else if (client is SonarrClient sonarr)
            {
                var queue = await sonarr.GetQueueAsync(ct: ct);
                queueCount = queue?.TotalRecords ?? 0;
            }
            else if (client is LidarrClient lidarr)
            {
                var queue = await lidarr.GetQueueAsync(ct: ct);
                queueCount = queue?.TotalRecords ?? 0;
            }

            foreach (var (qbitName, qbitCfg) in _config.QBitInstances)
            {
                if (qbitCfg.Disabled || qbitCfg.Host == "CHANGE_ME")
                    continue;

                var qbitClient = new QBittorrentClient(qbitCfg.Host, qbitCfg.Port, qbitCfg.UserName, qbitCfg.Password);
                try
                {
                    var loginSuccess = await qbitClient.LoginAsync(ct);
                    if (!loginSuccess)
                        continue;

                    var torrents = await qbitClient.GetTorrentsAsync(arrCfg.Category, ct);
                    categoryCount = torrents.Count;
                    break;
                }
                catch
                {
                    continue;
                }
            }

            _stateManager.Update(torrentStateName, s =>
            {
                s.QueueCount = queueCount;
                s.CategoryCount = categoryCount;
            });
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to update counts for {Instance}", instanceName);
        }
    }

    private async Task RunTorrentProcessingAsync(string instanceName, ArrInstanceConfig arrCfg, CancellationToken ct)
    {
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var processor = scope.ServiceProvider.GetRequiredService<ITorrentProcessor>();
            await processor.ProcessTorrentsAsync(arrCfg.Category, ct);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Torrent processing failed for {Instance}", instanceName);
        }
    }

    private async Task<SearchResult?> RunSearchAsync(string instanceName, ArrInstanceConfig arrCfg, CancellationToken ct)
    {
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var mediaSvc = scope.ServiceProvider.GetRequiredService<IArrMediaService>();
            return await mediaSvc.SearchMissingMediaAsync(arrCfg.Category, ct);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Search failed for {Instance}", instanceName);
            return null;
        }
    }

    private async Task StopAllWorkersAsync()
    {
        var snapshot = _workers.ToArray();
        foreach (var (_, (_, cts)) in snapshot)
            cts.Cancel();
        foreach (var (_, (task, cts)) in snapshot)
        {
            try { await task.WaitAsync(TimeSpan.FromSeconds(10)); } catch { }
            cts.Dispose();
        }
        _workers.Clear();
    }
}

using System.Collections.Concurrent;
using Torrentarr.Core.Configuration;
using Torrentarr.Core.Services;
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
        foreach (var (name, arrCfg) in _config.ArrInstances)
        {
            _stateManager.Initialize(name, new ArrProcessState
            {
                Name = name,
                Category = arrCfg.Category ?? "",
                Kind = arrCfg.Type,
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

        _stateManager.Update(instanceName, s => { s.Alive = false; s.Rebuilding = true; });

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
        _logger.LogInformation("Worker starting: {Instance} ({Type}/{Category})",
            instanceName, arrCfg.Type, arrCfg.Category);

        _stateManager.Update(instanceName, s => { s.Alive = true; s.Rebuilding = false; });

        try
        {
            // Initial sync on startup
            await RunSyncAsync(instanceName, ct);

            while (!ct.IsCancellationRequested)
            {
                var loopStart = DateTime.UtcNow;

                try
                {
                    // 1. Process torrents (unless SearchOnly mode)
                    if (!arrCfg.SearchOnly)
                    {
                        await RunTorrentProcessingAsync(instanceName, arrCfg, ct);
                    }

                    // 2. Search missing media (unless ProcessingOnly mode)
                    if (!arrCfg.ProcessingOnly && arrCfg.Search.SearchMissing)
                    {
                        var result = await RunSearchAsync(instanceName, arrCfg, ct);
                        if (result != null)
                        {
                            _stateManager.Update(instanceName, s =>
                            {
                                s.SearchSummary = $"{result.SearchesTriggered} searches triggered ({result.ItemsSearched} items)";
                                s.SearchTimestamp = DateTime.UtcNow.ToString("o");
                                s.MetricType = "search";
                            });
                        }
                    }

                    // 3. Re-sync DB after processing
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
            _stateManager.Update(instanceName, s => { s.Alive = false; s.Rebuilding = false; });
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
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Sync failed for {Instance}", instanceName);
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

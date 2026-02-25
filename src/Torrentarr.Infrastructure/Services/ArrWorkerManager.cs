using System.Collections.Concurrent;
using Torrentarr.Core;
using Torrentarr.Core.Configuration;
using Torrentarr.Core.Services;
using Torrentarr.Infrastructure.ApiClients.Arr;
using Torrentarr.Infrastructure.ApiClients.QBittorrent;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Serilog;
using Serilog.Context;

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

    // Per-instance last-search timestamp for SearchRequestsEvery throttling
    private readonly ConcurrentDictionary<string, DateTime> _lastSearchTime =
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
        InstanceContext.Current = instanceName;
        
        using (LogContext.PushProperty("ProcessInstance", instanceName))
        using (LogContext.PushProperty("ProcessType", "Worker"))
        {
            await RunWorkerCoreAsync(instanceName, arrCfg, ct);
        }
    }

    private async Task RunWorkerCoreAsync(string instanceName, ArrInstanceConfig arrCfg, CancellationToken ct)
    {
        var searchStateName = instanceName + "-search";
        var torrentStateName = instanceName + "-torrent";

        _logger.LogInformation(
            "Search loop starting for {Instance} (SearchMissing={SearchMissing}, DoUpgradeSearch={DoUpgrade}, QualityUnmetSearch={Quality}, CustomFormatUnmetSearch={CF})",
            instanceName,
            arrCfg.Search.SearchMissing,
            arrCfg.Search.DoUpgradeSearch,
            arrCfg.Search.QualityUnmetSearch,
            arrCfg.Search.CustomFormatUnmetSearch);
        _logger.LogInformation("Search loop initialized successfully, entering main loop");

        LogScriptConfig(instanceName, arrCfg);

        _stateManager.Update(searchStateName, s => { s.Alive = true; s.Rebuilding = false; s.Status = "Starting..."; });
        _stateManager.Update(torrentStateName, s => { s.Alive = true; s.Rebuilding = false; });

        try
        {
            while (!ct.IsCancellationRequested)
            {
                var loopStart = DateTime.UtcNow;

                try
                {
                    // 1. Full sync at TOP of each cycle (qBitrr pattern):
                    //    SyncAsync → SyncSearchMetadataAsync → MarkRequestsAsync
                    _stateManager.Update(searchStateName, s => s.Status = "Syncing database...");
                    await RunSyncAsync(instanceName, ct);

                    // 2. Process torrents (unless SearchOnly mode)
                    if (!arrCfg.SearchOnly)
                    {
                        _stateManager.Update(searchStateName, s => s.Status = "Processing torrents...");
                        await RunTorrentProcessingAsync(instanceName, arrCfg, ct);
                    }

                    // 3. Search — throttled by SearchRequestsEvery (default 300s)
                    if (!arrCfg.ProcessingOnly && ShouldRunSearch(instanceName, arrCfg))
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
            _logger.LogInformation("Starting torrent monitoring for {Instance}", instanceName);
            using var scope = _scopeFactory.CreateScope();
            var processor = scope.ServiceProvider.GetRequiredService<ITorrentProcessor>();
            await processor.ProcessTorrentsAsync(arrCfg.Category, ct);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Torrent processing failed for {Instance}", instanceName);
        }
    }

    private bool ShouldRunSearch(string instanceName, ArrInstanceConfig arrCfg)
    {
        var interval = TimeSpan.FromSeconds(arrCfg.Search.SearchRequestsEvery);
        var last = _lastSearchTime.GetValueOrDefault(instanceName, DateTime.MinValue);
        if (DateTime.UtcNow - last >= interval)
        {
            _lastSearchTime[instanceName] = DateTime.UtcNow;
            return true;
        }
        return false;
    }

    private async Task<SearchResult?> RunSearchAsync(string instanceName, ArrInstanceConfig arrCfg, CancellationToken ct)
    {
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var mediaSvc = scope.ServiceProvider.GetRequiredService<IArrMediaService>();

            SearchResult? result = null;
            if (arrCfg.Search.SearchMissing)
                result = await mediaSvc.SearchMissingMediaAsync(arrCfg.Category, ct);

            if (arrCfg.Search.DoUpgradeSearch || arrCfg.Search.QualityUnmetSearch || arrCfg.Search.CustomFormatUnmetSearch)
                await mediaSvc.SearchQualityUpgradesAsync(arrCfg.Category, ct);

            return result;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Search failed for {Instance}: {Message}", instanceName, ex.Message);
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

    private void LogScriptConfig(string instanceName, ArrInstanceConfig arrCfg)
    {
        var searchConfig = arrCfg.Search;
        
        _logger.LogDebug("Script Config:  SearchMissing={SearchMissing}", searchConfig.SearchMissing);
        _logger.LogDebug("Script Config:  QualityUnmetSearch={Quality}", searchConfig.QualityUnmetSearch);
        _logger.LogDebug("Script Config:  CustomFormatUnmetSearch={CF}", searchConfig.CustomFormatUnmetSearch);
        _logger.LogDebug("Script Config:  DoUpgradeSearch={Upgrade}", searchConfig.DoUpgradeSearch);
        _logger.LogDebug("Script Config:  AlsoSearchSpecials={Specials}", searchConfig.AlsoSearchSpecials);
        _logger.LogDebug("Script Config:  SearchUnmonitored={Unmonitored}", searchConfig.Unmonitored);
        _logger.LogDebug("Script Config:  SearchByYear={ByYear}", searchConfig.SearchByYear);
        _logger.LogDebug("Script Config:  PrioritizeTodaysReleases={Today}", searchConfig.PrioritizeTodaysReleases);
        _logger.LogDebug("Script Config:  SearchLimit={Limit}", searchConfig.SearchLimit);
        _logger.LogDebug("Script Config:  ReSearch={ReSearch}", arrCfg.ReSearch);
        _logger.LogDebug("Script Config:  ImportMode={ImportMode}", arrCfg.ImportMode);
        
        var qbitCfg = _config.QBitInstances.Values.FirstOrDefault(q => 
            q.ManagedCategories.Contains(arrCfg.Category));
        
        if (qbitCfg != null)
        {
            var seeding = qbitCfg.CategorySeeding;
            _logger.LogDebug("Script Config:  MaxUploadRatio={MaxRatio}", seeding.MaxUploadRatio);
            _logger.LogDebug("Script Config:  MaxSeedingTime={MaxTime}", seeding.MaxSeedingTime);
            _logger.LogDebug("Script Config:  RemoveTorrent={Remove}", seeding.RemoveTorrent);
            _logger.LogDebug("Script Config:  UploadRateLimitPerTorrent={ULimit}", seeding.UploadRateLimitPerTorrent);
            _logger.LogDebug("Script Config:  DownloadRateLimitPerTorrent={DLimit}", seeding.DownloadRateLimitPerTorrent);
            _logger.LogDebug("Script Config:  HitAndRunMode={HNR}", seeding.HitAndRunMode);
            _logger.LogDebug("Script Config:  MinSeedRatio={MinRatio}", seeding.MinSeedRatio);
            _logger.LogDebug("Script Config:  MinSeedingTimeDays={MinDays}", seeding.MinSeedingTimeDays);
        }
        
        _logger.LogDebug("Script Config:  Category={Category}", arrCfg.Category);
    }
}

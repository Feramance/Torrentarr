using System.Collections.Concurrent;
using Torrentarr.Core;
using Torrentarr.Core.Configuration;
using Torrentarr.Core.Services;
using Torrentarr.Infrastructure.ApiClients.Arr;
using Torrentarr.Infrastructure.ApiClients.QBittorrent;
using Torrentarr.Infrastructure.Database;
using Torrentarr.Infrastructure.Database.Models;
using Microsoft.EntityFrameworkCore;
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

    // §2.6: Per-instance timers for RSS Sync and Refresh Monitored Downloads
    private readonly ConcurrentDictionary<string, DateTime> _lastRssSyncTime =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, DateTime> _lastRefreshDownloadsTime =
        new(StringComparer.OrdinalIgnoreCase);

    // Cached Arr clients — created once per instance, reused across UpdateCountsAsync ticks
    private readonly ConcurrentDictionary<string, object> _arrClientCache =
        new(StringComparer.OrdinalIgnoreCase);

    // Cached QBit clients for count polling — keyed by qBit instance name
    private readonly ConcurrentDictionary<string, QBittorrentClient> _qbitClientCache =
        new(StringComparer.OrdinalIgnoreCase);

    // §4: Process restart limits (qBitrr parity): per-instance restart timestamps for rate limiting
    private readonly ConcurrentDictionary<string, List<DateTime>> _restartTimestamps =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly object _restartLock = new();

    private readonly IConnectivityService _connectivityService;

    private CancellationToken _appStopping;

    public ArrWorkerManager(
        ILogger<ArrWorkerManager> logger,
        IServiceScopeFactory scopeFactory,
        TorrentarrConfig config,
        ProcessStateManager stateManager,
        IConnectivityService connectivityService)
    {
        _logger = logger;
        _scopeFactory = scopeFactory;
        _config = config;
        _stateManager = stateManager;
        _connectivityService = connectivityService;
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

        // §5: Load persisted search activity into state so Processes page shows last activity across restarts
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<TorrentarrDbContext>();
            var activities = await db.SearchActivity.ToListAsync(stoppingToken);
            foreach (var a in activities)
            {
                var stateKey = a.Category + "-search";
                if (_stateManager.GetState(stateKey) != null)
                    _stateManager.Update(stateKey, s => { s.SearchSummary = a.Summary; s.SearchTimestamp = a.Timestamp; });
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Could not load persisted search activity");
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
    /// <returns>True if restart was performed; false if gated by AutoRestartProcesses or MaxProcessRestarts.</returns>
    public async Task<bool> RestartWorkerAsync(string instanceName)
    {
        var settings = _config.Settings;
        if (!settings.AutoRestartProcesses)
        {
            _logger.LogWarning("Restart skipped for {Instance}: AutoRestartProcesses is disabled", instanceName);
            return false;
        }

        var windowSeconds = settings.ProcessRestartWindow;
        var maxRestarts = settings.MaxProcessRestarts;
        var delaySeconds = settings.ProcessRestartDelay;

        lock (_restartLock)
        {
            var list = _restartTimestamps.GetOrAdd(instanceName, _ => new List<DateTime>());
            var cutoff = DateTime.UtcNow.AddSeconds(-windowSeconds);
            list.RemoveAll(d => d < cutoff);
            if (list.Count >= maxRestarts)
            {
                _logger.LogWarning(
                    "Restart skipped for {Instance}: {Count} restarts in last {Window}s (max {Max})",
                    instanceName, list.Count, windowSeconds, maxRestarts);
                return false;
            }
            list.Add(DateTime.UtcNow);
        }

        if (delaySeconds > 0)
            await Task.Delay(TimeSpan.FromSeconds(delaySeconds));

        _logger.LogInformation("Restarting worker for {Instance}", instanceName);

        var searchStateName = instanceName + "-search";
        var torrentStateName = instanceName + "-torrent";

        _stateManager.Update(searchStateName, s => { s.Alive = false; s.Rebuilding = true; });
        _stateManager.Update(torrentStateName, s => { s.Alive = false; s.Rebuilding = true; });

        if (_workers.TryRemove(instanceName, out var old))
        {
            old.Cts.Cancel();
            try { await old.Task.WaitAsync(TimeSpan.FromSeconds(10)); }
            catch (OperationCanceledException) { }
            catch (TimeoutException) { _logger.LogWarning("Worker {Instance} did not stop within 10s", instanceName); }
            catch (Exception ex) { _logger.LogError(ex, "Worker {Instance} faulted during shutdown", instanceName); }
            old.Cts.Dispose();
        }

        StartWorker(instanceName, _appStopping);
        return true;
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

        // §1.2: ForceResetTempProfiles — restore any profiles switched in a previous session
        if (arrCfg.Search.UseTempForMissing && arrCfg.Search.ForceResetTempProfiles)
        {
            try
            {
                using var startupScope = _scopeFactory.CreateScope();
                var switcher = startupScope.ServiceProvider.GetRequiredService<QualityProfileSwitcherService>();
                await switcher.ForceResetAllTempProfilesAsync(instanceName, arrCfg, ct);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "ForceResetTempProfiles failed for {Instance}", instanceName);
            }
        }

        // §2.5: Consecutive error counter for exponential backoff
        int consecutiveErrors = 0;

        try
        {
            while (!ct.IsCancellationRequested)
            {
                var loopStart = DateTime.UtcNow;

                try
                {
                    // §2.4: Check connectivity before processing; sleep NoInternetSleepTimer on failure
                    if (!await _connectivityService.IsConnectedAsync(ct))
                    {
                        _logger.LogWarning("No internet connectivity detected, skipping cycle. Sleeping {Seconds}s",
                            _config.Settings.NoInternetSleepTimer);
                        _stateManager.Update(searchStateName, s => s.Status = "Waiting for connectivity...");
                        try { await Task.Delay(TimeSpan.FromSeconds(_config.Settings.NoInternetSleepTimer), ct); }
                        catch (OperationCanceledException) { break; }
                        continue;
                    }

                    // 1. Process torrents FIRST (qBitrr pattern: torrent monitoring before DB update)
                    if (!arrCfg.SearchOnly)
                    {
                        _stateManager.Update(searchStateName, s => s.Status = "Processing torrents...");
                        await RunTorrentProcessingAsync(instanceName, arrCfg, ct);
                    }

                    // 2. Sync DB from Arr API (after torrent processing, before search)
                    _stateManager.Update(searchStateName, s => s.Status = "Syncing database...");
                    await RunSyncAsync(instanceName, ct);

                    // §2.6: RSS Sync + Refresh Monitored Downloads (timer-gated)
                    await RunRssSyncIfDueAsync(instanceName, arrCfg, ct);
                    await RunRefreshMonitoredDownloadsIfDueAsync(instanceName, arrCfg, ct);

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
                            // §5: Persist search activity for Processes page across restarts
                            try
                            {
                                using var scope = _scopeFactory.CreateScope();
                                var db = scope.ServiceProvider.GetRequiredService<TorrentarrDbContext>();
                                var existing = await db.SearchActivity.FindAsync([instanceName], ct);
                                var ts = DateTime.UtcNow.ToString("o");
                                var summary = $"{result.SearchesTriggered} searches triggered ({result.ItemsSearched} items)";
                                if (existing != null)
                                {
                                    existing.Summary = summary;
                                    existing.Timestamp = ts;
                                }
                                else
                                    db.SearchActivity.Add(new SearchActivity { Category = instanceName, Summary = summary, Timestamp = ts });
                                await db.SaveChangesAsync(ct);
                            }
                            catch (Exception ex)
                            {
                                _logger.LogTrace(ex, "Could not persist search activity for {Instance}", instanceName);
                            }
                        }
                    }

                    // §2.5: Successful cycle — reset backoff counter
                    consecutiveErrors = 0;
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    // §2.5: Exponential backoff: Math.Min(2 × 1.5^n, 30) minutes
                    consecutiveErrors++;
                    var backoffMinutes = Math.Min(2.0 * Math.Pow(1.5, consecutiveErrors), 30.0);
                    _logger.LogError(ex, "Worker loop error #{Count} for {Instance} — backing off {Minutes:F1} min",
                        consecutiveErrors, instanceName, backoffMinutes);
                    _stateManager.Update(searchStateName, s => s.Status = $"Error — retrying in {backoffMinutes:F0} min...");
                    try { await Task.Delay(TimeSpan.FromMinutes(backoffMinutes), ct); }
                    catch (OperationCanceledException) { break; }
                    continue; // skip normal LoopSleepTimer sleep
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

            var client = _arrClientCache.GetOrAdd(instanceName, _ => arrCfg.Type.ToLowerInvariant() switch
            {
                "radarr" => (object)new RadarrClient(arrCfg.URI, arrCfg.APIKey),
                "sonarr" => new SonarrClient(arrCfg.URI, arrCfg.APIKey),
                "lidarr" => new LidarrClient(arrCfg.URI, arrCfg.APIKey),
                _ => new object()
            });

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

                var qbitClient = _qbitClientCache.GetOrAdd(qbitName, _ =>
                    new QBittorrentClient(qbitCfg.Host, qbitCfg.Port, qbitCfg.UserName, qbitCfg.Password));
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

    private async Task RunRssSyncIfDueAsync(string instanceName, ArrInstanceConfig arrCfg, CancellationToken ct)
    {
        // §2.6: RssSyncTimer is in minutes
        var interval = TimeSpan.FromMinutes(arrCfg.RssSyncTimer > 0 ? arrCfg.RssSyncTimer : 15);
        var last = _lastRssSyncTime.GetValueOrDefault(instanceName, DateTime.MinValue);
        if (DateTime.UtcNow - last < interval) return;

        try
        {
            _logger.LogDebug("Triggering RSS sync for {Instance}", instanceName);
            switch (arrCfg.Type.ToLowerInvariant())
            {
                case "radarr": await new Torrentarr.Infrastructure.ApiClients.Arr.RadarrClient(arrCfg.URI, arrCfg.APIKey).RssSyncAsync(ct); break;
                case "sonarr": await new Torrentarr.Infrastructure.ApiClients.Arr.SonarrClient(arrCfg.URI, arrCfg.APIKey).RssSyncAsync(ct); break;
                case "lidarr": await new Torrentarr.Infrastructure.ApiClients.Arr.LidarrClient(arrCfg.URI, arrCfg.APIKey).RssSyncAsync(ct); break;
            }
            _lastRssSyncTime[instanceName] = DateTime.UtcNow;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "RSS sync failed for {Instance}", instanceName);
        }
    }

    private async Task RunRefreshMonitoredDownloadsIfDueAsync(string instanceName, ArrInstanceConfig arrCfg, CancellationToken ct)
    {
        // §2.6: RefreshDownloadsTimer is in minutes
        var interval = TimeSpan.FromMinutes(arrCfg.RefreshDownloadsTimer > 0 ? arrCfg.RefreshDownloadsTimer : 1);
        var last = _lastRefreshDownloadsTime.GetValueOrDefault(instanceName, DateTime.MinValue);
        if (DateTime.UtcNow - last < interval) return;

        try
        {
            _logger.LogDebug("Triggering RefreshMonitoredDownloads for {Instance}", instanceName);
            switch (arrCfg.Type.ToLowerInvariant())
            {
                case "radarr": await new Torrentarr.Infrastructure.ApiClients.Arr.RadarrClient(arrCfg.URI, arrCfg.APIKey).RefreshMonitoredDownloadsAsync(ct); break;
                case "sonarr": await new Torrentarr.Infrastructure.ApiClients.Arr.SonarrClient(arrCfg.URI, arrCfg.APIKey).RefreshMonitoredDownloadsAsync(ct); break;
                case "lidarr": await new Torrentarr.Infrastructure.ApiClients.Arr.LidarrClient(arrCfg.URI, arrCfg.APIKey).RefreshMonitoredDownloadsAsync(ct); break;
            }
            _lastRefreshDownloadsTime[instanceName] = DateTime.UtcNow;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "RefreshMonitoredDownloads failed for {Instance}", instanceName);
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

            // §1.2: Restore quality profiles that have timed out before starting the search cycle
            if (arrCfg.Search.UseTempForMissing && !arrCfg.Search.KeepTempProfile && arrCfg.Search.TempProfileResetTimeoutMinutes > 0)
            {
                try
                {
                    var switcher = scope.ServiceProvider.GetRequiredService<QualityProfileSwitcherService>();
                    await switcher.RestoreTimedOutProfilesAsync(instanceName, arrCfg, ct);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "RestoreTimedOutProfiles failed for {Instance}", instanceName);
                }
            }

            SearchResult? result = null;

            // §2.7: DoUpgradeSearch is exclusive — when active, skip missing-media search
            if (arrCfg.Search.DoUpgradeSearch)
            {
                result = await mediaSvc.SearchQualityUpgradesAsync(arrCfg.Category, ct);
            }
            else
            {
                if (arrCfg.Search.SearchMissing)
                    result = await mediaSvc.SearchMissingMediaAsync(arrCfg.Category, ct);

                // QualityUnmetSearch / CustomFormatUnmetSearch are always additive (not exclusive)
                if (arrCfg.Search.QualityUnmetSearch || arrCfg.Search.CustomFormatUnmetSearch)
                    await mediaSvc.SearchQualityUpgradesAsync(arrCfg.Category, ct);
            }

            // §1.3: SearchAgainOnSearchCompletion — reset Searched=false so items re-enter the queue next cycle
            if (arrCfg.Search.SearchAgainOnSearchCompletion && result != null && result.SearchesTriggered > 0)
                await ResetSearchedFlagAsync(instanceName, arrCfg, ct);

            return result;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Search failed for {Instance}: {Message}", instanceName, ex.Message);
            return null;
        }
    }

    private async Task ResetSearchedFlagAsync(string instanceName, ArrInstanceConfig arrCfg, CancellationToken ct)
    {
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<Torrentarr.Infrastructure.Database.TorrentarrDbContext>();

            switch (arrCfg.Type.ToLowerInvariant())
            {
                case "radarr":
                    await db.Movies
                        .Where(m => m.ArrInstance == instanceName && m.Searched)
                        .ExecuteUpdateAsync(s => s.SetProperty(m => m.Searched, false), ct);
                    break;
                case "sonarr":
                    await db.Episodes
                        .Where(e => e.ArrInstance == instanceName && e.Searched)
                        .ExecuteUpdateAsync(s => s.SetProperty(e => e.Searched, false), ct);
                    break;
                case "lidarr":
                    await db.Albums
                        .Where(a => a.ArrInstance == instanceName && a.Searched)
                        .ExecuteUpdateAsync(s => s.SetProperty(a => a.Searched, false), ct);
                    break;
            }

            _logger.LogTrace("SearchAgainOnSearchCompletion: reset Searched=false for {Instance}", instanceName);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to reset Searched flag for {Instance}", instanceName);
        }
    }

    private async Task StopAllWorkersAsync()
    {
        var snapshot = _workers.ToArray();
        foreach (var (_, (_, cts)) in snapshot)
            cts.Cancel();
        foreach (var (name, (task, cts)) in snapshot)
        {
            try { await task.WaitAsync(TimeSpan.FromSeconds(10)); }
            catch (OperationCanceledException) { }
            catch (TimeoutException) { _logger.LogWarning("Worker {Instance} did not stop within 10s during shutdown", name); }
            catch (Exception ex) { _logger.LogError(ex, "Worker {Instance} faulted during shutdown", name); }
            cts.Dispose();
        }
        _workers.Clear();
    }

    private void LogScriptConfig(string instanceName, ArrInstanceConfig arrCfg)
    {
        var searchConfig = arrCfg.Search;
        var torrentConfig = arrCfg.Torrent;

        // Instance config summary (matches qBitrr's "{Name} Config:" debug line)
        _logger.LogDebug("{Instance} Config:  Managed: {Managed}, Re-search: {ReSearch}, ImportMode: {ImportMode}, Category: {Category}, URI: {URI}, RefreshDownloadsTimer={Refresh}, RssSyncTimer={Rss}",
            instanceName, arrCfg.Managed, arrCfg.ReSearch, arrCfg.ImportMode, arrCfg.Category, arrCfg.URI, arrCfg.RefreshDownloadsTimer, arrCfg.RssSyncTimer);

        // Torrent config fields (matches qBitrr "Script Config:" debug lines order)
        _logger.LogDebug("Script Config:  CaseSensitiveMatches={Value}", torrentConfig.CaseSensitiveMatches);
        _logger.LogDebug("Script Config:  FolderExclusionRegex={Value}", torrentConfig.FolderExclusionRegex);
        _logger.LogDebug("Script Config:  FileNameExclusionRegex={Value}", torrentConfig.FileNameExclusionRegex);
        _logger.LogDebug("Script Config:  FileExtensionAllowlist={Value}", torrentConfig.FileExtensionAllowlist);
        _logger.LogDebug("Script Config:  AutoDelete={Value}", torrentConfig.AutoDelete);
        _logger.LogDebug("Script Config:  IgnoreTorrentsYoungerThan={Value}", torrentConfig.IgnoreTorrentsYoungerThan);
        _logger.LogDebug("Script Config:  MaximumETA={Value}", torrentConfig.MaximumETA);
        _logger.LogDebug("Script Config:  MaximumDeletablePercentage={Value}", torrentConfig.MaximumDeletablePercentage);
        _logger.LogDebug("Script Config:  StalledDelay={Value}", torrentConfig.StalledDelay);
        _logger.LogDebug("Script Config:  ReSearchStalled={Value}", torrentConfig.ReSearchStalled);

        // Search config fields
        _logger.LogDebug("Script Config:  SearchMissing={SearchMissing}", searchConfig.SearchMissing);
        _logger.LogDebug("Script Config:  AlsoSearchSpecials={Specials}", searchConfig.AlsoSearchSpecials);
        _logger.LogDebug("Script Config:  SearchUnmonitored={Unmonitored}", searchConfig.Unmonitored);
        _logger.LogDebug("Script Config:  SearchByYear={ByYear}", searchConfig.SearchByYear);
        _logger.LogDebug("Script Config:  SearchInReverse={InReverse}", searchConfig.SearchInReverse);
        _logger.LogDebug("Script Config:  CommandLimit={Limit}", searchConfig.SearchLimit);
        _logger.LogDebug("Script Config:  DoUpgradeSearch={Upgrade}", searchConfig.DoUpgradeSearch);
        _logger.LogDebug("Script Config:  QualityUnmetSearch={Quality}", searchConfig.QualityUnmetSearch);
        _logger.LogDebug("Script Config:  CustomFormatUnmetSearch={CF}", searchConfig.CustomFormatUnmetSearch);
        _logger.LogDebug("Script Config:  PrioritizeTodaysReleases={Today}", searchConfig.PrioritizeTodaysReleases);
        _logger.LogDebug("Script Config:  SearchBySeries={BySeries}", searchConfig.SearchBySeries);
        _logger.LogDebug("Script Config:  SearchOmbiRequests={Ombi}", searchConfig.Ombi?.SearchOmbiRequests ?? false);
        _logger.LogDebug("Script Config:  SearchOverseerrRequests={Overseerr}", searchConfig.Overseerr?.SearchOverseerrRequests ?? false);

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

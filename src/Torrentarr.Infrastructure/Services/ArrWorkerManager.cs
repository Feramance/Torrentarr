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
/// Background service that manages in-process torrent and search tasks per configured Arr instance.
/// Torrent and search loops run concurrently so search is not blocked behind torrent processing.
/// Tasks can be restarted individually via RestartWorkerAsync (called by restart endpoints).
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
    /// <summary>qBitrr loop_completed: candidate list was fully drained on the previous search tick.</summary>
    private readonly ConcurrentDictionary<string, bool> _loopCompleted =
        new(StringComparer.OrdinalIgnoreCase);

    private int _ffprobeUpdateAttempted;

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
    private readonly SearchYearCursor _yearCursor;

    private CancellationToken _appStopping;

    public ArrWorkerManager(
        ILogger<ArrWorkerManager> logger,
        IServiceScopeFactory scopeFactory,
        TorrentarrConfig config,
        ProcessStateManager stateManager,
        IConnectivityService connectivityService,
        SearchYearCursor? yearCursor = null)
    {
        _logger = logger;
        _scopeFactory = scopeFactory;
        _config = config;
        _stateManager = stateManager;
        _connectivityService = connectivityService;
        _yearCursor = yearCursor ?? new SearchYearCursor();
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
        _logger.LogInformation("In-process torrent and search tasks initialized, entering loops");

        LogScriptConfig(instanceName, arrCfg);

        _stateManager.Update(searchStateName, s => { s.Alive = true; s.Rebuilding = false; s.Status = "Starting..."; });
        _stateManager.Update(torrentStateName, s => { s.Alive = true; s.Rebuilding = false; });

        // Diagnostic only — do not block category init / torrent processing on a hung Arr.
        _ = ProbeArrVersionAsync(instanceName, arrCfg, ct);

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

        // Ensure qBit category exists and tracker tags are pre-created
        try
        {
            using var initScope = _scopeFactory.CreateScope();
            var ensure = initScope.ServiceProvider.GetRequiredService<QBitCategoryEnsureService>();
            await ensure.EnsureCategoryOnAllInstancesAsync(arrCfg.Category, ct);
            if (initScope.ServiceProvider.GetRequiredService<ISeedingService>() is SeedingService seedingConcrete)
                await seedingConcrete.EnsureAllTrackerTagsExistAsync(ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Category/tag initialization failed for {Instance}", instanceName);
        }

        await TryUpdateFFprobeAsync(ct);

        try
        {
            await Task.WhenAll(
                RunTorrentLoopAsync(instanceName, arrCfg, torrentStateName, ct),
                RunSearchLoopAsync(instanceName, arrCfg, searchStateName, ct));
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

    private async Task TryUpdateFFprobeAsync(CancellationToken ct)
    {
        if (Interlocked.Exchange(ref _ffprobeUpdateAttempted, 1) != 0)
            return;
        if (!_config.Settings.FFprobeAutoUpdate)
            return;

        try
        {
            using var scope = _scopeFactory.CreateScope();
            var media = scope.ServiceProvider.GetService<IMediaValidationService>();
            if (media == null)
                return;
            await media.UpdateFFprobeAsync(ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "FFprobe auto-update failed");
        }
    }

    private async Task RunTorrentLoopAsync(
        string instanceName, ArrInstanceConfig arrCfg, string torrentStateName, CancellationToken ct)
    {
        int consecutiveErrors = 0;
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var loopStart = DateTime.UtcNow;
                try
                {
                    if (!await _connectivityService.IsConnectedAsync(ct))
                    {
                        _logger.LogWarning("No internet connectivity detected, skipping torrent cycle. Sleeping {Seconds}s",
                            _config.Settings.NoInternetSleepTimer);
                        _stateManager.Update(torrentStateName, s => s.Status = "Waiting for connectivity...");
                        try { await Task.Delay(TimeSpan.FromSeconds(_config.Settings.NoInternetSleepTimer), ct); }
                        catch (OperationCanceledException) { break; }
                        continue;
                    }

                    if (!arrCfg.SearchOnly)
                    {
                        _stateManager.Update(torrentStateName, s => s.Status = "Processing torrents...");
                        await RunTorrentProcessingAsync(instanceName, arrCfg, ct);
                    }

                    await RunRssSyncIfDueAsync(instanceName, arrCfg, ct);
                    await RunRefreshMonitoredDownloadsIfDueAsync(instanceName, arrCfg, ct);
                    consecutiveErrors = 0;
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    consecutiveErrors++;
                    var backoffMinutes = Math.Min(2.0 * Math.Pow(1.5, consecutiveErrors), 30.0);
                    _logger.LogError(ex, "Torrent loop error #{Count} for {Instance} — backing off {Minutes:F1} min",
                        consecutiveErrors, instanceName, backoffMinutes);
                    _stateManager.Update(torrentStateName, s => s.Status = $"Error — retrying in {backoffMinutes:F0} min...");
                    try { await Task.Delay(TimeSpan.FromMinutes(backoffMinutes), ct); }
                    catch (OperationCanceledException) { break; }
                    continue;
                }

                await SleepRemainderAsync(loopStart, torrentStateName, ct);
            }
        }
        catch (OperationCanceledException) { }
    }

    private async Task RunSearchLoopAsync(
        string instanceName, ArrInstanceConfig arrCfg, string searchStateName, CancellationToken ct)
    {
        int consecutiveErrors = 0;
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var loopStart = DateTime.UtcNow;
                try
                {
                    if (!await _connectivityService.IsConnectedAsync(ct))
                    {
                        _logger.LogWarning("No internet connectivity detected, skipping search cycle. Sleeping {Seconds}s",
                            _config.Settings.NoInternetSleepTimer);
                        _stateManager.Update(searchStateName, s => s.Status = "Waiting for connectivity...");
                        try { await Task.Delay(TimeSpan.FromSeconds(_config.Settings.NoInternetSleepTimer), ct); }
                        catch (OperationCanceledException) { break; }
                        continue;
                    }

                    _stateManager.Update(searchStateName, s =>
                    {
                        s.Status = "Syncing database...";
                        s.SearchSummary = "Updating database";
                    });
                    await RunSyncAsync(instanceName, arrCfg, ct);

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
                            await PersistSearchActivityAsync(instanceName, result, ct);
                        }
                    }

                    consecutiveErrors = 0;
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    consecutiveErrors++;
                    var backoffMinutes = Math.Min(2.0 * Math.Pow(1.5, consecutiveErrors), 30.0);
                    _logger.LogError(ex, "Search loop error #{Count} for {Instance} — backing off {Minutes:F1} min",
                        consecutiveErrors, instanceName, backoffMinutes);
                    _stateManager.Update(searchStateName, s => s.Status = $"Error — retrying in {backoffMinutes:F0} min...");
                    try { await Task.Delay(TimeSpan.FromMinutes(backoffMinutes), ct); }
                    catch (OperationCanceledException) { break; }
                    continue;
                }

                await SleepRemainderAsync(loopStart, searchStateName, ct);
            }
        }
        catch (OperationCanceledException) { }
    }

    private async Task SleepRemainderAsync(DateTime loopStart, string stateName, CancellationToken ct)
    {
        var elapsed = (int)(DateTime.UtcNow - loopStart).TotalMilliseconds;
        var sleepMs = Math.Max(0, _config.Settings.LoopSleepTimer * 1000 - elapsed);
        if (sleepMs <= 0)
            return;
        _stateManager.Update(stateName, s => s.Status = "Waiting for next cycle...");
        try { await Task.Delay(sleepMs, ct); }
        catch (OperationCanceledException) { }
    }

    private async Task PersistSearchActivityAsync(string instanceName, SearchResult result, CancellationToken ct)
    {
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

    private async Task RunSyncAsync(string instanceName, ArrInstanceConfig arrCfg, CancellationToken ct)
    {
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var svc = scope.ServiceProvider.GetRequiredService<ArrSyncService>();
            await svc.SyncAsync(instanceName, ct);
            if (arrCfg.Search.SearchMissing)
                await svc.MarkRequestsAsync(instanceName, ct);

            await UpdateCountsAsync(instanceName, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Sync failed for {Instance}", instanceName);
            throw;
        }
    }

    private async Task ProbeArrVersionAsync(string instanceName, ArrInstanceConfig arrCfg, CancellationToken ct)
    {
        using var probeCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        probeCts.CancelAfter(TimeSpan.FromSeconds(5));
        await LogArrVersionAsync(instanceName, arrCfg, probeCts.Token);
    }

    private async Task LogArrVersionAsync(string instanceName, ArrInstanceConfig arrCfg, CancellationToken ct)
    {
        try
        {
            string? version = arrCfg.Type.ToLowerInvariant() switch
            {
                "radarr" => (await new RadarrClient(arrCfg.URI, arrCfg.APIKey).GetSystemInfoAsync(ct)).Version,
                "sonarr" => (await new SonarrClient(arrCfg.URI, arrCfg.APIKey).GetSystemInfoAsync(ct)).Version,
                "lidarr" => (await new LidarrClient(arrCfg.URI, arrCfg.APIKey).GetSystemInfoAsync(ct)).Version,
                _ => null
            };
            if (!TryGetKnownArrVersion(version, out var knownVersion))
            {
                _logger.LogWarning(
                    "Could not read {Type} version for {Instance} at {Uri} (empty or failed status response)",
                    arrCfg.Type, instanceName, arrCfg.URI);
                return;
            }

            _logger.LogInformation(
                "Connected to {Type} {Version} for instance {Instance} at {Uri}",
                arrCfg.Type, knownVersion, instanceName, arrCfg.URI);
        }
        catch (OperationCanceledException)
        {
            _logger.LogWarning("Could not read {Type} version for {Instance} at {Uri}",
                arrCfg.Type, instanceName, arrCfg.URI);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not read {Type} version for {Instance} at {Uri}",
                arrCfg.Type, instanceName, arrCfg.URI);
        }
    }

    /// <summary>
    /// Returns true when <paramref name="version"/> is a usable Arr version string.
    /// Empty status responses must not be logged as a successful connection.
    /// </summary>
    internal static bool TryGetKnownArrVersion(string? version, out string knownVersion)
    {
        if (string.IsNullOrWhiteSpace(version))
        {
            knownVersion = string.Empty;
            return false;
        }

        knownVersion = version.Trim();
        return true;
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
                "radarr" => (object)new RadarrClient(arrCfg.URI, arrCfg.APIKey, arrCfg.SkipTLSVerify),
                "sonarr" => new SonarrClient(arrCfg.URI, arrCfg.APIKey, arrCfg.SkipTLSVerify),
                "lidarr" => new LidarrClient(arrCfg.URI, arrCfg.APIKey, arrCfg.SkipTLSVerify),
                "readarr" => new ReadarrClient(arrCfg.URI, arrCfg.APIKey, arrCfg.SkipTLSVerify),
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
            else if (client is ReadarrClient readarr)
            {
                var queue = await readarr.GetQueueAsync(ct: ct);
                queueCount = queue?.TotalRecords ?? 0;
            }

            foreach (var (qbitName, qbitCfg) in _config.QBitInstances)
            {
                if (qbitCfg.Disabled || qbitCfg.Host == "CHANGE_ME")
                    continue;

                var qbitClient = _qbitClientCache.GetOrAdd(qbitName, _ =>
                    new QBittorrentClient(qbitCfg.Host, qbitCfg.Port, qbitCfg.UserName, qbitCfg.Password, qbitCfg.SkipTLSVerify));
                try
                {
                    var loginSuccess = await qbitClient.LoginAsync(ct);
                    if (!loginSuccess)
                        continue;

                    var torrents = await qbitClient.GetTorrentsAsync(arrCfg.Category, cancellationToken: ct);
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

            var pathTracker = scope.ServiceProvider.GetRequiredService<IImportPathTracker>();
            var completedRoot = _config.Settings.CompletedDownloadFolder;
            if (!string.IsNullOrWhiteSpace(completedRoot))
            {
                pathTracker.RemoveEmptyPathsUnder(completedRoot);
                pathTracker.ClearIfFolderEmpty(completedRoot);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Torrent processing failed for {Instance}", instanceName);
        }
    }

    private async Task RunRssSyncIfDueAsync(string instanceName, ArrInstanceConfig arrCfg, CancellationToken ct)
    {
        // qBitrr: RssSyncTimer <= 0 disables the command (no 15-minute fallback).
        if (!IsPeriodicCommandEnabled(arrCfg.RssSyncTimer))
            return;

        var interval = TimeSpan.FromMinutes(arrCfg.RssSyncTimer);
        var last = _lastRssSyncTime.GetValueOrDefault(instanceName, DateTime.MinValue);
        if (DateTime.UtcNow - last < interval) return;

        try
        {
            _logger.LogDebug("Triggering RSS sync for {Instance}", instanceName);
            switch (arrCfg.Type.ToLowerInvariant())
            {
                case "radarr": await new Torrentarr.Infrastructure.ApiClients.Arr.RadarrClient(arrCfg.URI, arrCfg.APIKey, arrCfg.SkipTLSVerify).RssSyncAsync(ct); break;
                case "sonarr": await new Torrentarr.Infrastructure.ApiClients.Arr.SonarrClient(arrCfg.URI, arrCfg.APIKey, arrCfg.SkipTLSVerify).RssSyncAsync(ct); break;
                case "lidarr": await new Torrentarr.Infrastructure.ApiClients.Arr.LidarrClient(arrCfg.URI, arrCfg.APIKey, arrCfg.SkipTLSVerify).RssSyncAsync(ct); break;
                case "readarr": await new Torrentarr.Infrastructure.ApiClients.Arr.ReadarrClient(arrCfg.URI, arrCfg.APIKey, arrCfg.SkipTLSVerify).RssSyncAsync(ct); break;
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
        // qBitrr: RefreshDownloadsTimer <= 0 disables; Lidarr does not support RefreshMonitoredDownloads.
        if (!IsPeriodicCommandEnabled(arrCfg.RefreshDownloadsTimer))
            return;
        if (string.Equals(arrCfg.Type, "lidarr", StringComparison.OrdinalIgnoreCase))
            return;

        var interval = TimeSpan.FromMinutes(arrCfg.RefreshDownloadsTimer);
        var last = _lastRefreshDownloadsTime.GetValueOrDefault(instanceName, DateTime.MinValue);
        if (DateTime.UtcNow - last < interval) return;

        try
        {
            _logger.LogDebug("Triggering RefreshMonitoredDownloads for {Instance}", instanceName);
            switch (arrCfg.Type.ToLowerInvariant())
            {
                case "radarr": await new Torrentarr.Infrastructure.ApiClients.Arr.RadarrClient(arrCfg.URI, arrCfg.APIKey, arrCfg.SkipTLSVerify).RefreshMonitoredDownloadsAsync(ct); break;
                case "sonarr": await new Torrentarr.Infrastructure.ApiClients.Arr.SonarrClient(arrCfg.URI, arrCfg.APIKey, arrCfg.SkipTLSVerify).RefreshMonitoredDownloadsAsync(ct); break;
                case "readarr": await new Torrentarr.Infrastructure.ApiClients.Arr.ReadarrClient(arrCfg.URI, arrCfg.APIKey, arrCfg.SkipTLSVerify).RefreshMonitoredDownloadsAsync(ct); break;
            }
            _lastRefreshDownloadsTime[instanceName] = DateTime.UtcNow;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "RefreshMonitoredDownloads failed for {Instance}", instanceName);
        }
    }

    /// <summary>qBitrr: timer values of 0 or less disable the periodic Arr command.</summary>
    internal static bool IsPeriodicCommandEnabled(int timerMinutes) => timerMinutes > 0;

    internal bool ShouldRunSearch(string instanceName, ArrInstanceConfig arrCfg)
    {
        if (!arrCfg.Search.SearchMissing)
            return false;

        var interval = TimeSpan.FromSeconds(arrCfg.Search.SearchRequestsEvery);
        var last = _lastSearchTime.GetValueOrDefault(instanceName, DateTime.MinValue);
        if (DateTime.UtcNow - last >= interval)
        {
            _lastSearchTime[instanceName] = DateTime.UtcNow;
            return true;
        }
        return false;
    }

    internal async Task<SearchResult?> RunSearchAsync(string instanceName, ArrInstanceConfig arrCfg, CancellationToken ct)
    {
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var mediaSvc = scope.ServiceProvider.GetRequiredService<IArrMediaService>();

            if (!arrCfg.Search.SearchMissing)
                return null;

            // §1.2: Restore quality profiles that have timed out before starting the search cycle.
            // qBitrr still restores on timeout when KeepTempProfile is true (KeepTemp only skips
            // immediate restore after a successful search).
            if (arrCfg.Search.UseTempForMissing && arrCfg.Search.TempProfileResetTimeoutMinutes > 0)
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

            if (arrCfg.Search.SearchAgainOnSearchCompletion &&
                _loopCompleted.TryGetValue(instanceName, out var prevCompleted) && prevCompleted)
            {
                await ResetSearchedFlagAsync(instanceName, arrCfg, ct);
                _yearCursor.Reset(instanceName);
                _loopCompleted[instanceName] = false;
            }

            SearchResult? result = null;
            var drainedFlags = new List<bool>();

            // §2.7: DoUpgradeSearch is exclusive — when active, skip missing-media search
            if (arrCfg.Search.DoUpgradeSearch)
            {
                result = await mediaSvc.SearchQualityUpgradesAsync(arrCfg.Category, ct);
                drainedFlags.Add(result.LoopCompleted);
            }
            else
            {
                if (arrCfg.Search.SearchMissing)
                {
                    result = await mediaSvc.SearchMissingMediaAsync(arrCfg.Category, ct);
                    drainedFlags.Add(result.LoopCompleted);
                }

                // QualityUnmetSearch / CustomFormatUnmetSearch are always additive (not exclusive)
                if (arrCfg.Search.QualityUnmetSearch || arrCfg.Search.CustomFormatUnmetSearch)
                {
                    var upgradeResult = await mediaSvc.SearchQualityUpgradesAsync(arrCfg.Category, ct);
                    drainedFlags.Add(upgradeResult.LoopCompleted);
                    if (result == null)
                        result = upgradeResult;
                    else
                    {
                        result.SearchesTriggered += upgradeResult.SearchesTriggered;
                        result.ItemsSearched += upgradeResult.ItemsSearched;
                    }
                }
            }

            var loopCompleted = drainedFlags.Count > 0 && drainedFlags.TrueForAll(f => f);
            if (loopCompleted
                && SearchYearCursor.ShouldFilter(arrCfg)
                && _yearCursor.Advance(instanceName))
            {
                loopCompleted = false;
            }
            _loopCompleted[instanceName] = loopCompleted;
            if (result != null)
                result.LoopCompleted = loopCompleted;

            return result;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Search failed for {Instance}: {Message}", instanceName, ex.Message);
            return null;
        }
    }

    internal async Task ResetSearchedFlagAsync(string instanceName, ArrInstanceConfig arrCfg, CancellationToken ct)
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
                        .ExecuteUpdateAsync(s => s
                            .SetProperty(m => m.Searched, false)
                            .SetProperty(m => m.Upgrade, false), ct);
                    await PruneOrphansAsync(
                        instanceName, "movies",
                        async () =>
                        {
                            var client = new RadarrClient(arrCfg.URI, arrCfg.APIKey, arrCfg.SkipTLSVerify);
                            var movies = await client.GetMoviesAsync(ct);
                            return movies.Select(m => m.Id).ToList();
                        },
                        async ids =>
                        {
                            await db.Movies.Where(m => m.ArrInstance == instanceName && !ids.Contains(m.ArrId))
                                .ExecuteDeleteAsync(ct);
                        },
                        ct);
                    break;
                case "sonarr":
                    await db.Episodes
                        .Where(e => e.ArrInstance == instanceName && e.Searched)
                        .ExecuteUpdateAsync(s => s
                            .SetProperty(e => e.Searched, false)
                            .SetProperty(e => e.Upgrade, false), ct);
                    await db.Series
                        .Where(s => s.ArrInstance == instanceName && s.Searched)
                        .ExecuteUpdateAsync(s => s
                            .SetProperty(s => s.Searched, false)
                            .SetProperty(s => s.Upgrade, false), ct);
                    await PruneSonarrOrphansAsync(instanceName, arrCfg, db, ct);
                    break;
                case "lidarr":
                    await db.Albums
                        .Where(a => a.ArrInstance == instanceName && a.Searched)
                        .ExecuteUpdateAsync(s => s
                            .SetProperty(a => a.Searched, false)
                            .SetProperty(a => a.Upgrade, false), ct);
                    await PruneOrphansAsync(
                        instanceName, "albums",
                        async () =>
                        {
                            var client = new LidarrClient(arrCfg.URI, arrCfg.APIKey, arrCfg.SkipTLSVerify);
                            var albums = await client.GetAlbumsAsync(ct: ct);
                            return albums.Select(a => a.Id).ToList();
                        },
                        async ids =>
                        {
                            await db.Albums.Where(a => a.ArrInstance == instanceName && !ids.Contains(a.ArrId))
                                .ExecuteDeleteAsync(ct);
                        },
                        ct);
                    break;
                case "readarr":
                    await db.Books
                        .Where(b => b.ArrInstance == instanceName && b.Searched)
                        .ExecuteUpdateAsync(s => s
                            .SetProperty(b => b.Searched, false)
                            .SetProperty(b => b.Upgrade, false), ct);
                    await PruneOrphansAsync(
                        instanceName, "books",
                        async () =>
                        {
                            var client = new ReadarrClient(arrCfg.URI, arrCfg.APIKey, arrCfg.SkipTLSVerify);
                            var books = await client.GetBooksAsync(ct: ct);
                            return books.Select(b => b.Id).ToList();
                        },
                        async ids =>
                        {
                            await db.Books.Where(b => b.ArrInstance == instanceName && !ids.Contains(b.ArrId))
                                .ExecuteDeleteAsync(ct);
                        },
                        ct);
                    break;
            }

            _logger.LogTrace("SearchAgainOnSearchCompletion: reset Searched==true rows for {Instance}", instanceName);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to reset Searched flag for {Instance}", instanceName);
        }
    }

    private async Task PruneOrphansAsync(
        string instanceName,
        string entityLabel,
        Func<Task<List<int>>> collectIds,
        Func<List<int>, Task> deleteMissing,
        CancellationToken ct)
    {
        try
        {
            var ids = await collectIds();
            if (ids.Count == 0)
            {
                _logger.LogWarning(
                    "{Instance}: No {Entity} returned from Arr API during reset; skipping DB prune to prevent data loss",
                    instanceName, entityLabel);
                return;
            }

            await deleteMissing(ids);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "{Instance}: failed to prune orphan {Entity} during SearchAgain reset", instanceName, entityLabel);
        }
    }

    private async Task PruneSonarrOrphansAsync(
        string instanceName,
        ArrInstanceConfig arrCfg,
        TorrentarrDbContext db,
        CancellationToken ct)
    {
        try
        {
            var client = new SonarrClient(arrCfg.URI, arrCfg.APIKey, arrCfg.SkipTLSVerify);
            var series = await client.GetSeriesAsync(ct);
            var seriesIds = series.Select(s => s.Id).ToList();
            if (seriesIds.Count == 0)
            {
                _logger.LogWarning(
                    "{Instance}: No series returned from Arr API during reset; skipping DB prune to prevent data loss",
                    instanceName);
                return;
            }

            await db.Series.Where(s => s.ArrInstance == instanceName && !seriesIds.Contains(s.ArrId))
                .ExecuteDeleteAsync(ct);

            var (skipEpisodePrune, episodeIds) = await ArrSeriesEpisodeFetch.CollectEpisodeIdsAsync(
                seriesIds,
                async (sid, token) =>
                {
                    var episodes = await client.GetEpisodesAsync(sid, token);
                    return (IReadOnlyList<int>)episodes.Select(e => e.Id).ToList();
                },
                _logger,
                instanceName,
                ct);

            if (skipEpisodePrune)
            {
                _logger.LogWarning(
                    "{Instance}: skipped episode prune because one or more series episode fetches failed",
                    instanceName);
                return;
            }

            if (episodeIds.Count == 0)
            {
                _logger.LogWarning(
                    "{Instance}: No episodes returned from Arr API during reset; skipping episode prune to prevent data loss",
                    instanceName);
                return;
            }

            await db.Episodes.Where(e => e.ArrInstance == instanceName && !episodeIds.Contains(e.ArrId))
                .ExecuteDeleteAsync(ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "{Instance}: failed to prune orphan Sonarr rows during SearchAgain reset", instanceName);
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

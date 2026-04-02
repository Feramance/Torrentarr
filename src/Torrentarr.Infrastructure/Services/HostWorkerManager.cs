using System.Collections.Concurrent;
using System.Linq;
using Torrentarr.Core.Configuration;
using Torrentarr.Core.Services;
using Torrentarr.Infrastructure.ApiClients.QBittorrent;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Serilog.Context;

namespace Torrentarr.Infrastructure.Services;

/// <summary>
/// Manages fire-and-forget Task.Run loops for Host-only work (Failed/Recheck categories, free space, tracker sort).
/// Modeled after <see cref="ArrWorkerManager"/> — monitors tasks and restarts faulted workers.
/// </summary>
public class HostWorkerManager : BackgroundService
{
    public const string FailedWorkerName = "Failed";
    public const string RecheckWorkerName = "Recheck";
    public const string FreeSpaceWorkerName = "FreeSpaceManager";
    public const string TrackerSortWorkerName = "TrackerSortManager";

    /// <summary>Names used in <see cref="ProcessStateManager"/> and restart API paths.</summary>
    public static readonly string[] AllHostWorkerNames =
    {
        FailedWorkerName, RecheckWorkerName, FreeSpaceWorkerName, TrackerSortWorkerName
    };

    private readonly ILogger<HostWorkerManager> _logger;
    private readonly TorrentarrConfig _config;
    private readonly QBittorrentConnectionManager _qbitManager;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ProcessStateManager _stateManager;

    private readonly ConcurrentDictionary<string, (Task Task, CancellationTokenSource Cts)> _workers =
        new(StringComparer.OrdinalIgnoreCase);

    private readonly ConcurrentDictionary<string, List<DateTime>> _restartTimestamps = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _restartLock = new();

    private CancellationToken _appStopping;
    private bool _qbitConfigured;
    private long _lastTrackerSortTicksUtc = DateTime.MinValue.Ticks;

    public HostWorkerManager(
        ILogger<HostWorkerManager> logger,
        TorrentarrConfig config,
        QBittorrentConnectionManager qbitManager,
        IServiceScopeFactory scopeFactory,
        ProcessStateManager stateManager)
    {
        _logger = logger;
        _config = config;
        _qbitManager = qbitManager;
        _scopeFactory = scopeFactory;
        _stateManager = stateManager;

        _qbitConfigured = config.QBitInstances.Values.Any(q =>
            !q.Disabled && q.Host != "CHANGE_ME" && q.UserName != "CHANGE_ME" && q.Password != "CHANGE_ME");
    }

    private static long ParseFreeSpaceString(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Trim() == "-1") return -1;
        var v = value.Trim().ToUpperInvariant();
        try
        {
            if (v.EndsWith("G")) return long.Parse(v[..^1]) * 1024L * 1024L * 1024L;
            if (v.EndsWith("M")) return long.Parse(v[..^1]) * 1024L * 1024L;
            if (v.EndsWith("K")) return long.Parse(v[..^1]) * 1024L;
            return long.Parse(v);
        }
        catch { return -1; }
    }

    private static bool GlobalSortTorrentsEnabled(TorrentarrConfig config) =>
        config.QBitInstances.Values.Any(q => q.Trackers.Any(t => t.SortTorrents))
        || config.ArrInstances.Values.Any(a => a.Torrent.Trackers.Any(t => t.SortTorrents));

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _appStopping = stoppingToken;
        _logger.LogInformation("Host worker manager starting");

        if (!_qbitConfigured)
        {
            _logger.LogWarning("No qBittorrent instances configured — Host subprocess workers not started");
            try { await Task.Delay(Timeout.Infinite, stoppingToken); }
            catch (OperationCanceledException) { }
            return;
        }

        var anyConnected = false;
        foreach (var (name, qbit) in _config.QBitInstances)
        {
            if (!qbit.Disabled && qbit.Host != "CHANGE_ME")
            {
                var ok = await _qbitManager.InitializeAsync(name, qbit, stoppingToken);
                if (ok) anyConnected = true;
            }
        }

        if (!anyConnected)
        {
            _logger.LogWarning("Failed to connect to any qBittorrent instance — Host subprocess workers not started");
            try { await Task.Delay(Timeout.Infinite, stoppingToken); }
            catch (OperationCanceledException) { }
            return;
        }

        InitializeProcessStates();

        StartHostWorker(FailedWorkerName, RunFailedLoopAsync, stoppingToken);
        StartHostWorker(RecheckWorkerName, RunRecheckLoopAsync, stoppingToken);

        var freeSpaceBytes = ParseFreeSpaceString(_config.Settings.FreeSpace);
        if (_config.Settings.AutoPauseResume && freeSpaceBytes > 0)
            StartHostWorker(FreeSpaceWorkerName, RunFreeSpaceLoopAsync, stoppingToken);

        if (GlobalSortTorrentsEnabled(_config))
            StartHostWorker(TrackerSortWorkerName, RunTrackerSortLoopAsync, stoppingToken);

        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken);
                foreach (var name in _workers.Keys.ToList())
                {
                    if (!_workers.TryGetValue(name, out var pair))
                        continue;
                    var (task, _) = pair;
                    if (stoppingToken.IsCancellationRequested)
                        break;
                    if (task.IsFaulted)
                    {
                        _logger.LogWarning(task.Exception?.GetBaseException(),
                            "Host worker {Name} faulted — restarting", name);
                        await RestartHostWorkerFromWatchAsync(name, stoppingToken);
                    }
                    else if (task.IsCompletedSuccessfully)
                    {
                        _logger.LogWarning("Host worker {Name} exited unexpectedly — restarting", name);
                        await RestartHostWorkerFromWatchAsync(name, stoppingToken);
                    }
                }
            }
        }
        catch (OperationCanceledException) { }
        finally
        {
            await StopAllHostWorkersAsync();
        }
    }

    private void InitializeProcessStates()
    {
        _stateManager.Initialize(FailedWorkerName, new ArrProcessState
        {
            Name = FailedWorkerName,
            Category = FailedWorkerName,
            Kind = "category",
            Alive = false,
            CategoryCount = null
        });
        _stateManager.Initialize(RecheckWorkerName, new ArrProcessState
        {
            Name = RecheckWorkerName,
            Category = RecheckWorkerName,
            Kind = "category",
            Alive = false,
            CategoryCount = null
        });
        _stateManager.Initialize(FreeSpaceWorkerName, new ArrProcessState
        {
            Name = FreeSpaceWorkerName,
            Category = FreeSpaceWorkerName,
            Kind = "torrent",
            MetricType = "free-space",
            Alive = false,
            CategoryCount = null
        });
        _stateManager.Initialize(TrackerSortWorkerName, new ArrProcessState
        {
            Name = TrackerSortWorkerName,
            Category = TrackerSortWorkerName,
            Kind = "torrent",
            MetricType = "tracker-sort",
            Alive = false,
            CategoryCount = null
        });
    }

    /// <summary>Restart a Host subprocess worker (Failed, Recheck, FreeSpaceManager, TrackerSortManager).</summary>
    public async Task<bool> RestartWorkerAsync(string workerName)
    {
        if (!AllHostWorkerNames.Contains(workerName, StringComparer.OrdinalIgnoreCase))
            return false;

        var settings = _config.Settings;
        if (!settings.AutoRestartProcesses)
        {
            _logger.LogWarning("Host worker restart skipped for {Name}: AutoRestartProcesses is disabled", workerName);
            return false;
        }

        var windowSeconds = settings.ProcessRestartWindow;
        var maxRestarts = settings.MaxProcessRestarts;
        var delaySeconds = settings.ProcessRestartDelay;

        lock (_restartLock)
        {
            var list = _restartTimestamps.GetOrAdd(workerName, _ => new List<DateTime>());
            var cutoff = DateTime.UtcNow.AddSeconds(-windowSeconds);
            list.RemoveAll(d => d < cutoff);
            if (list.Count >= maxRestarts)
            {
                _logger.LogWarning(
                    "Host worker restart skipped for {Name}: {Count} restarts in last {Window}s (max {Max})",
                    workerName, list.Count, windowSeconds, maxRestarts);
                return false;
            }
            list.Add(DateTime.UtcNow);
        }

        if (delaySeconds > 0)
            await Task.Delay(TimeSpan.FromSeconds(delaySeconds));

        _logger.LogInformation("Restarting host worker {Name}", workerName);
        await RestartHostWorkerFromWatchAsync(workerName, _appStopping);
        return true;
    }

    /// <summary>Restart every running Host subprocess worker.</summary>
    public async Task<string[]> RestartAllWorkersAsync()
    {
        var workerNames = _workers.Keys.ToList();
        var restartedWorkers = new List<string>(workerNames.Count);
        foreach (var name in workerNames)
        {
            await RestartHostWorkerFromWatchAsync(name, _appStopping);
            if (_workers.ContainsKey(name))
                restartedWorkers.Add(name);
        }
        return restartedWorkers.ToArray();
    }

    private async Task RestartHostWorkerFromWatchAsync(string name, CancellationToken appStopping)
    {
        if (_workers.TryRemove(name, out var old))
        {
            try { old.Cts.Cancel(); } catch { /* ignore */ }
            try { await old.Task.WaitAsync(TimeSpan.FromSeconds(10)); }
            catch (OperationCanceledException) { }
            catch (TimeoutException) { _logger.LogWarning("Host worker {Name} did not stop within 10s", name); }
            catch (Exception ex) { _logger.LogError(ex, "Host worker {Name} faulted during shutdown", name); }
            try { old.Cts.Dispose(); } catch { /* ignore */ }
        }

        if (appStopping.IsCancellationRequested)
            return;

        switch (name)
        {
            case FailedWorkerName:
                StartHostWorker(FailedWorkerName, RunFailedLoopAsync, appStopping);
                break;
            case RecheckWorkerName:
                StartHostWorker(RecheckWorkerName, RunRecheckLoopAsync, appStopping);
                break;
            case FreeSpaceWorkerName:
                if (_config.Settings.AutoPauseResume && ParseFreeSpaceString(_config.Settings.FreeSpace) > 0)
                    StartHostWorker(FreeSpaceWorkerName, RunFreeSpaceLoopAsync, appStopping);
                break;
            case TrackerSortWorkerName:
                if (GlobalSortTorrentsEnabled(_config))
                    StartHostWorker(TrackerSortWorkerName, RunTrackerSortLoopAsync, appStopping);
                break;
        }
    }

    private void StartHostWorker(string name, Func<CancellationToken, Task> loop, CancellationToken appStopping)
    {
        var cts = CancellationTokenSource.CreateLinkedTokenSource(appStopping);
        var task = Task.Run(async () =>
        {
            using (LogContext.PushProperty("ProcessInstance", name))
            using (LogContext.PushProperty("ProcessType", "HostSubprocess"))
                await loop(cts.Token);
        }, CancellationToken.None);
        _workers[name] = (task, cts);
    }

    private async Task StopAllHostWorkersAsync()
    {
        var snapshot = _workers.ToArray();
        foreach (var (_, (_, cts)) in snapshot)
        {
            try { cts.Cancel(); } catch { /* ignore */ }
        }
        foreach (var (name, (task, cts)) in snapshot)
        {
            try { await task.WaitAsync(TimeSpan.FromSeconds(10)); }
            catch (OperationCanceledException) { }
            catch (TimeoutException) { _logger.LogWarning("Host worker {Name} did not stop within 10s during shutdown", name); }
            catch (Exception ex) { _logger.LogError(ex, "Host worker {Name} faulted during shutdown", name); }
            try { cts.Dispose(); } catch { /* ignore */ }
        }
        _workers.Clear();
    }

    private Task RunFailedLoopAsync(CancellationToken ct) =>
        RunCategoryLoopAsync(
            FailedWorkerName,
            _config.Settings.FailedCategory,
            "[{Instance}] Deleting failed torrent: {Name}",
            LogLevel.Warning,
            "[{Instance}] Error processing failed category",
            (client, hash, token) => client.DeleteTorrentsAsync(new List<string> { hash }, deleteFiles: true, token),
            ct);

    private Task RunRecheckLoopAsync(CancellationToken ct) =>
        RunCategoryLoopAsync(
            RecheckWorkerName,
            _config.Settings.RecheckCategory,
            "[{Instance}] Re-checking torrent: {Name}",
            LogLevel.Information,
            "[{Instance}] Error processing recheck category",
            (client, hash, token) => client.RecheckTorrentsAsync(new List<string> { hash }, token),
            ct);

    private async Task RunCategoryLoopAsync(
        string workerName,
        string category,
        string actionLogTemplate,
        LogLevel actionLogLevel,
        string categoryErrorLogTemplate,
        Func<QBittorrentClient, string, CancellationToken, Task> processTorrentAsync,
        CancellationToken ct)
    {
        _stateManager.Update(workerName, s => { s.Alive = true; s.Rebuilding = false; });
        var consecutiveErrors = 0;
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var loopStart = DateTime.UtcNow;
                try
                {
                    if (_qbitManager.GetAllClients().Count == 0)
                    {
                        await Task.Delay(TimeSpan.FromSeconds(_config.Settings.LoopSleepTimer), ct);
                        continue;
                    }

                    var totalCount = 0;
                    foreach (var (instanceName, client) in _qbitManager.GetAllClients())
                    {
                        try
                        {
                            var categoryTorrents = await client.GetTorrentsAsync(category, ct);
                            totalCount += categoryTorrents.Count;
                            foreach (var torrent in categoryTorrents)
                            {
                                if (torrent.AddedOn > 0)
                                {
                                    var addedAt = DateTimeOffset.FromUnixTimeSeconds(torrent.AddedOn).UtcDateTime;
                                    if ((DateTime.UtcNow - addedAt).TotalSeconds < _config.Settings.IgnoreTorrentsYoungerThan)
                                        continue;
                                }
                                if (actionLogLevel == LogLevel.Warning)
                                    _logger.LogWarning(actionLogTemplate, instanceName, torrent.Name);
                                else
                                    _logger.LogInformation(actionLogTemplate, instanceName, torrent.Name);
                                await processTorrentAsync(client, torrent.Hash, ct);
                            }
                        }
                        catch (Exception ex)
                        {
                            _logger.LogError(ex, categoryErrorLogTemplate, instanceName);
                        }
                    }
                    _stateManager.Update(workerName, s => { s.CategoryCount = totalCount; s.Alive = true; });
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
                    _logger.LogError(ex, "Host worker {Name} loop error #{Count} — backing off {Minutes:F1} min",
                        workerName, consecutiveErrors, backoffMinutes);
                    try { await Task.Delay(TimeSpan.FromMinutes(backoffMinutes), ct); }
                    catch (OperationCanceledException) { break; }
                    continue;
                }

                var elapsed = (int)(DateTime.UtcNow - loopStart).TotalMilliseconds;
                var sleepMs = Math.Max(0, _config.Settings.LoopSleepTimer * 1000 - elapsed);
                if (sleepMs > 0)
                {
                    try { await Task.Delay(sleepMs, ct); }
                    catch (OperationCanceledException) { break; }
                }
            }
        }
        finally
        {
            _stateManager.Update(workerName, s => { s.Alive = false; s.Rebuilding = false; });
        }
    }

    private async Task RunFreeSpaceLoopAsync(CancellationToken ct)
    {
        _stateManager.Update(FreeSpaceWorkerName, s => { s.Alive = true; s.Rebuilding = false; });
        var consecutiveErrors = 0;
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var loopStart = DateTime.UtcNow;
                try
                {
                    if (_qbitManager.GetAllClients().Count == 0)
                    {
                        await Task.Delay(TimeSpan.FromSeconds(_config.Settings.LoopSleepTimer), ct);
                        continue;
                    }

                    using var scope = _scopeFactory.CreateScope();
                    var freeSpace = scope.ServiceProvider.GetRequiredService<IFreeSpaceService>();
                    var result = await freeSpace.ProcessGlobalManagedCategoriesHostPassAsync(ct);
                    _stateManager.Update(FreeSpaceWorkerName, s =>
                    {
                        s.CategoryCount = result.PausedTorrentCount;
                        s.MetricType = "free-space";
                        s.Alive = result.ManagerAlive;
                    });
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
                    _logger.LogError(ex, "Host worker {Name} loop error #{Count} — backing off {Minutes:F1} min",
                        FreeSpaceWorkerName, consecutiveErrors, backoffMinutes);
                    try { await Task.Delay(TimeSpan.FromMinutes(backoffMinutes), ct); }
                    catch (OperationCanceledException) { break; }
                    continue;
                }

                var elapsed = (int)(DateTime.UtcNow - loopStart).TotalMilliseconds;
                var sleepMs = Math.Max(0, _config.Settings.LoopSleepTimer * 1000 - elapsed);
                if (sleepMs > 0)
                {
                    try { await Task.Delay(sleepMs, ct); }
                    catch (OperationCanceledException) { break; }
                }
            }
        }
        finally
        {
            _stateManager.Update(FreeSpaceWorkerName, s => { s.Alive = false; s.Rebuilding = false; });
        }
    }

    private async Task RunTrackerSortLoopAsync(CancellationToken ct)
    {
        _stateManager.Update(TrackerSortWorkerName, s => { s.Alive = true; s.Rebuilding = false; });
        var consecutiveErrors = 0;
        var minimumSortInterval = TimeSpan.FromSeconds(Math.Max(1, _config.Settings.LoopSleepTimer));
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var loopStart = DateTime.UtcNow;
                try
                {
                    if (_qbitManager.GetAllClients().Count == 0)
                    {
                        await Task.Delay(TimeSpan.FromSeconds(_config.Settings.LoopSleepTimer), ct);
                        continue;
                    }

                    if (!GlobalSortTorrentsEnabled(_config))
                    {
                        await Task.Delay(TimeSpan.FromSeconds(_config.Settings.LoopSleepTimer), ct);
                        continue;
                    }

                    var lastTicks = System.Threading.Interlocked.Read(ref _lastTrackerSortTicksUtc);
                    if (DateTime.UtcNow - new DateTime(lastTicks, DateTimeKind.Utc) < minimumSortInterval)
                    {
                        var waitMs = Math.Max(0, (int)(minimumSortInterval - (DateTime.UtcNow - new DateTime(lastTicks, DateTimeKind.Utc))).TotalMilliseconds);
                        if (waitMs > 0)
                        {
                            try { await Task.Delay(waitMs, ct); }
                            catch (OperationCanceledException) { break; }
                        }
                        continue;
                    }

                    using var scope = _scopeFactory.CreateScope();
                    var sorter = scope.ServiceProvider.GetRequiredService<ITrackerQueueSortService>();
                    await sorter.SortTorrentQueuesByTrackerPriorityAsync(ct);
                    System.Threading.Interlocked.Exchange(ref _lastTrackerSortTicksUtc, DateTime.UtcNow.Ticks);
                    _stateManager.Update(TrackerSortWorkerName, s => { s.Alive = true; s.MetricType = "tracker-sort"; });
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
                    _logger.LogError(ex, "Host worker {Name} loop error #{Count} — backing off {Minutes:F1} min",
                        TrackerSortWorkerName, consecutiveErrors, backoffMinutes);
                    try { await Task.Delay(TimeSpan.FromMinutes(backoffMinutes), ct); }
                    catch (OperationCanceledException) { break; }
                    continue;
                }

                var elapsed = (int)(DateTime.UtcNow - loopStart).TotalMilliseconds;
                var sleepMs = Math.Max(0, _config.Settings.LoopSleepTimer * 1000 - elapsed);
                if (sleepMs > 0)
                {
                    try { await Task.Delay(sleepMs, ct); }
                    catch (OperationCanceledException) { break; }
                }
            }
        }
        finally
        {
            _stateManager.Update(TrackerSortWorkerName, s => { s.Alive = false; s.Rebuilding = false; });
        }
    }
}

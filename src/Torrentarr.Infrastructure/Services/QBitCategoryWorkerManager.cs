using System.Collections.Concurrent;
using Torrentarr.Core.Configuration;
using Torrentarr.Core.Services;
using Torrentarr.Infrastructure.ApiClients.QBittorrent;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Torrentarr.Infrastructure.Services;

/// <summary>
/// Processes torrents in qBit <c>ManagedCategories</c> not owned by an Arr instance (qBitrr PlaceHolderArr parity).
/// </summary>
public class QBitCategoryWorkerManager : BackgroundService
{
    private readonly ILogger<QBitCategoryWorkerManager> _logger;
    private readonly TorrentarrConfig _config;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ProcessStateManager _stateManager;
    private readonly IConnectivityService _connectivityService;
    private readonly IConfigReloader? _reloader;

    private readonly ConcurrentDictionary<string, (Task Task, CancellationTokenSource Cts)> _workers =
        new(StringComparer.OrdinalIgnoreCase);

    private CancellationToken _appStopping = CancellationToken.None;

    public QBitCategoryWorkerManager(
        ILogger<QBitCategoryWorkerManager> logger,
        TorrentarrConfig config,
        IServiceScopeFactory scopeFactory,
        ProcessStateManager stateManager,
        IConnectivityService connectivityService, IConfigReloader? reloader = null)
    {
        _logger = logger;
        _config = config;
        _reloader = reloader;
        _scopeFactory = scopeFactory;
        _stateManager = stateManager;
        _connectivityService = connectivityService;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _appStopping = stoppingToken;

        foreach (var category in CategoryOwnershipHelper.GetQBitOnlyManagedCategories(_config))
        {
            var stateName = $"qbit-{category}";
            _stateManager.Initialize(stateName, new ArrProcessState
            {
                Name = stateName,
                Category = category,
                Kind = "category",
                MetricType = "category",
                Alive = false
            });
            StartCategoryWorker(category, stoppingToken);
        }

        if (_workers.IsEmpty)
        {
            _logger.LogDebug("No qBit-only managed categories to process");
            try { await Task.Delay(Timeout.Infinite, stoppingToken); }
            catch (OperationCanceledException) { }
            return;
        }

        _logger.LogInformation("Started {Count} qBit-only category worker(s)", _workers.Count);
        try { await Task.Delay(Timeout.Infinite, stoppingToken); }
        catch (OperationCanceledException) { }

        await StopAllCategoriesAsync();
    }

    public async Task RestartCategoryAsync(string category)
    {
        if (!_workers.ContainsKey(category))
        {
            StartCategoryWorker(category, _appStopping);
            return;
        }

        _logger.LogInformation("Restarting qBit category worker for {Category}", category);
        var stateName = $"qbit-{category}";
        _stateManager.Update(stateName, s => { s.Alive = false; s.Rebuilding = true; });

        if (_workers.TryRemove(category, out var old))
        {
            old.Cts.Cancel();
            try { await old.Task.WaitAsync(TimeSpan.FromSeconds(10)); }
            catch (OperationCanceledException) { }
            catch (TimeoutException)
            {
                _logger.LogWarning("qBit category worker {Category} did not stop within 10s", category);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "qBit category worker {Category} faulted during shutdown", category);
            }
            old.Cts.Dispose();
        }

        StartCategoryWorker(category, _appStopping);
    }

    public async Task RestartAllCategoriesAsync()
    {
        foreach (var category in _workers.Keys.ToList())
            await RestartCategoryAsync(category);

        foreach (var category in CategoryOwnershipHelper.GetQBitOnlyManagedCategories(_config))
        {
            if (!_workers.ContainsKey(category))
                StartCategoryWorker(category, _appStopping);
        }
    }

    public async Task SyncWorkersWithConfigAsync()
    {
        foreach (var (category, worker) in _workers.ToArray())
            if (worker.Task.IsCompleted && _workers.TryRemove(new KeyValuePair<string, (Task Task, CancellationTokenSource Cts)>(category, worker)))
                worker.Cts.Dispose();
        var desired = CategoryOwnershipHelper.GetQBitOnlyManagedCategories(_config).ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var category in desired)
        {
            var stateName = $"qbit-{category}";
            if (_stateManager.GetState(stateName) == null)
            {
                _stateManager.Initialize(stateName, new ArrProcessState
                {
                    Name = stateName,
                    Category = category,
                    Kind = "category",
                    MetricType = "category",
                    Alive = false
                });
            }

            if (!_workers.ContainsKey(category))
                StartCategoryWorker(category, _appStopping);
        }

        foreach (var category in _workers.Keys.ToList())
        {
            if (!desired.Contains(category))
                await StopCategoryAsync(category);
        }
    }

    private void StartCategoryWorker(string category, CancellationToken appStopping)
    {
        lock (_workers)
        {
            if (_workers.ContainsKey(category)) return;
            var cts = CancellationTokenSource.CreateLinkedTokenSource(appStopping);
            Task task;
            using (ExecutionContext.SuppressFlow())
                task = Task.Run(() => RunCategoryLoopAsync(category, $"qbit-{category}", cts.Token), CancellationToken.None);
            _workers[category] = (task, cts);
        }
    }

    private async Task StopCategoryAsync(string category, bool strict = false)
    {
        if (!_workers.TryGetValue(category, out var worker))
            return;

        worker.Cts.Cancel();
        try { await worker.Task.WaitAsync(TimeSpan.FromSeconds(10)); }
        catch (OperationCanceledException) { }
        catch (TimeoutException) { if (strict) throw; }
        catch (Exception ex) { _logger.LogDebug(ex, "qBit category worker {Category} stop error", category); }
        _workers.TryRemove(category, out _);
        worker.Cts.Dispose();

        var stateName = $"qbit-{category}";
        _stateManager.Update(stateName, s => s.Alive = false);
    }

    public async Task SuspendAsync()
    {
        try
        {
            foreach (var category in _workers.Keys.ToList()) await StopCategoryAsync(category, strict: true);
        }
        catch
        {
            foreach (var (category, worker) in _workers.ToArray())
                if (worker.Cts.IsCancellationRequested)
                    using (ExecutionContext.SuppressFlow())
                        _ = Task.Run(async () =>
                        {
                            try { await worker.Task; }
                            catch (Exception ex) { _logger.LogDebug(ex, "Category {Category} exited after failed suspension", category); }
                            if (_workers.TryRemove(new KeyValuePair<string, (Task Task, CancellationTokenSource Cts)>(category, worker)))
                                worker.Cts.Dispose();
                            if (!_appStopping.IsCancellationRequested)
                                await SyncWorkersWithConfigAsync();
                        });
            await SyncWorkersWithConfigAsync();
            throw;
        }
    }

    private async Task StopAllCategoriesAsync()
    {
        foreach (var category in _workers.Keys.ToList())
            await StopCategoryAsync(category);
    }

    private Task WaitAsync(TimeSpan delay, CancellationToken ct) => _reloader?.WaitForChangeAsync(_config.Revision, delay, ct) ?? Task.Delay(delay, ct);

    private async Task RunCategoryLoopAsync(string category, string stateName, CancellationToken ct)
    {
        _stateManager.Update(stateName, s => { s.Alive = true; s.Rebuilding = false; });

        try
        {
            long initializedRevision = -1;
            while (!ct.IsCancellationRequested)
            {
                using var configuration = _config.BeginOperation();
                var loopStart = DateTime.UtcNow;
                try
                {
                    if (!await _connectivityService.IsConnectedAsync(ct))
                    {
                        _stateManager.Update(stateName, s => s.Status = "Waiting for connectivity...");
                        await WaitAsync(TimeSpan.FromSeconds(_config.Settings.NoInternetSleepTimer), ct);
                        continue;
                    }

                    using var processing = ConfigMutationGuard.EnterProcessing();
                    if (initializedRevision != _config.Revision)
                    {
                        using var initScope = _scopeFactory.CreateScope();
                        var ensure = initScope.ServiceProvider.GetRequiredService<QBitCategoryEnsureService>();
                        await ensure.EnsureCategoryOnAllInstancesAsync(category, ct);
                        await EnsureTrackerTagsAsync(initScope, ct);
                        initializedRevision = _config.Revision;
                    }
                    _stateManager.Update(stateName, s => s.Status = "Processing torrents...");
                    using var scope = _scopeFactory.CreateScope();
                    var processor = scope.ServiceProvider.GetRequiredService<ITorrentProcessor>();
                    var seeding = scope.ServiceProvider.GetRequiredService<ISeedingService>();
                    var pathTracker = scope.ServiceProvider.GetRequiredService<IImportPathTracker>();

                    await processor.ProcessTorrentsAsync(category, ct);
                    await seeding.RemoveCompletedTorrentsAsync(category, ct);

                    ClientDownloadPaths.Clean(_config, pathTracker);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    break;
                }
                catch (ConfigurationBusyException) { }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error in qBit category worker for {Category}", category);
                }

                var elapsed = DateTime.UtcNow - loopStart;
                var sleep = TimeSpan.FromSeconds(_config.Settings.LoopSleepTimer) - elapsed;
                if (sleep > TimeSpan.Zero)
                    await WaitAsync(sleep, ct);
            }
        }
        finally
        {
            _stateManager.Update(stateName, s => s.Alive = false);
        }
    }

    private async Task EnsureTrackerTagsAsync(IServiceScope scope, CancellationToken ct)
    {
        var seeding = scope.ServiceProvider.GetRequiredService<ISeedingService>();
        if (seeding is SeedingService concrete)
            await concrete.EnsureAllTrackerTagsExistAsync(ct);
    }
}

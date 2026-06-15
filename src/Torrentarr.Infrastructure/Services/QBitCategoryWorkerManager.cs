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
    private readonly QBittorrentConnectionManager _qbitManager;

    private readonly Dictionary<string, CancellationTokenSource> _workerCts = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<Task> _workerTasks = new();

    public QBitCategoryWorkerManager(
        ILogger<QBitCategoryWorkerManager> logger,
        TorrentarrConfig config,
        IServiceScopeFactory scopeFactory,
        ProcessStateManager stateManager,
        IConnectivityService connectivityService,
        QBittorrentConnectionManager qbitManager)
    {
        _logger = logger;
        _config = config;
        _scopeFactory = scopeFactory;
        _stateManager = stateManager;
        _connectivityService = connectivityService;
        _qbitManager = qbitManager;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
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

            var cts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
            _workerCts[category] = cts;
            _workerTasks.Add(RunCategoryLoopAsync(category, stateName, cts.Token));
        }

        if (_workerTasks.Count == 0)
        {
            _logger.LogDebug("No qBit-only managed categories to process");
            try { await Task.Delay(Timeout.Infinite, stoppingToken); }
            catch (OperationCanceledException) { }
            return;
        }

        _logger.LogInformation("Started {Count} qBit-only category worker(s)", _workerTasks.Count);
        try { await Task.WhenAll(_workerTasks); }
        catch (OperationCanceledException) { }
    }

    public async Task RestartCategoryAsync(string category)
    {
        if (!_workerCts.TryGetValue(category, out var existing))
            return;

        existing.Cancel();
        try { await Task.WhenAny(_workerTasks); } catch { /* ignore */ }

        var cts = CancellationTokenSource.CreateLinkedTokenSource(CancellationToken.None);
        _workerCts[category] = cts;
        _ = RunCategoryLoopAsync(category, $"qbit-{category}", cts.Token);
    }

    private async Task RunCategoryLoopAsync(string category, string stateName, CancellationToken ct)
    {
        _stateManager.Update(stateName, s => { s.Alive = true; s.Rebuilding = false; });

        try
        {
            using var initScope = _scopeFactory.CreateScope();
            var ensure = initScope.ServiceProvider.GetRequiredService<QBitCategoryEnsureService>();
            await ensure.EnsureCategoryOnAllInstancesAsync(category, ct);
            await EnsureTrackerTagsAsync(initScope, ct);

            while (!ct.IsCancellationRequested)
            {
                var loopStart = DateTime.UtcNow;
                try
                {
                    if (!await _connectivityService.IsConnectedAsync(ct))
                    {
                        _stateManager.Update(stateName, s => s.Status = "Waiting for connectivity...");
                        await Task.Delay(TimeSpan.FromSeconds(_config.Settings.NoInternetSleepTimer), ct);
                        continue;
                    }

                    _stateManager.Update(stateName, s => s.Status = "Processing torrents...");
                    using var scope = _scopeFactory.CreateScope();
                    var processor = scope.ServiceProvider.GetRequiredService<ITorrentProcessor>();
                    var seeding = scope.ServiceProvider.GetRequiredService<ISeedingService>();
                    var pathTracker = scope.ServiceProvider.GetRequiredService<IImportPathTracker>();

                    await processor.ProcessTorrentsAsync(category, ct);
                    await seeding.RemoveCompletedTorrentsAsync(category, ct);

                    var completedRoot = _config.Settings.CompletedDownloadFolder;
                    if (!string.IsNullOrWhiteSpace(completedRoot))
                    {
                        pathTracker.RemoveEmptyPathsUnder(completedRoot);
                        pathTracker.ClearIfFolderEmpty(completedRoot);
                    }
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error in qBit category worker for {Category}", category);
                }

                var elapsed = DateTime.UtcNow - loopStart;
                var sleep = TimeSpan.FromSeconds(_config.Settings.LoopSleepTimer) - elapsed;
                if (sleep > TimeSpan.Zero)
                    await Task.Delay(sleep, ct);
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

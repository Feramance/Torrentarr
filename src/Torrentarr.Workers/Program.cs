using Microsoft.Extensions.Logging;
using Torrentarr.Core.Configuration;
using Torrentarr.Core.Interfaces;
using Torrentarr.Core.Services;
using Torrentarr.Infrastructure.ApiClients.QBittorrent;
using Torrentarr.Infrastructure.Database;
using Torrentarr.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Serilog;
using Serilog.Core;
using Serilog.Events;

// Parse command line arguments
var instanceName = args.Contains("--instance") && args.Length > Array.IndexOf(args, "--instance") + 1
    ? args[Array.IndexOf(args, "--instance") + 1]
    : "Unknown";
var statusPath = args.Contains("--status-path") && args.Length > Array.IndexOf(args, "--status-path") + 1
    ? args[Array.IndexOf(args, "--status-path") + 1]
    : null;
var parentPid = args.Contains("--parent-pid") && args.Length > Array.IndexOf(args, "--parent-pid") + 1
    && int.TryParse(args[Array.IndexOf(args, "--parent-pid") + 1], out var parsedParentPid) ? parsedParentPid : 0;

// Data directory: aligned with resolved config path (see ConfigurationLoader.GetDataDirectoryPath)
var basePath = ConfigurationLoader.GetDataDirectoryPath();
var logsPath = Path.Combine(basePath, "logs");
var dbPath = Path.Combine(basePath, "torrentarr.db");
Directory.CreateDirectory(basePath);
Directory.CreateDirectory(logsPath);

// Mutable level switch — lets log level be changed at runtime via file
var levelSwitch = new LoggingLevelSwitch(LogEventLevel.Debug);

// Configure Serilog - write to .config/logs/ with process metadata enrichment
Log.Logger = new LoggerConfiguration()
    .MinimumLevel.ControlledBy(levelSwitch)
    .Enrich.WithProperty("ProcessType", "Worker")
    .Enrich.WithProperty("ProcessInstance", instanceName)
    .Enrich.WithProperty("ProcessId", Environment.ProcessId)
    .Enrich.WithProperty("MachineName", Environment.MachineName)
    .Filter.ByExcluding(e =>
        e.RenderMessage().Contains("DbCommand") ||
        e.RenderMessage().Contains("started tracking") ||
        e.RenderMessage().Contains("changed state from") ||
        e.RenderMessage().Contains("generated temporary value") ||
        e.RenderMessage().Contains("Closing data reader") ||
        e.RenderMessage().Contains("DetectChanges") ||
        e.RenderMessage().Contains("SaveChanges") ||
        e.RenderMessage().Contains("Opening connection") ||
        e.RenderMessage().Contains("Opened connection") ||
        e.RenderMessage().Contains("Closing connection") ||
        e.RenderMessage().Contains("Closed connection") ||
        e.RenderMessage().Contains("was detected as changed") ||
        e.RenderMessage().Contains("Executing endpoint") ||
        e.RenderMessage().Contains("Executed endpoint") ||
        e.RenderMessage().Contains("Request starting") ||
        e.RenderMessage().Contains("Request finished") ||
        e.RenderMessage().Contains("Writing value of type") ||
        e.RenderMessage().Contains("is valid for the request") ||
        e.RenderMessage().Contains("A data reader"))
    .WriteTo.Console()
    .WriteTo.File(
        Path.Combine(logsPath, $"worker-{instanceName}.log"),
        rollingInterval: RollingInterval.Day,
        shared: true,
        retainedFileCountLimit: 7)
    .CreateLogger();

// Monitor for log level changes via file
var logLevelFilePath = Path.Combine(logsPath, $"worker-{instanceName}.loglevel");
var logWatcherCts = new CancellationTokenSource();
_ = Task.Run(async () =>
{
    while (!logWatcherCts.Token.IsCancellationRequested)
    {
        try
        {
            if (File.Exists(logLevelFilePath))
            {
                var level = await File.ReadAllTextAsync(logLevelFilePath, logWatcherCts.Token);
                level = level.Trim().ToUpperInvariant();
                var newLevel = level switch
                {
                    "TRACE" or "VERBOSE" => LogEventLevel.Verbose,
                    "DEBUG" => LogEventLevel.Debug,
                    "INFORMATION" or "INFO" => LogEventLevel.Information,
                    "WARNING" or "WARN" => LogEventLevel.Warning,
                    "ERROR" => LogEventLevel.Error,
                    "CRITICAL" or "FATAL" => LogEventLevel.Fatal,
                    _ => LogEventLevel.Information
                };
                levelSwitch.MinimumLevel = newLevel;
                Log.Information("Log level changed to {Level} via file", level);
                File.Delete(logLevelFilePath);
            }
        }
        catch (OperationCanceledException)
        {
            break;
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Log level watcher encountered an error");
        }
        try { await Task.Delay(TimeSpan.FromSeconds(5), logWatcherCts.Token); }
        catch (OperationCanceledException) { break; }
    }
}, logWatcherCts.Token);

try
{
    Log.Information("Torrentarr Worker starting for instance: {Instance}", instanceName);

    // Load configuration
    var configLoader = new ConfigurationLoader();
    TorrentarrConfig? config = null;

    try
    {
        config = configLoader.Load();
        Log.Information("Configuration loaded successfully");
    }
    catch (FileNotFoundException ex)
    {
        Log.Error("Configuration file not found: {Message}", ex.Message);
        return 1;
    }

    // Verify this instance exists in configuration
    if (!config.ArrInstances.TryGetValue(instanceName, out var instanceConfig))
    {
        Log.Error("Arr instance {Instance} not found in configuration", instanceName);
        return 1;
    }

    Log.Information("Worker configured for {Type} instance at {URI}",
        instanceConfig.Type, instanceConfig.URI);

    // Create host builder
    var builder = Host.CreateApplicationBuilder(args);

    builder.Services.AddSerilog();
    builder.Services.AddSingleton(config);
    builder.Services.AddSingleton(instanceConfig);
    builder.Services.AddSingleton(new WorkerContext { InstanceName = instanceName });

    // Add database context - use same dbPath as defined at startup
    builder.Services.AddDbContext<TorrentarrDbContext>(options =>
    {
        options.UseSqlite($"Data Source={dbPath}");
    });

    // Add services
    builder.Services.AddSingleton<DatabaseRestartCoordinator>();
    builder.Services.AddSingleton<QBittorrentConnectionManager>();
    builder.Services.AddSingleton<ITorrentClientFactory, QBittorrentTorrentClientFactory>();
    builder.Services.AddSingleton<ITorrentClientRegistry>(sp => sp.GetRequiredService<QBittorrentConnectionManager>());
    builder.Services.AddSingleton<ITorrentCacheService, TorrentCacheService>();
    builder.Services.AddSingleton<IMediaValidationService, MediaValidationService>();
    builder.Services.AddScoped<ITorrentProcessor, TorrentProcessor>();
    builder.Services.AddScoped<ArrSyncService>();
    builder.Services.AddScoped<ISearchExecutor, SearchExecutor>();
    builder.Services.AddScoped<QualityProfileSwitcherService>();
    builder.Services.AddScoped<IArrMediaService, ArrMediaService>();
    builder.Services.AddScoped<ISeedingService, SeedingService>();
    builder.Services.AddScoped<IArrImportService, ArrImportService>();
    builder.Services.AddScoped<QBitCategoryEnsureService>();
    builder.Services.AddSingleton<IConnectivityService, ConnectivityService>();
    builder.Services.AddSingleton<IPathMappingService, PathMappingService>();
    builder.Services.AddSingleton<ITorrentInventoryService, TorrentInventoryService>();
    builder.Services.AddSingleton<ISafeDeletionService, SafeDeletionService>();
    builder.Services.AddSingleton<IImportPathTracker, ImportPathTracker>();
    builder.Services.AddSingleton<SearchYearCursor>();
    builder.Services.AddSingleton<StalledUploadTracker>();

    builder.Services.AddHostedService<ArrWorkerService>();

    var host = builder.Build();

    _ = Task.Run(async () =>
    {
        var restartCoordinator = host.Services.GetRequiredService<DatabaseRestartCoordinator>();
        while (statusPath != null)
        {
            var tmp = statusPath + ".tmp";
            var statusDirectory = Path.GetDirectoryName(statusPath);
            if (!string.IsNullOrEmpty(statusDirectory))
                Directory.CreateDirectory(statusDirectory);
            try
            {
                await File.WriteAllTextAsync(tmp, System.Text.Json.JsonSerializer.Serialize(new { version = 1, instance = instanceName, pid = Environment.ProcessId, heartbeat = DateTimeOffset.UtcNow, restartRequested = restartCoordinator.RestartRequested }));
                File.Move(tmp, statusPath, true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Log.Warning(ex, "Unable to write worker status for {Instance}", instanceName);
            }
            await Task.Delay(TimeSpan.FromSeconds(5));
        }
    });
    _ = Task.Run(async () =>
    {
        if (Console.IsInputRedirected && (await Console.In.ReadLineAsync())?.Trim().Equals("shutdown", StringComparison.OrdinalIgnoreCase) == true)
            host.Services.GetRequiredService<IHostApplicationLifetime>().StopApplication();
    });
    _ = Task.Run(async () =>
    {
        while (parentPid > 0)
        {
            try { System.Diagnostics.Process.GetProcessById(parentPid); }
            catch { host.Services.GetRequiredService<IHostApplicationLifetime>().StopApplication(); break; }
            await Task.Delay(TimeSpan.FromSeconds(5));
        }
    });

    await host.RunAsync();

    logWatcherCts.Cancel();
    return 0;
}
catch (Exception ex)
{
    Log.Fatal(ex, "Worker terminated unexpectedly");
    return 1;
}
finally
{
    await Log.CloseAndFlushAsync();
}

class WorkerContext
{
    public string InstanceName { get; set; } = "";
}

/// <summary>
/// Background service that processes torrents for an Arr instance
/// </summary>
class ArrWorkerService : BackgroundService
{
    private readonly ILogger<ArrWorkerService> _logger;
    private readonly TorrentarrConfig _config;
    private readonly ArrInstanceConfig _instanceConfig;
    private readonly WorkerContext _context;
    private readonly IServiceProvider _serviceProvider;
    private readonly QBittorrentConnectionManager _qbitManager;
    private readonly IConnectivityService _connectivityService;
    private readonly SearchYearCursor _yearCursor;
    private DateTime _lastRssSync = DateTime.MinValue;
    private DateTime _lastRefreshDownloads = DateTime.MinValue;
    private bool _searchLoopCompleted;
    private volatile bool _initialized;
    private bool _qbitSetupComplete;

    private int _consecutiveErrors = 0;
    private DateTime _lastErrorTime = DateTime.MinValue;
    private int _consecutiveSearchErrors = 0;
    private DateTime _lastSearchErrorTime = DateTime.MinValue;
    private readonly List<TimeSpan> _backoffDelays = new()
    {
        TimeSpan.FromMinutes(2),
        TimeSpan.FromMinutes(5),
        TimeSpan.FromMinutes(10),
        TimeSpan.FromMinutes(20),
        TimeSpan.FromMinutes(30)
    };

    public ArrWorkerService(
        ILogger<ArrWorkerService> logger,
        TorrentarrConfig config,
        ArrInstanceConfig instanceConfig,
        WorkerContext context,
        IServiceProvider serviceProvider,
        QBittorrentConnectionManager qbitManager,
        IConnectivityService connectivityService,
        SearchYearCursor yearCursor)
    {
        _logger = logger;
        _config = config;
        _instanceConfig = instanceConfig;
        _context = context;
        _serviceProvider = serviceProvider;
        _qbitManager = qbitManager;
        _connectivityService = connectivityService;
        _yearCursor = yearCursor;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Arr Worker for {Instance} starting", _context.InstanceName);
        _logger.LogInformation("Type: {Type}, URI: {URI}, Category: {Category}",
            _instanceConfig.Type, _instanceConfig.URI, _instanceConfig.Category);

        // Initialize connections to all configured qBit instances (retry on later loops if this fails)
        await _qbitManager.EnsureAllConnectedAsync(_config.GetAllTorrentClients(), stoppingToken);
        if (!_qbitManager.IsConnected() && _config.GetAllTorrentClients().Any(q => !q.Value.Disabled && q.Value.Host != "CHANGE_ME"))
            _logger.LogWarning("Failed to connect to any qBittorrent instance; will retry each cycle");

        try
        {
            await InitializeAsync(stoppingToken);
            _initialized = true;
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Worker initialization failed; continuing into the processing loop");
        }

        try { await Task.WhenAll(RunTorrentLoopAsync(stoppingToken), RunSearchLoopAsync(stoppingToken)); }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
    }

    private async Task RunTorrentLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                if (!await _connectivityService.IsConnectedAsync(cancellationToken))
                {
                    await Task.Delay(TimeSpan.FromSeconds(_config.Settings.NoInternetSleepTimer), cancellationToken);
                    continue;
                }
                await _qbitManager.EnsureAllConnectedAsync(_config.GetAllTorrentClients(), cancellationToken);
                if (!_initialized)
                {
                    await InitializeAsync(cancellationToken);
                    _initialized = true;
                }
                else if (!_qbitSetupComplete)
                {
                    _qbitSetupComplete = await EnsureQBitSetupAsync(cancellationToken);
                }
                var backoffDelay = GetBackoffDelay();
                if (backoffDelay > TimeSpan.Zero)
                {
                    await Task.Delay(backoffDelay, cancellationToken);
                    continue;
                }
                await ProcessTorrentsAsync(cancellationToken);
                _consecutiveErrors = 0;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { break; }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error processing torrents for {Instance}", _context.InstanceName);
                HandleProcessingError();
            }
            await Task.Delay(TimeSpan.FromSeconds(_config.Settings.LoopSleepTimer), cancellationToken);
        }
    }

    private async Task RunSearchLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                if (!_initialized)
                {
                    await Task.Delay(TimeSpan.FromSeconds(_config.Settings.LoopSleepTimer), cancellationToken);
                    continue;
                }
                if (!await _connectivityService.IsConnectedAsync(cancellationToken))
                {
                    await Task.Delay(TimeSpan.FromSeconds(_config.Settings.NoInternetSleepTimer), cancellationToken);
                    continue;
                }
                var backoffDelay = GetSearchBackoffDelay();
                if (backoffDelay > TimeSpan.Zero)
                {
                    await Task.Delay(backoffDelay, cancellationToken);
                    continue;
                }
                await ProcessSearchAsync(cancellationToken);
                _consecutiveSearchErrors = 0;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { break; }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error searching for {Instance}", _context.InstanceName);
                HandleSearchError();
            }
            await Task.Delay(TimeSpan.FromSeconds(_config.Settings.LoopSleepTimer), cancellationToken);
        }
    }

    private TimeSpan GetBackoffDelay()
    {
        // Reset if no errors in last 5 minutes
        if (_consecutiveErrors > 0 && DateTime.UtcNow - _lastErrorTime > TimeSpan.FromMinutes(5))
        {
            _logger.LogInformation("No errors in last 5 minutes, resetting backoff counter");
            _consecutiveErrors = 0;
            return TimeSpan.Zero;
        }

        if (_consecutiveErrors == 0)
        {
            return TimeSpan.Zero;
        }

        var delayIndex = Math.Min(_consecutiveErrors - 1, _backoffDelays.Count - 1);
        var timeSinceLastError = DateTime.UtcNow - _lastErrorTime;
        var targetDelay = _backoffDelays[delayIndex];

        var remainingDelay = targetDelay - timeSinceLastError;
        return remainingDelay > TimeSpan.Zero ? remainingDelay : TimeSpan.Zero;
    }

    private void HandleProcessingError()
    {
        _consecutiveErrors++;
        _lastErrorTime = DateTime.UtcNow;
        _logger.LogWarning("Processing error #{Count}, next backoff delay will be approximately {Delay}",
            _consecutiveErrors, _backoffDelays[Math.Min(_consecutiveErrors - 1, _backoffDelays.Count - 1)]);
    }

    private TimeSpan GetSearchBackoffDelay()
    {
        if (_consecutiveSearchErrors > 0 && DateTime.UtcNow - _lastSearchErrorTime > TimeSpan.FromMinutes(5))
            _consecutiveSearchErrors = 0;
        if (_consecutiveSearchErrors == 0)
            return TimeSpan.Zero;
        var delay = _backoffDelays[Math.Min(_consecutiveSearchErrors - 1, _backoffDelays.Count - 1)];
        var remaining = delay - (DateTime.UtcNow - _lastSearchErrorTime);
        return remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero;
    }

    private void HandleSearchError()
    {
        _consecutiveSearchErrors++;
        _lastSearchErrorTime = DateTime.UtcNow;
        _logger.LogWarning("Search error #{Count}, next backoff delay will be approximately {Delay}",
            _consecutiveSearchErrors, _backoffDelays[Math.Min(_consecutiveSearchErrors - 1, _backoffDelays.Count - 1)]);
    }

    private async Task ProcessTorrentsAsync(CancellationToken cancellationToken)
    {
        _logger.LogTrace("Processing torrents for {Instance}", _context.InstanceName);

        // Create a scope for scoped services (DbContext, TorrentProcessor, etc.)
        using var scope = _serviceProvider.CreateScope();
        var torrentProcessor = scope.ServiceProvider.GetRequiredService<ITorrentProcessor>();
        var cacheService = scope.ServiceProvider.GetRequiredService<ITorrentCacheService>();

        // NOTE: Free space management and special categories (failed, recheck) are handled
        // GLOBALLY by the Host orchestrator - not per-worker. This matches qBitrr's design where:
        // - FreeSpaceManager runs ONCE per qBittorrent instance, handling ALL categories
        // - PlaceHolderArr handles special categories globally

        // Clean expired cache entries
        cacheService.CleanExpired();
        try
        {
            await RunPeriodicCommandsAsync(cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Periodic Arr command failed; continuing torrent processing for {Instance}", _context.InstanceName);
        }

        // Process all torrents for this category (excluding special categories which are handled globally)
        if (!_instanceConfig.SearchOnly)
            await torrentProcessor.ProcessTorrentsAsync(_instanceConfig.Category, cancellationToken);

        if (!_instanceConfig.SearchOnly
            && !string.IsNullOrWhiteSpace(_config.Settings.CompletedDownloadFolder)
            && _config.Settings.CompletedDownloadFolder != "CHANGE_ME")
        {
            var pathTracker = scope.ServiceProvider.GetRequiredService<IImportPathTracker>();
            pathTracker.RemoveEmptyPathsUnder(_config.Settings.CompletedDownloadFolder);
            pathTracker.ClearIfFolderEmpty(_config.Settings.CompletedDownloadFolder);
        }

    }

    private async Task ProcessSearchAsync(CancellationToken cancellationToken)
    {
        using var scope = _serviceProvider.CreateScope();
        var syncService = scope.ServiceProvider.GetRequiredService<ArrSyncService>();
        var arrMediaService = scope.ServiceProvider.GetRequiredService<IArrMediaService>();
        if (_searchLoopCompleted && _instanceConfig.Search.SearchAgainOnSearchCompletion)
        {
            await ResetSearchedFlagsAsync(scope.ServiceProvider.GetRequiredService<TorrentarrDbContext>(), cancellationToken);
            _searchLoopCompleted = false;
        }
        await syncService.SyncAsync(_context.InstanceName, cancellationToken);
        if (_instanceConfig.Search.SearchMissing)
            await syncService.MarkRequestsAsync(_context.InstanceName, cancellationToken);

        SearchResult? searchResult = null;
        if (!_instanceConfig.ProcessingOnly && ShouldRunSearch())
        {
            if (_instanceConfig.Search.UseTempForMissing && _instanceConfig.Search.TempProfileResetTimeoutMinutes > 0)
            {
                try
                {
                    await scope.ServiceProvider.GetRequiredService<QualityProfileSwitcherService>()
                        .RestoreTimedOutProfilesAsync(_context.InstanceName, _instanceConfig, cancellationToken);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to restore timed-out temporary profiles for {Instance}; continuing search", _context.InstanceName);
                }
            }

            // §2.7: DoUpgradeSearch is exclusive — when active, skip missing-media search
            if (_instanceConfig.Search.DoUpgradeSearch)
            {
                _logger.LogInformation("Searching for quality upgrades (exclusive) in {Instance}", _context.InstanceName);
                searchResult = await arrMediaService.SearchQualityUpgradesAsync(_instanceConfig.Category, cancellationToken);
            }
            else
            {
                if (_instanceConfig.Search.SearchMissing)
                {
                    _logger.LogInformation("Searching for missing media in {Instance}", _context.InstanceName);
                    searchResult = await arrMediaService.SearchMissingMediaAsync(_instanceConfig.Category, cancellationToken);
                    if (searchResult.SearchesTriggered > 0)
                        _logger.LogInformation("Triggered {Count} searches for missing media", searchResult.SearchesTriggered);
                }

                // QualityUnmetSearch / CustomFormatUnmetSearch are always additive
                if (_instanceConfig.Search.QualityUnmetSearch || _instanceConfig.Search.CustomFormatUnmetSearch)
                {
                    var upgradeResult = await arrMediaService.SearchQualityUpgradesAsync(_instanceConfig.Category, cancellationToken);
                    if (searchResult == null)
                        searchResult = upgradeResult;
                    else
                    {
                        searchResult.SearchesTriggered += upgradeResult.SearchesTriggered;
                        searchResult.ItemsSearched += upgradeResult.ItemsSearched;
                        searchResult.LoopCompleted &= upgradeResult.LoopCompleted;
                    }
                    if (upgradeResult.SearchesTriggered > 0)
                        _logger.LogInformation("Triggered {Count} searches for quality upgrades", upgradeResult.SearchesTriggered);
                }
            }
        }

        if (searchResult?.LoopCompleted == true)
        {
            var hasMoreYears = SearchYearCursor.ShouldFilter(_instanceConfig)
                && _yearCursor.Advance(_context.InstanceName);
            _searchLoopCompleted = !hasMoreYears;
        }
    }

    private async Task ResetSearchedFlagsAsync(TorrentarrDbContext db, CancellationToken ct)
    {
        switch (_instanceConfig.Type.ToLowerInvariant())
        {
            case "radarr":
                await db.Movies.Where(x => x.ArrInstance == _context.InstanceName && x.Searched)
                    .ExecuteUpdateAsync(s => s.SetProperty(x => x.Searched, false).SetProperty(x => x.Upgrade, false), ct);
                break;
            case "sonarr":
                await db.Episodes.Where(x => x.ArrInstance == _context.InstanceName && x.Searched)
                    .ExecuteUpdateAsync(s => s.SetProperty(x => x.Searched, false).SetProperty(x => x.Upgrade, false), ct);
                await db.Series.Where(x => x.ArrInstance == _context.InstanceName && x.Searched)
                    .ExecuteUpdateAsync(s => s.SetProperty(x => x.Searched, false).SetProperty(x => x.Upgrade, false), ct);
                break;
            case "lidarr":
                await db.Albums.Where(x => x.ArrInstance == _context.InstanceName && x.Searched)
                    .ExecuteUpdateAsync(s => s.SetProperty(x => x.Searched, false).SetProperty(x => x.Upgrade, false), ct);
                break;
            case "readarr":
                await db.Books.Where(x => x.ArrInstance == _context.InstanceName && x.Searched)
                    .ExecuteUpdateAsync(s => s.SetProperty(x => x.Searched, false).SetProperty(x => x.Upgrade, false), ct);
                break;
        }
    }

    private async Task InitializeAsync(CancellationToken cancellationToken)
    {
        _qbitSetupComplete = await EnsureQBitSetupAsync(cancellationToken);
        if (_instanceConfig.Search.UseTempForMissing && _instanceConfig.Search.ForceResetTempProfiles)
        {
            using var scope = _serviceProvider.CreateScope();
            await scope.ServiceProvider.GetRequiredService<QualityProfileSwitcherService>()
                .ForceResetAllTempProfilesAsync(_context.InstanceName, _instanceConfig, cancellationToken);
        }
    }

    private async Task<bool> EnsureQBitSetupAsync(CancellationToken cancellationToken)
    {
        using var scope = _serviceProvider.CreateScope();
        var ensure = scope.ServiceProvider.GetRequiredService<QBitCategoryEnsureService>();
        var categoriesReady = await ensure.EnsureCategoryOnAllInstancesAsync(_instanceConfig.Category, cancellationToken);
        if (!categoriesReady)
            _logger.LogWarning("Category setup failed on one or more qBittorrent instances; continuing with available clients");
        var tagsReady = true;
        if (scope.ServiceProvider.GetRequiredService<ISeedingService>() is SeedingService seeding)
        {
            tagsReady = await seeding.EnsureAllTrackerTagsExistAsync(cancellationToken);
            if (!tagsReady)
                _logger.LogWarning("Tracker tag setup failed on one or more qBittorrent instances; continuing with available clients");
        }

        var allConfiguredClientsConnected = _config.GetAllTorrentClients()
            .Where(q => !q.Value.Disabled && q.Value.Host != "CHANGE_ME")
            .All(q => _qbitManager.IsConnected(q.Key));
        return categoriesReady && tagsReady && allConfiguredClientsConnected;
    }

    private async Task RunPeriodicCommandsAsync(CancellationToken cancellationToken)
    {
        var now = DateTime.UtcNow;
        if (_instanceConfig.RssSyncTimer > 0 && now - _lastRssSync >= TimeSpan.FromMinutes(_instanceConfig.RssSyncTimer))
        {
            try
            {
                switch (_instanceConfig.Type.ToLowerInvariant())
                {
                    case "radarr": await new Torrentarr.Infrastructure.ApiClients.Arr.RadarrClient(_instanceConfig.URI, _instanceConfig.APIKey, _instanceConfig.SkipTLSVerify).RssSyncAsync(cancellationToken); break;
                    case "sonarr": await new Torrentarr.Infrastructure.ApiClients.Arr.SonarrClient(_instanceConfig.URI, _instanceConfig.APIKey, _instanceConfig.SkipTLSVerify).RssSyncAsync(cancellationToken); break;
                    case "lidarr": await new Torrentarr.Infrastructure.ApiClients.Arr.LidarrClient(_instanceConfig.URI, _instanceConfig.APIKey, _instanceConfig.SkipTLSVerify).RssSyncAsync(cancellationToken); break;
                    case "readarr": await new Torrentarr.Infrastructure.ApiClients.Arr.ReadarrClient(_instanceConfig.URI, _instanceConfig.APIKey, _instanceConfig.SkipTLSVerify).RssSyncAsync(cancellationToken); break;
                }
                _lastRssSync = now;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch (Exception ex) { _logger.LogWarning(ex, "RSS sync failed for {Instance}", _context.InstanceName); }
        }
        if (_instanceConfig.RefreshDownloadsTimer > 0
            && !string.Equals(_instanceConfig.Type, "lidarr", StringComparison.OrdinalIgnoreCase)
            && now - _lastRefreshDownloads >= TimeSpan.FromMinutes(_instanceConfig.RefreshDownloadsTimer))
        {
            try
            {
                switch (_instanceConfig.Type.ToLowerInvariant())
                {
                    case "radarr": await new Torrentarr.Infrastructure.ApiClients.Arr.RadarrClient(_instanceConfig.URI, _instanceConfig.APIKey, _instanceConfig.SkipTLSVerify).RefreshMonitoredDownloadsAsync(cancellationToken); break;
                    case "sonarr": await new Torrentarr.Infrastructure.ApiClients.Arr.SonarrClient(_instanceConfig.URI, _instanceConfig.APIKey, _instanceConfig.SkipTLSVerify).RefreshMonitoredDownloadsAsync(cancellationToken); break;
                    case "readarr": await new Torrentarr.Infrastructure.ApiClients.Arr.ReadarrClient(_instanceConfig.URI, _instanceConfig.APIKey, _instanceConfig.SkipTLSVerify).RefreshMonitoredDownloadsAsync(cancellationToken); break;
                }
                _lastRefreshDownloads = now;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch (Exception ex) { _logger.LogWarning(ex, "Download refresh failed for {Instance}", _context.InstanceName); }
        }
    }

    private DateTime _lastSearchTime = DateTime.MinValue;

    private bool ShouldRunSearch()
    {
        if (!_instanceConfig.Search.SearchMissing)
            return false;

        var searchInterval = TimeSpan.FromSeconds(_instanceConfig.Search.SearchRequestsEvery);

        if (DateTime.UtcNow - _lastSearchTime >= searchInterval)
        {
            _lastSearchTime = DateTime.UtcNow;
            return true;
        }

        return false;
    }
}

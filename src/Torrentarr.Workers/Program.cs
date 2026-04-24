using Microsoft.Extensions.Logging;
using Torrentarr.Core.Configuration;
using Torrentarr.Core.Services;
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
    builder.Services.AddSingleton<QBittorrentConnectionManager>();
    builder.Services.AddSingleton<ITorrentCacheService, TorrentCacheService>();
    builder.Services.AddSingleton<IMediaValidationService, MediaValidationService>();
    builder.Services.AddScoped<ITorrentProcessor, TorrentProcessor>();
    builder.Services.AddScoped<ArrSyncService>();
    builder.Services.AddScoped<ISearchExecutor, SearchExecutor>();
    builder.Services.AddScoped<IArrMediaService, ArrMediaService>();
    builder.Services.AddScoped<ISeedingService, SeedingService>();
    builder.Services.AddScoped<IArrImportService, ArrImportService>();
    builder.Services.AddScoped<IDatabaseHealthService, DatabaseHealthService>();
    builder.Services.AddSingleton<IConnectivityService, ConnectivityService>();

    builder.Services.AddHostedService<ArrWorkerService>();

    var host = builder.Build();

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

    private int _consecutiveErrors = 0;
    private DateTime _lastErrorTime = DateTime.MinValue;
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
        IConnectivityService connectivityService)
    {
        _logger = logger;
        _config = config;
        _instanceConfig = instanceConfig;
        _context = context;
        _serviceProvider = serviceProvider;
        _qbitManager = qbitManager;
        _connectivityService = connectivityService;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Arr Worker for {Instance} starting", _context.InstanceName);
        _logger.LogInformation("Type: {Type}, URI: {URI}, Category: {Category}",
            _instanceConfig.Type, _instanceConfig.URI, _instanceConfig.Category);

        // Initialize connections to all configured qBit instances
        var anyConnected = false;
        foreach (var (name, qbitConfig) in _config.QBitInstances)
        {
            try
            {
                var ok = await _qbitManager.InitializeAsync(name, qbitConfig, stoppingToken);
                if (ok) anyConnected = true;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error connecting to qBittorrent instance '{Name}'", name);
            }
        }

        if (!anyConnected && _config.QBitInstances.Any(q => !q.Value.Disabled))
        {
            _logger.LogError("Failed to connect to any qBittorrent instance");
            return;
        }

        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    // Check for exponential backoff
                    var backoffDelay = GetBackoffDelay();
                    if (backoffDelay > TimeSpan.Zero)
                    {
                        _logger.LogWarning("In exponential backoff mode, waiting {Delay} before next attempt", backoffDelay);
                        await Task.Delay(backoffDelay, stoppingToken);
                        continue;
                    }

                    // §2.4: Check internet connectivity, sleep NoInternetSleepTimer on failure
                    if (!await _connectivityService.IsConnectedAsync(stoppingToken))
                    {
                        _logger.LogWarning("No internet connectivity, skipping processing cycle. Sleeping {Seconds}s",
                            _config.Settings.NoInternetSleepTimer);
                        await Task.Delay(TimeSpan.FromSeconds(_config.Settings.NoInternetSleepTimer), stoppingToken);
                        continue;
                    }

                    await ProcessTorrentsAsync(stoppingToken);

                    // Reset error counter on successful processing
                    _consecutiveErrors = 0;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error processing torrents for {Instance}", _context.InstanceName);
                    HandleProcessingError();
                }

                // Sleep for configured interval
                var sleepTime = TimeSpan.FromSeconds(_config.Settings.LoopSleepTimer);
                _logger.LogTrace("Sleeping for {Seconds} seconds", sleepTime.TotalSeconds);
                await Task.Delay(sleepTime, stoppingToken);
            }
        }
        catch (OperationCanceledException)
        {
            _logger.LogInformation("Worker for {Instance} shutting down gracefully", _context.InstanceName);
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

    private async Task ProcessTorrentsAsync(CancellationToken cancellationToken)
    {
        _logger.LogTrace("Processing torrents for {Instance}", _context.InstanceName);

        // Create a scope for scoped services (DbContext, TorrentProcessor, etc.)
        using var scope = _serviceProvider.CreateScope();
        var torrentProcessor = scope.ServiceProvider.GetRequiredService<ITorrentProcessor>();
        var arrMediaService = scope.ServiceProvider.GetRequiredService<IArrMediaService>();
        var seedingService = scope.ServiceProvider.GetRequiredService<ISeedingService>();
        var dbHealthService = scope.ServiceProvider.GetRequiredService<IDatabaseHealthService>();
        var cacheService = scope.ServiceProvider.GetRequiredService<ITorrentCacheService>();

        // NOTE: Free space management and special categories (failed, recheck) are handled
        // GLOBALLY by the Host orchestrator - not per-worker. This matches qBitrr's design where:
        // - FreeSpaceManager runs ONCE per qBittorrent instance, handling ALL categories
        // - PlaceHolderArr handles special categories globally

        // Clean expired cache entries
        cacheService.CleanExpired();

        // Periodic database health check (every 10 iterations)
        if (DateTime.UtcNow.Minute % 10 == 0)
        {
            var healthResult = await dbHealthService.CheckHealthAsync(cancellationToken);
            if (!healthResult.IsHealthy)
            {
                _logger.LogWarning("Database health check failed: {Message}", healthResult.Message);

                // Try WAL checkpoint first
                var checkpointed = await dbHealthService.CheckpointWalAsync(cancellationToken);
                if (!checkpointed)
                {
                    _logger.LogError("Database recovery failed, worker may experience issues");
                }
            }
            else
            {
                _logger.LogTrace("Database health check passed");
            }
        }

        // Process all torrents for this category (excluding special categories which are handled globally)
        await torrentProcessor.ProcessTorrentsAsync(_instanceConfig.Category, cancellationToken);

        // Manage seeding rules and remove completed torrents
        if (!_instanceConfig.SearchOnly)
        {
            var removalResult = await seedingService.RemoveCompletedTorrentsAsync(_instanceConfig.Category, cancellationToken);
            if (removalResult.TorrentsRemoved > 0)
            {
                _logger.LogInformation("Removed {Count} completed torrents that met seeding requirements",
                    removalResult.TorrentsRemoved);
            }
            if (removalResult.TorrentsProtected > 0)
            {
                _logger.LogTrace("{Count} torrents protected by H&R rules", removalResult.TorrentsProtected);
            }
        }

        // Search (if configured and on search cycle)
        if (!_instanceConfig.ProcessingOnly && ShouldRunSearch())
        {
            SearchResult? searchResult = null;

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
                    if (upgradeResult.SearchesTriggered > 0)
                        _logger.LogInformation("Triggered {Count} searches for quality upgrades", upgradeResult.SearchesTriggered);
                }
            }
        }
    }

    private DateTime _lastSearchTime = DateTime.MinValue;

    private bool ShouldRunSearch()
    {
        var searchInterval = TimeSpan.FromSeconds(_instanceConfig.Search.SearchRequestsEvery);

        if (DateTime.UtcNow - _lastSearchTime >= searchInterval)
        {
            _lastSearchTime = DateTime.UtcNow;
            return true;
        }

        return false;
    }
}

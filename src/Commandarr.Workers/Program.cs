using Microsoft.Extensions.Logging;
using Commandarr.Core.Configuration;
using Commandarr.Core.Services;
using Commandarr.Infrastructure.Database;
using Commandarr.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Serilog;

// Parse command line arguments
var instanceName = args.Contains("--instance") && args.Length > Array.IndexOf(args, "--instance") + 1
    ? args[Array.IndexOf(args, "--instance") + 1]
    : "Unknown";

// Configure Serilog
Log.Logger = new LoggerConfiguration()
    .WriteTo.Console()
    .WriteTo.File($"logs/worker-{instanceName}.log", rollingInterval: RollingInterval.Day)
    .CreateLogger();

try
{
    Log.Information("Commandarr Worker starting for instance: {Instance}", instanceName);

    // Load configuration
    var configLoader = new ConfigurationLoader();
    CommandarrConfig? config = null;

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

    // Add database context
    var homePath = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
    var dbPath = Path.Combine(homePath, ".config", "commandarr", "qbitrr.db");
    builder.Services.AddDbContext<CommandarrDbContext>(options =>
    {
        options.UseSqlite($"Data Source={dbPath}");
    });

    // Add services
    builder.Services.AddSingleton<QBittorrentConnectionManager>();
    builder.Services.AddScoped<ITorrentProcessor, TorrentProcessor>();
    builder.Services.AddScoped<IArrMediaService, ArrMediaServiceSimple>();
    builder.Services.AddScoped<ISeedingService, SeedingService>();
    builder.Services.AddScoped<IFreeSpaceService, FreeSpaceService>();

    builder.Services.AddHostedService<ArrWorkerService>();

    var host = builder.Build();

    await host.RunAsync();

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
    private readonly CommandarrConfig _config;
    private readonly ArrInstanceConfig _instanceConfig;
    private readonly WorkerContext _context;
    private readonly IServiceProvider _serviceProvider;
    private readonly QBittorrentConnectionManager _qbitManager;

    public ArrWorkerService(
        ILogger<ArrWorkerService> logger,
        CommandarrConfig config,
        ArrInstanceConfig instanceConfig,
        WorkerContext context,
        IServiceProvider serviceProvider,
        QBittorrentConnectionManager qbitManager)
    {
        _logger = logger;
        _config = config;
        _instanceConfig = instanceConfig;
        _context = context;
        _serviceProvider = serviceProvider;
        _qbitManager = qbitManager;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Arr Worker for {Instance} starting", _context.InstanceName);
        _logger.LogInformation("Type: {Type}, URI: {URI}, Category: {Category}",
            _instanceConfig.Type, _instanceConfig.URI, _instanceConfig.Category);

        // Initialize qBittorrent connection
        try
        {
            var initialized = await _qbitManager.InitializeAsync(_config.QBit);
            if (!initialized)
            {
                _logger.LogError("Failed to initialize qBittorrent connection");
                return;
            }
            _logger.LogInformation("Connected to qBittorrent successfully");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error connecting to qBittorrent");
            return;
        }

        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    await ProcessTorrentsAsync(stoppingToken);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error processing torrents for {Instance}", _context.InstanceName);
                }

                // Sleep for configured interval
                var sleepTime = TimeSpan.FromSeconds(_config.Settings.LoopSleepTimer);
                _logger.LogDebug("Sleeping for {Seconds} seconds", sleepTime.TotalSeconds);
                await Task.Delay(sleepTime, stoppingToken);
            }
        }
        catch (OperationCanceledException)
        {
            _logger.LogInformation("Worker for {Instance} shutting down gracefully", _context.InstanceName);
        }
    }

    private async Task ProcessTorrentsAsync(CancellationToken cancellationToken)
    {
        _logger.LogDebug("Processing torrents for {Instance}", _context.InstanceName);

        // Create a scope for scoped services (DbContext, TorrentProcessor, etc.)
        using var scope = _serviceProvider.CreateScope();
        var torrentProcessor = scope.ServiceProvider.GetRequiredService<ITorrentProcessor>();
        var arrMediaService = scope.ServiceProvider.GetRequiredService<IArrMediaService>();
        var seedingService = scope.ServiceProvider.GetRequiredService<ISeedingService>();
        var freeSpaceService = scope.ServiceProvider.GetRequiredService<IFreeSpaceService>();

        // 1. Check free space and pause downloads if needed
        var pausedDueToSpace = await freeSpaceService.PauseDownloadsIfLowSpaceAsync(cancellationToken);
        if (pausedDueToSpace)
        {
            _logger.LogWarning("Downloads paused due to low disk space");
        }
        else
        {
            // Try to resume if we have space
            await freeSpaceService.ResumeDownloadsIfSpaceAvailableAsync(cancellationToken);
        }

        // 2. Process all torrents for this category
        await torrentProcessor.ProcessTorrentsAsync(_instanceConfig.Category, cancellationToken);

        // 3. Manage seeding rules and remove completed torrents
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
                _logger.LogDebug("{Count} torrents protected by H&R rules", removalResult.TorrentsProtected);
            }
        }

        // 4. Search for missing media (if configured and on search cycle)
        if (!_instanceConfig.ProcessingOnly && ShouldRunSearch())
        {
            _logger.LogInformation("Searching for missing media in {Instance}", _context.InstanceName);
            var searchResult = await arrMediaService.SearchMissingMediaAsync(_instanceConfig.Category, cancellationToken);

            if (searchResult.SearchesTriggered > 0)
            {
                _logger.LogInformation("Triggered {Count} searches for missing media", searchResult.SearchesTriggered);
            }

            // 5. Search for quality upgrades (if enabled)
            if (_config.Settings.SearchLoopDelay > 0)
            {
                var upgradeResult = await arrMediaService.SearchQualityUpgradesAsync(_instanceConfig.Category, cancellationToken);
                if (upgradeResult.SearchesTriggered > 0)
                {
                    _logger.LogInformation("Triggered {Count} searches for quality upgrades", upgradeResult.SearchesTriggered);
                }
            }
        }
    }

    private int _searchCycleCounter = 0;
    private bool ShouldRunSearch()
    {
        // If SearchLoopDelay is -1 or 0, never search
        if (_config.Settings.SearchLoopDelay <= 0)
        {
            return false;
        }

        // Run search every N cycles
        _searchCycleCounter++;
        if (_searchCycleCounter >= _config.Settings.SearchLoopDelay)
        {
            _searchCycleCounter = 0;
            return true;
        }

        return false;
    }
}

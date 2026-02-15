using Microsoft.Extensions.Logging;
using Commandarr.Core.Configuration;
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

    public ArrWorkerService(
        ILogger<ArrWorkerService> logger,
        CommandarrConfig config,
        ArrInstanceConfig instanceConfig,
        WorkerContext context)
    {
        _logger = logger;
        _config = config;
        _instanceConfig = instanceConfig;
        _context = context;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Arr Worker for {Instance} starting", _context.InstanceName);
        _logger.LogInformation("Type: {Type}, URI: {URI}, Category: {Category}",
            _instanceConfig.Type, _instanceConfig.URI, _instanceConfig.Category);

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

        // TODO: Implement actual torrent processing logic:
        // 1. Connect to qBittorrent
        // 2. Get torrents for this category
        // 3. Check torrent states (downloading, stalled, completed, failed)
        // 4. Process imports to Arr
        // 5. Trigger searches for missing/wanted media
        // 6. Handle quality upgrades
        // 7. Manage seeding rules and Hit & Run protection

        // Placeholder: Just log that we're running
        _logger.LogInformation("Worker heartbeat for {Instance} - {Type} at {Time}",
            _context.InstanceName,
            _instanceConfig.Type,
            DateTime.UtcNow);

        await Task.CompletedTask;
    }
}

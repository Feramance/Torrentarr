using Torrentarr.Infrastructure.Logging;
using Microsoft.Extensions.Logging;
using Torrentarr.Core.Configuration;
using Torrentarr.Core.Interfaces;
using Torrentarr.Core.Models;
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
var runtimeMetrics = new WorkerRuntimeMetrics();

// Data directory: aligned with resolved config path (see ConfigurationLoader.GetDataDirectoryPath)
var basePath = ConfigurationLoader.GetDataDirectoryPath();
var logsPath = Path.Combine(basePath, "logs");
var dbPath = Path.Combine(basePath, "torrentarr.db");
ConfigMutationGuard.EnableProcessCoordination(ConfigurationLoader.GetDefaultConfigPath());
if (File.Exists(ConfigurationLoader.GetDefaultConfigPath() + ".rename.json"))
    throw new InvalidOperationException("Start Host or standalone WebUI to recover the pending instance rename before running workers.");
Directory.CreateDirectory(basePath);
Directory.CreateDirectory(logsPath);

// Mutable level switch — lets log level be changed at runtime via file
var levelSwitch = new LoggingLevelSwitch(LogEventLevel.Information);

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
    .WriteTo.Sink(new ServiceLogSink(logsPath, "Worker"))
    .WriteTo.Console(new ServiceLogFormatter(colour: true))
    .CreateLogger();

// Monitor for log level changes via file
var logLevelFilePath = Path.Combine(logsPath, $"worker-{instanceName}.loglevel");
var logWatcherCts = new CancellationTokenSource();
var logWatcherTask = Task.Run(async () =>
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

using var monitorCts = new CancellationTokenSource();
var monitorTasks = new List<Task>();
var restartCoordinator = new DatabaseRestartCoordinator();
try
{
    if (statusPath != null)
        monitorTasks.Add(Task.Run(async () =>
        {
            try
            {
                while (!monitorCts.IsCancellationRequested)
                {
                    try
                    {
                        var directory = Path.GetDirectoryName(statusPath);
                        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
                        await File.WriteAllTextAsync(statusPath + ".tmp", System.Text.Json.JsonSerializer.Serialize(
                            runtimeMetrics.Snapshot(runtimeMetrics.InstanceName ?? instanceName, restartCoordinator.RestartRequested), WorkerHeartbeat.JsonOptions), monitorCts.Token);
                        File.Move(statusPath + ".tmp", statusPath, true);
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    {
                        Log.Warning(ex, "Unable to write worker status for {Instance}", instanceName);
                    }
                    await Task.Delay(TimeSpan.FromSeconds(5), monitorCts.Token);
                }
            }
            catch (OperationCanceledException) when (monitorCts.IsCancellationRequested) { }
        }));
    Log.Information("Torrentarr Worker starting for instance: {Instance}", instanceName);

    // Load configuration
    var configLoader = new ConfigurationLoader();
    TorrentarrConfig? config = null;
    long startingRevision = 0;

    try
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                using var startupRead = ConfigMutationGuard.EnterProcessing(configLoader.ConfigPath);
                var activePath = Path.Combine(basePath, "active-config.json");
                var snapshot = File.Exists(activePath)
                    ? Newtonsoft.Json.JsonConvert.DeserializeObject<ConfigurationSnapshot>(File.ReadAllText(activePath)) : null;
                startingRevision = snapshot?.Revision ?? DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                config = statusPath != null && snapshot != null ? snapshot.Config : configLoader.Load();
                break;
            }
            catch (ConfigurationBusyException) when (attempt < 39)
            {
                await Task.Delay(250, monitorCts.Token);
            }
        }
        levelSwitch.MinimumLevel = config.Settings.ConsoleLevel?.Trim().ToUpperInvariant() switch
        {
            "TRACE" or "VERBOSE" => LogEventLevel.Verbose,
            "DEBUG" => LogEventLevel.Debug,
            "WARNING" or "WARN" => LogEventLevel.Warning,
            "ERROR" => LogEventLevel.Error,
            "CRITICAL" or "FATAL" => LogEventLevel.Fatal,
            _ => LogEventLevel.Information
        };
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
    builder.Services.AddSingleton<OperationalIssueReporter>();
    builder.Services.AddSingleton<IOperationalIssueReporter>(sp => sp.GetRequiredService<OperationalIssueReporter>());
    builder.Services.AddHostedService(sp => sp.GetRequiredService<OperationalIssueReporter>());
    builder.Services.AddSingleton(instanceConfig);
    builder.Services.AddSingleton(levelSwitch);
    builder.Services.AddSingleton<ConfigReloader>(sp => new ConfigReloader(
        sp.GetRequiredService<ILogger<ConfigReloader>>(), config, new ConfigurationLoader(), worker: true));
    builder.Services.AddSingleton<IConfigReloader>(sp => sp.GetRequiredService<ConfigReloader>());
    builder.Services.AddHostedService(sp => sp.GetRequiredService<ConfigReloader>());
    builder.Services.AddSingleton(runtimeMetrics);
    builder.Services.AddSingleton(new WorkerContext { InstanceName = instanceName, StartingRevision = startingRevision });

    // Add database context - use same dbPath as defined at startup
    builder.Services.AddDbContext<TorrentarrDbContext>(options =>
    {
        options.UseSqlite($"Data Source={dbPath}");
    });

    // Add services
    builder.Services.AddSingleton(restartCoordinator);
    builder.Services.AddSingleton<QBittorrentConnectionManager>();
    builder.Services.AddSingleton<ITorrentClientFactory, QBittorrentTorrentClientFactory>();
    builder.Services.AddSingleton<ITorrentClientRegistry>(sp => sp.GetRequiredService<QBittorrentConnectionManager>());
    builder.Services.AddSingleton<ITorrentCacheService, TorrentCacheService>();
    builder.Services.AddSingleton<IMediaValidationService, MediaValidationService>();
    builder.Services.AddScoped<ITorrentProcessor, TorrentProcessor>();
    builder.Services.AddScoped<ArrSyncService>();
    builder.Services.AddSingleton<SearchSubmissionTiming>();
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

    using var host = builder.Build();
    runtimeMetrics.PendingIssues = () => host.Services.GetRequiredService<IOperationalIssueReporter>().Pending;

    await ArrWorkerService.LoadSearchActivityAsync(host.Services, instanceName, runtimeMetrics, Log.Logger, monitorCts.Token);
    var liveConfig = host.Services.GetRequiredService<IConfigReloader>();
    liveConfig.ConfigReloaded += (_, args) =>
    {
        if (args.Success) levelSwitch.MinimumLevel = LiveConfigurationRuntime.ParseLevel(config.Settings.ConsoleLevel);
    };
    var lifetime = host.Services.GetRequiredService<IHostApplicationLifetime>();
    using var stoppingRegistration = lifetime.ApplicationStopping.Register(monitorCts.Cancel);
    using var input = new StreamReader(Console.OpenStandardInput());
    monitorTasks.Add(Task.Run(async () =>
    {
        try
        {
            while (Console.IsInputRedirected && !monitorCts.IsCancellationRequested)
            {
                var command = (await input.ReadLineAsync(monitorCts.Token))?.Trim();
                if (command == null) break;
                if (command.Equals("shutdown", StringComparison.OrdinalIgnoreCase))
                {
                    lifetime.StopApplication();
                    break;
                }
                if (command.StartsWith("reconnect:", StringComparison.OrdinalIgnoreCase))
                {
                    var clientId = command["reconnect:".Length..];
                    await host.Services.GetRequiredService<QBittorrentConnectionManager>()
                        .InvalidateAuthenticationFailureAsync(clientId, monitorCts.Token);
                }
            }
        }
        catch (OperationCanceledException) when (monitorCts.IsCancellationRequested) { }
    }));
    monitorTasks.Add(Task.Run(async () =>
    {
        try
        {
            while (parentPid > 0 && !monitorCts.IsCancellationRequested)
            {
                try
                {
                    using var parent = System.Diagnostics.Process.GetProcessById(parentPid);
                    if (parent.HasExited) { lifetime.StopApplication(); break; }
                }
                catch (ArgumentException) { lifetime.StopApplication(); break; }
                await Task.Delay(TimeSpan.FromSeconds(5), monitorCts.Token);
            }
        }
        catch (OperationCanceledException) when (monitorCts.IsCancellationRequested) { }
    }));

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
    monitorCts.Cancel();
    logWatcherCts.Cancel();
    await Task.WhenAll(monitorTasks.Append(logWatcherTask));
    logWatcherCts.Dispose();
    await Log.CloseAndFlushAsync();
}

class WorkerContext
{
    private string _name = "";
    private readonly AsyncLocal<string?> _operationName = new();
    public IConfigReloader? Reloader { get; set; }
    public long StartingRevision { get; init; }
    public string InstanceName { get => _operationName.Value ?? Reloader?.ResolveInstanceName(_name, StartingRevision) ?? _name; set => _name = value; }
    public IDisposable BeginOperation()
    {
        var previous = _operationName.Value;
        _operationName.Value = InstanceName;
        return new NameScope(() => _operationName.Value = previous);
    }
    private sealed class NameScope(Action release) : IDisposable { public void Dispose() => release(); }
}

/// <summary>
/// Background service that processes torrents for an Arr instance
/// </summary>
class ArrWorkerService : BackgroundService
{
    private readonly ILogger<ArrWorkerService> _logger;
    private readonly TorrentarrConfig _config;
    private ArrInstanceConfig _instanceConfig => _config.ArrInstances.GetValueOrDefault(_context.InstanceName) ?? new() { Managed = false };
    private readonly IConfigReloader? _reloader;
    private long _initializedRevision;
    private readonly WorkerContext _context;
    private readonly IServiceProvider _serviceProvider;
    private readonly QBittorrentConnectionManager _qbitManager;
    private readonly IConnectivityService _connectivityService;
    private readonly SearchYearCursor _yearCursor;
    private readonly WorkerRuntimeMetrics _runtimeMetrics;
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
        SearchYearCursor yearCursor,
        WorkerRuntimeMetrics runtimeMetrics)
    {
        _logger = logger;
        _config = config;
        _reloader = serviceProvider.GetService<IConfigReloader>();
        _context = context;
        _context.Reloader = _reloader;
        _serviceProvider = serviceProvider;
        _qbitManager = qbitManager;
        _connectivityService = connectivityService;
        _yearCursor = yearCursor;
        _runtimeMetrics = runtimeMetrics;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Arr Worker for {Instance} starting", _context.InstanceName);
        _logger.LogInformation("Type: {Type}, URI: {URI}, Category: {Category}",
            _instanceConfig.Type, _instanceConfig.URI, _instanceConfig.Category);

        using var initialization = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        initialization.CancelAfter(WorkerLoopProgress.CycleTimeout);
        try
        {
            await _qbitManager.EnsureAllConnectedAsync(_config.GetAllTorrentClients(), initialization.Token);
            await InitializeAsync(initialization.Token);
            _initialized = true;
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { throw; }
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
            using var configuration = _config.BeginOperation();
            using var identity = _context.BeginOperation();
            _runtimeMetrics.InstanceName = _context.InstanceName;
            if (!_instanceConfig.Managed || string.IsNullOrWhiteSpace(_instanceConfig.URI) || _instanceConfig.URI == "CHANGE_ME")
            {
                await SleepLoopAsync("Torrent", "Disabled", TimeSpan.FromSeconds(2), cancellationToken);
                continue;
            }
            var cycleStart = System.Diagnostics.Stopwatch.GetTimestamp();
            using var cycle = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cycle.CancelAfter(WorkerLoopProgress.CycleTimeout);
            _runtimeMetrics.Torrent = new("Running", DateTimeOffset.UtcNow, LastSuccess: _runtimeMetrics.Torrent.LastSuccess);
            try
            {
                if (!await _connectivityService.IsConnectedAsync(cycle.Token))
                {
                    await SleepLoopAsync("Torrent", "Waiting for connectivity", TimeSpan.FromSeconds(_config.Settings.NoInternetSleepTimer), cancellationToken);
                    continue;
                }
                using var processing = ConfigMutationGuard.EnterProcessing();
                RequireCurrentConfiguration();
                await _qbitManager.EnsureAllConnectedAsync(_config.GetAllTorrentClients(), cycle.Token);
                if (!_initialized || _initializedRevision != _config.Revision)
                {
                    _qbitSetupComplete = false;
                    _serviceProvider.GetRequiredService<ITorrentCacheService>().Clear();
                    _serviceProvider.GetRequiredService<IImportPathTracker>().Clear();
                    _serviceProvider.GetRequiredService<StalledUploadTracker>().Clear();
                    if (!_initialized) await InitializeAsync(cycle.Token);
                    else _qbitSetupComplete = await EnsureQBitSetupAsync(cycle.Token);
                    _initialized = true;
                    _initializedRevision = _config.Revision;
                }
                else if (!_qbitSetupComplete)
                {
                    _qbitSetupComplete = await EnsureQBitSetupAsync(cycle.Token);
                }
                var backoffDelay = GetBackoffDelay();
                if (backoffDelay > TimeSpan.Zero)
                {
                    processing.Dispose();
                    await SleepLoopAsync("Torrent", "Backoff", backoffDelay, cancellationToken);
                    continue;
                }
                await ProcessTorrentsAsync(cycle.Token);
                _consecutiveErrors = 0;
                _runtimeMetrics.Torrent = _runtimeMetrics.Torrent with { LastSuccess = DateTimeOffset.UtcNow };
                await ObserveLoopAsync("torrent", true, cycle.Token);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { break; }
            catch (ConfigurationBusyException) { }
            catch (Exception ex)
            {
                _runtimeMetrics.Torrent = new(cycle.IsCancellationRequested ? "Timed out" : "Error", DateTimeOffset.UtcNow, LastSuccess: _runtimeMetrics.Torrent.LastSuccess);
                _logger.LogError(ex, "Error processing torrents for {Instance}", _context.InstanceName);
                await ObserveLoopAsync("torrent", false, cancellationToken);
                HandleProcessingError();
            }
            Interlocked.Exchange(ref _runtimeMetrics.TorrentRevision, _config.Revision);
            var remaining = GetLoopDelay(_config.Settings.LoopSleepTimer, System.Diagnostics.Stopwatch.GetElapsedTime(cycleStart));
            if (remaining > TimeSpan.Zero)
                await SleepLoopAsync("Torrent", "Sleeping", remaining, cancellationToken);
        }
    }

    private async Task RunSearchLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            using var configuration = _config.BeginOperation();
            using var identity = _context.BeginOperation();
            _runtimeMetrics.InstanceName = _context.InstanceName;
            if (!_instanceConfig.Managed || string.IsNullOrWhiteSpace(_instanceConfig.URI) || _instanceConfig.URI == "CHANGE_ME")
            {
                await SleepLoopAsync("Search", "Disabled", TimeSpan.FromSeconds(2), cancellationToken);
                continue;
            }
            var cycleStart = System.Diagnostics.Stopwatch.GetTimestamp();
            using var cycle = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cycle.CancelAfter(WorkerLoopProgress.CycleTimeout);
            _runtimeMetrics.Search = new("Running", DateTimeOffset.UtcNow, LastSuccess: _runtimeMetrics.Search.LastSuccess);
            try
            {
                if (!_initialized)
                {
                    await SleepLoopAsync("Search", "Initializing", TimeSpan.FromSeconds(_config.Settings.LoopSleepTimer), cancellationToken);
                    continue;
                }
                if (!await _connectivityService.IsConnectedAsync(cycle.Token))
                {
                    await SleepLoopAsync("Search", "Waiting for connectivity", TimeSpan.FromSeconds(_config.Settings.NoInternetSleepTimer), cancellationToken);
                    continue;
                }
                var backoffDelay = GetSearchBackoffDelay();
                if (backoffDelay > TimeSpan.Zero)
                {
                    await SleepLoopAsync("Search", "Backoff", backoffDelay, cancellationToken);
                    continue;
                }
                using var processing = ConfigMutationGuard.EnterProcessing();
                RequireCurrentConfiguration();
                await ProcessSearchAsync(cycle.Token, DateTimeOffset.UtcNow + WorkerLoopProgress.CycleTimeout - System.Diagnostics.Stopwatch.GetElapsedTime(cycleStart));
                _consecutiveSearchErrors = 0;
                _runtimeMetrics.Search = _runtimeMetrics.Search with { LastSuccess = DateTimeOffset.UtcNow };
                await ObserveLoopAsync("search", true, cycle.Token);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { break; }
            catch (ConfigurationBusyException) { }
            catch (Exception ex)
            {
                _runtimeMetrics.Search = new(cycle.IsCancellationRequested ? "Timed out" : "Error", DateTimeOffset.UtcNow, LastSuccess: _runtimeMetrics.Search.LastSuccess);
                _logger.LogError(ex, "Error searching for {Instance}", _context.InstanceName);
                await ObserveLoopAsync("search", false, cancellationToken);
                HandleSearchError();
            }
            Interlocked.Exchange(ref _runtimeMetrics.SearchRevision, _config.Revision);
            var remaining = GetLoopDelay(_config.Settings.LoopSleepTimer, System.Diagnostics.Stopwatch.GetElapsedTime(cycleStart));
            if (remaining > TimeSpan.Zero)
                await SleepLoopAsync("Search", "Sleeping", remaining, cancellationToken);
        }
    }

    private void RequireCurrentConfiguration()
    {
        // A rename may have completed while this cycle was awaiting connectivity.
        if (_reloader != null && (!_reloader.ReloadConfig() || _config.Revision != _config.LatestRevision))
            throw new ConfigurationBusyException("Configuration changed before processing started.");
    }

    private async Task SleepLoopAsync(string loop, string phase, TimeSpan delay, CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        if (loop == "Torrent") _runtimeMetrics.Torrent = new(phase, now, now + delay, _runtimeMetrics.Torrent.LastSuccess);
        else _runtimeMetrics.Search = new(phase, now, now + delay, _runtimeMetrics.Search.LastSuccess);
        if (loop == "Torrent") Interlocked.Exchange(ref _runtimeMetrics.TorrentRevision, _config.Revision);
        else Interlocked.Exchange(ref _runtimeMetrics.SearchRevision, _config.Revision);
        if (_reloader != null) await _reloader.WaitForChangeAsync(_config.Revision, delay, ct);
        else await Task.Delay(delay, ct);
    }

    internal static TimeSpan GetLoopDelay(int seconds, TimeSpan elapsed) =>
        TimeSpan.FromSeconds(seconds) > elapsed ? TimeSpan.FromSeconds(seconds) - elapsed : TimeSpan.Zero;

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

    private Task ObserveLoopAsync(string loop, bool healthy, CancellationToken ct)
    {
        var reporter = _serviceProvider.GetService<IOperationalIssueReporter>();
        if (reporter == null) return Task.CompletedTask;
        var issue = new OperationalIssueReport("worker." + loop, "system", "Worker " + loop + " cycle failed",
            "This loop could not complete its work; the worker will retry with backoff.", "Inspect worker logs and Arr/client connectivity.", ArrInstance: _context.InstanceName, SubjectType: "loop", SubjectId: loop, Target: "processes");
        return healthy ? reporter.ResolveAsync(issue, ct) : reporter.ReportAsync(issue, ct);
    }

    internal async Task ProcessTorrentsAsync(CancellationToken cancellationToken)
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

        try
        {
            var categoryCount = 0;
            foreach (var client in _qbitManager.GetAllClients().Values)
                try { categoryCount += (await client.GetTorrentsAsync(_instanceConfig.Category, cancellationToken: cancellationToken)).Count; }
                catch { }
            _runtimeMetrics.CategoryCount = categoryCount;

            var db = scope.ServiceProvider.GetRequiredService<TorrentarrDbContext>();
            _runtimeMetrics.QueueCount = _instanceConfig.Type.ToLowerInvariant() switch
            {
                "radarr" => await db.MovieQueue.CountAsync(q => q.ArrInstance == _context.InstanceName, cancellationToken),
                "sonarr" => await db.EpisodeQueue.CountAsync(q => q.ArrInstance == _context.InstanceName, cancellationToken),
                "lidarr" => await db.AlbumQueue.CountAsync(q => q.ArrInstance == _context.InstanceName, cancellationToken),
                "readarr" => await db.BookQueue.CountAsync(q => q.ArrInstance == _context.InstanceName, cancellationToken),
                _ => null
            };
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Unable to update process metrics for {Instance}", _context.InstanceName);
        }

        if (!_instanceConfig.SearchOnly)
        {
            var pathTracker = scope.ServiceProvider.GetRequiredService<IImportPathTracker>();
            ClientDownloadPaths.Clean(_config, pathTracker);
        }

    }

    internal Task ProcessSearchAsync(CancellationToken cancellationToken)
        => ProcessSearchAsync(cancellationToken, null);

    internal async Task ProcessSearchAsync(CancellationToken cancellationToken, DateTimeOffset? deadline)
    {
        using var scope = _serviceProvider.CreateScope();
        var syncService = scope.ServiceProvider.GetRequiredService<ArrSyncService>();
        var arrMediaService = scope.ServiceProvider.GetRequiredService<IArrMediaService>();
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

        if (_instanceConfig.ReSearch)
            await SonarrRetryQueue.ExecuteAsync(scope.ServiceProvider.GetRequiredService<TorrentarrDbContext>(),
                scope.ServiceProvider.GetRequiredService<ISearchExecutor>(), _context.InstanceName, _logger, cancellationToken,
                _instanceConfig, scope.ServiceProvider.GetService<IOperationalIssueReporter>(), deadline);
        await syncService.SyncAsync(_context.InstanceName, cancellationToken);
        if (_instanceConfig.Search.SearchMissing)
            await syncService.MarkRequestsAsync(_context.InstanceName, cancellationToken);

        SearchResult? searchResult = null;
        if (!_instanceConfig.ProcessingOnly && ShouldRunSearch())
        {
            if (_searchLoopCompleted && _instanceConfig.Search.SearchAgainOnSearchCompletion)
            {
                await ResetSearchedFlagsAsync(scope.ServiceProvider.GetRequiredService<TorrentarrDbContext>(), cancellationToken);
                _searchLoopCompleted = false;
            }

            // §2.7: DoUpgradeSearch is exclusive — when active, skip missing-media search
            if (_instanceConfig.Search.DoUpgradeSearch)
            {
                _logger.LogInformation("Searching for quality upgrades (exclusive) in {Instance}", _context.InstanceName);
                searchResult = await (deadline.HasValue
                        ? arrMediaService.SearchQualityUpgradesAsync(_instanceConfig.Category, deadline, cancellationToken)
                        : arrMediaService.SearchQualityUpgradesAsync(_instanceConfig.Category, cancellationToken));
            }
            else
            {
                if (_instanceConfig.Search.SearchMissing)
                {
                    _logger.LogInformation("Searching for missing media in {Instance}", _context.InstanceName);
                    searchResult = await (deadline.HasValue
                        ? arrMediaService.SearchMissingMediaAsync(_instanceConfig.Category, deadline, cancellationToken)
                        : arrMediaService.SearchMissingMediaAsync(_instanceConfig.Category, cancellationToken));
                    if (searchResult.SearchesTriggered > 0)
                        _logger.LogInformation("Triggered {Count} searches for missing media", searchResult.SearchesTriggered);
                }

                // QualityUnmetSearch / CustomFormatUnmetSearch are always additive
                if (searchResult?.BudgetExhausted != true && (_instanceConfig.Search.QualityUnmetSearch || _instanceConfig.Search.CustomFormatUnmetSearch))
                {
                    var upgradeResult = await (deadline.HasValue
                        ? arrMediaService.SearchQualityUpgradesAsync(_instanceConfig.Category, deadline, cancellationToken)
                        : arrMediaService.SearchQualityUpgradesAsync(_instanceConfig.Category, cancellationToken));
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
        _runtimeMetrics.RecordSearch(searchResult);
        if (searchResult != null)
        {
            try
            {
                var db = scope.ServiceProvider.GetRequiredService<TorrentarrDbContext>();
                var activity = await db.SearchActivity.FindAsync([_context.InstanceName], cancellationToken);
                if (activity == null)
                {
                    activity = new Torrentarr.Infrastructure.Database.Models.SearchActivity { Category = _context.InstanceName };
                    db.SearchActivity.Add(activity);
                }
                activity.Summary = _runtimeMetrics.SearchSummary!;
                activity.Timestamp = _runtimeMetrics.SearchTimestamp!;
                await db.SaveChangesWithRetryAsync(_logger, scope.ServiceProvider.GetRequiredService<DatabaseRestartCoordinator>(), cancellationToken: cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch (Exception ex) { _logger.LogWarning(ex, "Unable to persist search activity for {Instance}", _context.InstanceName); }
        }
    }

    internal static async Task LoadSearchActivityAsync(IServiceProvider services, string instance, WorkerRuntimeMetrics metrics, Serilog.ILogger logger, CancellationToken ct)
    {
        try
        {
            using var scope = services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<TorrentarrDbContext>();
            var activity = await db.SearchActivity.FindAsync([instance], ct);
            metrics.SearchSummary = activity?.Summary;
            metrics.SearchTimestamp = activity?.Timestamp;
            metrics.MetricType = activity == null ? null : "search";
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex) { logger.Warning(ex, "Unable to restore search activity for {Instance}", instance); }
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
            try
            {
                await scope.ServiceProvider.GetRequiredService<QualityProfileSwitcherService>()
                    .ForceResetAllTempProfilesAsync(_context.InstanceName, _instanceConfig, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "ForceResetTempProfiles failed for {Instance}; continuing worker startup", _context.InstanceName);
            }
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

    internal bool ShouldRunSearch()
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

sealed class WorkerRuntimeMetrics
{
    public Func<IReadOnlyList<PendingOperationalIssue>> PendingIssues { get; set; } = () => [];
    public string? InstanceName { get; set; }
    public long TorrentRevision;
    public long SearchRevision;
    private WorkerLoopProgress _torrent = new("Initializing", DateTimeOffset.UtcNow);
    private WorkerLoopProgress _search = new("Initializing", DateTimeOffset.UtcNow);
    public WorkerLoopProgress Torrent { get => Volatile.Read(ref _torrent); set => Volatile.Write(ref _torrent, value); }
    public WorkerLoopProgress Search { get => Volatile.Read(ref _search); set => Volatile.Write(ref _search, value); }
    public WorkerHeartbeat Snapshot(string instance, bool restartRequested) => new()
    {
        PendingIssues = PendingIssues(),
        Instance = instance,
        Pid = Environment.ProcessId,
        Heartbeat = DateTimeOffset.UtcNow,
        RestartRequested = restartRequested,
        ConfigRevision = Math.Min(Interlocked.Read(ref TorrentRevision), Interlocked.Read(ref SearchRevision)),
        SearchSummary = SearchSummary,
        SearchTimestamp = SearchTimestamp,
        QueueCount = QueueCount,
        CategoryCount = CategoryCount,
        MetricType = MetricType,
        Torrent = Torrent,
        Search = Search
    };

    public void RecordSearch(SearchResult? result)
    {
        if (result == null) return;
        SearchSummary = $"{result.SearchesTriggered} searches triggered ({result.ItemsSearched} items)";
        SearchTimestamp = DateTimeOffset.UtcNow.ToString("O");
        MetricType = "search";
    }

    public string? SearchSummary { get; set; }
    public string? SearchTimestamp { get; set; }
    public int? QueueCount { get; set; }
    public int? CategoryCount { get; set; }
    public string? MetricType { get; set; }
}

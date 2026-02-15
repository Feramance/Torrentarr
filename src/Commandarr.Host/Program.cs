using Commandarr.Core.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Serilog;
using System.Diagnostics;

// Configure Serilog
Log.Logger = new LoggerConfiguration()
    .WriteTo.Console()
    .WriteTo.File("logs/orchestrator.log", rollingInterval: RollingInterval.Day)
    .CreateLogger();

try
{
    Log.Information("Commandarr Host starting...");

    // Load configuration
    var configLoader = new ConfigurationLoader();
    CommandarrConfig? config = null;

    try
    {
        config = configLoader.Load();
        Log.Information("Configuration loaded successfully from {Path}", ConfigurationLoader.GetDefaultConfigPath());
    }
    catch (FileNotFoundException ex)
    {
        Log.Warning("Configuration file not found: {Message}", ex.Message);
        Log.Information("Using default configuration");
        config = new CommandarrConfig();
    }

    // Create host builder
    var builder = Host.CreateApplicationBuilder(args);

    builder.Services.AddSerilog();
    builder.Services.AddSingleton(config);
    builder.Services.AddHostedService<ProcessOrchestratorService>();

    var host = builder.Build();

    Log.Information("Starting Process Orchestrator...");
    await host.RunAsync();
}
catch (Exception ex)
{
    Log.Fatal(ex, "Host terminated unexpectedly");
    return 1;
}
finally
{
    await Log.CloseAndFlushAsync();
}

return 0;

/// <summary>
/// Background service that orchestrates all worker processes
/// </summary>
class ProcessOrchestratorService : BackgroundService
{
    private readonly ILogger<ProcessOrchestratorService> _logger;
    private readonly CommandarrConfig _config;
    private readonly Dictionary<string, Process> _processes = new();
    private readonly Dictionary<string, int> _restartCounts = new();

    public ProcessOrchestratorService(
        ILogger<ProcessOrchestratorService> logger,
        CommandarrConfig config)
    {
        _logger = logger;
        _config = config;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Process Orchestrator starting");

        try
        {
            // Start WebUI process
            if (!await StartWebUIAsync(stoppingToken))
            {
                _logger.LogError("Failed to start WebUI process");
            }

            // Start worker processes for each managed Arr instance
            foreach (var arrInstance in _config.ArrInstances.Where(x => x.Value.Managed))
            {
                if (!await StartWorkerAsync(arrInstance.Key, arrInstance.Value, stoppingToken))
                {
                    _logger.LogWarning("Failed to start worker for {Instance}", arrInstance.Key);
                }
            }

            // Monitor processes until cancellation
            while (!stoppingToken.IsCancellationRequested)
            {
                await MonitorProcessesAsync(stoppingToken);
                await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
            }
        }
        catch (OperationCanceledException)
        {
            _logger.LogInformation("Orchestrator shutting down gracefully");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error in process orchestrator");
        }
        finally
        {
            await StopAllProcessesAsync();
        }
    }

    private async Task<bool> StartWebUIAsync(CancellationToken cancellationToken)
    {
        try
        {
            var webUIPath = Path.Combine(AppContext.BaseDirectory, "Commandarr.WebUI.dll");

            if (!File.Exists(webUIPath))
            {
                _logger.LogWarning("WebUI executable not found at {Path}", webUIPath);
                return false;
            }

            var processInfo = new ProcessStartInfo
            {
                FileName = "dotnet",
                Arguments = $"\"{webUIPath}\" --urls \"http://{_config.WebUI.Host}:{_config.WebUI.Port}\"",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };

            var process = new Process { StartInfo = processInfo };

            process.OutputDataReceived += (sender, e) =>
            {
                if (!string.IsNullOrEmpty(e.Data))
                    _logger.LogInformation("[WebUI] {Output}", e.Data);
            };

            process.ErrorDataReceived += (sender, e) =>
            {
                if (!string.IsNullOrEmpty(e.Data))
                    _logger.LogError("[WebUI] {Error}", e.Data);
            };

            if (process.Start())
            {
                process.BeginOutputReadLine();
                process.BeginErrorReadLine();
                _processes["webui"] = process;
                _logger.LogInformation("WebUI started with PID {ProcessId} on {Host}:{Port}",
                    process.Id, _config.WebUI.Host, _config.WebUI.Port);
                return true;
            }

            return false;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to start WebUI");
            return false;
        }
    }

    private async Task<bool> StartWorkerAsync(string instanceName, ArrInstanceConfig instanceConfig, CancellationToken cancellationToken)
    {
        try
        {
            var workerPath = Path.Combine(AppContext.BaseDirectory, "Commandarr.Workers.dll");

            if (!File.Exists(workerPath))
            {
                _logger.LogWarning("Worker executable not found at {Path}", workerPath);
                return false;
            }

            var processInfo = new ProcessStartInfo
            {
                FileName = "dotnet",
                Arguments = $"\"{workerPath}\" --instance \"{instanceName}\"",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };

            var process = new Process { StartInfo = processInfo };

            process.OutputDataReceived += (sender, e) =>
            {
                if (!string.IsNullOrEmpty(e.Data))
                    _logger.LogInformation("[Worker-{Instance}] {Output}", instanceName, e.Data);
            };

            process.ErrorDataReceived += (sender, e) =>
            {
                if (!string.IsNullOrEmpty(e.Data))
                    _logger.LogError("[Worker-{Instance}] {Error}", instanceName, e.Data);
            };

            if (process.Start())
            {
                process.BeginOutputReadLine();
                process.BeginErrorReadLine();
                _processes[$"worker-{instanceName}"] = process;
                _logger.LogInformation("Worker started for {Instance} with PID {ProcessId}",
                    instanceName, process.Id);
                return true;
            }

            return false;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to start worker for {Instance}", instanceName);
            return false;
        }
    }

    private async Task MonitorProcessesAsync(CancellationToken cancellationToken)
    {
        var deadProcesses = new List<string>();

        foreach (var (name, process) in _processes.ToList())
        {
            if (process.HasExited)
            {
                _logger.LogWarning("Process {Name} has exited with code {ExitCode}", name, process.ExitCode);
                deadProcesses.Add(name);
            }
        }

        // Auto-restart dead processes if enabled
        if (_config.Settings.AutoRestartProcesses)
        {
            foreach (var name in deadProcesses)
            {
                _restartCounts.TryGetValue(name, out var restartCount);

                if (restartCount < _config.Settings.MaxProcessRestarts)
                {
                    _logger.LogInformation("Attempting to restart {Name} (attempt {Count}/{Max})",
                        name, restartCount + 1, _config.Settings.MaxProcessRestarts);

                    _processes.Remove(name);

                    if (name == "webui")
                    {
                        await Task.Delay(TimeSpan.FromSeconds(_config.Settings.ProcessRestartDelay), cancellationToken);
                        await StartWebUIAsync(cancellationToken);
                    }
                    else if (name.StartsWith("worker-"))
                    {
                        var instanceName = name.Substring("worker-".Length);
                        if (_config.ArrInstances.TryGetValue(instanceName, out var instanceConfig))
                        {
                            await Task.Delay(TimeSpan.FromSeconds(_config.Settings.ProcessRestartDelay), cancellationToken);
                            await StartWorkerAsync(instanceName, instanceConfig, cancellationToken);
                        }
                    }

                    _restartCounts[name] = restartCount + 1;
                }
                else
                {
                    _logger.LogError("Process {Name} has exceeded maximum restart attempts ({Max}), will not restart",
                        name, _config.Settings.MaxProcessRestarts);
                }
            }
        }
    }

    private async Task StopAllProcessesAsync()
    {
        _logger.LogInformation("Stopping all processes...");

        foreach (var (name, process) in _processes)
        {
            try
            {
                if (!process.HasExited)
                {
                    _logger.LogInformation("Stopping process {Name} (PID {ProcessId})", name, process.Id);
                    process.Kill(entireProcessTree: true);
                    await process.WaitForExitAsync();
                }
                process.Dispose();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error stopping process {Name}", name);
            }
        }

        _processes.Clear();
        _logger.LogInformation("All processes stopped");
    }
}

using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using Torrentarr.Core.Models;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Torrentarr.Core.Configuration;
using Torrentarr.Core.Interfaces;
using Torrentarr.Core.Services;

namespace Torrentarr.Infrastructure.Services;

/// <summary>Owns one OS worker process per Arr instance; WebUI stays in the host process.</summary>
public sealed class WorkerProcessSupervisor : BackgroundService, IProcessOrchestrator
{
    private readonly TorrentarrConfig _config;
    private readonly ILogger<WorkerProcessSupervisor> _logger;
    private readonly IMediaValidationService _mediaValidation;
    private readonly Dictionary<string, Process> _processes = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, List<DateTime>> _restartTimes = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, CancellationTokenSource> _pendingRetries = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<Task> _retryTasks = new();
    private readonly Func<string, ProcessStartInfo> _startInfo;
    private volatile bool _stopping;
    private readonly object _gate = new();
    private readonly SemaphoreSlim _lifecycle = new(1, 1);
    private readonly HashSet<string> _reportedWorkers = new(StringComparer.OrdinalIgnoreCase);

    public WorkerProcessSupervisor(TorrentarrConfig config, ILogger<WorkerProcessSupervisor> logger, IMediaValidationService mediaValidation)
        : this(config, logger, mediaValidation, name => ResolveWorkerStartInfo(name)) { }

    internal WorkerProcessSupervisor(TorrentarrConfig config, ILogger<WorkerProcessSupervisor> logger,
        IMediaValidationService mediaValidation, Func<string, ProcessStartInfo> startInfo)
    {
        _config = config;
        _logger = logger;
        _mediaValidation = mediaValidation;
        _startInfo = startInfo;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await EnsureFFprobeUpdatedAsync(stoppingToken);
        stoppingToken.ThrowIfCancellationRequested();
        StartWorkers();
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                MonitorWorkers();
                await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
            }
        }
        catch (OperationCanceledException) { }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        await StopWorkersAsync(cancellationToken);
        await base.StopAsync(cancellationToken);
    }

    private void StartWorkers()
    {
        foreach (var (name, config) in _config.ArrInstances)
            if (config.Managed && !string.IsNullOrWhiteSpace(config.URI) && config.URI != "CHANGE_ME")
                StartWorker(name);
    }

    async Task IProcessOrchestrator.StartAsync(CancellationToken cancellationToken)
    {
        await EnsureFFprobeUpdatedAsync(cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        _stopping = false;
        StartWorkers();
    }

    async Task IProcessOrchestrator.StopAsync(CancellationToken cancellationToken) => await StopWorkersAsync(cancellationToken);

    private async Task StopWorkersAsync(CancellationToken cancellationToken)
    {
        await _lifecycle.WaitAsync(CancellationToken.None);
        try
        {
            string[] names;
            Task[] retries;
            lock (_gate)
            {
                _stopping = true;
                foreach (var retry in _pendingRetries.Values) retry.Cancel();
                _pendingRetries.Clear();
                retries = _retryTasks.ToArray();
                names = _processes.Keys.ToArray();
            }
            foreach (var name in names)
                await StopWorkerAsync(name, cancellationToken);
            await Task.WhenAll(retries);
        }
        finally { _lifecycle.Release(); }
    }

    private async Task StopWorkerAsync(string instanceName, CancellationToken cancellationToken)
    {
        Process? process;
        lock (_gate)
        {
            CancelRetry(instanceName);
            _processes.Remove(instanceName, out process);
            _reportedWorkers.Remove(instanceName);
        }
        if (process == null) return;
        try
        {
            if (!process.HasExited)
            {
                await process.StandardInput.WriteLineAsync("shutdown");
                await process.WaitForExitAsync(cancellationToken).WaitAsync(TimeSpan.FromSeconds(10), cancellationToken);
            }
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or OperationCanceledException or TimeoutException)
        {
            if (!process.HasExited)
            {
                try { process.Kill(true); }
                catch (InvalidOperationException) { }
                await process.WaitForExitAsync(CancellationToken.None);
            }
        }
        finally { process.Dispose(); }
    }

    private void CancelRetry(string name)
    {
        if (_pendingRetries.Remove(name, out var retry)) retry.Cancel();
    }

    public async Task RestartProcessAsync(string processName, CancellationToken cancellationToken = default)
    {
        await _lifecycle.WaitAsync(cancellationToken);
        try
        {
            await StopWorkerAsync(processName, cancellationToken);
            if (!cancellationToken.IsCancellationRequested
                && _config.ArrInstances.TryGetValue(processName, out var config)
                && IsEligible(config))
                StartWorker(processName);
        }
        finally { _lifecycle.Release(); }
    }

    public Task RestartWorkerAsync(string processName) => RestartProcessAsync(processName);

    private async Task EnsureFFprobeUpdatedAsync(CancellationToken cancellationToken)
    {
        if (!_config.Settings.FFprobeAutoUpdate)
            return;

        try
        {
            await _mediaValidation.UpdateFFprobeAsync(cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "FFprobe auto-update failed before worker startup");
        }
    }
    public async Task RestartAllWorkersAsync()
    {
        string[] running;
        lock (_gate) running = _processes.Keys.Concat(_pendingRetries.Keys).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        foreach (var name in running)
            if (!_config.ArrInstances.TryGetValue(name, out var current) || !IsEligible(current))
                await StopWorkerAsync(name, CancellationToken.None);
        foreach (var (name, config) in _config.ArrInstances)
            if (IsEligible(config))
                await RestartProcessAsync(name);
    }

    public Task<Dictionary<string, ProcessStatus>> GetProcessStatusAsync()
    {
        var result = new Dictionary<string, ProcessStatus>(StringComparer.OrdinalIgnoreCase);
        lock (_gate)
        {
            foreach (var (name, process) in _processes)
                result[name] = ReadProcessStatus(name, process);
            foreach (var (name, config) in _config.ArrInstances)
                if (IsEligible(config) && !result.ContainsKey(name))
                    result[name] = new ProcessStatus
                    {
                        Name = name,
                        Kind = "worker",
                        IsAlive = false,
                        Status = _pendingRetries.ContainsKey(name) ? "Backoff" : "Stopped"
                    };
        }
        return Task.FromResult(result);
    }

    private ProcessStatus ReadProcessStatus(string name, Process process)
    {
        try
        {
            if (process.HasExited) return new ProcessStatus { Name = name, Kind = "worker", IsAlive = false, Status = "Stopped" };
            var started = process.StartTime.ToUniversalTime();
            var now = DateTimeOffset.UtcNow;
            WorkerHeartbeat? heartbeat = null;
            try
            {
                heartbeat = JsonSerializer.Deserialize<WorkerHeartbeat>(File.ReadAllText(StatusPath(name)), WorkerHeartbeat.JsonOptions);
                if (heartbeat == null || !heartbeat.Matches(name, process.Id, started)) heartbeat = null;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { }
            if (heartbeat != null) _reportedWorkers.Add(name);
            var alive = !process.HasExited && (heartbeat != null
                ? now - heartbeat.Heartbeat < TimeSpan.FromSeconds(15) && !heartbeat.IsStalled(now)
                : !_reportedWorkers.Contains(name) && now - started < WorkerLoopProgress.CycleTimeout);
            return new ProcessStatus
            {
                Name = name,
                Kind = "worker",
                ProcessId = process.HasExited ? null : process.Id,
                IsAlive = alive,
                LastHeartbeat = heartbeat?.Heartbeat.UtcDateTime,
                RestartRequested = heartbeat?.RestartRequested ?? false,
                SearchSummary = heartbeat?.SearchSummary,
                SearchTimestamp = heartbeat?.SearchTimestamp,
                QueueCount = heartbeat?.QueueCount,
                CategoryCount = heartbeat?.CategoryCount,
                MetricType = heartbeat?.MetricType,
                Status = !alive ? "Timed out" : heartbeat?.DescribeProgress() ?? "Initializing"
            };
        }
        catch (InvalidOperationException) { return new ProcessStatus { Name = name, Kind = "worker", IsAlive = false, Status = "Stopped" }; }
    }

    private void StartWorker(string instanceName)
    {
        lock (_gate)
        {
            if (_stopping || !_config.ArrInstances.TryGetValue(instanceName, out var config) || !IsEligible(config)) return;
            if (_processes.TryGetValue(instanceName, out var existing))
            {
                if (!existing.HasExited) return;
                _processes.Remove(instanceName);
                existing.Dispose();
            }
            Process? process = null;
            try
            {
                process = Process.Start(_startInfo(instanceName)) ?? throw new InvalidOperationException("Process.Start returned no process");
                var child = process;
                process.Exited += (sender, args) => { _ = Task.Run(() => WorkerExited(instanceName, child)); };
                _reportedWorkers.Remove(instanceName);
                _processes[instanceName] = process;
                process.EnableRaisingEvents = true;
            }
            catch (Exception ex)
            {
                if (process != null)
                {
                    _processes.Remove(instanceName);
                    try
                    {
                        if (!process.HasExited) process.Kill(true);
                        process.WaitForExit();
                    }
                    catch (Exception cleanupError) { _logger.LogWarning(cleanupError, "Unable to terminate failed worker launch for {Instance}", instanceName); }
                    finally { process.Dispose(); }
                }
                _logger.LogError(ex, "Unable to start worker for {Instance}", instanceName);
                ScheduleRetry(instanceName);
            }
        }
    }

    private void WorkerExited(string name, Process process)
    {
        lock (_gate)
        {
            if (!_processes.TryGetValue(name, out var current) || !ReferenceEquals(current, process)) return;
            _processes.Remove(name);
            _reportedWorkers.Remove(name);
            try { _logger.LogWarning("Worker {Instance} exited with code {Code}", name, process.ExitCode); }
            finally { process.Dispose(); }
            ScheduleRetry(name);
        }
    }

    private List<DateTime> RecentRestarts(string name)
    {
        if (!_restartTimes.TryGetValue(name, out var times)) _restartTimes[name] = times = new();
        times.RemoveAll(t => DateTime.UtcNow - t > TimeSpan.FromSeconds(_config.Settings.ProcessRestartWindow));
        return times;
    }

    private void ScheduleRetry(string name)
    {
        if (_stopping || !_config.Settings.AutoRestartProcesses || _pendingRetries.ContainsKey(name)) return;
        if (!_config.ArrInstances.TryGetValue(name, out var config) || !IsEligible(config)) return;
        var times = RecentRestarts(name);
        if (times.Count >= _config.Settings.MaxProcessRestarts)
        {
            _logger.LogWarning("Restart limit reached for {Instance}", name);
            return;
        }
        var count = times.Count;
        var delay = TimeSpan.FromSeconds(Math.Clamp(_config.Settings.ProcessRestartDelay * Math.Pow(1.5, count), 0, 1800));
        var retry = new CancellationTokenSource();
        _pendingRetries[name] = retry;
        var task = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(delay, retry.Token);
                lock (_gate)
                {
                    if (retry.IsCancellationRequested || _stopping
                        || !_pendingRetries.TryGetValue(name, out var current) || !ReferenceEquals(current, retry)) return;
                    _pendingRetries.Remove(name);
                    if (!_config.Settings.AutoRestartProcesses || !_config.ArrInstances.TryGetValue(name, out var cfg) || !IsEligible(cfg)
                        || _processes.ContainsKey(name)) return;
                    var recent = RecentRestarts(name);
                    if (recent.Count >= _config.Settings.MaxProcessRestarts) return;
                    recent.Add(DateTime.UtcNow);
                    StartWorker(name);
                }
            }
            catch (OperationCanceledException) when (retry.IsCancellationRequested) { }
            catch (Exception ex) { _logger.LogError(ex, "Unable to restart worker {Instance}", name); }
            finally
            {
                lock (_gate)
                {
                    if (_pendingRetries.TryGetValue(name, out var current) && ReferenceEquals(current, retry)) _pendingRetries.Remove(name);
                    retry.Dispose();
                }
            }
        });
        _retryTasks.Add(task);
        _ = task.ContinueWith(_ => { lock (_gate) _retryTasks.Remove(task); }, TaskScheduler.Default);
    }

    internal void MonitorWorkers()
    {
        lock (_gate)
        {
            foreach (var (name, process) in _processes.ToArray())
            {
                if (process.HasExited) continue;
                var status = ReadProcessStatus(name, process);
                if (!status.IsAlive || status.RestartRequested)
                {
                    _logger.LogWarning("Worker {Instance} timed out or requested a restart; terminating it", name);
                    try { process.Kill(true); }
                    catch (InvalidOperationException) { }
                }
            }
        }
    }

    internal static ProcessStartInfo ResolveWorkerStartInfo(string instanceName, string? baseDirectory = null)
    {
        var configured = Environment.GetEnvironmentVariable("TORRENTARR_WORKER_PATH");
        if (!string.IsNullOrWhiteSpace(configured))
            return Build(configured, instanceName);

        var baseDir = baseDirectory ?? AppContext.BaseDirectory;
        var releaseName = RuntimeInformation.ProcessArchitecture switch
        {
            Architecture.Arm64 when OperatingSystem.IsLinux() => "linux-arm64",
            Architecture.Arm64 => "osx-arm64",
            _ when OperatingSystem.IsWindows() => "windows-x64.exe",
            _ when OperatingSystem.IsMacOS() => "osx-x64",
            _ => "linux-x64"
        };
        var releasedWorker = Path.Combine(baseDir, $"torrentarr-workers-{releaseName}");
        if (File.Exists(releasedWorker)) return Build(releasedWorker, instanceName);
        var executable = Path.Combine(baseDir, OperatingSystem.IsWindows() ? "Torrentarr.Workers.exe" : "Torrentarr.Workers");
        var dll = Path.Combine(baseDir, "Torrentarr.Workers.dll");
        // A source project reference copies the apphost but can omit its DLL.
        if (File.Exists(executable) && (File.Exists(dll) || !File.Exists(Path.Combine(baseDir, "Torrentarr.Workers.runtimeconfig.json"))))
            return Build(executable, instanceName);
        if (File.Exists(dll)) return Build("dotnet", instanceName, dll);
        var configuration = new DirectoryInfo(baseDir).Parent?.Name == "Release" ? "Release" : "Debug";
        var devDll = Path.GetFullPath(Path.Combine(baseDir, "..", "..", "..", "..", "Torrentarr.Workers", "bin", configuration, "net10.0", "Torrentarr.Workers.dll"));
        return Build(File.Exists(devDll) ? "dotnet" : "Torrentarr.Workers", instanceName, File.Exists(devDll) ? devDll : null);
    }

    private static ProcessStartInfo Build(string executable, string instance, string? dll = null)
    {
        var info = new ProcessStartInfo(executable) { WorkingDirectory = Directory.GetCurrentDirectory(), UseShellExecute = false, RedirectStandardInput = true, RedirectStandardOutput = false, RedirectStandardError = false };
        info.Environment["TORRENTARR_CONFIG"] = Path.GetFullPath(ConfigurationLoader.GetDefaultConfigPath());
        if (dll != null) info.ArgumentList.Add(dll);
        info.ArgumentList.Add("--instance");
        info.ArgumentList.Add(instance);
        info.ArgumentList.Add("--parent-pid");
        info.ArgumentList.Add(Environment.ProcessId.ToString());
        info.ArgumentList.Add("--status-path");
        info.ArgumentList.Add(StatusPath(instance));
        return info;
    }

    private static string StatusPath(string instance) => Path.Combine(ConfigurationLoader.GetDataDirectoryPath(), "workers", instance + ".json");

    private static bool IsEligible(ArrInstanceConfig config) =>
        config.Managed && !string.IsNullOrWhiteSpace(config.URI) && config.URI != "CHANGE_ME";
}

using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Torrentarr.Core.Configuration;
using Torrentarr.Core.Interfaces;

namespace Torrentarr.Infrastructure.Services;

/// <summary>Owns one OS worker process per Arr instance; WebUI stays in the host process.</summary>
public sealed class WorkerProcessSupervisor : BackgroundService, IProcessOrchestrator
{
    private readonly TorrentarrConfig _config;
    private readonly ILogger<WorkerProcessSupervisor> _logger;
    private readonly Dictionary<string, Process> _processes = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, int> _restartCounts = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, List<DateTime>> _restartTimes = new(StringComparer.OrdinalIgnoreCase);
    private volatile bool _stopping;
    private readonly object _gate = new();

    public WorkerProcessSupervisor(TorrentarrConfig config, ILogger<WorkerProcessSupervisor> logger)
    {
        _config = config;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
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

    async Task IProcessOrchestrator.StartAsync(CancellationToken cancellationToken) { StartWorkers(); await Task.CompletedTask; }

    async Task IProcessOrchestrator.StopAsync(CancellationToken cancellationToken) => await StopWorkersAsync(cancellationToken);

    private async Task StopWorkersAsync(CancellationToken cancellationToken)
    {
        _stopping = true;
        string[] names;
        lock (_gate) names = _processes.Keys.ToArray();
        foreach (var name in names)
            await StopWorkerAsync(name, cancellationToken);
        lock (_gate)
            _processes.Clear();
    }

    private async Task StopWorkerAsync(string instanceName, CancellationToken cancellationToken)
    {
        Process? process;
        lock (_gate)
        {
            _processes.Remove(instanceName, out process);
        }
        if (process == null)
            return;

        try
        {
            if (!process.HasExited)
            {
                await process.StandardInput.WriteLineAsync("shutdown");
                await process.WaitForExitAsync(cancellationToken).WaitAsync(TimeSpan.FromSeconds(10), cancellationToken);
            }
        }
        catch
        {
            try { if (!process.HasExited) process.Kill(true); } catch { }
        }
        process.Dispose();
    }

    public async Task RestartProcessAsync(string processName, CancellationToken cancellationToken = default)
    {
        await StopWorkerAsync(processName, cancellationToken);
        if (!cancellationToken.IsCancellationRequested
            && _config.ArrInstances.TryGetValue(processName, out var config)
            && IsEligible(config))
            StartWorker(processName);
    }

    public Task RestartWorkerAsync(string processName) => RestartProcessAsync(processName);
    public async Task RestartAllWorkersAsync()
    {
        string[] running;
        lock (_gate) running = _processes.Keys.ToArray();
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
            {
                var heartbeat = StatusPath(name);
                var lastHeartbeat = File.Exists(heartbeat) ? File.GetLastWriteTimeUtc(heartbeat) : (DateTime?)null;
                var restartRequested = false;
                if (File.Exists(heartbeat))
                {
                    try
                    {
                        using var doc = JsonDocument.Parse(File.ReadAllText(heartbeat));
                        restartRequested = doc.RootElement.TryGetProperty("restartRequested", out var value) && value.GetBoolean();
                    }
                    catch { }
                }
                var started = process.StartTime.ToUniversalTime();
                var alive = !process.HasExited && (DateTime.UtcNow - started < TimeSpan.FromSeconds(15)
                    || (lastHeartbeat != null && lastHeartbeat >= started && DateTime.UtcNow - lastHeartbeat < TimeSpan.FromSeconds(15)));
                result[name] = new ProcessStatus { Name = name, Kind = "worker", ProcessId = alive ? process.Id : null, IsAlive = alive, LastHeartbeat = lastHeartbeat, RestartRequested = restartRequested };
            }
            foreach (var (name, config) in _config.ArrInstances)
                if (IsEligible(config) && !result.ContainsKey(name))
                    result[name] = new ProcessStatus { Name = name, Kind = "worker", IsAlive = false };
        }
        return Task.FromResult(result);
    }

    private void StartWorker(string instanceName)
    {
        lock (_gate)
        {
            if (_stopping) return;
            if (_processes.TryGetValue(instanceName, out var existing) && !existing.HasExited) return;
            try
            {
                var process = Process.Start(ResolveWorkerStartInfo(instanceName));
                if (process == null)
                {
                    _logger.LogError("Unable to start worker for {Instance}", instanceName);
                    return;
                }
                process.Exited += (_, _) => _ = RestartAfterCrashAsync(instanceName, process);
                _processes[instanceName] = process;
                process.EnableRaisingEvents = true;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Unable to start worker for {Instance}", instanceName);
            }
        }
    }

    private async Task RestartAfterCrashAsync(string instanceName, Process process)
    {
        var count = 0;
        lock (_gate)
        {
            if (!_processes.TryGetValue(instanceName, out var current) || !ReferenceEquals(current, process))
                return;
            _processes.Remove(instanceName);
            var now = DateTime.UtcNow;
            if (_restartTimes.TryGetValue(instanceName, out var times))
            {
                times.RemoveAll(t => now - t > TimeSpan.FromSeconds(_config.Settings.ProcessRestartWindow));
                if (times.Count == 0)
                    _restartCounts.Remove(instanceName);
            }
            count = _restartCounts.TryGetValue(instanceName, out var previous) ? previous + 1 : 1;
            _restartCounts[instanceName] = count;
        }
        _logger.LogWarning("Worker {Instance} exited with code {Code}; restart #{Count}", instanceName, process.ExitCode, count);
        try
        {
            var delaySeconds = _config.Settings.ProcessRestartDelay * Math.Pow(1.5, count - 1);
            await Task.Delay(TimeSpan.FromSeconds(Math.Min(TimeSpan.FromMinutes(30).TotalSeconds, Math.Max(0, delaySeconds))));
            if (!_stopping && _config.Settings.AutoRestartProcesses && CanRestart(instanceName)
                && _config.ArrInstances.TryGetValue(instanceName, out var config) && IsEligible(config))
                StartWorker(instanceName);
        }
        catch (Exception ex) { _logger.LogError(ex, "Unable to restart worker {Instance}", instanceName); }
    }

    private bool CanRestart(string instanceName)
    {
        lock (_gate)
        {
            var now = DateTime.UtcNow;
            if (!_restartTimes.TryGetValue(instanceName, out var times))
                _restartTimes[instanceName] = times = new List<DateTime>();
            times.RemoveAll(t => now - t > TimeSpan.FromSeconds(_config.Settings.ProcessRestartWindow));
            if (times.Count == 0)
                _restartCounts.Remove(instanceName);
            if (times.Count >= _config.Settings.MaxProcessRestarts)
            {
                _logger.LogWarning("Restart limit reached for {Instance}", instanceName);
                return false;
            }
            times.Add(now);
            return true;
        }
    }

    private void MonitorWorkers()
    {
        lock (_gate)
        {
            foreach (var (name, process) in _processes)
            {
                if (process.HasExited)
                    continue;
                var path = StatusPath(name);
                if (DateTime.UtcNow - process.StartTime.ToUniversalTime() >= TimeSpan.FromSeconds(15)
                    && (!File.Exists(path) || DateTime.UtcNow - File.GetLastWriteTimeUtc(path) > TimeSpan.FromSeconds(15)))
                {
                    _logger.LogWarning("Worker {Instance} heartbeat is stale; terminating it", name);
                    try { process.Kill(true); } catch { }
                }
            }
        }
    }

    private static ProcessStartInfo ResolveWorkerStartInfo(string instanceName)
    {
        var configured = Environment.GetEnvironmentVariable("TORRENTARR_WORKER_PATH");
        if (!string.IsNullOrWhiteSpace(configured))
            return Build(configured, instanceName, Path.GetDirectoryName(configured));

        var baseDir = AppContext.BaseDirectory;
        var releaseName = RuntimeInformation.ProcessArchitecture switch
        {
            Architecture.Arm64 when OperatingSystem.IsLinux() => "linux-arm64",
            Architecture.Arm64 => "osx-arm64",
            _ when OperatingSystem.IsWindows() => "windows-x64.exe",
            _ when OperatingSystem.IsMacOS() => "osx-x64",
            _ => "linux-x64"
        };
        var releasedWorker = Path.Combine(baseDir, $"torrentarr-workers-{releaseName}");
        if (File.Exists(releasedWorker)) return Build(releasedWorker, instanceName, baseDir);
        var executable = Path.Combine(baseDir, OperatingSystem.IsWindows() ? "Torrentarr.Workers.exe" : "Torrentarr.Workers");
        if (File.Exists(executable)) return Build(executable, instanceName, baseDir);
        var dll = Path.Combine(baseDir, "Torrentarr.Workers.dll");
        if (File.Exists(dll)) return Build("dotnet", instanceName, baseDir, dll);
        var devDll = Path.GetFullPath(Path.Combine(baseDir, "..", "..", "..", "..", "Torrentarr.Workers", "bin", "Debug", "net10.0", "Torrentarr.Workers.dll"));
        return Build(File.Exists(devDll) ? "dotnet" : "Torrentarr.Workers", instanceName, Path.GetDirectoryName(devDll), File.Exists(devDll) ? devDll : null);
    }

    private static ProcessStartInfo Build(string executable, string instance, string? workingDirectory, string? dll = null)
    {
        var info = new ProcessStartInfo(executable) { WorkingDirectory = workingDirectory ?? AppContext.BaseDirectory, UseShellExecute = false, RedirectStandardInput = true, RedirectStandardOutput = false, RedirectStandardError = false };
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

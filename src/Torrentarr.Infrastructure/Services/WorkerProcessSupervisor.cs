using System.Diagnostics;
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
        Process[] processes;
        lock (_gate) processes = _processes.Values.ToArray();
        foreach (var process in processes)
        {
            try
            {
                if (!process.HasExited)
                {
                    await process.StandardInput.WriteLineAsync("shutdown");
                    await process.WaitForExitAsync(cancellationToken).WaitAsync(TimeSpan.FromSeconds(10), cancellationToken);
                }
            }
            catch { try { if (!process.HasExited) process.Kill(true); } catch { } }
            process.Dispose();
        }
        lock (_gate) _processes.Clear();
    }

    public async Task RestartProcessAsync(string processName, CancellationToken cancellationToken = default)
    {
        StopWorker(processName);
        await Task.Yield();
        if (!cancellationToken.IsCancellationRequested) StartWorker(processName);
    }

    public Task RestartWorkerAsync(string processName) => RestartProcessAsync(processName);
    public async Task RestartAllWorkersAsync()
    {
        foreach (var (name, config) in _config.ArrInstances)
            if (IsEligible(config))
                await RestartProcessAsync(name);
    }

    public Task<Dictionary<string, ProcessStatus>> GetProcessStatusAsync()
    {
        var result = new Dictionary<string, ProcessStatus>(StringComparer.OrdinalIgnoreCase);
        lock (_gate)
        foreach (var (name, process) in _processes)
        {
            var heartbeat = StatusPath(name);
            var lastHeartbeat = File.Exists(heartbeat) ? File.GetLastWriteTimeUtc(heartbeat) : (DateTime?)null;
            var started = process.StartTime.ToUniversalTime();
            var alive = !process.HasExited && (DateTime.UtcNow - started < TimeSpan.FromSeconds(15)
                || (lastHeartbeat != null && lastHeartbeat >= started && DateTime.UtcNow - lastHeartbeat < TimeSpan.FromSeconds(15)));
            result[name] = new ProcessStatus { Name = name, Kind = "worker", ProcessId = alive ? process.Id : null, IsAlive = alive, LastHeartbeat = lastHeartbeat };
        }
        return Task.FromResult(result);
    }

    private void StartWorker(string instanceName)
    {
        lock (_gate)
        {
            if (_processes.TryGetValue(instanceName, out var existing) && !existing.HasExited) return;
            var start = ResolveWorkerStartInfo(instanceName);
            var process = Process.Start(start);
            if (process == null) throw new InvalidOperationException($"Unable to start worker for {instanceName}");
            process.EnableRaisingEvents = true;
            process.Exited += (_, _) => _ = RestartAfterCrashAsync(instanceName, process);
            _processes[instanceName] = process;
        }
    }

    private async Task RestartAfterCrashAsync(string instanceName, Process process)
    {
        var count = 0;
        lock (_gate)
        {
            if (_processes.TryGetValue(instanceName, out var current) && ReferenceEquals(current, process))
                _processes.Remove(instanceName);
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
        foreach (var (name, process) in _processes.ToArray())
        {
            if (process.HasExited)
                continue;
            var path = StatusPath(name);
            if (File.Exists(path) && DateTime.UtcNow - process.StartTime.ToUniversalTime() >= TimeSpan.FromSeconds(15)
                && DateTime.UtcNow - File.GetLastWriteTimeUtc(path) > TimeSpan.FromSeconds(15))
            {
                _logger.LogWarning("Worker {Instance} heartbeat is stale; terminating it", name);
                try { process.Kill(true); } catch { }
            }
        }
    }

    private void StopWorker(string instanceName)
    {
        lock (_gate)
        {
            if (!_processes.Remove(instanceName, out var process)) return;
            try { if (!process.HasExited) process.Kill(true); } catch { }
            process.Dispose();
        }
    }

    private static ProcessStartInfo ResolveWorkerStartInfo(string instanceName)
    {
        var configured = Environment.GetEnvironmentVariable("TORRENTARR_WORKER_PATH");
        if (!string.IsNullOrWhiteSpace(configured))
            return Build(configured, instanceName, Path.GetDirectoryName(configured));

        var baseDir = AppContext.BaseDirectory;
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

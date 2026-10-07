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
        try { await Task.Delay(Timeout.InfiniteTimeSpan, stoppingToken); }
        catch (OperationCanceledException) { }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        await StopWorkersAsync(cancellationToken);
        await base.StopAsync(cancellationToken);
    }

    private void StartWorkers()
    {
        foreach (var name in _config.ArrInstances.Keys)
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
        foreach (var name in _config.ArrInstances.Keys)
            await RestartProcessAsync(name);
    }

    public Task<Dictionary<string, ProcessStatus>> GetProcessStatusAsync()
    {
        var result = new Dictionary<string, ProcessStatus>(StringComparer.OrdinalIgnoreCase);
        lock (_gate)
        foreach (var (name, process) in _processes)
            result[name] = new ProcessStatus { Name = name, Kind = "worker", ProcessId = process.HasExited ? null : process.Id, IsAlive = !process.HasExited };
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
            await Task.Delay(TimeSpan.FromSeconds(Math.Min(30, 2 * Math.Pow(1.5, count - 1))));
            if (!_stopping && _config.ArrInstances.ContainsKey(instanceName)) StartWorker(instanceName);
        }
        catch (Exception ex) { _logger.LogError(ex, "Unable to restart worker {Instance}", instanceName); }
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
        info.ArgumentList.Add(Path.Combine(ConfigurationLoader.GetDataDirectoryPath(), "workers", instance + ".json"));
        return info;
    }
}

using System.Diagnostics;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Torrentarr.Core.Configuration;
using Torrentarr.Core.Interfaces;
using Torrentarr.Core.Models;
using Torrentarr.Core.Services;
using Torrentarr.Infrastructure.Services;
using Xunit;

namespace Torrentarr.Infrastructure.Tests.Services;

public class WorkerLifecycleTests
{
    [Fact]
    public void Heartbeat_RejectsPriorProcessAndDetectsStuckLoopDespiteFreshPublisher()
    {
        var now = DateTimeOffset.UtcNow;
        var heartbeat = new WorkerHeartbeat
        {
            Instance = "Radarr",
            Pid = 12,
            Heartbeat = now,
            Torrent = new("Running", now.AddMinutes(-31)),
            Search = new("Sleeping", now.AddHours(-1), now.AddMinutes(5))
        };
        heartbeat.Matches("Radarr", 13, now.AddSeconds(-1)).Should().BeFalse();
        heartbeat.Matches("Sonarr", 12, now.AddSeconds(-1)).Should().BeFalse();
        heartbeat.Matches("Radarr", 12, now.AddSeconds(1)).Should().BeFalse();
        heartbeat.Matches("radarr", 12, now.AddSeconds(-1)).Should().BeTrue();
        heartbeat.IsStalled(now).Should().BeTrue();
        heartbeat.Search!.IsStalled(now).Should().BeFalse();
        heartbeat.Search.IsStalled(now.AddMinutes(6)).Should().BeTrue();
    }

    [Fact]
    public async Task FailedLaunch_RetriesToLimit_AndStopsCleanly()
    {
        var attempts = 0;
        var config = Config();
        config.Settings.ProcessRestartDelay = 0;
        config.Settings.MaxProcessRestarts = 2;
        using var supervisor = new WorkerProcessSupervisor(config, NullLogger<WorkerProcessSupervisor>.Instance,
            Mock.Of<IMediaValidationService>(), _ => { Interlocked.Increment(ref attempts); throw new IOException("launch failed"); });
        var orchestrator = (IProcessOrchestrator)supervisor;
        await orchestrator.StartAsync();
        await WaitUntil(() => Volatile.Read(ref attempts) == 3);
        await orchestrator.StopAsync();
        attempts.Should().Be(3);
        (await supervisor.GetProcessStatusAsync())["Radarr"].Status.Should().Be("Stopped");
    }

    [Fact]
    public async Task ReconcileConfiguration_SameRevision_DoesNotBypassRestartBackoff()
    {
        var attempts = 0;
        var config = Config();
        config.Apply(config, 2);
        config.Settings.ProcessRestartDelay = 60;
        using var supervisor = new WorkerProcessSupervisor(config, NullLogger<WorkerProcessSupervisor>.Instance,
            Mock.Of<IMediaValidationService>(), _ => { Interlocked.Increment(ref attempts); throw new IOException("launch failed"); });
        var orchestrator = (IProcessOrchestrator)supervisor;
        try
        {
            await supervisor.ReconcileConfigurationAsync();
            await supervisor.ReconcileConfigurationAsync();
            attempts.Should().Be(1);
            (await supervisor.GetProcessStatusAsync())["Radarr"].Status.Should().Be("Backoff");
        }
        finally { await orchestrator.StopAsync(); }
    }

    [Fact]
    public async Task ManualRestart_CancelsOldRetryWithoutChargingAllowance()
    {
        if (OperatingSystem.IsWindows()) return;
        var attempts = 0;
        var config = Config();
        config.Settings.ProcessRestartDelay = 1;
        config.Settings.MaxProcessRestarts = 1;
        using var supervisor = new WorkerProcessSupervisor(config, NullLogger<WorkerProcessSupervisor>.Instance,
            Mock.Of<IMediaValidationService>(), _ =>
            {
                if (Interlocked.Increment(ref attempts) <= 2) throw new IOException("launch failed");
                return new ProcessStartInfo("/bin/sh") { ArgumentList = { "-c", "read command" }, UseShellExecute = false, RedirectStandardInput = true };
            });
        var orchestrator = (IProcessOrchestrator)supervisor;
        await orchestrator.StartAsync();
        try
        {
            await supervisor.RestartProcessAsync("Radarr");
            await WaitUntil(() => Volatile.Read(ref attempts) == 3);
            (await supervisor.GetProcessStatusAsync())["Radarr"].IsAlive.Should().BeTrue();
        }
        finally { await orchestrator.StopAsync(); }
        attempts.Should().Be(3);
    }

    [Fact]
    public async Task ConcurrentManualRestarts_LeaveOnlyOneOwnedChild_AndShutdownWaitsForExit()
    {
        if (OperatingSystem.IsWindows()) return;
        var children = new List<int>();
        var config = Config();
        using var supervisor = new WorkerProcessSupervisor(config, NullLogger<WorkerProcessSupervisor>.Instance,
            Mock.Of<IMediaValidationService>(), _ => new ProcessStartInfo("/bin/sh")
            {
                ArgumentList = { "-c", "read command" },
                UseShellExecute = false,
                RedirectStandardInput = true
            });
        var orchestrator = (IProcessOrchestrator)supervisor;
        await orchestrator.StartAsync();
        children.Add((await supervisor.GetProcessStatusAsync())["Radarr"].ProcessId!.Value);
        try
        {
            await Task.WhenAll(supervisor.RestartProcessAsync("Radarr"), supervisor.RestartProcessAsync("Radarr"));
            children.Add((await supervisor.GetProcessStatusAsync())["Radarr"].ProcessId!.Value);
        }
        finally { await orchestrator.StopAsync(); }
        foreach (var pid in children)
        {
            try { using var process = Process.GetProcessById(pid); process.HasExited.Should().BeTrue(); }
            catch (ArgumentException) { }
        }
    }

    [Fact]
    public async Task ImmediateChildExit_CannotDisposeProcessDuringStatusRead()
    {
        if (OperatingSystem.IsWindows()) return;
        var config = Config();
        config.Settings.AutoRestartProcesses = false;
        using var supervisor = new WorkerProcessSupervisor(config, NullLogger<WorkerProcessSupervisor>.Instance,
            Mock.Of<IMediaValidationService>(), _ => new ProcessStartInfo("/bin/true")
            {
                UseShellExecute = false,
                RedirectStandardInput = true
            });
        var orchestrator = (IProcessOrchestrator)supervisor;
        try
        {
            for (var attempt = 0; attempt < 100; attempt++)
            {
                await supervisor.RestartProcessAsync("Radarr");
                await supervisor.GetProcessStatusAsync();
                supervisor.MonitorWorkers();
            }
        }
        finally { await orchestrator.StopAsync(); }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task PendingRetry_IsCancelledByConfigRemovalOrShutdown(bool removeInstance)
    {
        var attempts = 0;
        var config = Config();
        config.Settings.ProcessRestartDelay = 1;
        using var supervisor = new WorkerProcessSupervisor(config, NullLogger<WorkerProcessSupervisor>.Instance,
            Mock.Of<IMediaValidationService>(), _ => { Interlocked.Increment(ref attempts); throw new IOException("launch failed"); });
        var orchestrator = (IProcessOrchestrator)supervisor;
        await orchestrator.StartAsync();
        if (removeInstance)
        {
            config.ArrInstances.Clear();
            await supervisor.RestartAllWorkersAsync();
        }
        await orchestrator.StopAsync();
        await Task.Delay(1100);
        attempts.Should().Be(1);
    }

    [Fact]
    public async Task CrashedChild_UsesTheSameBoundedRetryPolicyAsLaunchFailure()
    {
        if (OperatingSystem.IsWindows()) return;
        var attempts = 0;
        var config = Config();
        config.Settings.ProcessRestartDelay = 0;
        config.Settings.MaxProcessRestarts = 2;
        using var supervisor = new WorkerProcessSupervisor(config, NullLogger<WorkerProcessSupervisor>.Instance,
            Mock.Of<IMediaValidationService>(), _ =>
            {
                Interlocked.Increment(ref attempts);
                return new ProcessStartInfo("/bin/false") { UseShellExecute = false, RedirectStandardInput = true };
            });
        var orchestrator = (IProcessOrchestrator)supervisor;
        await orchestrator.StartAsync();
        try { await WaitUntil(() => Volatile.Read(ref attempts) == 3); }
        finally { await orchestrator.StopAsync(); }
        attempts.Should().Be(3);
    }


    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RemovedWorker_IsRetriedAfterAcknowledgment_UnlessRestored(bool restored)
    {
        if (OperatingSystem.IsWindows()) return;
        var config = Config();
        config.Apply(config, 2);
        using var supervisor = new WorkerProcessSupervisor(config, NullLogger<WorkerProcessSupervisor>.Instance,
            Mock.Of<IMediaValidationService>(), _ => new ProcessStartInfo("/bin/sh")
            { ArgumentList = { "-c", "read command" }, UseShellExecute = false, RedirectStandardInput = true });
        var orchestrator = (IProcessOrchestrator)supervisor;
        var status = Path.GetTempFileName();
        await orchestrator.StartAsync();
        try
        {
            var pid = (await supervisor.GetProcessStatusAsync())["Radarr"].ProcessId!.Value;
            var files = (Dictionary<string, string>)typeof(WorkerProcessSupervisor)
                .GetField("_statusFiles", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(supervisor)!;
            files["Radarr"] = status;
            var instance = config.ArrInstances["Radarr"];
            config.ArrInstances.Clear();
            await supervisor.StopRemovedWorkersAsync(default);
            (await supervisor.GetProcessStatusAsync()).Should().ContainKey("Radarr");
            if (restored) config.ArrInstances["Radarr"] = instance;
            await File.WriteAllTextAsync(status, System.Text.Json.JsonSerializer.Serialize(new WorkerHeartbeat
            { Instance = "Radarr", Pid = pid, Heartbeat = DateTimeOffset.UtcNow, ConfigRevision = 2 }, WorkerHeartbeat.JsonOptions));
            await supervisor.StopRemovedWorkersAsync(default);
            (await supervisor.GetProcessStatusAsync()).ContainsKey("Radarr").Should().Be(restored);
        }
        finally { await orchestrator.StopAsync(); File.Delete(status); }
    }

    private static TorrentarrConfig Config()
    {
        var config = new TorrentarrConfig();
        config.Settings.FFprobeAutoUpdate = false;
        config.Settings.AutoRestartProcesses = true;
        config.Settings.ProcessRestartWindow = 3600;
        config.ArrInstances["Radarr"] = new ArrInstanceConfig { Managed = true, URI = "http://127.0.0.1:1" };
        return config;
    }

    private static async Task WaitUntil(Func<bool> ready)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!ready()) await Task.Delay(20, timeout.Token);
    }
}

using System.Reflection;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Newtonsoft.Json;
using Torrentarr.Core.Configuration;
using Torrentarr.Core.Services;
using Torrentarr.Infrastructure.Services;
using Xunit;

namespace Torrentarr.Infrastructure.Tests.Services;

[Collection(nameof(ConfigReloaderTests))]
public sealed class LiveConfigurationTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "torrentarr-live-" + Guid.NewGuid().ToString("N"));
    private readonly TorrentarrConfig _config = new();
    private readonly ConfigurationLoader _loader;
    public LiveConfigurationTests()
    {
        Directory.CreateDirectory(_directory);
        _loader = new(Path.Combine(_directory, "config.toml"));
        _loader.SaveConfig(_config);
    }
    private ConfigReloader Create(bool worker = false, TorrentarrConfig? config = null) =>
        new(NullLogger<ConfigReloader>.Instance, config ?? _config, _loader, worker);
    private static TorrentarrConfig Candidate(string threshold) => new() { Settings = new() { FreeSpace = threshold } };

    [Fact]
    public async Task OperationKeepsItsSnapshotWhileNextOperationSeesNewRevision()
    {
        using var live = Create();
        live.Apply(Candidate("1G")).Should().BeTrue();
        var revision = live.Revision;
        using (_config.BeginOperation())
        {
            live.Apply(Candidate("1T")).Should().BeTrue();
            await Task.Yield();
            _config.Settings.FreeSpace.Should().Be("1G");
            _config.Revision.Should().Be(revision);
        }
        _config.Settings.FreeSpace.Should().Be("1T");
        _config.Revision.Should().BeGreaterThan(revision);
    }

    [Theory]
    [InlineData("garbage")]
    [InlineData("9223372036854775807T")]
    [InlineData("-2G")]
    public void InvalidThresholdRetainsWorkingConfiguration(string value)
    {
        using var live = Create();
        live.Apply(Candidate("2G")).Should().BeTrue();
        var revision = live.Revision;
        live.Apply(Candidate(value)).Should().BeFalse();
        live.Revision.Should().Be(revision);
        _config.Settings.FreeSpace.Should().Be("2G");
        live.GetStatus().State.Should().Be("error");
    }

    [Fact]
    public void SameConfigurationIsDeduplicatedAndPendingComponentsAreReported()
    {
        using var live = Create();
        live.Apply(Candidate("1T")).Should().BeTrue();
        var revision = live.Revision;
        live.Apply(Candidate("1T")).Should().BeTrue();
        live.Revision.Should().Be(revision);
        live.Acknowledge("worker:movies", 0);
        live.GetStatus().State.Should().Be("pending");
        live.Acknowledge("worker:movies", revision);
        live.GetStatus().State.Should().Be("applied");
    }

    [Fact]
    public async Task ChangeWakesAnHourLongWaitAndSnapshotIsPrivate()
    {
        using var live = Create();
        live.Apply(Candidate("1G"));
        var waiting = live.WaitForChangeAsync(live.Revision, TimeSpan.FromHours(1), CancellationToken.None);
        var next = Candidate("1T");
        next.WebUI.Token = "never-return-this-token";
        live.Apply(next).Should().BeTrue();
        await waiting.WaitAsync(TimeSpan.FromSeconds(2));
        JsonConvert.SerializeObject(live.GetStatus()).Should().NotContain(next.WebUI.Token);
        if (!OperatingSystem.IsWindows())
            File.GetUnixFileMode(live.SnapshotPath).Should().Be(UnixFileMode.UserRead | UnixFileMode.UserWrite);
    }

    [Fact]
    public async Task PollingDetectsAtomicEditsAndRetainsConfigurationOnPartialWrites()
    {
        using var live = Create();
        await live.StartAsync(CancellationToken.None);
        try
        {
            // Deliberately disable native events to exercise mounted-file fallback.
            var watcher = (FileSystemWatcher?)typeof(ConfigReloader).GetField("_watcher", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(live);
            if (watcher != null) watcher.EnableRaisingEvents = false;
            ConfigMutationGuard.WriteAtomic(_loader.ConfigPath, "[Settings]\nConfigVersion = \"6.18.0\"\nFreeSpace = \"1T\"\n");
            await UntilAsync(() => _config.Settings.FreeSpace == "1T");
            var revision = live.Revision;
            File.WriteAllText(_loader.ConfigPath, "[Settings]\nFreeSpace = \"secret-unfinished");
            await UntilAsync(() => live.GetStatus().State == "error");
            _config.Revision.Should().Be(revision);
            JsonConvert.SerializeObject(live.GetStatus()).Should().NotContain("secret-unfinished");
            ConfigMutationGuard.WriteAtomic(_loader.ConfigPath, "[Settings]\nFreeSpace = \"2G\"\n");
            await UntilAsync(() => _config.Settings.FreeSpace == "2G");
        }
        finally { await live.StopAsync(CancellationToken.None); }
    }

    [Fact]
    public void WorkerConsumesAcceptedSnapshotAndRenameChainsWithoutReadingRawFile()
    {
        using var host = Create();
        host.Apply(Candidate("1G"), new() { ["Radarr-A"] = "Radarr-B" });
        var workerConfig = new TorrentarrConfig();
        using var worker = Create(true, workerConfig);
        File.WriteAllText(_loader.ConfigPath, "invalid TOML [");
        worker.ReloadConfig().Should().BeTrue();
        workerConfig.Settings.FreeSpace.Should().Be("1G");
        worker.ResolveInstanceName("Radarr-A").Should().Be("Radarr-B");
        host.Apply(Candidate("2G"), new() { ["Radarr-B"] = "Radarr-A" });
        worker.ReloadConfig().Should().BeTrue();
        worker.ResolveInstanceName("Radarr-A").Should().Be("Radarr-A");
        workerConfig.Settings.FreeSpace.Should().Be("2G");
    }

    [Fact]
    public void ReusedSectionNameDoesNotInheritAnOlderWorkersRename()
    {
        using var host = Create();
        host.Apply(Candidate("1G"), new() { ["Radarr-A"] = "Radarr-B" });
        var newborn = Candidate("1G");
        newborn.Apply(newborn, host.Revision);
        using var worker = Create(true, newborn);
        worker.ReloadConfig().Should().BeTrue();
        worker.ResolveInstanceName("Radarr-A").Should().Be("Radarr-A");
        host.Apply(Candidate("2G"), new() { ["Radarr-B"] = "Radarr-C" });
        worker.ReloadConfig().Should().BeTrue();
        worker.ResolveInstanceName("Radarr-A").Should().Be("Radarr-A");
        host.Apply(Candidate("3G"), new() { ["Radarr-A"] = "Radarr-D" });
        worker.ReloadConfig().Should().BeTrue();
        worker.ResolveInstanceName("Radarr-A").Should().Be("Radarr-D");
    }

    [Fact]
    public async Task RealWorkerAcknowledgesChangesAndRenamesWithoutChangingPid()
    {
        using var live = Create();
        var initial = Candidate("1G");
        initial.Settings.FFprobeAutoUpdate = false;
        initial.Settings.ConsoleLevel = "ERROR";
        initial.Settings.LoopSleepTimer = 3600;
        initial.ArrInstances["Radarr-A"] = new() { Type = "radarr", URI = "http://127.0.0.1:1", Category = "movies", Managed = false };
        _loader.SaveConfig(initial);
        live.Apply(initial).Should().BeTrue();
        var statusPath = Path.Combine(_directory, "heartbeat.json");
        var info = new System.Diagnostics.ProcessStartInfo("dotnet")
        {
            UseShellExecute = false,
            RedirectStandardInput = true,
            WorkingDirectory = _directory
        };
        info.ArgumentList.Add(typeof(ArrWorkerService).Assembly.Location);
        foreach (var arg in new[] { "--instance", "Radarr-A", "--status-path", statusPath,
            "--parent-pid", Environment.ProcessId.ToString() }) info.ArgumentList.Add(arg);
        info.Environment["TORRENTARR_CONFIG"] = _loader.ConfigPath;
        using var child = System.Diagnostics.Process.Start(info)!;
        try
        {
            async Task WaitForRevision(long revision, string name)
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
                while (true)
                {
                    child.HasExited.Should().BeFalse();
                    try
                    {
                        var status = System.Text.Json.JsonSerializer.Deserialize<Torrentarr.Core.Models.WorkerHeartbeat>(
                            File.ReadAllText(statusPath), Torrentarr.Core.Models.WorkerHeartbeat.JsonOptions);
                        if (status?.ConfigRevision >= revision && status.Instance == name)
                        {
                            status.Pid.Should().Be(child.Id);
                            return;
                        }
                    }
                    catch (IOException) { }
                    await Task.Delay(50, timeout.Token);
                }
            }
            await WaitForRevision(live.Revision, "Radarr-A");
            var next = JsonConvert.DeserializeObject<TorrentarrConfig>(JsonConvert.SerializeObject(initial))!;
            next.Settings.FreeSpace = "1T";
            next.ArrInstances["Radarr-B"] = next.ArrInstances["Radarr-A"];
            next.ArrInstances.Remove("Radarr-A");
            live.Apply(next, new() { ["Radarr-A"] = "Radarr-B" }).Should().BeTrue();
            await WaitForRevision(live.Revision, "Radarr-B");
            child.HasExited.Should().BeFalse();
        }
        finally
        {
            if (!child.HasExited)
            {
                await child.StandardInput.WriteLineAsync("shutdown");
                try { await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5)); }
                catch (TimeoutException) { child.Kill(true); await child.WaitForExitAsync(); }
            }
        }
    }

    private static async Task UntilAsync(Func<bool> predicate)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        while (!predicate()) await Task.Delay(20, timeout.Token);
    }
    public void Dispose() => Directory.Delete(_directory, true);
}

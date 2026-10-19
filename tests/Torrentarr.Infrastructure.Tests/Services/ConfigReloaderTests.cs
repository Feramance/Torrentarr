using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Torrentarr.Core.Services;
using Torrentarr.Core.Configuration;
using Torrentarr.Infrastructure.Services;
using Xunit;

namespace Torrentarr.Infrastructure.Tests.Services;

[CollectionDefinition(nameof(ConfigReloaderTests), DisableParallelization = true)]
public sealed class ConfigReloaderTestsCollection;

/// <summary>
/// Tests for ConfigReloader.
/// Each test uses a dedicated temp directory so tests are isolated from each
/// other and from the developer's real config file.  The TORRENTARR_CONFIG
/// environment variable is saved/restored around each test.
/// </summary>
[Collection(nameof(ConfigReloaderTests))]
public sealed class ConfigReloaderTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _configPath;
    private readonly string? _originalEnvVar;

    public ConfigReloaderTests()
    {
        _tempDir = Path.Combine(
            Path.GetTempPath(),
            $"torrentarr-cfg-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
        _configPath = Path.Combine(_tempDir, "config.toml");

        // Snapshot original env var so we can restore it in Dispose
        _originalEnvVar = Environment.GetEnvironmentVariable("TORRENTARR_CONFIG");
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable(
            "TORRENTARR_CONFIG", _originalEnvVar);

        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, recursive: true);
    }

    /// <summary>
    /// Sets TORRENTARR_CONFIG and constructs a ConfigReloader so its
    /// constructor sees the env var value.
    /// </summary>
    private ConfigReloader CreateReloader(string envPath)
    {
        Environment.SetEnvironmentVariable("TORRENTARR_CONFIG", envPath);
        return new ConfigReloader(NullLogger<ConfigReloader>.Instance);
    }

    // Minimal TOML that ConfigurationLoader.Load() accepts without error.
    private const string MinimalToml = "[Settings]\n";

    [Fact]
    public void ReloadConfig_SnapshotCanBeAppliedByWorker()
    {
        File.WriteAllText(_configPath, MinimalToml);
        using var reloader = CreateReloader(_configPath);
        reloader.ReloadConfig().Should().BeTrue();
        var workerConfig = new TorrentarrConfig();
        using var worker = new ConfigReloader(NullLogger<ConfigReloader>.Instance,
            workerConfig, new ConfigurationLoader(_configPath), worker: true);

        worker.ReloadConfig().Should().BeTrue();

        workerConfig.Revision.Should().Be(reloader.Revision);
        workerConfig.Settings.ConfigVersion.Should().Be(ConfigurationLoader.ExpectedConfigVersion);
        var status = worker.GetStatus();
        status.AcceptedRevision.Should().Be(reloader.Revision);
        status.State.Should().Be("applied");
        status.Components["configuration"].AppliedRevision.Should().Be(reloader.Revision);
    }

    [Fact]
    public async Task WorkerSnapshot_ChangesAtTheNextOperationBoundary()
    {
        Environment.SetEnvironmentVariable("TORRENTARR_CONFIG", _configPath);
        var loader = new ConfigurationLoader(_configPath);
        var hostConfig = ConfigurationLoader.GenerateDefaultConfig();
        hostConfig.TorrentClients["one"] = new() { Maintenance = new() { PathMappings = [new() { ClientPath = "/old", LocalPath = _tempDir }] } };
        loader.SaveConfig(hostConfig);
        using var host = new ConfigReloader(NullLogger<ConfigReloader>.Instance, hostConfig, loader);
        host.ReloadConfig().Should().BeTrue();
        var workerConfig = new TorrentarrConfig();
        using var worker = new ConfigReloader(NullLogger<ConfigReloader>.Instance, workerConfig, loader, worker: true);
        worker.ReloadConfig().Should().BeTrue();
        var revision = workerConfig.Revision;
        using (workerConfig.BeginOperation())
        using (ConfigMutationGuard.EnterProcessing(_configPath))
        {
            var updated = loader.Load();
            updated.TorrentClients["one"].Maintenance.PathMappings[0].ClientPath = "/new";
            loader.SaveConfig(updated);
            host.Apply(updated).Should().BeTrue();
            Task reload;
            using (ExecutionContext.SuppressFlow()) reload = Task.Run(() => worker.ReloadConfig().Should().BeTrue());
            await reload;
            workerConfig.TorrentClients["one"].Maintenance.PathMappings[0].ClientPath.Should().Be("/old");
            workerConfig.Revision.Should().Be(revision);
        }
        workerConfig.TorrentClients["one"].Maintenance.PathMappings[0].ClientPath.Should().Be("/new");
        workerConfig.Revision.Should().BeGreaterThan(revision);
    }

    [Fact]
    public void SnapshotPublicationFailure_PreservesActiveRevisionAndReportsTheSavedFileSeparately()
    {
        Environment.SetEnvironmentVariable("TORRENTARR_CONFIG", _configPath);
        var loader = new ConfigurationLoader(_configPath);
        var current = ConfigurationLoader.GenerateDefaultConfig();
        loader.SaveConfig(current);
        using var host = new ConfigReloader(NullLogger<ConfigReloader>.Instance, current, loader);
        host.ReloadConfig().Should().BeTrue();
        var previousRevision = host.Revision;
        var updated = loader.Load();
        updated.TorrentClients["one"] = new() { Maintenance = new() { PathMappings = [new() { ClientPath = "/downloads", LocalPath = _tempDir }] } };
        File.Delete(host.SnapshotPath);
        Directory.CreateDirectory(host.SnapshotPath);
        loader.SaveConfig(updated);
        host.Apply(updated).Should().BeFalse();
        host.Revision.Should().Be(previousRevision);
        current.TorrentClients.Should().NotContainKey("one");
        loader.Load().TorrentClients["one"].Maintenance.PathMappings.Should().ContainSingle();
        host.GetStatus().State.Should().Be("error");
    }

    // ── ConfigPath resolution ─────────────────────────────────────────────────

    [Fact]
    public void ConfigPath_WhenEnvVarPointsToExistingFile_ReturnsEnvPath()
    {
        File.WriteAllText(_configPath, MinimalToml);

        using var reloader = CreateReloader(_configPath);

        reloader.ConfigPath.Should().Be(_configPath);
    }

    [Fact]
    public void ConfigPath_WhenEnvVarPointsToNonExistentFile_StillUsesEnvPath()
    {
        // Matches ConfigurationLoader.GetDefaultConfigPath(): TORRENTARR_CONFIG wins even if missing.
        var bogus = "/does/not/exist/config.toml";
        using var reloader = CreateReloader(bogus);

        reloader.ConfigPath.Should().Be(bogus);
    }

    [Fact]
    public void ReloadConfig_WhenEnvVarPointsToNonExistentFile_ReturnsFalse()
    {
        using var reloader = CreateReloader("/does/not/exist/config.toml");

        var result = reloader.ReloadConfig();

        result.Should().BeFalse();
    }

    // ── ReloadConfig – failure paths ──────────────────────────────────────────

    [Fact]
    public void ReloadConfig_WhenConfigFileMissing_ReturnsFalse()
    {
        // _configPath does not exist yet → ConfigurationLoader.Load() throws
        // FileNotFoundException → caught internally → returns false.
        using var reloader = CreateReloader(_configPath);

        var result = reloader.ReloadConfig();

        result.Should().BeFalse();
    }

    [Fact]
    public void ReloadConfig_WhenConfigFileMissing_FiresEventWithSuccessFalse()
    {
        using var reloader = CreateReloader(_configPath);
        ConfigReloadedEventArgs? captured = null;
        reloader.ConfigReloaded += (_, args) => captured = args;

        reloader.ReloadConfig();

        captured.Should().NotBeNull();
        captured!.Success.Should().BeFalse();
    }

    [Fact]
    public void ReloadConfig_WhenConfigFileMissing_EventHasErrorMessage()
    {
        using var reloader = CreateReloader(_configPath);
        ConfigReloadedEventArgs? captured = null;
        reloader.ConfigReloaded += (_, args) => captured = args;

        reloader.ReloadConfig();

        captured!.ErrorMessage.Should().NotBeNullOrEmpty();
    }

    // ── ReloadConfig – success paths ──────────────────────────────────────────

    [Fact]
    public void ReloadConfig_PersistsMigrationsAndBackup_AndReleasesWriterBeforeEvent()
    {
        File.WriteAllText(_configPath, MinimalToml);
        using var reloader = CreateReloader(_configPath);
        var notified = false;
        reloader.ConfigReloaded += (_, args) =>
        {
            args.Success.Should().BeTrue();
            ConfigMutationGuard.OwnsMutation(_configPath).Should().BeFalse();
            using var processing = ConfigMutationGuard.EnterProcessing(_configPath);
            notified = true;
        };

        reloader.ReloadConfig().Should().BeTrue();

        notified.Should().BeTrue();
        File.ReadAllText(_configPath + ".bak").Should().Be(MinimalToml);
        File.ReadAllText(_configPath).Should().Contain(ConfigurationLoader.ExpectedConfigVersion);
    }

    [Fact]
    public void ReloadConfig_WhileProcessing_PublishesAndPersistsMigrations()
    {
        File.WriteAllText(_configPath, MinimalToml);
        using var reloader = CreateReloader(_configPath);
        using var processing = ConfigMutationGuard.EnterProcessing(_configPath);

        reloader.ReloadConfig().Should().BeTrue();

        File.ReadAllText(_configPath).Should().Contain(ConfigurationLoader.ExpectedConfigVersion);
        File.ReadAllText(_configPath + ".bak").Should().Be(MinimalToml);
    }

    [Fact]
    public void ReloadConfig_WhenConfigFileExists_ReturnsTrue()
    {
        File.WriteAllText(_configPath, MinimalToml);
        using var reloader = CreateReloader(_configPath);

        var result = reloader.ReloadConfig();

        result.Should().BeTrue();
    }

    [Fact]
    public void ReloadConfig_WhenConfigFileExists_FiresEventWithSuccessTrue()
    {
        File.WriteAllText(_configPath, MinimalToml);
        using var reloader = CreateReloader(_configPath);
        ConfigReloadedEventArgs? captured = null;
        reloader.ConfigReloaded += (_, args) => captured = args;

        reloader.ReloadConfig();

        captured.Should().NotBeNull();
        captured!.Success.Should().BeTrue();
    }

    [Fact]
    public void ReloadConfig_EventArgs_ReloadedAt_IsRecent()
    {
        File.WriteAllText(_configPath, MinimalToml);
        using var reloader = CreateReloader(_configPath);
        ConfigReloadedEventArgs? captured = null;
        reloader.ConfigReloaded += (_, args) => captured = args;

        var before = DateTime.UtcNow.AddSeconds(-1);
        reloader.ReloadConfig();

        captured!.ReloadedAt.Should().BeOnOrAfter(before);
    }

    [Fact]
    public void ReloadConfig_CanBeCalledMultipleTimes()
    {
        File.WriteAllText(_configPath, MinimalToml);
        using var reloader = CreateReloader(_configPath);

        var first = reloader.ReloadConfig();
        var second = reloader.ReloadConfig();

        first.Should().BeTrue();
        second.Should().BeTrue();
    }

    // ── StartWatching / StopWatching ──────────────────────────────────────────

    [Fact]
    public void StartWatching_DoesNotThrow()
    {
        File.WriteAllText(_configPath, MinimalToml);
        using var reloader = CreateReloader(_configPath);

        var act = () => reloader.StartWatching();

        act.Should().NotThrow();
        reloader.StopWatching();
    }

    [Fact]
    public void StopWatching_DoesNotThrow()
    {
        File.WriteAllText(_configPath, MinimalToml);
        using var reloader = CreateReloader(_configPath);

        var act = () => reloader.StopWatching();

        act.Should().NotThrow();
    }

    [Fact]
    public void StartAndStopWatching_CanBeToggled()
    {
        File.WriteAllText(_configPath, MinimalToml);
        using var reloader = CreateReloader(_configPath);

        reloader.StartWatching();
        reloader.StopWatching();
        reloader.StartWatching();
        reloader.StopWatching();
    }

    // ── Dispose ───────────────────────────────────────────────────────────────

    [Fact]
    public void Dispose_DoesNotThrow()
    {
        var reloader = CreateReloader(_configPath);

        var act = () => reloader.Dispose();

        act.Should().NotThrow();
    }

    [Fact]
    public void Dispose_CanBeCalledMultipleTimes()
    {
        var reloader = CreateReloader(_configPath);

        var act = () =>
        {
            reloader.Dispose();
            reloader.Dispose();
        };

        act.Should().NotThrow();
    }
}

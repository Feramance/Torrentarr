using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Torrentarr.Core.Services;
using Torrentarr.Infrastructure.Services;
using Xunit;

namespace Torrentarr.Infrastructure.Tests.Services;

/// <summary>
/// Tests for ConfigReloader.
/// Each test uses a dedicated temp directory so tests are isolated from each
/// other and from the developer's real config file.  The TORRENTARR_CONFIG
/// environment variable is saved/restored around each test.
/// </summary>
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

    // ── ConfigPath resolution ─────────────────────────────────────────────────

    [Fact]
    public void ConfigPath_WhenEnvVarPointsToExistingFile_ReturnsEnvPath()
    {
        File.WriteAllText(_configPath, MinimalToml);

        using var reloader = CreateReloader(_configPath);

        reloader.ConfigPath.Should().Be(_configPath);
    }

    [Fact]
    public void ConfigPath_WhenEnvVarPointsToNonExistentFile_DoesNotUseThatPath()
    {
        // Non-existent file → env var is ignored; ConfigPath falls through to
        // the default search list and will be some path other than the bogus one.
        using var reloader = CreateReloader("/does/not/exist/config.toml");

        reloader.ConfigPath.Should().NotBe("/does/not/exist/config.toml");
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

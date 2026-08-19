using FluentAssertions;
using Torrentarr.Core.Configuration;
using Xunit;

namespace Torrentarr.Core.Tests.Configuration;

/// <summary>
/// Tests for <see cref="ConfigurationLoader.GetDataDirectoryPath"/> with env / override isolation.
/// </summary>
public sealed class GetDataDirectoryPathTests : IDisposable
{
    private readonly string? _originalTorrentarrConfig;
    private readonly string? _originalQbitrrConfig;
    private readonly string? _originalOverride;
    private readonly string? _originalDataOverride;
    private readonly string? _originalQbitrrDataOverride;

    public GetDataDirectoryPathTests()
    {
        _originalTorrentarrConfig = Environment.GetEnvironmentVariable("TORRENTARR_CONFIG");
        _originalQbitrrConfig = Environment.GetEnvironmentVariable("QBITRR_CONFIG");
        _originalOverride = ConfigurationLoader.TestConfigPathOverride;
        _originalDataOverride = Environment.GetEnvironmentVariable("TORRENTARR_OVERRIDES_DATA_PATH");
        _originalQbitrrDataOverride = Environment.GetEnvironmentVariable("QBITRR_OVERRIDES_DATA_PATH");
        Environment.SetEnvironmentVariable("TORRENTARR_OVERRIDES_DATA_PATH", null);
        Environment.SetEnvironmentVariable("QBITRR_OVERRIDES_DATA_PATH", null);
        Environment.SetEnvironmentVariable("QBITRR_CONFIG", null);
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("TORRENTARR_CONFIG", _originalTorrentarrConfig);
        Environment.SetEnvironmentVariable("QBITRR_CONFIG", _originalQbitrrConfig);
        Environment.SetEnvironmentVariable("TORRENTARR_OVERRIDES_DATA_PATH", _originalDataOverride);
        Environment.SetEnvironmentVariable("QBITRR_OVERRIDES_DATA_PATH", _originalQbitrrDataOverride);
        ConfigurationLoader.TestConfigPathOverride = _originalOverride;
    }

    [Fact]
    public void GetDataDirectoryPath_UsesTestConfigPathOverrideDirectory_WhenEnvNotSet()
    {
        Environment.SetEnvironmentVariable("TORRENTARR_CONFIG", null);

        var dir = Path.Combine(Path.GetTempPath(), $"torrentarr-data-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        var cfgFile = Path.Combine(dir, "config.toml");
        ConfigurationLoader.TestConfigPathOverride = cfgFile;

        try
        {
            var expected = Path.GetFullPath(dir);
            ConfigurationLoader.GetDataDirectoryPath().Should().Be(expected);
        }
        finally
        {
            if (Directory.Exists(dir))
                Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void GetDataDirectoryPath_ReturnsConfig_WhenEnvUnderDockerConfig()
    {
        Environment.SetEnvironmentVariable("TORRENTARR_CONFIG", "/config/foo.toml");
        ConfigurationLoader.TestConfigPathOverride = null;

        ConfigurationLoader.GetDataDirectoryPath().Should().Be("/config");
    }

    [Fact]
    public void GetDataDirectoryPath_ReturnsParentDirectory_WhenEnvIsAbsoluteFilePath()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"torrentarr-dd-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        var cfgFile = Path.Combine(dir, "config.toml");
        Environment.SetEnvironmentVariable("TORRENTARR_CONFIG", cfgFile);
        ConfigurationLoader.TestConfigPathOverride = null;

        try
        {
            var expected = Path.GetFullPath(dir);
            ConfigurationLoader.GetDataDirectoryPath().Should().Be(expected);
        }
        finally
        {
            if (Directory.Exists(dir))
                Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void GetDataDirectoryPath_UsesOverridesDataPath()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"torrentarr-ov-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        Environment.SetEnvironmentVariable("TORRENTARR_OVERRIDES_DATA_PATH", dir);
        Environment.SetEnvironmentVariable("TORRENTARR_CONFIG", "/config/foo.toml");
        ConfigurationLoader.TestConfigPathOverride = null;
        try
        {
            ConfigurationLoader.GetDataDirectoryPath().Should().Be(Path.GetFullPath(dir));
        }
        finally
        {
            Environment.SetEnvironmentVariable("TORRENTARR_OVERRIDES_DATA_PATH", null);
            if (Directory.Exists(dir))
                Directory.Delete(dir, recursive: true);
        }
    }
}

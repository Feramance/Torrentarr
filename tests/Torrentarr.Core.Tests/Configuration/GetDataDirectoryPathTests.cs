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
    private readonly string? _originalOverride;

    public GetDataDirectoryPathTests()
    {
        _originalTorrentarrConfig = Environment.GetEnvironmentVariable("TORRENTARR_CONFIG");
        _originalOverride = ConfigurationLoader.TestConfigPathOverride;
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("TORRENTARR_CONFIG", _originalTorrentarrConfig);
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
}

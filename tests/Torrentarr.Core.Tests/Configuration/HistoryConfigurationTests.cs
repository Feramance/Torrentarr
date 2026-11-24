using FluentAssertions;
using Torrentarr.Core.Configuration;
using Xunit;

namespace Torrentarr.Core.Tests.Configuration;

[Collection("Configuration environment")]
public sealed class HistoryConfigurationTests
{
    [Fact]
    public void RetentionDefaultsToThirtyDaysAndRoundTrips()
    {
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".toml");
        try
        {
            File.WriteAllText(path, "[Settings]\n");
            var loader = new ConfigurationLoader(path);
            var config = loader.Load();
            config.Settings.HistoryRetentionDays.Should().Be(30);
            config.Settings.HistoryRetentionDays = 90;
            loader.SaveConfig(config);
            loader.Load().Settings.HistoryRetentionDays.Should().Be(90);
        }
        finally { File.Delete(path); }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void RetentionMustBePositive(int days)
    {
        var config = new TorrentarrConfig();
        config.Settings.HistoryRetentionDays = days;
        ConfigValidationHelper.ValidateAll(config).Ok.Should().BeFalse();
    }
}

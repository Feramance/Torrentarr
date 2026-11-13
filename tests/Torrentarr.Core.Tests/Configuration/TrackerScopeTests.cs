using FluentAssertions;
using Torrentarr.Core.Configuration;
using Torrentarr.Core.Models;
using Xunit;

namespace Torrentarr.Core.Tests.Configuration;

public sealed class TrackerScopeTests : IDisposable
{
    private readonly string _directory = Directory.CreateTempSubdirectory().FullName;
    private string ConfigPath => Path.Combine(_directory, "config.toml");

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    [Fact]
    public void MixedConfigPreservesRulesAndScopesThroughMigrationAndSave()
    {
        const string original = """
            [Settings]
            ConfigVersion = '5.14.5'
            [qBit]
            Host = 'localhost'
            [TorrentClient.seedbox]
            Host = 'seedbox'
            [Sonarr-TV]
            Category = 'tv'
            [Radarr-Movies]
            Category = 'movies'
            [[Trackers]]
            ClientIds = ['qBit', 'seedbox']
            URI = 'https://tracker.example'
            Priority = 1
            [[Trackers]]
            ArrInstances = ['Sonarr-TV', 'Radarr-Movies']
            URI = 'https://other.example'
            Priority = 6
            [[qBit.Trackers]]
            URI = 'https://tracker.example'
            Priority = 2
            [[qBit.Trackers]]
            URI = 'https://tracker.example'
            Priority = 3
            [[TorrentClient.seedbox.Trackers]]
            URI = 'https://tracker.example'
            Priority = 4
            [[Sonarr-TV.Torrent.Trackers]]
            URI = 'https://tracker.example'
            Priority = 5
            [Sonarr-TV.Torrent.Trackers.Protection]
            Policy = 'onlyencodes'
            AdditionalSeedHours = 8
            """;
        File.WriteAllText(ConfigPath, original);
        var loader = new ConfigurationLoader(ConfigPath);
        var config = loader.Load();
        var migrated = File.ReadAllText(ConfigPath);

        File.ReadAllText(ConfigPath + ".bak").Should().Be(original);
        loader.Load().Trackers.Should().BeEquivalentTo(config.Trackers, o => o.WithStrictOrdering());
        File.ReadAllText(ConfigPath).Should().Be(migrated);
        migrated.Should().NotContain("[[qBit.Trackers]]").And.NotContain(".Torrent.Trackers");
        TrackerRules.ForClient(config, "qBit").Select(t => t.Priority).Should().Equal(1, 2, 3);
        TrackerRules.ForClient(config, "seedbox").Select(t => t.Priority).Should().Equal(1, 4);
        TrackerRules.ForTorrent(config, new TorrentInfo { Category = "tv", ClientInstanceId = "seedbox" })
            .Select(t => t.Priority).Should().Equal(1, 4, 6, 5);
        TrackerRules.ForTorrent(config, new TorrentInfo { Category = "movies", ClientInstanceId = "seedbox" })
            .Select(t => t.Priority).Should().Equal(1, 4, 6);
        config.Trackers.Single(t => t.Priority == 5).Protection!.AdditionalSeedHours.Should().Be(8);

        loader.SaveConfig(config);
        File.ReadAllText(ConfigPath).Should().Contain("[[Trackers]]");
        loader.Load().Trackers.Should().BeEquivalentTo(config.Trackers, o => o.WithStrictOrdering());
    }

    [Theory]
    [InlineData("")]
    [InlineData("ClientIds = ['qBit']\nArrInstances = []")]
    [InlineData("ClientIds = []")]
    [InlineData("ArrInstances = []")]
    [InlineData("ClientIds = ['qBit', 'QBIT']")]
    [InlineData("ArrInstances = ['Sonarr-TV', 'SONARR-TV']")]
    [InlineData("ClientIds = ['missing']")]
    [InlineData("ArrInstances = ['missing']")]
    [InlineData("ClientIds = [5]")]
    [InlineData("ClientIds = 'qBit'")]
    [InlineData("ClientIds = ['qBit']\nProtection = { Policy = 'missing' }")]
    public void InvalidScopeOrProtectionLeavesConfigUntouched(string assignment)
    {
        var original = $"""
            [Settings]
            ConfigVersion = '6.20.3'
            [qBit]
            [Sonarr-TV]
            [[Trackers]]
            URI = 'https://tracker.example'
            {assignment}
            """;
        File.WriteAllText(ConfigPath, original);
        var load = () => new ConfigurationLoader(ConfigPath).Load();
        load.Should().Throw<Exception>().WithMessage("*Tracker*");
        File.ReadAllText(ConfigPath).Should().Be(original);
        File.Exists(ConfigPath + ".bak").Should().BeFalse();
    }
}

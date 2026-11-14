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
    public void SavePreservesOmittedAndExplicitRemovalAndMessageSettings()
    {
        var loader = new ConfigurationLoader(ConfigPath);
        var config = new TorrentarrConfig();
        config.TorrentClients["seedbox"] = new();
        config.ArrInstances["Radarr-Movies"] = new()
        {
            Category = "movies",
            Torrent = new()
            {
                RemoveTrackerWithMessage = ["legacy"],
                SeedingMode = new() { RemoveTrackerWithMessage = [] }
            }
        };
        config.Trackers = [new() { ClientIds = ["seedbox"], Uri = "tracker.example", RemoveTorrent = -1 }];
        config.Settings.TrackerRules = [new() { TrackerUrl = "tracker.example", MinimumRatio = 2 }];
        loader.SaveConfig(config);

        var loaded = loader.Load();
        loaded.TorrentClients["seedbox"].CategorySeeding.RemoveTorrent.Should().BeNull();
        loaded.Trackers.Single().RemoveTorrent.Should().Be(-1);
        loaded.ArrInstances["Radarr-Movies"].Torrent.SeedingMode!.RemoveTrackerWithMessage.Should().BeEmpty();
        loaded.Settings.TrackerRules.Should().BeEquivalentTo(config.Settings.TrackerRules);
    }

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
    [InlineData("ratio", 2, 0, true)]
    [InlineData("ratio", 0, 2, false)]
    [InlineData("time", 0, 2, true)]
    [InlineData("time", 2, 0, false)]
    public void SingleConditionModesValidateAndRoundTrip(string mode, int ratio, int days, bool valid)
    {
        File.WriteAllText(ConfigPath, $"""
            [Settings]
            ConfigVersion = '6.20.5'
            [qBit]
            [[Trackers]]
            ClientIds = ['qBit']
            URI = 'example.test'
            HitAndRunMode = '{mode}'
            MinSeedRatio = {ratio}
            MinSeedingTimeDays = {days}
            """);
        var loader = new ConfigurationLoader(ConfigPath);
        if (!valid)
        {
            var load = () => loader.Load();
            load.Should().Throw<InvalidDataException>().WithMessage("*positive*");
            return;
        }
        var config = loader.Load();
        loader.SaveConfig(config);
        loader.Load().Trackers.Single().HitAndRunMode.Should().Be(mode);
    }

    [Theory]
    [InlineData("and")]
    [InlineData("or")]
    [InlineData("disabled")]
    [InlineData(null)]
    public void ScopedLegacyProtectionMigratesOnce(string? mode)
    {
        var original = $"""
            [Settings]
            ConfigVersion = '6.20.4'
            [qBit]
            [[Trackers]]
            ClientIds = ['qBit']
            URI = 'example.test'
            MinSeedingTime = 2
            MinSeedRatio = 1.5
            HitAndRunMinimumDownloadPercent = 20
            {(mode == null ? "" : $"HitAndRunMode = '{mode}'")}
            [[Trackers]]
            ClientIds = ['qBit']
            URI = 'explicit.example.test'
            HitAndRunMode = 'or'
            [Trackers.Protection]
            Policy = 'onlyencodes'
            """;
        File.WriteAllText(ConfigPath, original);
        var loader = new ConfigurationLoader(ConfigPath);
        var config = loader.Load();
        var rule = config.Trackers[0];
        rule.HitAndRunMode.Should().Be(mode);
        rule.MinSeedRatio.Should().Be(1.5);
        if (mode is "and" or "or")
            rule.Protection.Should().BeEquivalentTo(new TrackerProtectionConfig
            {
                RequireLegacyClearance = true,
                MinimumSeedHours = 48,
                DownloadThresholdPercent = 20
            });
        else
            rule.Protection.Should().BeNull();
        config.Trackers[1].Protection.Should().BeEquivalentTo(new TrackerProtectionConfig { Policy = "onlyencodes" });

        var migrated = File.ReadAllText(ConfigPath);
        loader.Load().Trackers.Should().BeEquivalentTo(config.Trackers);
        File.ReadAllText(ConfigPath).Should().Be(migrated);
        File.ReadAllText(ConfigPath + ".bak").Should().Be(original);
        loader.SaveConfig(config);
        loader.Load().Trackers.Should().BeEquivalentTo(config.Trackers);
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

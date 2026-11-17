using FluentAssertions;
using Tomlyn;
using Tomlyn.Model;
using Torrentarr.Core.Configuration;
using Torrentarr.Core.Services;
using Xunit;

namespace Torrentarr.Core.Tests.Configuration;

[Collection("Configuration environment")]
public sealed class TorrentScanIntervalTests
{
    [Theory]
    [InlineData(-1, 5)]
    [InlineData(0, 5)]
    [InlineData(5, 5)]
    [InlineData(10, 15)]
    [InlineData(22, 15)]
    [InlineData(23, 30)]
    [InlineData(45, 60)]
    [InlineData(75, 90)]
    [InlineData(105, 120)]
    [InlineData(121, 120)]
    [InlineData(3600, 120)]
    [InlineData(int.MaxValue, 120)]
    public void LegacyInterval_MapsToNearestAllowedValue(int before, int expected)
        => TorrentScanInterval.FromLegacy(before).Should().Be(expected);

    [Theory]
    [InlineData(5)]
    [InlineData(15)]
    [InlineData(30)]
    [InlineData(60)]
    [InlineData(90)]
    [InlineData(120)]
    public void AllowedIntervals_PassValidationAndRoundTrip(int seconds)
    {
        var client = new TorrentClientInstanceConfig { TorrentScanIntervalSeconds = seconds };
        StorageConfigurationValidation.Validate(client).Should().BeNull();
        TomlSerializer.Deserialize<TorrentClientInstanceConfig>(TomlSerializer.Serialize(client))!
            .TorrentScanIntervalSeconds.Should().Be(seconds);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("10")]
    [InlineData("121")]
    [InlineData("30.5")]
    [InlineData("'30'")]
    public void UnsupportedExplicitInterval_IsRejectedBeforeMigration(string value)
    {
        var root = TomlSerializer.Deserialize<TomlTable>($"[TorrentClient.one]\nTorrentScanIntervalSeconds = {value}")!;
        FluentActions.Invoking(() => TorrentScanInterval.Migrate(root)).Should().Throw<FormatException>();
    }

    [Theory]
    [InlineData("30.5")]
    [InlineData("10")]
    [InlineData("null")]
    [InlineData("\"30\"")]
    public void JsonUpdates_RejectCoercionAndUnsupportedValues(string value)
    {
        var json = "{\"TorrentScanIntervalSeconds\":" + value + "}";
        FluentActions.Invoking(() => Newtonsoft.Json.JsonConvert.DeserializeObject<TorrentClientInstanceConfig>(json))
            .Should().Throw<Newtonsoft.Json.JsonException>();
    }

    [Fact]
    public void Migration_PreservesExplicitValuesAndPersistsOnceWithBackup()
    {
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".toml");
        var original = """
            [Settings]
            ConfigVersion = "6.20.2"
            LoopSleepTimer = 3600
            [qBit]
            Host = "localhost"
            [TorrentClient.one]
            Host = "localhost"
            [TorrentClient.explicit]
            Host = "localhost"
            TorrentScanIntervalSeconds = 15
            """;
        try
        {
            File.WriteAllText(path, original);
            var loader = new ConfigurationLoader(path);
            var config = loader.Load();
            config.QBitInstances["qBit"].TorrentScanIntervalSeconds.Should().Be(120);
            config.TorrentClients["one"].TorrentScanIntervalSeconds.Should().Be(120);
            config.TorrentClients["explicit"].TorrentScanIntervalSeconds.Should().Be(15);
            config.Settings.ConfigVersion.Should().Be(ConfigurationLoader.ExpectedConfigVersion);
            File.ReadAllText(path + ".bak").Should().Be(original);
            var migrated = File.ReadAllText(path);
            loader.Load();
            File.ReadAllText(path).Should().Be(migrated);
            File.ReadAllText(path + ".bak").Should().Be(original);
            loader.SaveConfig(config);
            loader.Load().QBitInstances["qBit"].TorrentScanIntervalSeconds.Should().Be(120);
        }
        finally { File.Delete(path); File.Delete(path + ".bak"); }
    }

    [Fact]
    public void NewClientInMigratedFile_DefaultsToThirtyRatherThanLegacyTimer()
    {
        var root = TomlSerializer.Deserialize<TomlTable>("""
            [Settings]
            ConfigVersion = "6.20.3"
            LoopSleepTimer = 3600
            [TorrentClient.new]
            Host = "fixture"
            [qBit-new]
            Host = "fixture"
            """)!;
        TorrentScanInterval.Migrate(root).Should().BeTrue();
        ((TomlTable)((TomlTable)root["TorrentClient"])["new"])["TorrentScanIntervalSeconds"].Should().Be(30L);
        ((TomlTable)root["qBit-new"])["TorrentScanIntervalSeconds"].Should().Be(30L);
    }

    [Fact]
    public void NewClientsAndSchema_DefaultToThirtySeconds()
    {
        new TorrentClientInstanceConfig().TorrentScanIntervalSeconds.Should().Be(30);
        new QBitConfig().TorrentScanIntervalSeconds.Should().Be(30);
        foreach (var name in new[] { "TorrentClient", "qBit" })
        {
            var field = ConfigSchemaBuilder.Sections[name].Single(f => f.key == "TorrentScanIntervalSeconds");
            field.kind.Should().Be("select"); field.@default.Should().Be(30);
            field.options.Should().Equal("5", "15", "30", "60", "90", "120");
        }
        var config = new TorrentarrConfig();
        config.TorrentClients["one"] = new() { TorrentScanIntervalSeconds = 10 };
        ConfigValidationHelper.ValidateTorrentClients(config).Ok.Should().BeFalse();
    }

    [Fact]
    public void Schedule_SeparatesClientsSkipsMissedTicksAndResetsOnConfigChange()
    {
        var clock = new ManualClock();
        var schedule = new TorrentScanSchedule(clock);
        var intervals = new Dictionary<string, int> { ["fast"] = 5, ["slow"] = 120 };
        schedule.TakeDue(intervals, 0).Should().BeEquivalentTo(new[] { "fast", "slow" });
        schedule.Delay.Should().Be(TimeSpan.FromSeconds(5));
        clock.Now += TimeSpan.FromSeconds(5);
        schedule.TakeDue(intervals, 0).Should().Equal("fast");
        clock.Now += TimeSpan.FromSeconds(17);
        schedule.Complete(["fast"], intervals);
        schedule.Delay.Should().Be(TimeSpan.FromSeconds(3));
        schedule.TakeDue(intervals, 0).Should().BeEmpty();
        clock.Now = DateTimeOffset.UnixEpoch.AddSeconds(120);
        schedule.TakeDue(intervals, 0).Should().BeEquivalentTo(new[] { "fast", "slow" });
        schedule.TakeDue(intervals, 1).Should().BeEquivalentTo(new[] { "fast", "slow" });
        schedule.Reset();
        schedule.TakeDue(intervals, 1).Should().BeEquivalentTo(new[] { "fast", "slow" });
        intervals.Remove("fast");
        schedule.TakeDue(intervals, 1).Should().BeEmpty();
        schedule.Delay.Should().Be(TimeSpan.FromSeconds(120));
    }

    private sealed class ManualClock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = DateTimeOffset.UnixEpoch;
        public override DateTimeOffset GetUtcNow() => Now;
    }
}

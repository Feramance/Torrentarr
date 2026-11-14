using System.Security.Cryptography;
using FluentAssertions;
using Tomlyn;
using Tomlyn.Model;
using Torrentarr.Core.Configuration;
using Xunit;

namespace Torrentarr.Core.Tests.Configuration;

[Collection("Configuration environment")]
public sealed class QBitrrStartupMigrationTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
    private string ConfigPath => Path.Combine(_directory, "config.toml");
    private static string Fixture => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "qbitrr-5.14.8.toml"));

    public QBitrrStartupMigrationTests() => Directory.CreateDirectory(_directory);

    [Fact]
    public void FixtureMatchesPinnedReleaseAndMasterChecksum()
    {
        var bytes = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", "qbitrr-5.14.8.toml"));
        Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant().Should()
            .Be("4eac53c23929513497c67802e6a62e03b88a88ab8f99b94683d5388629700ee7");
    }

    [Fact]
    public void UnmodifiedUpstreamConfigMigratesWithoutEnablingPlaceholderStorage()
    {
        var config = MigrateTwice(Fixture);
        config.QBitInstances.Should().ContainSingle();
        var qbit = config.QBitInstances["qBit"];
        qbit.Host.Should().Be("CHANGE_ME");
        qbit.FreeSpace.Should().BeEmpty();
        qbit.CategorySeeding.StalledDelay.Should().Be(-1);
        config.WebUI.AuthDisabled.Should().BeFalse();
        config.WebUI.LocalAuthEnabled.Should().BeFalse();
        config.WebUI.OIDCEnabled.Should().BeFalse();
        config.WebUI.AllowInsecureExposure.Should().BeFalse();
        config.WebUI.AllowInsecureTokenQuery.Should().BeFalse();
        AssertArrSettings(config, TomlSerializer.Deserialize<TomlTable>(Fixture)!);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(0)]
    public void ConfiguredCredentialsRulesAndModernStalledSentinelsSurvive(int stalledDelay)
    {
        var root = TomlSerializer.Deserialize<TomlTable>(Fixture)!;
        var settings = (TomlTable)root["Settings"];
        settings["CompletedDownloadFolder"] = _directory;
        settings["FreeSpaceFolder"] = _directory;
        settings["FreeSpace"] = "20G";
        var qbit = (TomlTable)root["qBit"];
        qbit["Host"] = "https://qbit.example";
        qbit["UserName"] = "fixture-user";
        qbit["Password"] = "fixture-password";
        var seeding = (TomlTable)qbit["CategorySeeding"];
        seeding["HitAndRunMode"] = "and";
        seeding["MinSeedRatio"] = 2.5;
        seeding["MinSeedingTimeDays"] = 3;
        seeding["StalledDelay"] = stalledDelay;
        qbit["Trackers"] = new TomlTableArray { new TomlTable
        {
            ["URI"] = "https://tracker.example/announce", ["HitAndRunMode"] = "or",
            ["MaxSeedingTime"] = "2w", ["MinSeedRatio"] = 3.0
        } };
        var web = (TomlTable)root["WebUI"];
        web["Token"] = "fixture-token";
        web["LocalAuthEnabled"] = true;
        web["OIDCEnabled"] = true;
        web["Username"] = "fixture-admin";
        web["PasswordHash"] = "fixture-hash";
        ((TomlTable)web["OIDC"])["ClientSecret"] = "fixture-secret";
        foreach (var name in root.Keys.Where(ArrSectionHelper.IsArrSection))
        {
            var arr = (TomlTable)root[name];
            arr["APIKey"] = "fixture-" + name;
            ((TomlTable)arr["Torrent"])["StalledDelay"] = stalledDelay;
            var search = (TomlTable)arr["EntrySearch"];
            search["SearchMissing"] = false;
            search["DoUpgradeSearch"] = true;
            search["SearchInReverse"] = true;
            search["QualityProfileMappings"] = new TomlTable { ["HD"] = "SD" };
        }
        ((TomlTable)((TomlTable)root["Sonarr-TV"])["Torrent"])["Trackers"] =
            new TomlTableArray { new TomlTable
            {
                ["URI"] = "https://tracker.example/announce", ["HitAndRunMode"] = "and",
                ["MaximumETA"] = "2h", ["MinSeedRatio"] = 4.0
            } };

        var config = MigrateTwice(TomlSerializer.Serialize(root));
        var client = config.QBitInstances["qBit"];
        client.Host.Should().Be("https://qbit.example");
        client.UserName.Should().Be("fixture-user");
        client.Password.Should().Be("fixture-password");
        client.DownloadPath.Should().Be(_directory);
        client.FreeSpace.Should().ContainSingle().Which.Should().BeEquivalentTo(
            new FreeSpaceMonitorConfig { Path = _directory, Enabled = true, Limit = "20G" });
        client.CategorySeeding.HitAndRunMode.Should().Be("and");
        client.CategorySeeding.MinSeedRatio.Should().Be(2.5);
        client.CategorySeeding.MinSeedingTimeDays.Should().Be(3);
        client.CategorySeeding.StalledDelay.Should().Be(stalledDelay);
        TrackerRules.ForClient(config, "qBit").Should().ContainSingle().Which.Should().BeEquivalentTo(new TrackerConfig
        {
            Uri = "https://tracker.example/announce",
            HitAndRunMode = "or",
            MaxSeedingTime = 1209600,
            MinSeedRatio = 3,
            MinSeedingTimeDays = 0,
            HitAndRunMinimumDownloadPercent = 10,
            HitAndRunPartialSeedRatio = 1,
            TrackerUpdateBuffer = 0
        });
        TrackerRules.ForIntegration(config, "Sonarr-TV", config.ArrInstances["Sonarr-TV"]).Should().ContainSingle().Which.Should()
            .BeEquivalentTo(new TrackerConfig
            {
                Uri = "https://tracker.example/announce",
                HitAndRunMode = "and",
                MaxETA = 7200,
                MinSeedRatio = 4,
                MinSeedingTimeDays = 0,
                HitAndRunMinimumDownloadPercent = 10,
                HitAndRunPartialSeedRatio = 1,
                TrackerUpdateBuffer = 0
            });
        config.WebUI.Token.Should().Be("fixture-token");
        config.WebUI.AuthDisabled.Should().BeFalse();
        config.WebUI.LocalAuthEnabled.Should().BeTrue();
        config.WebUI.OIDCEnabled.Should().BeTrue();
        config.WebUI.Username.Should().Be("fixture-admin");
        config.WebUI.PasswordHash.Should().Be("fixture-hash");
        config.WebUI.OIDC!.ClientSecret.Should().Be("fixture-secret");
        AssertArrSettings(config, root);
    }

    [Fact]
    public void MultipleQBitInstancesKeepTheirRulesAndDisableAmbiguousStorage()
    {
        var root = TomlSerializer.Deserialize<TomlTable>(Fixture)!;
        ((TomlTable)root["Settings"])["CompletedDownloadFolder"] = _directory;
        ((TomlTable)root["Settings"])["FreeSpace"] = "10G";
        root["qBit-seedbox"] = new TomlTable
        {
            ["Host"] = "seedbox.example",
            ["CategorySeeding"] = new TomlTable { ["MinSeedRatio"] = 5.0 }
        };
        var config = MigrateTwice(TomlSerializer.Serialize(root));
        config.QBitInstances.Should().HaveCount(2);
        config.QBitInstances["qBit"].CategorySeeding.MinSeedRatio.Should().Be(1);
        config.QBitInstances["qBit-seedbox"].CategorySeeding.MinSeedRatio.Should().Be(5);
        foreach (var client in config.QBitInstances.Values)
        {
            client.DownloadPath.Should().BeNull();
            client.FreeSpace.Should().ContainSingle().Which.Enabled.Should().BeFalse();
            client.FreeSpace.Single().Path.Should().Be(_directory);
        }
    }

    [Fact]
    public void QBitrrEnvironmentOverridesRemainRuntimeOnly()
    {
        const string variable = "QBITRR_SETTINGS_FREE_SPACE";
        var previous = Environment.GetEnvironmentVariable(variable);
        var root = TomlSerializer.Deserialize<TomlTable>(Fixture)!;
        var settings = (TomlTable)root["Settings"];
        settings["CompletedDownloadFolder"] = _directory;
        settings["FreeSpace"] = "10G";
        try
        {
            Environment.SetEnvironmentVariable(variable, "42G");
            MigrateTwice(TomlSerializer.Serialize(root)).QBitInstances["qBit"].FreeSpace.Single().Limit.Should().Be("42G");
            File.ReadAllText(ConfigPath).Should().NotContain("42G");
            Environment.SetEnvironmentVariable(variable, null);
            new ConfigurationLoader(ConfigPath).LoadOrCreate().Config.QBitInstances["qBit"].FreeSpace.Single().Limit.Should().Be("10G");
        }
        finally { Environment.SetEnvironmentVariable(variable, previous); }
    }

    private TorrentarrConfig MigrateTwice(string original)
    {
        File.WriteAllText(ConfigPath, original);
        var loader = new ConfigurationLoader(ConfigPath);
        var (first, created) = loader.LoadOrCreate();
        created.Should().BeFalse();
        first.Settings.ConfigVersion.Should().Be(ConfigurationLoader.ExpectedConfigVersion);
        File.ReadAllText(ConfigPath + ".bak").Should().Be(original);
        var migrated = File.ReadAllText(ConfigPath);
        TomlSerializer.Deserialize<TomlTable>(migrated)!.Should().ContainKey("Settings");
        var (second, createdAgain) = loader.LoadOrCreate();
        createdAgain.Should().BeFalse();
        second.Should().BeEquivalentTo(first);
        File.ReadAllText(ConfigPath).Should().Be(migrated);
        File.ReadAllText(ConfigPath + ".bak").Should().Be(original);
        return second;
    }

    private static void AssertArrSettings(TorrentarrConfig config, TomlTable source)
    {
        config.ArrInstances.Keys.Should().BeEquivalentTo("Sonarr-TV", "Sonarr-Anime", "Radarr-1080", "Radarr-4K", "Lidarr-Music", "Readarr-Books");
        foreach (var (name, arr) in config.ArrInstances)
        {
            var original = (TomlTable)source[name];
            arr.APIKey.Should().Be((string)original["APIKey"]);
            arr.URI.Should().Be((string)original["URI"]);
            arr.Category.Should().Be((string)original["Category"]);
            var torrent = (TomlTable)original["Torrent"];
            arr.Torrent.StalledDelay.Should().Be(Convert.ToInt32(torrent["StalledDelay"]));
            arr.Torrent.FolderExclusionRegex.Should().Equal(((TomlArray)torrent["FolderExclusionRegex"]).Cast<string>());
            arr.Torrent.FileNameExclusionRegex.Should().Equal(((TomlArray)torrent["FileNameExclusionRegex"]).Cast<string>());
            arr.Torrent.FileExtensionAllowlist.Should().Equal(((TomlArray)torrent["FileExtensionAllowlist"]).Cast<string>());
            var search = (TomlTable)original["EntrySearch"];
            arr.Search.SearchMissing.Should().Be((bool)search["SearchMissing"]);
            arr.Search.DoUpgradeSearch.Should().Be((bool)search["DoUpgradeSearch"]);
            arr.Search.SearchInReverse.Should().Be((bool)search["SearchInReverse"]);
            arr.Search.QualityProfileMappings.Should().BeEquivalentTo(
                ((TomlTable)search["QualityProfileMappings"]).ToDictionary(p => p.Key, p => (string)p.Value));
            arr.Torrent.SeedingMode!.MaxSeedingTime.Should().Be(-1);
            arr.Torrent.SeedingMode.MaxUploadRatio.Should().Be(-1);
        }
    }

    public void Dispose() => Directory.Delete(_directory, recursive: true);
}

using FluentAssertions;
using Tomlyn;
using Tomlyn.Model;
using Torrentarr.Core.Configuration;
using Xunit;

namespace Torrentarr.Core.Tests.Configuration;

[Collection("Configuration environment")]
public sealed class StorageMigrationTests : IDisposable
{
    private readonly string _file = Path.GetTempFileName();
    private TorrentarrConfig Load(string toml)
    {
        File.WriteAllText(_file, toml);
        return new ConfigurationLoader(_file).Load();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void LegacyDurationsSurviveStorageMigrationAndRuntimeOverrides(bool withEnvironment)
    {
        var root = Path.GetTempPath().Replace('\\', '/').TrimEnd('/');
        var original = $"""
            [Settings]
            ConfigVersion = "5.14.7"
            CompletedDownloadFolder = '{root}/file'
            FreeSpace = "10G"
            AutoPauseResume = true
            [qBit]
            Host = "localhost"
            [[qBit.Maintenance.PathMappings]]
            ClientPath = "/remote"
            LocalPath = '{root}'
            [qBit.CategorySeeding]
            MaxSeedingTime = "2w"
            [[qBit.Trackers]]
            URI = "https://one.example/announce"
            MaxSeedingTime = "2w"
            [[qBit.Trackers]]
            URI = "https://two.example/announce"
            MaxSeedingTime = "2w"
            [[qBit.Trackers]]
            URI = "https://three.example/announce"
            MaxSeedingTime = "2w"
            """;
        var previousLimit = Environment.GetEnvironmentVariable("TORRENTARR_SETTINGS_FREE_SPACE");
        var previousRoot = Environment.GetEnvironmentVariable("TORRENTARR_SETTINGS_COMPLETED_DOWNLOAD_FOLDER");
        var previousAliasLimit = Environment.GetEnvironmentVariable("QBITRR_SETTINGS_FREE_SPACE");
        var previousAliasRoot = Environment.GetEnvironmentVariable("QBITRR_SETTINGS_COMPLETED_DOWNLOAD_FOLDER");
        try
        {
            Environment.SetEnvironmentVariable("QBITRR_SETTINGS_FREE_SPACE", null);
            Environment.SetEnvironmentVariable("QBITRR_SETTINGS_COMPLETED_DOWNLOAD_FOLDER", null);
            Environment.SetEnvironmentVariable("TORRENTARR_SETTINGS_FREE_SPACE", withEnvironment ? "42G" : null);
            Environment.SetEnvironmentVariable("TORRENTARR_SETTINGS_COMPLETED_DOWNLOAD_FOLDER", withEnvironment ? root + "/runtime" : null);
            File.WriteAllText(_file, original);
            var loader = new ConfigurationLoader(_file);
            string? migrated = null;
            for (var pass = 0; pass < 2; pass++)
            {
                var config = loader.Load();
                var client = config.QBitInstances["qBit"];
                client.CategorySeeding.MaxSeedingTime.Should().Be(1209600);
                config.Trackers.Should().HaveCount(3).And.OnlyContain(t => t.MaxSeedingTime == 1209600);
                client.DownloadPath.Should().Be(withEnvironment ? "/remote/runtime" : "/remote/file");
                var monitor = client.FreeSpace.Single();
                monitor.Path.Should().Be(root + (withEnvironment ? "/runtime" : "/file"));
                monitor.Limit.Should().Be(withEnvironment ? "42G" : "10G");
                monitor.Enabled.Should().BeTrue();
                File.ReadAllText(_file + ".bak").Should().Be(original);
                var persisted = File.ReadAllText(_file);
                if (migrated != null) persisted.Should().Be(migrated);
                migrated = persisted;
                var configTable = TomlSerializer.Deserialize<TomlTable>(persisted)!;
                var table = (TomlTable)configTable["qBit"];
                ((TomlTable)table["CategorySeeding"])["MaxSeedingTime"].Should().Be("2w");
                ((TomlTableArray)configTable["Trackers"]).Should().HaveCount(3)
                    .And.OnlyContain(t => (string)t["MaxSeedingTime"] == "2w");
                persisted.Should().NotContain("42G").And.NotContain(root + "/runtime");
            }
        }
        finally
        {
            Environment.SetEnvironmentVariable("TORRENTARR_SETTINGS_FREE_SPACE", previousLimit);
            Environment.SetEnvironmentVariable("TORRENTARR_SETTINGS_COMPLETED_DOWNLOAD_FOLDER", previousRoot);
            Environment.SetEnvironmentVariable("QBITRR_SETTINGS_FREE_SPACE", previousAliasLimit);
            Environment.SetEnvironmentVariable("QBITRR_SETTINGS_COMPLETED_DOWNLOAD_FOLDER", previousAliasRoot);
        }
    }

    [Theory]
    [InlineData("10G", true, true)]
    [InlineData("10G", false, false)]
    [InlineData("-1", true, false)]
    [InlineData("0", true, false)]
    public void SingleClientMigratesEnabledStateAndLimit_WithBackupAndIdempotence(string limit, bool auto, bool enabled)
    {
        var root = Path.GetTempPath().Replace('\\', '/');
        var original = $"""
            [Settings]
            ConfigVersion = "6.18.0"
            CompletedDownloadFolder = '{root}'
            FreeSpaceFolder = '{root}'
            FreeSpace = "{limit}"
            AutoPauseResume = {auto.ToString().ToLowerInvariant()}
            [qBit]
            Host = "localhost"
            """;
        var config = Load(original);
        config.Settings.ConfigVersion.Should().Be(ConfigurationLoader.ExpectedConfigVersion);
        config.QBitInstances["qBit"].DownloadPath.Should().Be(root);
        var monitor = config.QBitInstances["qBit"].FreeSpace.Single();
        monitor.Path.Should().Be(root); monitor.Enabled.Should().Be(enabled);
        if (limit != "-1") monitor.Limit.Should().Be(limit);
        File.ReadAllText(_file + ".bak").Should().Be(original);
        var migrated = File.ReadAllText(_file);
        migrated.Should().NotContain("CompletedDownloadFolder").And.NotContain("AutoPauseResume").And.NotContain("FreeSpaceFolder");
        new ConfigurationLoader(_file).Load();
        File.ReadAllText(_file).Should().Be(migrated);
        File.ReadAllText(_file + ".bak").Should().Be(original);
    }

    [Fact]
    public void UnmappableEnvironmentOverrideIsLiveAndClearsWhenCorrected()
    {
        const string key = "TORRENTARR_SETTINGS_COMPLETED_DOWNLOAD_FOLDER";
        var prior = Environment.GetEnvironmentVariable(key);
        var root = Path.GetTempPath().Replace('\\', '/').TrimEnd('/');
        try
        {
            Environment.SetEnvironmentVariable(key, root + "-unmapped");
            var config = Load($"""
                [Settings]
                ConfigVersion = "{ConfigurationLoader.ExpectedConfigVersion}"
                [qBit]
                Host = "localhost"
                FreeSpace = []
                [[qBit.Maintenance.PathMappings]]
                ClientPath = "/remote"
                LocalPath = '{root}'
                """);
            config.QBitInstances["qBit"].Migration.EnvironmentDownloadPathFailed.Should().BeTrue();
            config.QBitInstances["qBit"].Migration.Notices.Should().Contain(StorageConfigMigration.DownloadPathNotice);
            File.ReadAllText(_file).Should().NotContain("EnvironmentDownloadPathFailed").And.NotContain("Migration.Notices");
            Environment.SetEnvironmentVariable(key, root + "/completed");
            var corrected = new ConfigurationLoader(_file).Load().QBitInstances["qBit"];
            corrected.DownloadPath.Should().Be("/remote/completed");
            corrected.Migration.EnvironmentDownloadPathFailed.Should().BeFalse();
        }
        finally { Environment.SetEnvironmentVariable(key, prior); }
    }

    [Theory]
    [InlineData("qBit")]
    [InlineData("TorrentClient.named")]
    public void ExplicitClientValuesAndEmptyListWin(string section)
    {
        var config = Load($"""
            [Settings]
            CompletedDownloadFolder = "/old"
            FreeSpace = "20G"
            [{section}]
            Host = "localhost"
            DownloadPath = "/explicit"
            FreeSpace = []
            """);
        var client = config.GetAllTorrentClients().Values.Single();
        client.DownloadPath.Should().Be("/explicit"); client.FreeSpace.Should().BeEmpty();
    }

    [Fact]
    public void CompletedRootIsReverseMappedToClientNamespace()
    {
        var root = Path.GetTempPath().Replace('\\', '/').TrimEnd('/');
        var config = Load($"""
            [Settings]
            CompletedDownloadFolder = '{root}/completed'
            FreeSpace = "10G"
            [TorrentClient.named]
            Host = "localhost"
            [[TorrentClient.named.Maintenance.PathMappings]]
            ClientPath = "/remote"
            LocalPath = '{root}'
            """);
        config.TorrentClients["named"].DownloadPath.Should().Be("/remote/completed");
        config.TorrentClients["named"].FreeSpace.Single().Path.Should().Be(root + "/completed");
    }

    [Fact]
    public void MultipleClientsUseTheirOwnPaths_AndWarnForAmbiguousPaths()
    {
        var first = Path.Combine(Path.GetTempPath(), "first").Replace('\\', '/');
        var config = Load($"""
            [Settings]
            CompletedDownloadFolder = "/old"
            FreeSpace = "20G"
            [qBit-one]
            Host = "localhost"
            DownloadPath = '{first}'
            [TorrentClient.two]
            Host = "localhost"
            """);
        config.QBitInstances["qBit-one"].FreeSpace.Single().Path.Should().Be(first);
        config.QBitInstances["qBit-one"].FreeSpace.Single().Enabled.Should().BeTrue();
        config.TorrentClients["two"].DownloadPath.Should().BeNull();
        config.TorrentClients["two"].FreeSpace.Should().OnlyContain(m => !m.Enabled);
        config.QBitInstances["qBit-one"].Migration.Notices.Should().BeEmpty();
        config.TorrentClients["two"].Migration.Notices.Should().ContainSingle().Which.Should().Contain("ambiguous");
        var persisted = File.ReadAllText(_file);
        var loader = new ConfigurationLoader(_file);
        loader.Load().TorrentClients["two"].Migration.Notices.Should().Equal(config.TorrentClients["two"].Migration.Notices);
        File.ReadAllText(_file).Should().Be(persisted);
        config.TorrentClients["two"].Migration.Notices.Clear();
        loader.SaveConfig(config);
        loader.Load().TorrentClients["two"].Migration.Notices.Should().BeEmpty();
    }

    [Theory]
    [InlineData("qBit")]
    [InlineData("TorrentClient.named")]
    public void MissingLegacyStoragePaths_PersistAnActionableMigrationNotice(string section)
    {
        var config = Load($"""
            [Settings]
            FreeSpace = "10G"
            [{section}]
            Host = "localhost"
            """);
        var client = config.GetAllTorrentClients().Values.Single();
        client.FreeSpace.Should().BeEmpty();
        client.Migration.Notices.Should().ContainSingle().Which.Should().Contain("free-space protection remains disabled until configured");
        new ConfigurationLoader(_file).Load().GetAllTorrentClients().Values.Single().Migration.Notices.Should().Equal(client.Migration.Notices);
    }

    [Theory]
    [InlineData("qBit")]
    [InlineData("TorrentClient.named")]
    public void FailedEnvironmentPathOverride_IsReevaluatedAndNeverSavedAsAnActiveFailure(string section)
    {
        const string key = "TORRENTARR_SETTINGS_COMPLETED_DOWNLOAD_FOLDER";
        var previous = Environment.GetEnvironmentVariable(key);
        var root = Path.Combine(Path.GetTempPath(), "mapped").Replace('\\', '/');
        try
        {
            Environment.SetEnvironmentVariable(key, root + "-outside");
            var config = Load($"""
                [Settings]
                [{section}]
                Host = "localhost"
                DownloadPath = "/remote/working"
                [{section}.Migration]
                Notices = ["{StorageConfigMigration.EnvironmentDownloadPathNotice}"]
                [[{section}.Maintenance.PathMappings]]
                ClientPath = "/remote"
                LocalPath = '{root}'
                """);
            config.GetAllTorrentClients().Values.Single().Migration.EnvironmentDownloadPathFailed.Should().BeTrue();
            var loader = new ConfigurationLoader(_file);
            loader.SaveConfig(config);
            File.ReadAllText(_file).Should().NotContain("EnvironmentDownloadPathFailed");

            Environment.SetEnvironmentVariable(key, root + "/corrected");
            var client = loader.Load().GetAllTorrentClients().Values.Single();
            client.DownloadPath.Should().Be("/remote/corrected");
            client.Migration.EnvironmentDownloadPathFailed.Should().BeFalse();
            client.Migration.Notices.Should().Contain(StorageConfigMigration.EnvironmentDownloadPathNotice);
        }
        finally
        {
            Environment.SetEnvironmentVariable(key, previous);
        }
    }

    [Theory]
    [InlineData("qBit")]
    [InlineData("TorrentClient.named")]
    public void SaveAndLoadPreservesMonitorsAndMappings(string section)
    {
        var path = Path.GetTempPath();
        var remote = @"D:\downloads";
        var config = Load($"""
            [Settings]
            ConfigVersion = "6.19.0"
            [{section}]
            Host = "localhost"
            DownloadPath = '{remote}'
            [[{section}.FreeSpace]]
            Path = '{path}'
            Enabled = true
            Limit = "500M"
            [[{section}.Maintenance.PathMappings]]
            ClientPath = '{remote}'
            LocalPath = '{path}'
            """);
        new ConfigurationLoader(_file).SaveConfig(config);
        var loaded = new ConfigurationLoader(_file).Load();
        loaded.GetAllTorrentClients().Values.Single().DownloadPath.Should().Be(remote);
        loaded.GetAllTorrentClients().Values.Single().FreeSpace.Single().Limit.Should().Be("500M");
        loaded.GetAllTorrentClients().Values.Single().Maintenance.PathMappings.Should().HaveCount(1);
        File.ReadAllText(_file).Should().NotContain("CompletedDownloadFolder");
    }

    [Theory]
    [InlineData("relative", "10G")]
    [InlineData("/tmp", "-1")]
    [InlineData("/tmp", "bad")]
    [InlineData("/tmp", "9999999999999999999T")]
    public void InvalidMonitorIsRejected(string path, string limit)
    {
        var client = new TorrentClientInstanceConfig { FreeSpace = [new() { Path = path, Limit = limit }] };
        StorageConfigMigration.Validate(client).Should().NotBeNull();
    }

    [Fact]
    public void DuplicateNormalizedMonitorPathsAreRejected()
    {
        var path = Path.GetTempPath();
        StorageConfigMigration.Validate(new() { FreeSpace = [new() { Path = path }, new() { Path = path.TrimEnd(Path.DirectorySeparatorChar) }] })
            .Should().Contain("duplicate");
    }

    public void Dispose()
    {
        File.Delete(_file); File.Delete(_file + ".bak");
    }
}

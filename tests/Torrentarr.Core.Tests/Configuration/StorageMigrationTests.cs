using FluentAssertions;
using Torrentarr.Core.Configuration;
using Xunit;

namespace Torrentarr.Core.Tests.Configuration;

public sealed class StorageMigrationTests : IDisposable
{
    private readonly string _file = Path.GetTempFileName();
    private TorrentarrConfig Load(string toml)
    {
        File.WriteAllText(_file, toml);
        return new ConfigurationLoader(_file).Load();
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

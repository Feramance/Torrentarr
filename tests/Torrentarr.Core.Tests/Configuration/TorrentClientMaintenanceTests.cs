using FluentAssertions;
using Torrentarr.Core.Configuration;
using Torrentarr.Core.Services;
using Xunit;

namespace Torrentarr.Core.Tests.Configuration;

public sealed class TorrentClientMaintenanceTests
{
    [Fact]
    public void CanonicalClient_RoundTripsWithoutChangingLegacySections()
    {
        var path = Path.Combine(Path.GetTempPath(), $"torrentarr-{Guid.NewGuid():N}.toml");
        try
        {
            var config = ConfigurationLoader.GenerateDefaultConfig();
            config.QBitInstances["qBit"] = new QBitConfig { Host = "legacy", UserName = "u", Password = "p" };
            config.TorrentClients["seedbox"] = new TorrentClientInstanceConfig
            {
                Type = "qbittorrent",
                Host = "canonical",
                UserName = "u",
                Password = "p",
                Maintenance = new MaintenanceConfig
                {
                    Enabled = true,
                    Scope = "all",
                    PathMappings = [new() { ClientPath = "/downloads", LocalPath = Path.GetTempPath() }],
                }
            };
            config.QBitInstances["qBit"].Maintenance = config.TorrentClients["seedbox"].Maintenance;
            var loader = new ConfigurationLoader(path);
            loader.SaveConfig(config);
            var text = File.ReadAllText(path);
            text.Should().Contain("[qBit]");
            text.Should().Contain("[TorrentClient.seedbox]");

            var loaded = loader.Load();
            loaded.QBitInstances.Should().ContainKey("qBit");
            loaded.TorrentClients["seedbox"].Maintenance.Scope.Should().Be("all");
            foreach (var maintenance in new[] { loaded.TorrentClients["seedbox"].Maintenance, loaded.QBitInstances["qBit"].Maintenance })
            {
                maintenance.PathMappings.Single().ClientPath.Should().Be("/downloads");
            }
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    [Theory]
    [InlineData("qBit")]
    [InlineData("qBit-other")]
    [InlineData("TorrentClient.seedbox")]
    public void RemovedSections_LoadWithoutWritingAndAreOmittedOnSave(string section)
    {
        var path = Path.Combine(Path.GetTempPath(), $"removed-maintenance-{Guid.NewGuid():N}.toml");
        try
        {
            var config = ConfigurationLoader.GenerateDefaultConfig();
            var maintenance = new MaintenanceConfig { PathMappings = [new() { ClientPath = "/downloads", LocalPath = Path.GetTempPath() }] };
            if (section.StartsWith("TorrentClient.")) config.TorrentClients["seedbox"] = new() { Host = "fixture", Maintenance = maintenance };
            else config.QBitInstances[section] = new() { Host = "fixture", Maintenance = maintenance };
            var loader = new ConfigurationLoader(path);
            loader.SaveConfig(config);
            File.AppendAllText(path, $"\n[[{section}.Maintenance.SharePolicies]]\nName = 'old'\nAction = 'delete'\nMaximumRatio = 2\n[[{section}.Maintenance.Notifications]]\nEnabled = true\nUrl = 'https://notify.example'\nToken = 'old-secret'\n");
            var original = File.ReadAllText(path);
            var loaded = loader.Load();
            File.ReadAllText(path).Should().Be(original);
            var client = loaded.GetTorrentClient(section.StartsWith("TorrentClient.") ? "seedbox" : section)!;
            client.Maintenance.PathMappings.Single().ClientPath.Should().Be("/downloads");
            loader.SaveConfig(loaded);
            File.ReadAllText(path).Should().NotContain("SharePolicies").And.NotContain("Notifications").And.NotContain("old-secret");
        }
        finally { File.Delete(path); File.Delete(path + ".bak"); }
    }

    [Fact]
    public void DuplicateLegacyAndCanonicalIds_AreRejected()
    {
        var path = Path.Combine(Path.GetTempPath(), $"torrentarr-{Guid.NewGuid():N}.toml");
        try
        {
            File.WriteAllText(path, """
                [Settings]
                ConfigVersion = "6.14.6"
                [qBit]
                Host = "one"
                [TorrentClient.qBit]
                Type = "qbittorrent"
                Host = "two"
                """);
            var action = () => new ConfigurationLoader(path).Load();
            action.Should().Throw<InvalidDataException>().WithMessage("*both legacy and canonical*");
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    [Theory]
    [InlineData("*/15 * * * *", "2026-09-29T12:30:00Z", true)]
    [InlineData("*/15 * * * *", "2026-09-29T12:31:00Z", false)]
    public void CronSchedule_SupportsSteps(string expression, string instant, bool expected)
        => CronSchedule.Matches(expression, DateTime.Parse(instant).ToUniversalTime()).Should().Be(expected);
}

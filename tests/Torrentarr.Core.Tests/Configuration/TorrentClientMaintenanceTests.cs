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
                Type = "qbittorrent", Host = "canonical", UserName = "u", Password = "p",
                Maintenance = new MaintenanceConfig
                {
                    Enabled = true,
                    Scope = "all",
                    PathMappings = [new() { ClientPath = "/downloads", LocalPath = Path.GetTempPath() }],
                    SharePolicies = [new() { Name = "public", Priority = 10, MaximumRatio = 2, Action = "recycle" }]
                }
            };
            var loader = new ConfigurationLoader(path);
            loader.SaveConfig(config);
            var text = File.ReadAllText(path);
            text.Should().Contain("[qBit]");
            text.Should().Contain("[TorrentClient.seedbox]");

            var loaded = loader.Load();
            loaded.QBitInstances.Should().ContainKey("qBit");
            loaded.TorrentClients["seedbox"].Maintenance.SharePolicies.Should().ContainSingle();
            loaded.TorrentClients["seedbox"].Maintenance.Scope.Should().Be("all");
        }
        finally { if (File.Exists(path)) File.Delete(path); }
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

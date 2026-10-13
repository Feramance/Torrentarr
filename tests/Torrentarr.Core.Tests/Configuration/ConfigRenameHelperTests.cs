using FluentAssertions;
using Newtonsoft.Json.Linq;
using Torrentarr.Core.Configuration;
using Xunit;

namespace Torrentarr.Core.Tests.Configuration;

public class ConfigRenameHelperTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Rename_RestoresOnlyPlaceholders_AndKeepsExplicitSecrets(bool wholeSection)
    {
        var config = new TorrentarrConfig();
        config.QBitInstances["qBit"] = new()
        {
            Password = "password",
            Maintenance = new() { Notifications = [new() { Url = "https://notify.example", Token = "token" }] }
        };
        var renamed = JObject.FromObject(config.QBitInstances["qBit"]);
        renamed["Password"] = "[redacted]";
        renamed["Maintenance"]!["Notifications"]![0]!["Token"] = "[redacted]";
        var changes = wholeSection ? new JObject { ["qBit-renamed"] = renamed }
            : new JObject
            {
                ["qBit-renamed.Password"] = "[redacted]",
                ["qBit-renamed.Maintenance.Notifications"] = renamed["Maintenance"]!["Notifications"]!.DeepClone()
            };
        ConfigRenameHelper.PreserveSecrets(config, changes, new JObject { ["qBit"] = "qBit-renamed" });
        changes["qBit-renamed.Password"]!.Value<string>().Should().Be("password");
        var notifications = changes["qBit-renamed.Maintenance.Notifications"] ?? changes.SelectToken("qBit-renamed.Maintenance.Notifications");
        notifications![0]!["Token"]!.Value<string>().Should().Be("token");
        changes["qBit-renamed.Password"] = "new-password";
        ConfigRenameHelper.PreserveSecrets(config, changes, new JObject { ["qBit"] = "qBit-renamed" });
        changes["qBit-renamed.Password"]!.Value<string>().Should().Be("new-password");
        config.QBitInstances["qBit"].Password.Should().Be("password");
    }

    [Fact]
    public void ArrRename_PreservesExistingApiKey()
    {
        var config = new TorrentarrConfig();
        config.ArrInstances["Radarr-old"] = new() { APIKey = "secret" };
        var changes = new JObject { ["Radarr-new.APIKey"] = "[redacted]" };
        ConfigRenameHelper.PreserveSecrets(config, changes, new JObject { ["Radarr-old"] = "Radarr-new" });
        changes["Radarr-new.APIKey"]!.Value<string>().Should().Be("secret");
    }

    [Theory]
    [InlineData("Settings")]
    [InlineData("qBit-existing")]
    [InlineData("qBit-invalid.name")]
    public void Rename_RejectsInvalidDestination(string destination)
    {
        var config = new TorrentarrConfig();
        config.QBitInstances["qBit"] = new();
        config.QBitInstances["qBit-existing"] = new();
        var action = () => ConfigRenameHelper.PreserveSecrets(config, new JObject(), new JObject { ["qBit"] = destination });
        action.Should().Throw<ArgumentException>();
    }
}

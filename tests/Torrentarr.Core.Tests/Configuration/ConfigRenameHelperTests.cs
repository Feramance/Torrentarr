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
        };
        var renamed = JObject.FromObject(config.QBitInstances["qBit"]);
        renamed["Password"] = "[redacted]";
        var changes = wholeSection ? new JObject { ["qBit-renamed"] = renamed }
            : new JObject
            {
                ["qBit-renamed.Password"] = "[redacted]",
            };
        ConfigRenameHelper.PreserveSecrets(config, changes, new JObject { ["qBit"] = "qBit-renamed" });
        changes["qBit-renamed.Password"]!.Value<string>().Should().Be("password");
        changes["qBit-renamed.Password"] = "new-password";
        ConfigRenameHelper.PreserveSecrets(config, changes, new JObject { ["qBit"] = "qBit-renamed" });
        changes["qBit-renamed.Password"]!.Value<string>().Should().Be("new-password");
        config.QBitInstances["qBit"].Password.Should().Be("password");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CanonicalRename_RestoresNestedSecretsAndKeepsExplicitPassword(bool dotted)
    {
        var config = new TorrentarrConfig();
        config.TorrentClients["old"] = new()
        {
            Password = "secret",
        };
        var section = JObject.FromObject(config.TorrentClients["old"], Newtonsoft.Json.JsonSerializer.Create(new Newtonsoft.Json.JsonSerializerSettings
        { ContractResolver = new Newtonsoft.Json.Serialization.CamelCasePropertyNamesContractResolver() }));
        section["password"] = "[redacted]";
        var changes = dotted ? new JObject { ["TorrentClient.new"] = section }
            : new JObject { ["TorrentClient"] = new JObject { ["new"] = section } };
        ConfigRenameHelper.PreserveSecrets(config, changes, new JObject { ["old"] = "new" });
        changes["TorrentClient.new.Password"]!.Value<string>().Should().Be("secret");
        changes["TorrentClient.new.Password"] = "replacement";
        ConfigRenameHelper.PreserveSecrets(config, changes, new JObject { ["old"] = "new" });
        changes["TorrentClient.new.Password"]!.Value<string>().Should().Be("replacement");
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

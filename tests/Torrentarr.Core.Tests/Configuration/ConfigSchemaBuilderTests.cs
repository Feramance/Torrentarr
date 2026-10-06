using System.Text.Json;
using FluentAssertions;
using Torrentarr.Core.Configuration;
using Xunit;

namespace Torrentarr.Core.Tests.Configuration;

public class ConfigSchemaBuilderTests
{
    [Fact]
    public void Registry_MatchesPinnedQbitrr5145Inventory()
    {
        ConfigSchemaBuilder.Sections.Keys.Should().BeEquivalentTo(["Settings", "WebUI", "qBit", "TorrentClient", "Arr"]);
        var fields = ConfigSchemaBuilder.Sections.SelectMany(section => section.Value).ToList();

        fields.Where(field => field.section != "TorrentClient").Should().HaveCount(124);
        fields.Select(field => $"{field.section}.{field.key}").Should().OnlyHaveUniqueItems();
        fields.Should().Contain(field => field.section == "Arr" && field.key == "Torrent.FileExtensionAllowlist");
        fields.Should().Contain(field => field.section == "Arr" && field.arrKinds != null && field.arrKinds.Contains("readarr"));
        fields.Should().Contain(field => field.section == "WebUI" && field.key.StartsWith("OIDC.") && field.secure);
        fields.Should().Contain(field => field.section == "qBit" && field.key == "CategorySeeding.HitAndRunMode");
        fields.Should().Contain(field => field.section == "TorrentClient" && field.key == "Maintenance.Enabled");
    }

    [Fact]
    public void Registry_UsesTorrentarrSchemaVersionAndQbitrrWireNames()
    {
        var json = JsonSerializer.SerializeToElement(ConfigSchemaBuilder.Build());
        var configVersion = json.GetProperty("sections").GetProperty("Settings")
            .EnumerateArray().Single(field => field.GetProperty("key").GetString() == "ConfigVersion");

        configVersion.GetProperty("default").GetString().Should().Be("6.14.6");
        configVersion.TryGetProperty("path", out _).Should().BeTrue();
        configVersion.TryGetProperty("secure", out _).Should().BeTrue();
        configVersion.TryGetProperty("requiresRestart", out _).Should().BeTrue();
    }

    [Fact]
    public void Registry_HasNoImpossibleNumericBounds()
    {
        ConfigSchemaBuilder.Sections.SelectMany(section => section.Value)
            .Where(field => field.minimum.HasValue && field.maximum.HasValue)
            .Should().OnlyContain(field => field.minimum <= field.maximum);
    }
}

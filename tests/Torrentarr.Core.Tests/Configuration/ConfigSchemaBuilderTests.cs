using System.Text.Json;
using FluentAssertions;
using Torrentarr.Core.Configuration;
using Xunit;

namespace Torrentarr.Core.Tests.Configuration;

public class ConfigSchemaBuilderTests
{
    [Fact]
    public void MaintenanceRegistry_CoversEveryPersistentPropertyAndCollectionItem()
    {
        var fields = ConfigSchemaBuilder.Sections["TorrentClient"];
        void Check(Type type, string prefix)
        {
            foreach (var property in type.GetProperties().Where(p => !p.IsDefined(typeof(Tomlyn.Serialization.TomlIgnoreAttribute), true)))
            {
                var key = prefix + property.Name;
                var propertyType = property.PropertyType;
                if (propertyType == typeof(string) || propertyType.IsPrimitive)
                    fields.Should().Contain(f => f.key == key);
                else if (propertyType.IsGenericType && propertyType.GetGenericTypeDefinition() == typeof(List<>))
                {
                    var field = fields.Single(f => f.key == key);
                    var itemType = propertyType.GetGenericArguments()[0];
                    if (itemType != typeof(string))
                        field.itemFields!.Select(f => f.key).Should().BeEquivalentTo(itemType.GetProperties().Select(p => p.Name));
                }
                else if (propertyType.IsGenericType && propertyType.GetGenericTypeDefinition() == typeof(Dictionary<,>))
                    foreach (var operation in Enum.GetValues<Torrentarr.Core.Models.MaintenanceOperation>()) Check(typeof(MaintenanceOperationPolicy), key + "." + operation + ".");
                else if (Nullable.GetUnderlyingType(propertyType) is { } nullable && nullable.IsPrimitive)
                    fields.Should().Contain(f => f.key == key);
                else Check(propertyType, key + ".");
            }
        }
        Check(typeof(MaintenanceConfig), "Maintenance.");
        fields.Should().NotContain(f => f.key == "Maintenance.Armed");
    }

    [Theory]
    [InlineData("*/15 * * * *", true)]
    [InlineData("0 0 29 2 *", true)]
    [InlineData("0,30 1-5/2 * * 0,7", true)]
    [InlineData("60 * * * *", false)]
    [InlineData("* * * *", false)]
    [InlineData("*/0 * * * *", false)]
    [InlineData("5-2 * * * *", false)]
    [InlineData("* * * * nonsense", false)]
    public void MaintenanceValidation_ChecksScheduleSyntax(string schedule, bool valid)
    {
        var error = MaintenanceConfigValidation.Validate(new MaintenanceConfig { Schedule = schedule });
        (error == null).Should().Be(valid);
    }

    [Fact]
    public void MaintenanceValidation_RejectsInvalidScopeAndLimits()
    {
        MaintenanceConfigValidation.Validate(new MaintenanceConfig { Scope = "unknown" }).Should().NotBeNull();
        MaintenanceConfigValidation.Validate(new MaintenanceConfig { Repair = new() { MaxPerRun = -1 } }).Should().NotBeNull();
        MaintenanceConfigValidation.Validate(new MaintenanceConfig { CategoryTransitions = [new() { From = "a", To = "b", DelayMinutes = -1 }] }).Should().NotBeNull();
    }

    [Fact]
    public void Registry_MatchesPinnedQbitrr5145Inventory()
    {
        ConfigSchemaBuilder.Sections.Keys.Should().BeEquivalentTo(["Settings", "WebUI", "qBit", "TorrentClient", "Arr", "TrackerProtection"]);
        var fields = ConfigSchemaBuilder.Sections.SelectMany(section => section.Value).ToList();

        fields.Should().NotContain(field => field.section == "Settings" && field.key == "FreeSpace");
        fields.Should().Contain(field => field.section == "qBit" && field.key == "FreeSpace");
        fields.Select(field => $"{field.section}.{field.key}").Should().OnlyHaveUniqueItems();
        fields.Should().Contain(field => field.section == "Arr" && field.key == "Torrent.FileExtensionAllowlist");
        fields.Should().Contain(field => field.section == "Arr" && field.arrKinds != null && field.arrKinds.Contains("readarr"));
        fields.Should().Contain(field => field.section == "WebUI" && field.key.StartsWith("OIDC.") && field.secure);
        fields.Should().Contain(field => field.section == "qBit" && field.key == "CategorySeeding.HitAndRunMode");
        fields.Should().Contain(field => field.section == "TorrentClient" && field.key == "Maintenance.Enabled");
    }

    [Fact]
    public void TorrentClients_ExposeEverySharedTorrentSetting()
    {
        var sharedKeys = ConfigSchemaBuilder.Sections["qBit"].Select(field => field.key);
        ConfigSchemaBuilder.Sections["TorrentClient"].Select(field => field.key)
            .Should().Contain(sharedKeys);
    }

    [Fact]
    public void Registry_UsesTorrentarrSchemaVersionAndQbitrrWireNames()
    {
        var json = JsonSerializer.SerializeToElement(ConfigSchemaBuilder.Build());
        var configVersion = json.GetProperty("sections").GetProperty("Settings")
            .EnumerateArray().Single(field => field.GetProperty("key").GetString() == "ConfigVersion");

        configVersion.GetProperty("default").GetString().Should().Be(ConfigurationLoader.ExpectedConfigVersion);
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

using FluentAssertions;
using Tomlyn;
using Tomlyn.Model;
using Torrentarr.Core.Configuration;
using Xunit;

namespace Torrentarr.Core.Tests.Configuration;

public class MaintenanceSimplificationTests
{
    [Theory]
    [InlineData("qBit-test", "qBit-test")]
    [InlineData("TorrentClient.seedbox", "seedbox")]
    public void InterimFieldsFillOnlyMissingCanonicalValues_AndMigrationIsIdempotent(string section, string id)
    {
        var root = TomlSerializer.Deserialize<TomlTable>($$"""
            [{{section}}.Maintenance]
            Enabled = false
            MinimumTorrentAgeMinutes = 22
            [{{section}}.Maintenance.Orphans]
            Enabled = false
            MaxFilesPerRun = 0
            [{{section}}.Maintenance.ErrorRecovery]
            Outcome = 'recycle'
            Enabled = false
            MaxPerRun = 0
            [{{section}}.Maintenance.Operations.ErrorRecovery]
            ExecutionMode = 'session'
            Schedule = '1 2 * * *'
            IncludedCategories = ['movies']
            [{{section}}.Storage]
            AutomaticCleanup = true
            [{{section}}.Storage.Orphans]
            Enabled = true
            MaxFilesPerRun = 9
            MinimumAgeMinutes = 777
            [{{section}}.Recovery]
            Enabled = true
            MaxPerRun = 8
            MinimumTorrentAgeMinutes = 3
            IncludedCategories = ['tv']
            ExcludedTags = ['keep']
            """)!;
        MaintenanceSimplificationMigration.Migrate(root).Should().BeTrue();
        MaintenanceSimplificationMigration.Migrate(root).Should().BeFalse();
        var table = section.StartsWith("TorrentClient.") ? (TomlTable)((TomlTable)root["TorrentClient"])[id] : (TomlTable)root[id];
        var client = TomlSerializer.Deserialize<TorrentClientInstanceConfig>(TomlSerializer.Serialize(table))!;
        var cfg = client.Maintenance;
        cfg.Enabled.Should().BeFalse(); cfg.MinimumTorrentAgeMinutes.Should().Be(22);
        cfg.Orphans.Enabled.Should().BeFalse(); cfg.Orphans.MaxFilesPerRun.Should().Be(0); cfg.Orphans.MinimumAgeMinutes.Should().Be(777);
        cfg.ErrorRecovery.Enabled.Should().BeFalse();
        cfg.Operations["ErrorRecovery"].Enabled.Should().BeFalse();
        cfg.ErrorRecovery.Outcome.Should().Be("recycle"); cfg.ErrorRecovery.MaxPerRun.Should().Be(0);
        var policy = cfg.Operations["ErrorRecovery"];
        policy.ExecutionMode.Should().Be("manual"); policy.Schedule.Should().Be("1 2 * * *");
        policy.IncludedCategories.Should().Equal("movies"); policy.ExcludedTags.Should().Equal("keep");
        table.Should().NotContainKey("Storage").And.NotContainKey("Recovery");
        cfg.Armed.Should().BeFalse();
    }

    [Theory]
    [InlineData(false, "manual")]
    [InlineData(true, "automatic")]
    public void DailyCleanupMapsOnlyItsOwnPolicies(bool enabled, string mode)
    {
        var root = TomlSerializer.Deserialize<TomlTable>($"[qBit.Storage]\nAutomaticCleanup = {enabled.ToString().ToLowerInvariant()}")!;
        MaintenanceSimplificationMigration.Migrate(root).Should().BeTrue();
        var cfg = TomlSerializer.Deserialize<TorrentClientInstanceConfig>(TomlSerializer.Serialize(root["qBit"]))!.Maintenance;
        cfg.Enabled.Should().Be(enabled);
        cfg.Operations["OrphanScan"].ExecutionMode.Should().Be(mode);
        cfg.Operations["RecycleRetention"].ExecutionMode.Should().Be(mode);
        cfg.Operations["RecycleRetention"].Enabled.Should().Be(enabled);
        cfg.Hardlinks.Enabled.Should().BeFalse(); cfg.ErrorRecovery.Enabled.Should().BeFalse(); cfg.Repair.Enabled.Should().BeFalse();
    }

    [Fact]
    public void OriginalMaintenanceIsUntouchedIncludingFiltersOutcomesAndSchedules()
    {
        var root = TomlSerializer.Deserialize<TomlTable>("""
            [qBit.Maintenance]
            Enabled = true
            Scope = 'explicit'
            ExplicitCategories = ['movies']
            [qBit.Maintenance.ErrorRecovery]
            Outcome = 'recycle'
            [qBit.Maintenance.Operations.ErrorRecovery]
            Enabled = true
            ExecutionMode = 'automatic'
            Schedule = '*/7 * * * *'
            MaxActionsPerRun = 0
            IncludedCategories = ['tv']
            """)!;
        var original = TomlSerializer.Serialize(root);
        MaintenanceSimplificationMigration.Migrate(root).Should().BeFalse();
        TomlSerializer.Serialize(root).Should().Be(original);
    }

    [Theory]
    [InlineData("qBit-test", "session", true, "manual")]
    [InlineData("TorrentClient.seedbox", "session", true, "manual")]
    [InlineData("qBit-test", "manual", false, "manual")]
    [InlineData("TorrentClient.seedbox", "automatic", false, "automatic")]
    public void ModesMigrateLosslesslyAndRoundTrip(string section, string mode, bool changed, string expected)
    {
        var root = TomlSerializer.Deserialize<TomlTable>($$"""
            [{{section}}.Maintenance]
            Enabled = false
            [{{section}}.Maintenance.Operations.PrivateTagging]
            Enabled = false
            ExecutionMode = '{{mode}}'
            Schedule = '*/7 * * * *'
            MaxActionsPerRun = 0
            IncludedCategories = ['movies']
            ExcludedTags = ['keep']
            """)!;
        var original = TomlSerializer.Serialize(root);
        MaintenanceSimplificationMigration.Migrate(root).Should().Be(changed);
        TomlSerializer.Serialize(root).Should().Be(original.Replace("'session'", "'manual'").Replace("\"session\"", "\"manual\""));
        var saved = TomlSerializer.Serialize(root);
        root = TomlSerializer.Deserialize<TomlTable>(saved)!;
        MaintenanceSimplificationMigration.Migrate(root).Should().BeFalse();
        TomlSerializer.Serialize(root).Should().Be(saved);
        saved.Should().Contain(expected);
    }

    [Fact]
    public void MissingPoliciesAreManualAndNewSessionSettingsAreRejected()
    {
        var cfg = new MaintenanceConfig();
        foreach (var operation in Enum.GetValues<Torrentarr.Core.Models.MaintenanceOperation>())
            MaintenancePolicies.Mode(cfg, operation).Should().Be("manual");
        cfg.Operations["PrivateTagging"] = new() { ExecutionMode = "session" };
        MaintenanceConfigValidation.Validate(cfg).Should().Contain("execution mode");
    }

    [Theory]
    [InlineData("[qBit.Storage]\nAutomaticCleanup = 1")]
    [InlineData("[qBit.Recovery]\nEnabled = 'true'")]
    [InlineData("[qBit]\nStorage = 'bad'")]
    public void InvalidInterimConfigurationIsRejected(string text)
    {
        var root = TomlSerializer.Deserialize<TomlTable>(text)!;
        var act = () => MaintenanceSimplificationMigration.Migrate(root);
        act.Should().Throw<InvalidDataException>();
    }
}

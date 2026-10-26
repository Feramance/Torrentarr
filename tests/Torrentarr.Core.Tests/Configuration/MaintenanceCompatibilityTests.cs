using System.Text.RegularExpressions;
using FluentAssertions;
using Torrentarr.Core.Configuration;
using Xunit;

namespace Torrentarr.Core.Tests.Configuration;

public sealed class MaintenanceCompatibilityTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "torrentarr-maintenance-config-" + Guid.NewGuid().ToString("N"));
    private string ConfigPath => Path.Combine(_folder, "config.toml");
    public MaintenanceCompatibilityTests() => Directory.CreateDirectory(_folder);
    public void Dispose() => Directory.Delete(_folder, true);
    private static string Repository()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory != null && !File.Exists(Path.Combine(directory.FullName, "config.example.toml"))) directory = directory.Parent;
        return directory?.FullName ?? throw new DirectoryNotFoundException("Repository root not found.");
    }

    [Theory]
    [InlineData("qBit-test", "qBit-test")]
    [InlineData("TorrentClient.seedbox", "seedbox")]
    public void MaintenanceAndProtection_RoundTripLegacyAndCanonical(string section, string id)
    {
        File.WriteAllText(ConfigPath, $$"""
            [Settings]
            ConfigVersion = "{{ConfigurationLoader.ExpectedConfigVersion}}"
            [{{section}}]
            Host = 'localhost'
            Port = 8080
            Password = 'retained-secret'
            [{{section}}.Maintenance]
            Enabled = true
            [{{section}}.Maintenance.Operations.ErrorRecovery]
            Enabled = true
            ExecutionMode = 'manual'
            Schedule = '*/5 * * * *'
            MaxActionsPerRun = 0
            ExcludedTags = ['deliberate-pause']
            [{{section}}.Maintenance.ErrorRecovery]
            Outcome = 'keep'
            [[{{section}}.Maintenance.CategoryMappings]]
            ClientPath = '/downloads'
            Category = 'movies'
            [[{{section}}.Maintenance.CategoryTransitions]]
            From = 'movies'
            To = 'seed'
            DelayMinutes = 60
            [[{{section}}.Maintenance.TrackerAssignments]]
            TrackerContains = 'tracker.example'
            Category = ''
            Tags = ['private', 'keep']
            [[{{section}}.Trackers]]
            URI = 'https://tracker.example'
            Protection = { Policy = 'onlyencodes', AdditionalSeedHours = 7.0, AccountingGraceMinutes = 90 }
            """);
        var loader = new ConfigurationLoader(ConfigPath);
        var config = loader.Load();
        var client = config.GetTorrentClient(id)!;
        client.Maintenance.Operations["ErrorRecovery"].MaxActionsPerRun.Should().Be(0);
        client.Maintenance.Operations["ErrorRecovery"].ExcludedTags.Should().Contain("deliberate-pause");
        client.Maintenance.CategoryMappings.Should().ContainSingle();
        client.Maintenance.CategoryTransitions.Should().ContainSingle();
        client.Maintenance.TrackerAssignments.Should().ContainSingle();
        loader.SaveConfig(config);
        var reopened = loader.Load().GetTorrentClient(id)!;
        reopened.Maintenance.Should().BeEquivalentTo(client.Maintenance);
        reopened.Trackers.Single().Protection.Should().BeEquivalentTo(client.Trackers.Single().Protection);
        reopened.Password.Should().Be("retained-secret");
        File.ReadAllText(ConfigPath).Should().Contain(".Maintenance");
    }

    [Theory]
    [InlineData("qBit-test", "qBit-test")]
    [InlineData("TorrentClient.seedbox", "seedbox")]
    public void SessionModeIsMigratedBeforeValidationAndSurvivesRepeatedSave(string section, string id)
    {
        File.WriteAllText(ConfigPath, $$"""
            [Settings]
            ConfigVersion = '6.20.1'
            [{{section}}]
            Host = 'localhost'
            Password = 'retained-secret'
            [{{section}}.Maintenance]
            Enabled = true
            [{{section}}.Maintenance.Operations.PrivateTagging]
            Enabled = false
            ExecutionMode = 'session'
            Schedule = '*/7 * * * *'
            MaxActionsPerRun = 0
            ExcludedTags = ['keep']
            """);
        var loader = new ConfigurationLoader(ConfigPath);
        for (var attempt = 0; attempt < 3; attempt++)
        {
            var config = loader.Load();
            config.Settings.ConfigVersion.Should().Be("6.20.2");
            var client = config.GetTorrentClient(id)!;
            var policy = client.Maintenance.Operations["PrivateTagging"];
            policy.ExecutionMode.Should().Be("manual");
            policy.Enabled.Should().BeFalse();
            policy.Schedule.Should().Be("*/7 * * * *");
            policy.MaxActionsPerRun.Should().Be(0);
            policy.ExcludedTags.Should().Equal("keep");
            client.Password.Should().Be("retained-secret");
            loader.SaveConfig(config);
        }
    }

    [Fact]
    public void GeneratedDocumentation_CoversEveryPersistentControl()
    {
        var text = File.ReadAllText(Path.Combine(Repository(), "docs/configuration/maintenance-controls.md"));
        foreach (var field in ConfigSchemaBuilder.Sections["TorrentClient"].Where(f => f.key.StartsWith("Maintenance.")))
        {
            text.Should().Contain("`" + field.key + "`");
            foreach (var item in field.itemFields ?? []) text.Should().Contain("`" + field.key + "[]." + item.key + "`");
        }
        foreach (var property in typeof(TrackerProtectionConfig).GetProperties())
        {
            ConfigSchemaBuilder.Sections["TrackerProtection"].Should().Contain(f => f.key == property.Name);
            text.Should().Contain("Trackers[].Protection." + property.Name);
        }
    }

    [Fact]
    public void CurrentDocumentationToml_ParsesWithProjectLoader()
    {
        var root = Path.Combine(Repository(), "docs");
        var failures = new List<string>();
        foreach (var path in Directory.EnumerateFiles(root, "*.md", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(root, path).Replace('\\', '/');
            if (relative.StartsWith("parity/") || relative.StartsWith("reviews/") || relative.StartsWith("development/pr-")
                || relative.StartsWith("development/webui-parity") || relative.StartsWith("development/webui-source-reconciliation")) continue;
            foreach (Match match in Regex.Matches(File.ReadAllText(path), "```toml\\n(.*?)```", RegexOptions.Singleline))
            {
                File.WriteAllText(ConfigPath, match.Groups[1].Value);
                try { new ConfigurationLoader(ConfigPath).Load(); }
                catch (Exception error) { failures.Add(relative + ": " + error.Message); }
            }
        }
        failures.Should().BeEmpty("current documented TOML examples must parse with the project loader");
    }

    [Fact]
    public void CookbookExamples_UseCanonicalMaintenance()
    {
        var text = File.ReadAllText(Path.Combine(Repository(), "docs/features/maintenance-examples.md"));
        text.Should().NotContain(".Storage.").And.NotContain(".Recovery]");
        foreach (Match match in Regex.Matches(text, "```toml\\n(.*?)```", RegexOptions.Singleline))
        {
            File.WriteAllText(ConfigPath, match.Groups[1].Value);
            var client = new ConfigurationLoader(ConfigPath).Load().GetTorrentClient("seedbox")!;
            StorageConfigurationValidation.Validate(client).Should().BeNull();
            client.Maintenance.Armed.Should().BeFalse();
        }
    }

    [Fact]
    public void CurrentMaintenanceExamples_ParseWithProjectLoader()
    {
        var root = Repository();
        foreach (var file in new[] { "docs/configuration/maintenance.md", "docs/configuration/seeding.md", "docs/webui/config-editor.md", "docs/features/disk-space.md" })
            foreach (Match match in Regex.Matches(File.ReadAllText(Path.Combine(root, file)), "```toml\\n(.*?)```", RegexOptions.Singleline))
            {
                File.WriteAllText(ConfigPath, match.Groups[1].Value);
                var act = () => new ConfigurationLoader(ConfigPath).Load();
                act.Should().NotThrow("documented TOML must load: {0}", file);
            }
        // The shipped example uses Linux local paths; adapt its monitor to the test platform.
        var example = File.ReadAllText(Path.Combine(root, "config.example.toml"));
        example = example.Replace("Path = \"/downloads\"", "Path = '" + _folder.Replace('\\', '/') + "'");
        File.WriteAllText(ConfigPath, example);
        new ConfigurationLoader(ConfigPath).Load().GetTorrentClient("qBit")!.Maintenance.Enabled.Should().BeFalse();
    }
}

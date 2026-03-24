using FluentAssertions;
using Torrentarr.Core.Configuration;
using Xunit;

namespace Torrentarr.Core.Tests.Configuration;

public class ConfigurationLoaderTests : IDisposable
{
    private readonly string _tempFilePath;

    public ConfigurationLoaderTests()
    {
        _tempFilePath = Path.GetTempFileName();
    }

    public void Dispose()
    {
        if (File.Exists(_tempFilePath))
            File.Delete(_tempFilePath);
    }

    private void WriteToml(string content) => File.WriteAllText(_tempFilePath, content);

    [Fact]
    public void Load_ParsesSettings_CorrectValues()
    {
        WriteToml("""
            [Settings]
            LoopSleepTimer = 10
            FailedCategory = "my-failed"
            PingURLS = ["8.8.8.8", "1.1.1.1"]
            """);

        var config = new ConfigurationLoader(_tempFilePath).Load();

        config.Settings.LoopSleepTimer.Should().Be(10);
        config.Settings.FailedCategory.Should().Be("my-failed");
        config.Settings.PingURLS.Should().BeEquivalentTo(new[] { "8.8.8.8", "1.1.1.1" });
    }

    [Fact]
    public void Load_ParsesQBit_WithCategorySeeding()
    {
        WriteToml("""
            [qBit]
            Host = "192.168.1.100"
            Port = 8090

            [qBit.CategorySeeding]
            MaxUploadRatio = 2.5
            HitAndRunMode = true
            """);

        var config = new ConfigurationLoader(_tempFilePath).Load();

        config.QBitInstances.Should().ContainKey("qBit");
        config.QBitInstances["qBit"].Host.Should().Be("192.168.1.100");
        config.QBitInstances["qBit"].Port.Should().Be(8090);
        config.QBitInstances["qBit"].CategorySeeding.MaxUploadRatio.Should().BeApproximately(2.5, 0.001);
        config.QBitInstances["qBit"].CategorySeeding.HitAndRunMode.Should().Be("and"); // legacy true → "and"
    }

    [Fact]
    public void Load_ParsesMultipleArrInstances()
    {
        WriteToml("""
            [Radarr-Movies]
            URI = "http://radarr:7878"
            APIKey = "radarr-key"
            Category = "radarr"

            [Sonarr-TV]
            URI = "http://sonarr:8989"
            APIKey = "sonarr-key"
            Category = "sonarr"
            """);

        var config = new ConfigurationLoader(_tempFilePath).Load();

        config.ArrInstances.Should().ContainKey("Radarr-Movies");
        config.ArrInstances.Should().ContainKey("Sonarr-TV");

        config.ArrInstances["Radarr-Movies"].URI.Should().Be("http://radarr:7878");
        config.ArrInstances["Radarr-Movies"].APIKey.Should().Be("radarr-key");
        config.ArrInstances["Radarr-Movies"].Type.Should().Be("radarr");

        config.ArrInstances["Sonarr-TV"].URI.Should().Be("http://sonarr:8989");
        config.ArrInstances["Sonarr-TV"].Type.Should().Be("sonarr");
    }

    [Fact]
    public void Load_ParsesAdditionalQbitInstance_qBitDash()
    {
        WriteToml("""
            [qBit-backup]
            Host = "192.168.1.200"
            Port = 8091
            UserName = "admin"
            Password = "secret"
            """);

        var config = new ConfigurationLoader(_tempFilePath).Load();

        config.QBitInstances.Should().ContainKey("qBit-backup");
        config.QBitInstances["qBit-backup"].Host.Should().Be("192.168.1.200");
        config.QBitInstances["qBit-backup"].Port.Should().Be(8091);
    }

    [Fact]
    public void Load_ReturnsSectionDefaults_WhenSectionMissing()
    {
        WriteToml("""
            [Settings]
            LoopSleepTimer = 5
            """);

        var config = new ConfigurationLoader(_tempFilePath).Load();

        // WebUI defaults when [WebUI] omitted
        config.WebUI.Port.Should().Be(6969);
        config.WebUI.Theme.Should().Be("Dark");
        config.WebUI.LiveArr.Should().BeTrue();
    }

    [Fact]
    public void Load_Throws_WhenFileDoesNotExist()
    {
        var nonExistent = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString() + ".toml");

        var act = () => new ConfigurationLoader(nonExistent).Load();

        act.Should().Throw<FileNotFoundException>();
    }

    [Fact]
    public void GetDefaultConfigPath_ReturnsNonNullString()
    {
        var path = ConfigurationLoader.GetDefaultConfigPath();
        path.Should().NotBeNull();
        path.Should().NotBeEmpty();
    }

    // --- Migration 3: Process Restart Settings ---

    [Fact]
    public void Load_Migration3_AddsProcessRestartDefaults_WhenMissing()
    {
        WriteToml("""
            [Settings]
            ConfigVersion = "0.0.2"
            LoopSleepTimer = 5
            """);

        var config = new ConfigurationLoader(_tempFilePath).Load();

        config.Settings.AutoRestartProcesses.Should().BeTrue();
        config.Settings.MaxProcessRestarts.Should().Be(5);
        config.Settings.ProcessRestartWindow.Should().Be(300);
        config.Settings.ProcessRestartDelay.Should().Be(5);
    }

    [Fact]
    public void Load_Migration3_PreservesExistingRestartSettings()
    {
        WriteToml("""
            [Settings]
            ConfigVersion = "0.0.2"
            AutoRestartProcesses = false
            MaxProcessRestarts = 10
            """);

        var config = new ConfigurationLoader(_tempFilePath).Load();

        config.Settings.AutoRestartProcesses.Should().BeFalse();
        config.Settings.MaxProcessRestarts.Should().Be(10);
    }

    // --- Migration 4: qBit Category Settings ---

    [Fact]
    public void Load_Migration4_AddsCategorySeedingDefaults_WhenMissing()
    {
        WriteToml("""
            [Settings]
            ConfigVersion = "0.0.3"

            [qBit]
            Host = "localhost"
            Port = 8080
            """);

        var config = new ConfigurationLoader(_tempFilePath).Load();

        config.QBitInstances["qBit"].ManagedCategories.Should().NotBeNull();
        config.QBitInstances["qBit"].CategorySeeding.Should().NotBeNull();
        config.QBitInstances["qBit"].CategorySeeding.HitAndRunMode.Should().Be("disabled");
        config.QBitInstances["qBit"].CategorySeeding.MinSeedRatio.Should().Be(1.0);
    }

    [Fact]
    public void Load_Migration4_AddsCategorySeedingToAllInstances()
    {
        WriteToml("""
            [Settings]
            ConfigVersion = "0.0.3"

            [qBit]
            Host = "localhost"

            [qBit-seedbox]
            Host = "seedbox.example.com"
            """);

        var config = new ConfigurationLoader(_tempFilePath).Load();

        config.QBitInstances["qBit"].CategorySeeding.Should().NotBeNull();
        config.QBitInstances["qBit-seedbox"].CategorySeeding.Should().NotBeNull();
    }

    // --- Migration 5: HnR Settings Promotion ---

    [Fact]
    public void Load_Migration5_RemovesHnrFromSeedingMode()
    {
        WriteToml("""
            [Settings]
            ConfigVersion = "0.0.4"

            [qBit]
            Host = "localhost"

            [Radarr-Movies]
            URI = "http://radarr:7878"
            APIKey = "key"
            Category = "radarr"

            [Radarr-Movies.Torrent.SeedingMode]
            MaxUploadRatio = 2.0
            HitAndRunMode = "and"
            MinSeedRatio = 1.5
            """);

        var config = new ConfigurationLoader(_tempFilePath).Load();

        // SeedingMode should exist but HnR fields should be gone (they were removed from raw TOML)
        // The C# parser will use defaults since the fields were removed
        var seedingMode = config.ArrInstances["Radarr-Movies"].Torrent.SeedingMode;
        seedingMode.Should().NotBeNull();
        seedingMode!.MaxUploadRatio.Should().Be(2.0); // Non-HnR field preserved
    }

    [Fact]
    public void Load_Migration5_PromotesArrTrackersToQBit()
    {
        WriteToml("""
            [Settings]
            ConfigVersion = "0.0.4"

            [qBit]
            Host = "localhost"

            [Radarr-Movies]
            URI = "http://radarr:7878"
            APIKey = "key"
            Category = "radarr"

            [[Radarr-Movies.Torrent.Trackers]]
            URI = "https://tracker1.example.com/announce"
            MaxUploadRatio = 3.0
            """);

        var config = new ConfigurationLoader(_tempFilePath).Load();

        // Tracker should be promoted to qBit.Trackers
        config.QBitInstances["qBit"].Trackers.Should().NotBeEmpty();
        config.QBitInstances["qBit"].Trackers[0].Uri.Should().Be("https://tracker1.example.com/announce");
    }

    // --- Migration 6: HitAndRunClearMode Consolidation ---

    [Fact]
    public void Load_Migration6_ConsolidatesHitAndRunClearMode()
    {
        WriteToml("""
            [qBit]
            Host = "localhost"

            [qBit.CategorySeeding]
            HitAndRunMode = true
            HitAndRunClearMode = "or"
            """);

        var config = new ConfigurationLoader(_tempFilePath).Load();

        // HitAndRunClearMode should take priority and be consolidated into HitAndRunMode
        config.QBitInstances["qBit"].CategorySeeding.HitAndRunMode.Should().Be("or");
    }

    [Fact]
    public void Load_Migration6_BoolHitAndRunMode_WithoutClearMode_BecomesAnd()
    {
        WriteToml("""
            [qBit]
            Host = "localhost"

            [qBit.CategorySeeding]
            HitAndRunMode = true
            """);

        var config = new ConfigurationLoader(_tempFilePath).Load();

        config.QBitInstances["qBit"].CategorySeeding.HitAndRunMode.Should().Be("and");
    }

    [Fact]
    public void Load_Migration6_BoolFalseHitAndRunMode_BecomesDisabled()
    {
        WriteToml("""
            [qBit]
            Host = "localhost"

            [qBit.CategorySeeding]
            HitAndRunMode = false
            """);

        var config = new ConfigurationLoader(_tempFilePath).Load();

        config.QBitInstances["qBit"].CategorySeeding.HitAndRunMode.Should().Be("disabled");
    }

    [Fact]
    public void Load_Migration6_ClearModeOnTrackers()
    {
        WriteToml("""
            [qBit]
            Host = "localhost"

            [Radarr-Movies]
            URI = "http://radarr:7878"
            APIKey = "key"
            Category = "radarr"

            [[Radarr-Movies.Torrent.Trackers]]
            URI = "https://tracker.example.com/announce"
            HitAndRunMode = true
            HitAndRunClearMode = "and"
            """);

        var config = new ConfigurationLoader(_tempFilePath).Load();

        config.ArrInstances["Radarr-Movies"].Torrent.Trackers[0].HitAndRunMode.Should().Be("and");
    }

    // --- ValidateAndFillConfig ---

    [Fact]
    public void Load_ValidateAndFill_FillsMissingSettingsDefaults()
    {
        WriteToml("""
            [Settings]
            LoopSleepTimer = 10
            """);

        var config = new ConfigurationLoader(_tempFilePath).Load();

        // Missing fields should be filled with defaults
        config.Settings.ConsoleLevel.Should().Be("INFO");
        config.Settings.FailedCategory.Should().Be("failed");
        config.Settings.RecheckCategory.Should().Be("recheck");
        config.Settings.Tagless.Should().BeFalse();
        config.Settings.AutoPauseResume.Should().BeTrue();
        config.Settings.FFprobeAutoUpdate.Should().BeTrue();
    }

    [Fact]
    public void Load_ValidateAndFill_FillsMissingWebUIDefaults()
    {
        WriteToml("""
            [Settings]
            LoopSleepTimer = 5
            """);

        var config = new ConfigurationLoader(_tempFilePath).Load();

        config.WebUI.Host.Should().Be("0.0.0.0");
        config.WebUI.Port.Should().Be(6969);
        config.WebUI.Theme.Should().Be("Dark");
        config.WebUI.ViewDensity.Should().Be("Comfortable");
        config.WebUI.LiveArr.Should().BeTrue();
    }

    [Fact]
    public void Load_ValidateAndFill_NormalizesThemeCasing()
    {
        WriteToml("""
            [WebUI]
            Theme = "dark"
            ViewDensity = "compact"
            """);

        var config = new ConfigurationLoader(_tempFilePath).Load();

        config.WebUI.Theme.Should().Be("Dark");
        config.WebUI.ViewDensity.Should().Be("Compact");
    }

    [Fact]
    public void Load_ValidateAndFill_NormalizesLightTheme()
    {
        WriteToml("""
            [WebUI]
            Theme = "LIGHT"
            ViewDensity = "COMFORTABLE"
            """);

        var config = new ConfigurationLoader(_tempFilePath).Load();

        config.WebUI.Theme.Should().Be("Light");
        config.WebUI.ViewDensity.Should().Be("Comfortable");
    }

    [Fact]
    public void Load_ValidateAndFill_FillsQBitDefaults()
    {
        WriteToml("""
            [qBit]
            Host = "myhost"
            """);

        var config = new ConfigurationLoader(_tempFilePath).Load();

        config.QBitInstances["qBit"].Host.Should().Be("myhost");
        config.QBitInstances["qBit"].Port.Should().Be(8080); // Default filled
        config.QBitInstances["qBit"].Disabled.Should().BeFalse();
    }

    [Fact]
    public void Load_ValidateAndFill_FillsEntrySearchDefaults()
    {
        WriteToml("""
            [Radarr-Movies]
            URI = "http://radarr:7878"
            APIKey = "key"
            Category = "radarr"

            [Radarr-Movies.EntrySearch]
            SearchMissing = true
            """);

        var config = new ConfigurationLoader(_tempFilePath).Load();

        config.ArrInstances["Radarr-Movies"].Search.ForceResetTempProfiles.Should().BeFalse();
        config.ArrInstances["Radarr-Movies"].Search.ProfileSwitchRetryAttempts.Should().Be(3);
        config.ArrInstances["Radarr-Movies"].Search.TempProfileResetTimeoutMinutes.Should().Be(0);
    }

    [Fact]
    public void Load_SkipsMigrations_WhenVersionIsCurrent()
    {
        WriteToml($"""
            [Settings]
            ConfigVersion = "{ConfigurationLoader.ExpectedConfigVersion}"
            LoopSleepTimer = 5
            """);

        var config = new ConfigurationLoader(_tempFilePath).Load();

        // Should load fine without errors even when version is current
        config.Settings.LoopSleepTimer.Should().Be(5);
    }

    // --- WebUI Migration (Migration 1) ---

    [Fact]
    public void Load_Migration1_MovesHostPortTokenFromSettingsToWebUI()
    {
        WriteToml("""
            [Settings]
            ConfigVersion = "0.0.1"
            Host = "127.0.0.1"
            Port = 7070
            Token = "my-secret"
            LoopSleepTimer = 5
            """);

        var config = new ConfigurationLoader(_tempFilePath).Load();

        config.WebUI.Host.Should().Be("127.0.0.1");
        config.WebUI.Port.Should().Be(7070);
        config.WebUI.Token.Should().Be("my-secret");
    }

    // --- Quality Profile Mappings Migration (Migration 2) ---

    [Fact]
    public void Load_Migration2_ConvertsListsToMappings()
    {
        WriteToml("""
            [Settings]
            ConfigVersion = "0.0.1"

            [Radarr-Movies]
            URI = "http://radarr:7878"
            APIKey = "key"
            Category = "radarr"

            [Radarr-Movies.EntrySearch]
            MainQualityProfile = ["HD-1080p", "Ultra-HD"]
            TempQualityProfile = ["Any", "HD-720p"]
            """);

        var config = new ConfigurationLoader(_tempFilePath).Load();

        config.ArrInstances["Radarr-Movies"].Search.QualityProfileMappings
            .Should().ContainKey("HD-1080p")
            .WhoseValue.Should().Be("Any");
        config.ArrInstances["Radarr-Movies"].Search.QualityProfileMappings
            .Should().ContainKey("Ultra-HD")
            .WhoseValue.Should().Be("HD-720p");
    }

    [Fact]
    public void Load_WebUI_ParsesAuthBooleans()
    {
        WriteToml("""
            [Settings]
            LoopSleepTimer = 5

            [WebUI]
            AuthDisabled = false
            LocalAuthEnabled = true
            OIDCEnabled = true
            """);

        var config = new ConfigurationLoader(_tempFilePath).Load();

        config.WebUI.AuthDisabled.Should().BeFalse();
        config.WebUI.LocalAuthEnabled.Should().BeTrue();
        config.WebUI.OIDCEnabled.Should().BeTrue();
    }

    [Theory]
    [InlineData("Disabled", true, false, false)]
    [InlineData("TokenOnly", false, false, false)]
    [InlineData("Local", false, true, false)]
    [InlineData("OIDC", false, false, true)]
    public void Load_WebUI_MigratesAuthMode_ToBooleans(string authMode, bool expectAuthDisabled, bool expectLocalEnabled, bool expectOidcEnabled)
    {
        WriteToml($"""
            [Settings]
            LoopSleepTimer = 5

            [WebUI]
            AuthMode = "{authMode}"
            """);

        var config = new ConfigurationLoader(_tempFilePath).Load();

        config.WebUI.AuthDisabled.Should().Be(expectAuthDisabled);
        config.WebUI.LocalAuthEnabled.Should().Be(expectLocalEnabled);
        config.WebUI.OIDCEnabled.Should().Be(expectOidcEnabled);
    }

    [Fact]
    public void Save_WebUI_WritesAuthBooleans()
    {
        WriteToml("""
            [Settings]
            LoopSleepTimer = 5

            [WebUI]
            AuthDisabled = false
            LocalAuthEnabled = true
            OIDCEnabled = false
            """);

        var loader = new ConfigurationLoader(_tempFilePath);
        var config = loader.Load();
        config.WebUI.OIDCEnabled = true;
        loader.SaveConfig(config);

        var content = File.ReadAllText(_tempFilePath);
        content.Should().Contain("AuthDisabled = false");
        content.Should().Contain("LocalAuthEnabled = true");
        content.Should().Contain("OIDCEnabled = true");
    }

    [Fact]
    public void GenerateDefaultConfig_ReturnsAuthEnabledForNewInstalls()
    {
        var config = ConfigurationLoader.GenerateDefaultConfig();

        config.WebUI.AuthDisabled.Should().BeFalse("new installs get auth enabled by default");
        config.WebUI.LocalAuthEnabled.Should().BeTrue("new installs get local auth enabled by default");
    }

    [Fact]
    public void Load_ParsesTrackerSortTorrents_DefaultsFalseWhenMissing()
    {
        WriteToml("""
            [qBit]
            Host = "localhost"

            [Radarr-Movies]
            URI = "http://radarr:7878"
            APIKey = "key"
            Category = "radarr"

            [[Radarr-Movies.Torrent.Trackers]]
            URI = "https://tracker.example.com/announce"
            Priority = 10
            """);

        var config = new ConfigurationLoader(_tempFilePath).Load();

        config.ArrInstances["Radarr-Movies"].Torrent.Trackers.Should().HaveCount(1);
        config.ArrInstances["Radarr-Movies"].Torrent.Trackers[0].SortTorrents.Should().BeFalse();
    }

    [Fact]
    public void Save_WritesTrackerSortTorrents()
    {
        WriteToml("""
            [qBit]
            Host = "localhost"

            [Radarr-Movies]
            URI = "http://radarr:7878"
            APIKey = "key"
            Category = "radarr"
            """);

        var loader = new ConfigurationLoader(_tempFilePath);
        var config = loader.Load();
        config.ArrInstances["Radarr-Movies"].Torrent.Trackers.Add(new TrackerConfig
        {
            Uri = "https://tracker.example.com/announce",
            Priority = 10,
            SortTorrents = true
        });

        loader.SaveConfig(config);
        var content = File.ReadAllText(_tempFilePath);

        content.Should().Contain("SortTorrents = true");
    }

    [Fact]
    public void Load_ParsesTrackerSortTorrents_TrueWhenSet()
    {
        WriteToml("""
            [qBit]
            Host = "localhost"

            [Radarr-Movies]
            URI = "http://radarr:7878"
            APIKey = "key"
            Category = "radarr"

            [[Radarr-Movies.Torrent.Trackers]]
            URI = "https://tracker.example.com/announce"
            Priority = 10
            SortTorrents = true
            """);

        var config = new ConfigurationLoader(_tempFilePath).Load();

        config.ArrInstances["Radarr-Movies"].Torrent.Trackers[0].SortTorrents.Should().BeTrue();
    }
}

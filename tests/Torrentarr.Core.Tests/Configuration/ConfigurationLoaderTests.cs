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
}

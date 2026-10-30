using FluentAssertions;
using Torrentarr.Core.Configuration;
using Xunit;

namespace Torrentarr.Core.Tests.Configuration;

[Collection("Configuration environment")]
public class CorsConfigurationTests
{
    [Theory]
    [InlineData("https://torrentarr.example.com", true)]
    [InlineData("http://localhost:5173", true)]
    [InlineData("http://[::1]:5173", true)]
    [InlineData("*", false)]
    [InlineData("https://*.example.com", false)]
    [InlineData("null", false)]
    [InlineData("", false)]
    [InlineData(" https://example.com", false)]
    [InlineData("https://example.com/", false)]
    [InlineData("https://example.com/ui", false)]
    [InlineData("https://example.com?token=secret", false)]
    [InlineData("https://example.com#fragment", false)]
    [InlineData("https://user:secret@example.com", false)]
    [InlineData("ftp://example.com", false)]
    public void ValidateAll_ChecksExactHttpOrigins(string origin, bool valid)
    {
        var config = new TorrentarrConfig();
        config.WebUI.CorsAllowedOrigins = [origin];
        ConfigValidationHelper.ValidateAll(config).Ok.Should().Be(valid);
    }

    [Fact]
    public void Origins_RoundTripAndClearingPersists()
    {
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".toml");
        try
        {
            File.WriteAllText(path, "[WebUI]\nCorsAllowedOrigins = ['https://torrentarr.example.com', 'http://localhost:5173']\n");
            var loader = new ConfigurationLoader(path);
            var config = loader.Load();
            config.WebUI.CorsAllowedOrigins.Should().Equal("https://torrentarr.example.com", "http://localhost:5173");
            loader.SaveConfig(config);
            loader.Load().WebUI.CorsAllowedOrigins.Should().Equal(config.WebUI.CorsAllowedOrigins);
            config.WebUI.CorsAllowedOrigins.Clear();
            loader.SaveConfig(config);
            loader.Load().WebUI.CorsAllowedOrigins.Should().BeEmpty();
        }
        finally { File.Delete(path); }
    }

    [Theory]
    [InlineData("'https://example.com'")]
    [InlineData("[12]")]
    [InlineData("['https://example.com/path']")]
    public void Load_RejectsInvalidOriginSettings(string toml)
    {
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".toml");
        try
        {
            File.WriteAllText(path, "[WebUI]\nCorsAllowedOrigins = " + toml + "\n");
            var load = () => new ConfigurationLoader(path).Load();
            load.Should().Throw<Exception>();
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Schema_ExposesOriginsAsLiveEditableTags()
    {
        var field = ConfigSchemaBuilder.Sections["WebUI"].Single(f => f.key == "CorsAllowedOrigins");
        field.kind.Should().Be("tags");
        field.applyLive.Should().BeTrue();
        field.requiresRestart.Should().BeFalse();
    }
}

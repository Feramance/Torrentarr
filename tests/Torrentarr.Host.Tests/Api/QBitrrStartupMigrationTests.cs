using System.Net;
using System.Net.Http.Headers;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Torrentarr.Core.Configuration;
using Xunit;

namespace Torrentarr.Host.Tests.Api;

[Collection("HostWeb")]
public sealed class QBitrrStartupMigrationTests
{
    [Fact]
    public async Task StartupMigratesUpstreamConfigAndPersistsGeneratedTokenWithoutLosingSettings()
    {
        using var factory = new QBitrrWebApplicationFactory();
        var original = File.ReadAllText(factory.TempConfigPath);
        try
        {
            using var client = factory.CreateClientWithoutApiToken();
            var config = factory.Services.GetRequiredService<TorrentarrConfig>();
            config.Settings.ConfigVersion.Should().Be(ConfigurationLoader.ExpectedConfigVersion);
            config.ArrInstances.Should().HaveCount(6);
            config.WebUI.AuthDisabled.Should().BeFalse();
            config.WebUI.AllowInsecureExposure.Should().BeFalse();
            config.WebUI.AllowInsecureTokenQuery.Should().BeFalse();
            config.WebUI.Token.Should().NotBeNullOrWhiteSpace();
            Convert.FromBase64String(config.WebUI.Token).Should().HaveCount(32);
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", config.WebUI.Token);
            (await client.GetAsync("/health")).StatusCode.Should().Be(HttpStatusCode.OK);

            var persisted = File.ReadAllText(factory.TempConfigPath);
            var reloaded = new ConfigurationLoader(factory.TempConfigPath).LoadOrCreate();
            reloaded.CreatedDefault.Should().BeFalse();
            reloaded.Config.Should().BeEquivalentTo(config);
            (File.ReadAllText(factory.TempConfigPath) == persisted).Should().BeTrue("a second startup must not rewrite the migrated config");
            (File.ReadAllText(factory.TempConfigPath + ".bak") == original).Should().BeTrue("the backup must retain the original qBitrr config");
            reloaded.Config.QBitInstances["qBit"].Password.Should().Be("fixture-qbit-password");
            reloaded.Config.ArrInstances["Sonarr-TV"].APIKey.Should().Be("fixture-arr-key");
            reloaded.Config.ArrInstances["Sonarr-TV"].Search.SearchBySeries.Should().Be("smart");
            reloaded.Config.ArrInstances["Readarr-Books"].Torrent.FileExtensionAllowlist.Should().Contain(".m4b");
            reloaded.Config.QBitInstances["qBit"].CategorySeeding.StalledDelay.Should().Be(-1);
            reloaded.Config.QBitInstances["qBit"].FreeSpace.Should().BeEmpty();
        }
        finally
        {
            File.Delete(factory.TempConfigPath + ".bak");
        }
    }

    private sealed class QBitrrWebApplicationFactory : TorrentarrWebApplicationFactory
    {
        protected override string GetTestConfigToml() => File.ReadAllText(
            Path.Combine(AppContext.BaseDirectory, "Fixtures", "qbitrr-5.14.8.toml"))
            .Replace("Password = \"CHANGE_ME\"", "Password = \"fixture-qbit-password\"")
            .Replace("APIKey = \"CHANGE_ME\"", "APIKey = \"fixture-arr-key\"");
    }
}

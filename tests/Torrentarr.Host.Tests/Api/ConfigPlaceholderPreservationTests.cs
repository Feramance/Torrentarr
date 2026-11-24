using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Torrentarr.Core.Configuration;
using System.Net;
using System.Text;
using System.Text.Json;
using Xunit;

namespace Torrentarr.Host.Tests.Api;

/// <summary>
/// Regression: CHANGE_ME qBit placeholder sections must survive unrelated POST /web/config saves.
/// </summary>
[Collection("HostWeb")]
public class ConfigPlaceholderPreservationTests : IClassFixture<MultiQBitPlaceholderWebApplicationFactory>
{
    private readonly MultiQBitPlaceholderWebApplicationFactory _factory;

    public ConfigPlaceholderPreservationTests(MultiQBitPlaceholderWebApplicationFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task PostConfig_UnrelatedChange_PreservesChangeMeQBitSeedboxStubOnDisk()
    {
        _factory.SetConfigEnv();
        var client = _factory.CreateClientWithApiToken();

        var payload = new
        {
            changes = new Dictionary<string, object>
            {
                ["settings.loopSleepTimer"] = 12
            }
        };
        var content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");

        var response = await client.PostAsync("/web/config", content);
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var savedToml = await File.ReadAllTextAsync(_factory.TempConfigPath);
        savedToml.Should().Contain("[qBit-seedbox]");
        savedToml.Should().Contain("Host = \"CHANGE_ME\"");
    }

    [Fact]
    public async Task PostConfig_UnrelatedChange_PreservesLegacyOverseerrSettings()
    {
        _factory.SetConfigEnv();
        using var client = _factory.CreateClientWithApiToken();
        using var content = new StringContent("""{"changes":{"settings.loopSleepTimer":15}}""", Encoding.UTF8, "application/json");

        using var response = await client.PostAsync("/web/config", content);
        response.EnsureSuccessStatusCode();

        var savedToml = await File.ReadAllTextAsync(_factory.TempConfigPath);
        savedToml.Should().Contain("[Radarr-legacy.EntrySearch.Overseerr]");
        savedToml.Should().NotContain("[Radarr-legacy.EntrySearch.Seerr]");
        var legacy = _factory.Services.GetRequiredService<ConfigurationLoader>().Load()
            .ArrInstances["Radarr-legacy"].Search.Overseerr;
        legacy.Should().BeEquivalentTo(new OverseerrConfig
        {
            SearchOverseerrRequests = true,
            OverseerrURI = "https://overseerr.example/proxy",
            OverseerrAPIKey = "legacy-request-key",
            ApprovedOnly = false,
            SkipTLSVerify = true,
            Is4K = true
        });
    }
}

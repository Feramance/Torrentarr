using FluentAssertions;
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
}

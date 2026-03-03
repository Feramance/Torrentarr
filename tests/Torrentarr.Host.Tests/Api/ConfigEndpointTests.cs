using FluentAssertions;
using System.Net;
using System.Text;
using System.Text.Json;
using Xunit;

namespace Torrentarr.Host.Tests.Api;

[Collection("HostWeb")]
public class ConfigEndpointTests : IClassFixture<TorrentarrWebApplicationFactory>
{
    private readonly TorrentarrWebApplicationFactory _factory;

    public ConfigEndpointTests(TorrentarrWebApplicationFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task GetConfig_Returns200()
    {
        var client = _factory.CreateClient();

        var response = await client.GetAsync("/web/config");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task GetConfig_ReturnsFlatStructure_WithSettingsAndWebUI()
    {
        var client = _factory.CreateClient();

        var response = await client.GetAsync("/web/config");
        var body = await response.Content.ReadAsStringAsync();
        var json = JsonDocument.Parse(body).RootElement;

        // Should be a flat object (not nested under "config" etc.)
        json.ValueKind.Should().Be(JsonValueKind.Object);
        json.TryGetProperty("Settings", out _).Should().BeTrue();
        json.TryGetProperty("WebUI", out _).Should().BeTrue();
    }

    [Fact]
    public async Task GetConfig_DoesNotIncludeQBit_WhenNotConfigured()
    {
        // Default config has Host = "CHANGE_ME" → qBit section should not be present
        var client = _factory.CreateClient();

        var response = await client.GetAsync("/web/config");
        var body = await response.Content.ReadAsStringAsync();
        var json = JsonDocument.Parse(body).RootElement;

        // qBit should be absent because no [qBit] section in test config (QBitInstances is empty)
        json.TryGetProperty("qBit", out _).Should().BeFalse();
    }

    [Fact]
    public async Task PostConfig_Returns200_WithValidPayload()
    {
        var client = _factory.CreateClient();
        var payload = new { changes = new { } };
        var content = new StringContent(
            JsonSerializer.Serialize(payload),
            Encoding.UTF8,
            "application/json");

        var response = await client.PostAsync("/web/config", content);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadAsStringAsync();
        var json = JsonDocument.Parse(body).RootElement;
        json.GetProperty("status").GetString().Should().Be("ok");
    }

    [Fact]
    public async Task PostConfig_Returns400_WhenMissingChanges()
    {
        var client = _factory.CreateClient();
        var content = new StringContent("{}", Encoding.UTF8, "application/json");

        var response = await client.PostAsync("/web/config", content);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task PostConfig_ReturnsReloadType_None_ForWebuiKeys()
    {
        var client = _factory.CreateClient();
        var payload = new
        {
            changes = new Dictionary<string, object>
            {
                ["webui.theme"] = "dark"
            }
        };
        var content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");

        var response = await client.PostAsync("/web/config", content);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
        json.GetProperty("reloadType").GetString().Should().Be("frontend");
    }

    [Fact]
    public async Task PostConfig_ReturnsReloadType_Webui_ForSettingsKeys()
    {
        var client = _factory.CreateClient();
        var payload = new
        {
            changes = new Dictionary<string, object>
            {
                ["settings.loopSleepTimer"] = 10
            }
        };
        var content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");

        var response = await client.PostAsync("/web/config", content);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
        json.GetProperty("reloadType").GetString().Should().Be("webui");
    }

    [Fact]
    public async Task PostConfig_ReturnsReloadType_Full_ForQbitKeys()
    {
        var client = _factory.CreateClient();
        var payload = new
        {
            changes = new Dictionary<string, object>
            {
                ["qbit.host"] = "192.168.1.1"
            }
        };
        var content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");

        var response = await client.PostAsync("/web/config", content);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
        json.GetProperty("reloadType").GetString().Should().Be("full");
    }
}

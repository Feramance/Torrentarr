using FluentAssertions;
using System.Net;
using System.Text;
using System.Text.Json;
using Xunit;

namespace Torrentarr.Host.Tests.Api;

[Collection("HostWebCatalog")]
public class ConfigValidationEndpointTests : IClassFixture<ArrCatalogWebApplicationFactory>
{
    private readonly ArrCatalogWebApplicationFactory _factory;

    public ConfigValidationEndpointTests(ArrCatalogWebApplicationFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task PostConfig_RejectsOverlappingArrCategories()
    {
        _factory.SetConfigEnv();
        var client = _factory.CreateClientWithApiToken();
        var payload = new
        {
            changes = new Dictionary<string, object>
            {
                ["Radarr-4K"] = new Dictionary<string, object>
                {
                    ["URI"] = "http://radarr-4k:7878",
                    ["APIKey"] = "key",
                    ["Category"] = "radarr/4k"
                }
            }
        };
        var content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");

        var response = await client.PostAsync("/web/config", content);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var body = await response.Content.ReadAsStringAsync();
        body.Should().Contain("Overlapping Arr categories");
    }

    [Fact]
    public async Task PostConfig_RejectsOverlappingManagedCategories()
    {
        _factory.SetConfigEnv();
        var client = _factory.CreateClientWithApiToken();
        var payload = new
        {
            changes = new Dictionary<string, object>
            {
                ["qBit.ManagedCategories"] = new[] { "seed", "seed/tleech" }
            }
        };
        var content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");

        var response = await client.PostAsync("/web/config", content);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var body = await response.Content.ReadAsStringAsync();
        body.Should().Contain("Overlapping qBit ManagedCategories");
    }

    [Fact]
    public async Task PostConfig_RejectsArrQBitOverlap()
    {
        _factory.SetConfigEnv();
        var client = _factory.CreateClientWithApiToken();
        var payload = new
        {
            changes = new Dictionary<string, object>
            {
                ["qBit.ManagedCategories"] = new[] { "radarr" }
            }
        };
        var content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");

        var response = await client.PostAsync("/web/config", content);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var body = await response.Content.ReadAsStringAsync();
        body.Should().Match("*overlaps*");
    }

    [Fact]
    public async Task PostConfig_RejectsProtectedConfigVersion()
    {
        _factory.SetConfigEnv();
        var client = _factory.CreateClientWithApiToken();
        var payload = new
        {
            changes = new Dictionary<string, object>
            {
                ["Settings.ConfigVersion"] = "9.9.9"
            }
        };
        var content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");

        var response = await client.PostAsync("/web/config", content);

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        var body = await response.Content.ReadAsStringAsync();
        body.Should().Contain("Settings.ConfigVersion");
    }

    [Fact]
    public async Task PostConfig_AcceptsValidNonOverlappingChange()
    {
        _factory.SetConfigEnv();
        var client = _factory.CreateClientWithApiToken();
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
    }
}

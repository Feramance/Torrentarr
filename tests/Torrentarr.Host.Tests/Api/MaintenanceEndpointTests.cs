using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Torrentarr.Core.Configuration;
using Torrentarr.Core.Models;
using Xunit;

namespace Torrentarr.Host.Tests.Api;

[Collection("HostWeb")]
public sealed class MaintenanceEndpointTests
{
    [Theory]
    [InlineData("/web")]
    [InlineData("/api")]
    public async Task AllTenOperationsAndSchedulingEndpointsAreAvailable(string prefix)
    {
        using var factory = new TorrentarrWebApplicationFactory(); using var client = factory.CreateClientWithApiToken();
        foreach (var operation in Enum.GetNames<MaintenanceOperation>())
            (await client.PostAsJsonAsync(prefix + "/maintenance/preview", new { clientInstanceIds = new[] { "qBit" }, operations = new[] { operation } })).EnsureSuccessStatusCode();
        (await client.PostAsJsonAsync(prefix + "/maintenance/arm", new { planId = "missing" })).StatusCode.Should().Be(HttpStatusCode.Gone);
        (await client.PostAsJsonAsync(prefix + "/maintenance/disarm", new { clientInstanceIds = new[] { "qBit" } })).StatusCode.Should().Be(HttpStatusCode.Gone);
        var run = await client.PostAsJsonAsync(prefix + "/maintenance/run", new { clientInstanceIds = new[] { "qBit" } });
        run.StatusCode.Should().NotBe(HttpStatusCode.Gone);
        (await client.PostAsJsonAsync(prefix + "/maintenance/preview", new { operations = new[] { 999 } })).StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Theory]
    [InlineData("/web")]
    [InlineData("/api")]
    public async Task ReviewedPreviewAppliesOnce_WithoutPersistentAuthorization(string prefix)
    {
        using var factory = new TorrentarrWebApplicationFactory(); using var client = factory.CreateClientWithApiToken();
        factory.Services.GetRequiredService<TorrentarrConfig>().QBitInstances.Clear();
        var preview = await client.PostAsJsonAsync(prefix + "/maintenance/preview", new { }); preview.EnsureSuccessStatusCode();
        var plan = await preview.Content.ReadFromJsonAsync<JsonElement>();
        plan.GetProperty("operations").GetArrayLength().Should().Be(10);
        var id = plan.GetProperty("planId").GetString();
        (await client.PostAsJsonAsync(prefix + "/maintenance/apply", new { planId = id, authorizeOnce = true })).EnsureSuccessStatusCode();
        (await client.PostAsJsonAsync(prefix + "/maintenance/apply", new { planId = id, authorizeOnce = true })).StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await client.GetAsync(prefix + "/torrent-automation/status")).EnsureSuccessStatusCode();
    }

    [Fact]
    public async Task SchemaRetainsEveryToolWithMaintenanceAsCanonicalContract()
    {
        using var factory = new TorrentarrWebApplicationFactory(); using var client = factory.CreateClientWithApiToken();
        foreach (var prefix in new[] { "/web", "/api" })
        {
            var schema = await client.GetStringAsync(prefix + "/config/schema");
            schema.Should().Contain("Maintenance.Enabled").And.Contain("Maintenance.Hardlinks.Tag").And.Contain("Maintenance.CategoryMappings")
                .And.Contain("Maintenance.ErrorRecovery.Outcome").And.NotContain("Storage.AutomaticCleanup");
        }
    }
}

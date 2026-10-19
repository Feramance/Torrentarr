using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Moq;
using Torrentarr.Core.Configuration;
using Torrentarr.Core.Interfaces;
using Torrentarr.Core.Models;
using Xunit;

namespace Torrentarr.Host.Tests.Api;

[Collection("HostWeb")]
public sealed class MaintenanceEndpointTests
{
    [Theory]
    [InlineData("/web/maintenance/preview", "SharePolicy")]
    [InlineData("/api/maintenance/preview", "SharePolicy")]
    [InlineData("/web/maintenance/run", "SharePolicy")]
    [InlineData("/api/maintenance/run", "SharePolicy")]
    [InlineData("/web/maintenance/preview", 8)]
    [InlineData("/api/maintenance/preview", 8)]
    [InlineData("/web/maintenance/run", 8)]
    [InlineData("/api/maintenance/run", 8)]
    public async Task RemovedOperations_AreRejected(string endpoint, object operation)
    {
        using var factory = new TorrentarrWebApplicationFactory();
        using var client = factory.CreateClientWithApiToken();
        var response = await client.PostAsJsonAsync(endpoint, new { operations = new[] { operation } });
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task SchemaAndConfig_DoNotExposeRemovedFeatures()
    {
        using var factory = new TorrentarrWebApplicationFactory();
        using var client = factory.CreateClientWithApiToken();
        foreach (var endpoint in new[] { "/web/config/schema", "/api/config/schema", "/web/config", "/api/config" })
        {
            var response = await client.GetAsync(endpoint);
            response.EnsureSuccessStatusCode();
            (await response.Content.ReadAsStringAsync()).Should().NotContain("SharePolicies").And.NotContain("Notifications");
        }
    }

    [Theory]
    [InlineData("/web")]
    [InlineData("/api")]
    public async Task MixedPreview_ExposesReadinessAndAppliesOnlyReadyWork(string prefix)
    {
        using var factory = new TorrentarrWebApplicationFactory();
        var torrentClient = new Mock<ITorrentClient>();
        torrentClient.SetupGet(c => c.Identity).Returns(new TorrentClientIdentity("one", "fake"));
        torrentClient.SetupGet(c => c.Capabilities).Returns(TorrentClientCapabilities.QBittorrent);
        torrentClient.Setup(c => c.GetTorrentsAsync(It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([new() { Hash = "abc", Name = "movie", Category = "movies", SavePath = "/downloads", Size = 4, AddedOn = 1 }]);
        torrentClient.Setup(c => c.GetTorrentFilesAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([new() { Name = "movie.mkv", Size = 4 }]);
        torrentClient.Setup(c => c.SetCategoryAsync(It.IsAny<List<string>>(), "other", It.IsAny<CancellationToken>())).ReturnsAsync(true);
        var registry = new Mock<ITorrentClientRegistry>();
        registry.Setup(r => r.GetClient("one")).Returns(torrentClient.Object);
        registry.Setup(r => r.GetAllClients()).Returns(new Dictionary<string, ITorrentClient> { ["one"] = torrentClient.Object });
        using var app = factory.WithWebHostBuilder(builder => builder.ConfigureServices(services =>
        {
            services.RemoveAll<ITorrentClientRegistry>();
            services.AddSingleton(registry.Object);
        }));
        using var client = app.CreateClient();
        client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", "test-api-token");
        var config = app.Services.GetRequiredService<TorrentarrConfig>();
        config.TorrentClients["one"] = new()
        {
            Host = "fixture",
            Maintenance = new()
            {
                Enabled = true,
                Scope = "all",
                MinimumTorrentAgeMinutes = 0,
                Orphans = new() { Enabled = true },
                CategoryMappings = [new() { ClientPath = "/downloads", Category = "other" }]
            }
        };
        var response = await client.PostAsJsonAsync(prefix + "/maintenance/preview", new MaintenancePreviewRequest
        { ClientInstanceIds = ["one"], Operations = [MaintenanceOperation.OrphanScan, MaintenanceOperation.CategoryReconcile] });
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var plan = await response.Content.ReadFromJsonAsync<JsonElement>();
        plan.GetProperty("criticalErrors").GetArrayLength().Should().Be(0);
        plan.GetProperty("candidateCount").GetInt32().Should().Be(1);
        plan.GetProperty("diagnostics").EnumerateArray().Should().Contain(d => d.GetProperty("code").GetString() == "missing-mapping");
        plan.GetProperty("operationResults").EnumerateArray().Should().Contain(r => r.GetProperty("operation").GetString() == "OrphanScan" && r.GetProperty("state").GetString() == "blocked");
        plan.GetProperty("actions")[0].GetProperty("operation").GetString().Should().Be("CategoryReconcile");
        var planId = plan.GetProperty("planId").GetString()!;
        (await client.PostAsJsonAsync(prefix + "/maintenance/arm", new MaintenanceArmRequest { PlanId = planId })).EnsureSuccessStatusCode();
        var applied = await client.PostAsJsonAsync(prefix + "/maintenance/apply", new MaintenanceApplyRequest { PlanId = planId });
        applied.EnsureSuccessStatusCode();
        var run = await applied.Content.ReadFromJsonAsync<JsonElement>();
        run.GetProperty("applied").GetInt32().Should().Be(1);
        run.GetProperty("result").GetString().Should().Be("partial");
        run.GetProperty("pending").GetInt32().Should().Be(0);
        run.GetProperty("operationResults").GetArrayLength().Should().Be(2);
        torrentClient.Verify(c => c.DeleteTorrentsAsync(It.IsAny<List<string>>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()), Times.Never);
    }
}

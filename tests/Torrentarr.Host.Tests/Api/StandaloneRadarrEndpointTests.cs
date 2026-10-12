extern alias Standalone;

using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Torrentarr.Core.Configuration;
using Torrentarr.Core.Models;
using Torrentarr.Core.Services;
using System.Net.Http.Json;
using Torrentarr.Infrastructure.Database;
using Torrentarr.Infrastructure.Database.Models;
using Xunit;

namespace Torrentarr.Host.Tests.Api;

[Collection("HostWebCatalog")]
public class StandaloneRadarrEndpointTests
{
    [Fact]
    public async Task StandaloneRadarr_FiltersBeforePaging_AndKeepsItsContract()
    {
        using var factory = new StandaloneFactory();
        var client = factory.CreateClient();
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TorrentarrDbContext>();
        for (var i = 0; i < 55; i++)
            db.Movies.Add(new MoviesFilesModel
            {
                EntryId = i + 1,
                ArrInstance = "Radarr",
                ArrId = i + 1,
                TmdbId = i + 1,
                Title = $"Movie {i:00}",
                Year = 2000 + i,
                Monitored = true,
                HasFile = i < 50,
                MovieFileId = i < 50 ? i + 1 : 0,
                Reason = i < 50 ? "Quality" : "Missing",
                Searched = true
            });
        await db.SaveChangesAsync();
        async Task<JsonElement> Get(string query)
        {
            var response = await client.GetAsync("/web/radarr/movies/movies?" + query);
            response.EnsureSuccessStatusCode();
            return JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.Clone();
        }
        var first = await Get("page=0&page_size=50");
        var second = await Get("page=1&page_size=50");
        first.GetProperty("movies").GetArrayLength().Should().Be(50);
        second.GetProperty("movies").GetArrayLength().Should().Be(5);
        var firstIds = first.GetProperty("movies").EnumerateArray().Select(m => m.GetProperty("id").GetInt32());
        second.GetProperty("movies").EnumerateArray().Select(m => m.GetProperty("id").GetInt32()).Should().NotIntersectWith(firstIds);
        var filtered = await Get("page=0&page_size=2&has_file=false&reason=Missing");
        filtered.GetProperty("total").GetInt32().Should().Be(5);
        filtered.GetProperty("movies").GetArrayLength().Should().Be(2);
        foreach (var movie in filtered.GetProperty("movies").EnumerateArray())
        {
            movie.GetProperty("hasFile").GetBoolean().Should().BeFalse();
            movie.GetProperty("reason").GetString().Should().Be("Missing");
            movie.GetProperty("searched").GetBoolean().Should().BeTrue();
            movie.GetProperty("tmdbId").GetInt32().Should().BePositive();
            movie.GetProperty("arrId").GetInt32().Should().BePositive();
            movie.GetProperty("arrInstance").GetString().Should().Be("Radarr");
        }
        filtered.GetProperty("counts").GetProperty("available").GetInt32().Should().Be(50);
        filtered.GetProperty("counts").GetProperty("missing").GetInt32().Should().Be(5);
        (await Get("page=0&page_size=50&q=absent")).GetProperty("total").GetInt32().Should().Be(0);
        (await Get("page=0&pageSize=2")).GetProperty("movies").GetArrayLength().Should().Be(2);
    }

    [Fact]
    public async Task StandaloneMaintenance_PreviewAndApplyUseRegisteredSeedingService()
    {
        await using var factory = new StandaloneFactory();
        var client = factory.CreateClient();
        var config = factory.Services.GetRequiredService<TorrentarrConfig>();
        config.TorrentClients["one"] = new() { Host = "fixture", Maintenance = new() { Enabled = true, Armed = true } };
        var response = await client.PostAsJsonAsync("/web/maintenance/preview", new MaintenancePreviewRequest());
        response.EnsureSuccessStatusCode();
        var plan = await response.Content.ReadFromJsonAsync<JsonElement>();
        using var scope = factory.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<ISeedingService>().Should().NotBeNull();
        // A disconnected client blocks preview, so exercise apply with a valid empty plan instead.
        config.TorrentClients.Clear();
        var maintenance = factory.Services.GetRequiredService<IMaintenanceCoordinator>();
        var empty = await maintenance.PreviewAsync(new MaintenancePreviewRequest());
        var applied = await client.PostAsJsonAsync("/web/maintenance/apply", new MaintenanceApplyRequest { PlanId = empty.PlanId });
        applied.EnsureSuccessStatusCode();
        plan.GetProperty("planId").GetString().Should().NotBeNullOrWhiteSpace();
        var status = await client.GetFromJsonAsync<JsonElement>("/web/status");
        status.TryGetProperty("qbit", out _).Should().BeFalse();
    }

    private sealed class StandaloneFactory : WebApplicationFactory<Standalone::Program>
    {
        private readonly string _directory = Path.Combine(Path.GetTempPath(), "standalone-radarr-" + Guid.NewGuid());
        private readonly SqliteConnection _connection = new("Data Source=:memory:");
        private readonly string? _previousConfig = Environment.GetEnvironmentVariable("TORRENTARR_CONFIG");
        private readonly string? _previousOverride = ConfigurationLoader.TestConfigPathOverride;
        public StandaloneFactory()
        {
            Directory.CreateDirectory(_directory);
            File.WriteAllText(Path.Combine(_directory, "config.toml"), """
                [Settings]
                ConfigVersion = '6.14.6'
                [WebUI]
                AuthDisabled = true
                AllowInsecureExposure = true
                Token = 'standalone-test'
                [Radarr]
                URI = 'http://127.0.0.1:1'
                Category = 'movies'
                Managed = false
                """);
            _connection.Open();
        }
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            var path = Path.Combine(_directory, "config.toml");
            Environment.SetEnvironmentVariable("TORRENTARR_CONFIG", path);
            ConfigurationLoader.TestConfigPathOverride = path;
            builder.UseEnvironment("Testing");
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<IHostedService>();
                services.RemoveAll<DbContextOptions<TorrentarrDbContext>>();
                services.RemoveAll<DbContextOptions>();
                services.AddDbContext<TorrentarrDbContext>(options => options.UseSqlite(_connection));
            });
        }
        public override async ValueTask DisposeAsync()
        {
            await base.DisposeAsync();
            _connection.Dispose();
            Environment.SetEnvironmentVariable("TORRENTARR_CONFIG", _previousConfig);
            ConfigurationLoader.TestConfigPathOverride = _previousOverride;
            if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
        }
    }
}

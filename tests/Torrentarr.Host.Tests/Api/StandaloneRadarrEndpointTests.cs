extern alias Standalone;

using System.Text.Json;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.Extensions.Options;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Torrentarr.Core.Configuration;
using Torrentarr.Core.Interfaces;
using Moq;
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
    public async Task StandaloneApplicationStatus_DoesNotWaitForWorkersItDoesNotLaunch()
    {
        await using var factory = new StandaloneFactory();
        using var client = factory.CreateClient();
        var reloader = factory.Services.GetRequiredService<IConfigReloader>();
        var candidate = new ConfigurationLoader(reloader.ConfigPath).Load();
        candidate.ArrInstances["Radarr"].Managed = true;
        reloader.Apply(candidate).Should().BeTrue();
        var status = await client.GetFromJsonAsync<JsonElement>("/web/config/status");
        status.GetProperty("state").GetString().Should().Be("applied");
        status.GetProperty("components").TryGetProperty("worker:Radarr", out _).Should().BeFalse();
    }

    [Fact]
    public async Task StandaloneLiveUrlBaseChange_RefreshesCookiePathsAndClearedHtmlBase()
    {
        await using var factory = new StandaloneFactory("/old");
        using var client = factory.CreateClient();
        var reloader = factory.Services.GetRequiredService<IConfigReloader>();
        var options = factory.Services.GetRequiredService<IOptionsMonitor<CookieAuthenticationOptions>>();
        options.Get(CookieAuthenticationDefaults.AuthenticationScheme).Cookie.Path.Should().Be("/old/");
        var candidate = new ConfigurationLoader(reloader.ConfigPath).Load();
        candidate.WebUI.UrlBase = "/new";
        candidate.WebUI.AuthDisabled = false;
        candidate.WebUI.LocalAuthEnabled = true;
        candidate.WebUI.Username = "fixture";
        candidate.WebUI.PasswordHash = BCrypt.Net.BCrypt.HashPassword("fixture-password", 10);
        reloader.Apply(candidate).Should().BeTrue();
        var cookie = options.Get(CookieAuthenticationDefaults.AuthenticationScheme);
        cookie.Cookie.Path.Should().Be("/new/");
        cookie.LoginPath.Value.Should().Be("/new/login");
        cookie.AccessDeniedPath.Value.Should().Be("/new/login");
        using var browser = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        var login = await browser.PostAsJsonAsync("/new/web/login", new { username = "fixture", password = "fixture-password" });
        login.EnsureSuccessStatusCode();
        login.Headers.GetValues("Set-Cookie").Should().Contain(value => value.Contains("path=/new/", StringComparison.OrdinalIgnoreCase));
        (await browser.GetAsync("/new/web/token")).StatusCode.Should().Be(System.Net.HttpStatusCode.OK);
        candidate = new ConfigurationLoader(reloader.ConfigPath).Load();
        candidate.WebUI.UrlBase = "";
        reloader.Apply(candidate).Should().BeTrue();
        (await client.GetStringAsync("/ui")).Should().Contain("<base href=\"/\">");
    }

    [Fact]
    public async Task StandaloneEvents_SendInitialConfigurationSnapshot()
    {
        await using var factory = new StandaloneFactory();
        using var client = factory.CreateClient();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        using var response = await client.GetAsync("/web/config/events", HttpCompletionOption.ResponseHeadersRead, timeout.Token);
        response.Content.Headers.ContentType!.MediaType.Should().Be("text/event-stream");
        using var reader = new StreamReader(await response.Content.ReadAsStreamAsync(timeout.Token));
        (await reader.ReadLineAsync(timeout.Token)).Should().StartWith("data: ");
    }

    [Fact]
    public async Task StandaloneStoredCredentials_KeepEditedUri()
    {
        await using var factory = new StandaloneFactory();
        using var client = factory.CreateClient();
        var config = factory.Services.GetRequiredService<TorrentarrConfig>();
        config.ArrInstances["Radarr-original"] = new() { URI = "http://127.0.0.1:1", APIKey = "stored-secret" };
        var response = await client.PostAsJsonAsync("/web/arr/test-connection", new { arrType = "radarr", instanceKey = "Radarr-original", uri = "ftp://edited" });
        var result = await response.Content.ReadFromJsonAsync<JsonElement>();
        result.GetProperty("message").GetString().Should().Be("URI must use http or https scheme");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task StandaloneMappingSave_PersistsWhileProcessing(bool canonical)
    {
        await using var factory = new StandaloneFactory();
        using var client = factory.CreateClient();
        client.Timeout = TimeSpan.FromSeconds(10);
        var config = factory.Services.GetRequiredService<TorrentarrConfig>();
        var reloader = factory.Services.GetRequiredService<IConfigReloader>();
        var name = canonical ? "mapping" : "qBit-mapping";
        var initial = new QBitConfig { Host = "fixture", Disabled = true };
        if (canonical) config.TorrentClients[name] = initial; else config.QBitInstances[name] = initial;
        using var processing = ConfigMutationGuard.EnterProcessing(reloader.ConfigPath);
        var expected = new[] { new PathMappingConfig { ClientPath = "/downloads", LocalPath = Path.GetTempPath() } };
        var response = await client.PostAsJsonAsync("/web/config", new
        {
            changes = new Dictionary<string, object> { [(canonical ? "TorrentClient." : "") + name + ".Maintenance.PathMappings"] = expected }
        });
        response.StatusCode.Should().Be(System.Net.HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        new ConfigurationLoader(reloader.ConfigPath).Load().GetTorrentClient(name)!.Maintenance.PathMappings
            .Should().BeEquivalentTo(expected);
    }

    [Theory]
    [InlineData("/web/maintenance/preview", "SharePolicy")]
    [InlineData("/web/maintenance/preview", 8)]
    [InlineData("/web/maintenance/run", "SharePolicy")]
    [InlineData("/web/maintenance/run", 8)]
    public async Task StandaloneMaintenance_RejectsRemovedOperation(string endpoint, object operation)
    {
        await using var factory = new StandaloneFactory();
        using var client = factory.CreateClient();
        var response = await client.PostAsJsonAsync(endpoint, new { operations = new[] { operation } });
        response.StatusCode.Should().Be(System.Net.HttpStatusCode.BadRequest);
    }

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
        config.TorrentClients["one"] = new() { Host = "fixture" };
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

    [Theory]
    [InlineData("/web")]
    [InlineData("/api")]
    public async Task StandaloneMaintenance_SessionEndpointsAreGone(string prefix)
    {
        await using var factory = new StandaloneFactory();
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", "standalone-test");
        foreach (var command in new[] { "arm", "disarm" })
        {
            var response = await client.PostAsJsonAsync(prefix + "/maintenance/" + command, new { });
            response.StatusCode.Should().Be(System.Net.HttpStatusCode.Gone);
            (await response.Content.ReadAsStringAsync()).Should().Contain("Automatic").And.Contain("Manual");
        }
    }

    [Fact]
    public async Task StandaloneStatus_ConnectsClientsBeforeFirstStatusResponse()
    {
        await using var factory = new StandaloneFactory();
        var adapter = new Mock<ITorrentClient>();
        adapter.Setup(c => c.LoginAsync(It.IsAny<CancellationToken>())).ReturnsAsync(true);
        adapter.Setup(c => c.GetVersionAsync(It.IsAny<CancellationToken>())).ReturnsAsync("fixture-version");
        var adapterFactory = new Mock<ITorrentClientFactory>();
        adapterFactory.SetupGet(f => f.Type).Returns("qbittorrent");
        adapterFactory.Setup(f => f.Create(It.IsAny<string>(), It.IsAny<TorrentClientInstanceConfig>())).Returns(adapter.Object);
        await using var configured = factory.WithWebHostBuilder(builder => builder.ConfigureServices(services =>
        {
            services.RemoveAll<ITorrentClientFactory>();
            services.AddSingleton(adapterFactory.Object);
        }));
        var client = configured.CreateClient();
        var config = configured.Services.GetRequiredService<TorrentarrConfig>();
        config.QBitInstances.Clear();
        config.TorrentClients["one"] = new() { Host = "fixture" };
        var status = await client.GetFromJsonAsync<JsonElement>("/web/status");
        var instance = status.GetProperty("qbitInstances").GetProperty("one");
        instance.GetProperty("alive").GetBoolean().Should().BeTrue();
        instance.GetProperty("version").GetString().Should().Be("fixture-version");
        adapter.Verify(c => c.LoginAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task StandaloneConfig_RenameRemovesOldSectionAndPreservesOtherInstances(bool wholeSection)
    {
        await using var factory = new StandaloneFactory();
        var client = factory.CreateClient();
        var config = factory.Services.GetRequiredService<TorrentarrConfig>();
        config.QBitInstances["qBit"] = new()
        {
            Host = "old",
            Disabled = true,
            Password = "secret",
        };
        config.QBitInstances["qBit-other"] = new() { Host = "other", Password = "other-secret" };
        config.TorrentClients["canonical"] = new() { Host = "canonical" };
        var flat = await client.GetFromJsonAsync<JsonElement>("/web/config");
        var changes = new Dictionary<string, object?> { ["qBit-renamed"] = flat.GetProperty("qBit").Clone() };
        void RemoveFields(JsonElement value, string prefix)
        {
            foreach (var field in value.EnumerateObject())
                if (field.Value.ValueKind == JsonValueKind.Object) RemoveFields(field.Value, prefix + "." + field.Name);
                else changes[prefix + "." + field.Name] = null;
        }
        if (wholeSection) changes["qBit"] = null;
        else RemoveFields(flat.GetProperty("qBit"), "qBit");
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TorrentarrDbContext>();
        db.TorrentLibrary.Add(new() { Hash = "rename-state", QbitInstance = "qBit", Imported = true, FreeSpacePaused = true });
        await db.SaveChangesAsync();
        var response = await client.PostAsJsonAsync("/web/config", new
        {
            changes,
            renames = new Dictionary<string, string> { ["qBit"] = "qBit-renamed" }
        });
        response.EnsureSuccessStatusCode();
        var state = await db.TorrentLibrary.AsNoTracking().SingleAsync(t => t.Hash == "rename-state");
        state.QbitInstance.Should().Be("qBit-renamed"); state.Imported.Should().BeTrue(); state.FreeSpacePaused.Should().BeTrue();

        var saved = new ConfigurationLoader(factory.Services.GetRequiredService<IConfigReloader>().ConfigPath).Load();
        saved.QBitInstances.Should().NotContainKey("qBit");
        saved.QBitInstances["qBit-renamed"].Host.Should().Be("old");
        saved.QBitInstances["qBit-renamed"].Disabled.Should().BeTrue();
        saved.QBitInstances["qBit-renamed"].Password.Should().Be("secret");
        saved.QBitInstances["qBit-other"].Password.Should().Be("other-secret");
        saved.TorrentClients.Should().ContainKey("canonical");
        saved.ArrInstances.Should().ContainKey("Radarr");
        var deleted = await client.PostAsJsonAsync("/web/config", new
        {
            changes = new Dictionary<string, object?> { ["qBit-renamed"] = null, ["TorrentClient.canonical"] = null }
        });
        deleted.EnsureSuccessStatusCode();
        saved = new ConfigurationLoader(factory.Services.GetRequiredService<IConfigReloader>().ConfigPath).Load();
        saved.QBitInstances.Should().NotContainKey("qBit-renamed");
        saved.TorrentClients.Should().NotContainKey("canonical");
        saved.QBitInstances.Should().ContainKey("qBit-other");
    }

    [Fact]
    public async Task StandaloneConfig_CanonicalRenamePreservesSecretsAndStoredState()
    {
        await using var factory = new StandaloneFactory();
        var client = factory.CreateClient();
        var config = factory.Services.GetRequiredService<TorrentarrConfig>();
        config.TorrentClients["old"] = new()
        {
            Host = "fixture",
            Disabled = true,
            Password = "secret",
        };
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TorrentarrDbContext>();
        db.TorrentLibrary.Add(new() { Hash = "canonical-rename", QbitInstance = "old", FreeSpacePaused = true });
        await db.SaveChangesAsync();
        var response = await client.PostAsJsonAsync("/web/config", new
        {
            renames = new Dictionary<string, string> { ["old"] = "renamed" },
            changes = new Dictionary<string, object?>
            {
                ["TorrentClient.old"] = null,
                ["TorrentClient.renamed"] = new
                {
                    Host = "fixture",
                    Disabled = true,
                    Password = "[redacted]",
                }
            }
        });
        response.EnsureSuccessStatusCode();
        var saved = new ConfigurationLoader(factory.Services.GetRequiredService<IConfigReloader>().ConfigPath).Load();
        saved.TorrentClients.Should().NotContainKey("old");
        saved.TorrentClients["renamed"].Password.Should().Be("secret");
        var state = await db.TorrentLibrary.AsNoTracking().SingleAsync(t => t.Hash == "canonical-rename");
        state.QbitInstance.Should().Be("renamed");
        state.FreeSpacePaused.Should().BeTrue();
    }

    private sealed class StandaloneFactory : WebApplicationFactory<Standalone::Program>
    {
        private readonly string _directory = Path.Combine(Path.GetTempPath(), "standalone-radarr-" + Guid.NewGuid());
        private readonly SqliteConnection _connection = new("Data Source=:memory:");
        private readonly string? _previousConfig = Environment.GetEnvironmentVariable("TORRENTARR_CONFIG");
        private readonly string? _previousOverride = ConfigurationLoader.TestConfigPathOverride;
        public StandaloneFactory(string urlBase = "")
        {
            Directory.CreateDirectory(_directory);
            File.WriteAllText(Path.Combine(_directory, "config.toml"), $$"""
                [Settings]
                ConfigVersion = '6.14.6'
                [WebUI]
                AuthDisabled = true
                AllowInsecureExposure = true
                Token = 'standalone-test'
                UrlBase = '{{urlBase}}'
                [Radarr]
                URI = 'http://127.0.0.1:1'
                Category = 'movies'
                Managed = false
                """);
            Directory.CreateDirectory(Path.Combine(_directory, "wwwroot"));
            File.WriteAllText(Path.Combine(_directory, "wwwroot", "index.html"), "<html><head></head><body></body></html>");
            _connection.Open();
        }
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            var path = Path.Combine(_directory, "config.toml");
            Environment.SetEnvironmentVariable("TORRENTARR_CONFIG", path);
            ConfigurationLoader.TestConfigPathOverride = path;
            builder.UseEnvironment("Testing");
            builder.UseWebRoot(Path.Combine(_directory, "wwwroot"));
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
            // The configuration watcher can briefly create files while the test host shuts down.
            // Retry transient directory-not-empty errors instead of failing an otherwise successful test.
            for (var attempt = 0; attempt < 6; attempt++)
            {
                try
                {
                    if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
                    break;
                }
                catch (IOException) when (attempt < 5)
                {
                    await Task.Delay(TimeSpan.FromMilliseconds(100 * (attempt + 1)));
                }
            }
        }
    }
}

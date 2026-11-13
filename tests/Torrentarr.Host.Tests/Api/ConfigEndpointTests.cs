using FluentAssertions;
using Torrentarr.Core.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.EntityFrameworkCore;
using Torrentarr.Infrastructure.Database;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Xunit;

namespace Torrentarr.Host.Tests.Api;

[Collection("HostWeb")]
public class ConfigEndpointTests : IClassFixture<TorrentarrWebApplicationFactory>
{
    [Theory]
    [InlineData("/web/config")]
    [InlineData("/api/config")]
    public async Task ScopedTrackerRulesArePublishedAndReferencesFollowRenames(string endpoint)
    {
        using var client = _factory.CreateClientWithApiToken();
        var cfg = _factory.Services.GetRequiredService<TorrentarrConfig>();
        var loader = _factory.Services.GetRequiredService<ConfigurationLoader>();
        var originalRules = cfg.Trackers;
        var suffix = endpoint.StartsWith("/web") ? "web" : "api";
        var first = $"tracker-first-{suffix}";
        var second = $"tracker-second-{suffix}";
        var renamed = $"tracker-renamed-{suffix}";
        var firstArr = $"Radarr-tracker-{suffix}";
        var secondArr = $"Sonarr-tracker-{suffix}";
        var renamedArr = $"Radarr-renamed-{suffix}";

        Task<HttpResponseMessage> Save(Dictionary<string, object?> changes, Dictionary<string, string>? renames = null) =>
            client.PostAsJsonAsync(endpoint, new { changes, renames = renames ?? new() });

        try
        {
            using var save = await Save(new()
            {
                [$"TorrentClient.{first}.Host"] = "fixture",
                [$"TorrentClient.{first}.Disabled"] = true,
                [$"TorrentClient.{second}.Host"] = "fixture",
                [$"TorrentClient.{second}.Disabled"] = true,
                [$"{firstArr}.Category"] = "tracker-movies",
                [$"{secondArr}.Category"] = "tracker-tv",
                [$"{firstArr}.Managed"] = false,
                [$"{secondArr}.Managed"] = false,
                ["Trackers"] = new object[]
                {
                    new { Name = "Shared", URI = "https://tracker.example", ClientIds = new[] { first, second } },
                    new { Name = "Override", URI = "https://tracker.example", ArrInstances = new[] { firstArr, secondArr } }
                }
            });
            (await save.Content.ReadAsStringAsync()).Should().Contain("applied");
            save.EnsureSuccessStatusCode();
            cfg.Trackers.Should().HaveCount(2);
            using var document = JsonDocument.Parse(await client.GetStringAsync(endpoint));
            document.RootElement.GetProperty("Trackers").GetArrayLength().Should().Be(2);

            var before = File.ReadAllText(loader.ConfigPath);
            using var invalid = await Save(new()
            {
                ["Trackers"] = new[] { new { ClientIds = new[] { first }, ArrInstances = new[] { firstArr } } }
            });
            invalid.StatusCode.Should().Be(HttpStatusCode.BadRequest);
            using var deletion = await Save(new() { [$"TorrentClient.{first}"] = null });
            deletion.StatusCode.Should().Be(HttpStatusCode.BadRequest);
            (await deletion.Content.ReadAsStringAsync()).Should().Contain("Shared").And.Contain(first);
            File.ReadAllText(loader.ConfigPath).Should().Be(before);

            using var rename = await Save(new()
            {
                [$"TorrentClient.{first}"] = null,
                [$"TorrentClient.{renamed}.Host"] = "fixture",
                [$"TorrentClient.{renamed}.Disabled"] = true,
                [firstArr] = null,
                [$"{renamedArr}.Category"] = "tracker-movies",
                [$"{renamedArr}.Managed"] = false
            }, new() { [first] = renamed, [firstArr] = renamedArr });
            rename.EnsureSuccessStatusCode();
            cfg.Trackers[0].ClientIds.Should().Equal(renamed, second);
            cfg.Trackers[1].ArrInstances.Should().Equal(renamedArr, secondArr);
            loader.Load(false).Trackers.Should().BeEquivalentTo(cfg.Trackers, o => o.WithStrictOrdering());
        }
        finally
        {
            using var cleanup = await Save(new()
            {
                ["Trackers"] = originalRules.Select(rule => Newtonsoft.Json.Linq.JObject.FromObject(rule).ToString())
                    .Select(json => JsonSerializer.Deserialize<JsonElement>(json)).ToArray(),
                [$"TorrentClient.{first}"] = null,
                [$"TorrentClient.{second}"] = null,
                [$"TorrentClient.{renamed}"] = null,
                [firstArr] = null,
                [secondArr] = null,
                [renamedArr] = null
            });
            cleanup.EnsureSuccessStatusCode();
        }
    }

    [Theory]
    [InlineData("/web/torrent-clients/no-such-client/reconnect")]
    [InlineData("/api/torrent-clients/no-such-client/reconnect")]
    public async Task ManualReconnect_UnknownClientReturnsNotFound(string endpoint)
    {
        using var client = _factory.CreateClientWithApiToken();
        var response = await client.PostAsync(endpoint, null);
        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task ManualReconnect_DisabledClientCannotConnect()
    {
        var cfg = _factory.Services.GetRequiredService<Torrentarr.Core.Configuration.TorrentarrConfig>();
        const string id = "disabled-reconnect-fixture";
        cfg.TorrentClients[id] = new() { Host = "fixture", Disabled = true };
        try
        {
            using var client = _factory.CreateClientWithApiToken();
            var response = await client.PostAsync($"/api/torrent-clients/{id}/reconnect", null);
            response.StatusCode.Should().Be(HttpStatusCode.Conflict);
        }
        finally { cfg.TorrentClients.Remove(id); }
    }

    private readonly TorrentarrWebApplicationFactory _factory;

    public ConfigEndpointTests(TorrentarrWebApplicationFactory factory)
    {
        _factory = factory;
    }

    [Theory]
    [InlineData("/web/config", "TorrentClient.storage-migration-fixture")]
    [InlineData("/api/config", "qBit-storage-migration-fixture")]
    public async Task KeepFreeSpaceProtectionOff_RequiresValidExplicitSave(string endpoint, string section)
    {
        using var client = _factory.CreateClientWithApiToken();
        await AssertStorageMigrationSave(client, _factory.Services.GetRequiredService<ConfigurationLoader>(), endpoint, section);
    }

    internal static async Task AssertStorageMigrationSave(HttpClient client, ConfigurationLoader loader, string endpoint, string section)
    {
        const string pendingMonitor = "no unambiguous local monitor path; free-space protection remains disabled until configured";
        var id = section.Split('.').Last();
        var field = $"{section}.FreeSpace";
        var seed = await client.PostAsJsonAsync(endpoint, new
        {
            changes = new Dictionary<string, object>
            {
                [$"{section}.Host"] = "fixture",
                [$"{section}.Disabled"] = true,
                [$"{section}.Migration.Notices"] = new[] { pendingMonitor, StorageConfigMigration.DownloadPathNotice }
            }
        });
        seed.EnsureSuccessStatusCode();
        try
        {
            var unrelated = await client.PostAsJsonAsync(endpoint, new
            {
                changes = new Dictionary<string, object> { [$"{section}.TorrentScanIntervalSeconds"] = 60 }
            });
            unrelated.EnsureSuccessStatusCode();
            loader.Load(false).GetTorrentClient(id)!.Migration.Notices.Should().Contain(pendingMonitor);

            var before = File.ReadAllText(loader.ConfigPath);
            var invalid = await client.PostAsJsonAsync(endpoint, new
            {
                changes = new Dictionary<string, object> { [field] = new[] { new { Path = "relative", Enabled = false, Limit = "10G" } } },
                keepFreeSpaceProtectionOff = field
            });
            invalid.StatusCode.Should().Be(HttpStatusCode.BadRequest);
            File.ReadAllText(loader.ConfigPath).Should().Be(before);

            var saved = await client.PostAsJsonAsync(endpoint, new
            {
                changes = new Dictionary<string, object> { [field] = Array.Empty<object>() },
                keepFreeSpaceProtectionOff = field
            });
            saved.EnsureSuccessStatusCode();
            var updated = loader.Load(false).GetTorrentClient(id)!;
            updated.FreeSpace.Should().BeEmpty();
            updated.Migration.Notices.Should().Equal(StorageConfigMigration.DownloadPathNotice);
        }
        finally
        {
            var cleanup = await client.PostAsJsonAsync(endpoint, new
            {
                changes = new Dictionary<string, object?> { [section] = null }
            });
            cleanup.EnsureSuccessStatusCode();
        }
    }

    [Theory]
    [InlineData("/web/config", 10)]
    [InlineData("/web/config", 30.5)]
    [InlineData("/api/config", 10)]
    [InlineData("/api/config", 30.5)]
    public async Task UnsupportedScanInterval_IsRejectedWithoutChangingConfig(string endpoint, double value)
    {
        using var client = _factory.CreateClientWithApiToken();
        var loader = _factory.Services.GetRequiredService<ConfigurationLoader>();
        var before = File.ReadAllText(loader.ConfigPath);
        var response = await client.PostAsJsonAsync(endpoint, new
        {
            changes = new Dictionary<string, object> { ["TorrentClient.invalid-scan.TorrentScanIntervalSeconds"] = value }
        });
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        File.ReadAllText(loader.ConfigPath).Should().Be(before);
    }

    [Theory]
    [InlineData("/web/config")]
    [InlineData("/api/config")]
    public async Task ConfigSave_WakesHostOrchestrator(string endpoint)
    {
        using var factory = _factory.WithWebHostBuilder(builder => builder.ConfigureServices(services =>
            services.AddHostedService<ProcessOrchestratorService>()));
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", "test-api-token");
        var reloader = factory.Services.GetRequiredService<Torrentarr.Core.Services.IConfigReloader>();
        var config = factory.Services.GetRequiredService<TorrentarrConfig>();
        var previousRevision = reloader.Revision;
        var interval = config.Settings.LoopSleepTimer == 3600 ? 3599 : 3600;
        var response = await client.PostAsJsonAsync(endpoint, new
        {
            changes = new Dictionary<string, object> { ["Settings.LoopSleepTimer"] = interval }
        });
        response.EnsureSuccessStatusCode();
        reloader.Revision.Should().BeGreaterThan(previousRevision);
        config.Settings.LoopSleepTimer.Should().Be(interval);
        var acceptedRevision = reloader.Revision;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (reloader.GetStatus().Components.GetValueOrDefault("torrent-policy")?.AppliedRevision != acceptedRevision)
            await Task.Delay(10, timeout.Token);
    }

    [Theory]
    [InlineData("/web/config", false)]
    [InlineData("/web/config", true)]
    [InlineData("/api/config", false)]
    [InlineData("/api/config", true)]
    public async Task RenameClient_PreservesPasswordAndStoredState(string endpoint, bool canonical)
    {
        var client = _factory.CreateClientWithApiToken();
        using var scope = _factory.Services.CreateScope();
        var config = scope.ServiceProvider.GetRequiredService<Torrentarr.Core.Configuration.TorrentarrConfig>();
        var prefix = canonical ? "TorrentClient." : "";
        var source = new Torrentarr.Core.Configuration.QBitConfig()
        {
            Host = "fixture",
            Disabled = true,
            Password = "secret",
        };
        if (canonical) config.TorrentClients["qBit-before"] = source;
        else config.QBitInstances["qBit-before"] = source;
        var db = scope.ServiceProvider.GetRequiredService<TorrentarrDbContext>();
        db.TorrentLibrary.Add(new()
        {
            Hash = "rename-state",
            QbitInstance = "qBit-before",
            Imported = true,
            AllowedSeeding = true,
            AllowedStalled = true,
            FreeSpacePaused = true
        });
        db.OperationalIssues.Add(new() { Code = "rename.test", Category = "system", ClientInstance = "qBit-before", FirstObserved = DateTime.UtcNow, LastObserved = DateTime.UtcNow });
        await db.SaveChangesAsync();
        try
        {
            var response = await client.PostAsJsonAsync(endpoint, new
            {
                renames = new Dictionary<string, string> { ["qBit-before"] = "qBit-after" },
                changes = new Dictionary<string, object?>
                {
                    [prefix + "qBit-before"] = null,
                    [prefix + "qBit-after.Host"] = "fixture",
                    [prefix + "qBit-after.Disabled"] = true,
                    [prefix + "qBit-after.Password"] = "[redacted]",
                }
            });
            response.EnsureSuccessStatusCode();
            var state = await db.TorrentLibrary.AsNoTracking().SingleAsync(t => t.Hash == "rename-state");
            state.QbitInstance.Should().Be("qBit-after");
            (await db.OperationalIssues.AsNoTracking().SingleAsync(i => i.Code == "rename.test")).ClientInstance.Should().Be("qBit-after");
            state.Imported.Should().BeTrue(); state.AllowedSeeding.Should().BeTrue();
            state.AllowedStalled.Should().BeTrue(); state.FreeSpacePaused.Should().BeTrue();
            var clients = Torrentarr.Core.Configuration.TorrentClientConfigExtensions.GetAllTorrentClients(config);
            clients.Should().NotContainKey("qBit-before");
            clients["qBit-after"].Password.Should().Be("secret");
        }
        finally
        {
            await db.OperationalIssues.Where(i => i.Code == "rename.test").ExecuteDeleteAsync();
            await db.TorrentLibrary.Where(t => t.Hash == "rename-state").ExecuteDeleteAsync();
            config.QBitInstances.Remove("qBit-before");
            config.QBitInstances.Remove("qBit-after");
            config.TorrentClients.Remove("qBit-before");
            config.TorrentClients.Remove("qBit-after");
        }
    }

    [Theory]
    [InlineData("/web/config")]
    [InlineData("/api/config")]
    [InlineData("/WEB/CONFIG/")]
    [InlineData("/web/auth/set-password")]
    public async Task ConfigurationWrites_Return409DuringExclusiveChanges(string endpoint)
    {
        var client = _factory.CreateClientWithApiToken();
        var loader = _factory.Services.GetRequiredService<Torrentarr.Core.Configuration.ConfigurationLoader>();
        using var external = new FileStream(loader.ConfigPath + ".write-lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        var response = await client.PostAsJsonAsync(endpoint, new { changes = new { }, username = "user", password = "password" });
        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task GetConfig_Returns200()
    {
        var client = _factory.CreateClientWithApiToken();

        var response = await client.GetAsync("/web/config");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task GetConfig_ReturnsFlatStructure_WithSettingsAndWebUI()
    {
        var client = _factory.CreateClientWithApiToken();

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
        var client = _factory.CreateClientWithApiToken();

        var response = await client.GetAsync("/web/config");
        var body = await response.Content.ReadAsStringAsync();
        var json = JsonDocument.Parse(body).RootElement;

        // qBit should be absent because no [qBit] section in test config (QBitInstances is empty)
        json.TryGetProperty("qBit", out _).Should().BeFalse();
    }

    [Fact]
    public async Task PostConfig_Returns200_WithValidPayload()
    {
        var client = _factory.CreateClientWithApiToken();
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
        var client = _factory.CreateClientWithApiToken();
        var content = new StringContent("{}", Encoding.UTF8, "application/json");

        var response = await client.PostAsync("/web/config", content);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task PostConfig_ReturnsReloadType_None_ForWebuiKeys()
    {
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
        var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
        json.GetProperty("reloadType").GetString().Should().Be("live");
        json.GetProperty("restartRequirements").GetArrayLength().Should().Be(0);
    }

    [Fact]
    public async Task PostConfig_ReturnsReloadType_Webui_ForSettingsKeys()
    {
        var client = _factory.CreateClientWithApiToken();
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
        json.GetProperty("reloadType").GetString().Should().Be("live");
        json.GetProperty("restartRequirements").GetArrayLength().Should().Be(0);
    }

    [Fact]
    public async Task PostConfig_ReturnsReloadType_Full_ForQbitKeys()
    {
        var client = _factory.CreateClientWithApiToken();
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
        json.GetProperty("reloadType").GetString().Should().Be("live");
        json.GetProperty("restartRequirements").GetArrayLength().Should().Be(0);
    }

    [Fact]
    public async Task ConfigRoundTrip_PreservesEntrySearchAndImportMode()
    {
        var client = _factory.CreateClientWithApiToken();
        var payload = new
        {
            changes = new Dictionary<string, object>
            {
                ["Radarr-RoundTrip.URI"] = "http://localhost:7878",
                ["Radarr-RoundTrip.APIKey"] = "test-key",
                ["Radarr-RoundTrip.Category"] = "movies-roundtrip",
                ["Radarr-RoundTrip.Managed"] = true,
                ["Radarr-RoundTrip.ImportMode"] = "Copy",
                ["Radarr-RoundTrip.EntrySearch.SearchMissing"] = false,
                ["Radarr-RoundTrip.EntrySearch.DoUpgradeSearch"] = true
            }
        };
        var post = await client.PostAsync("/web/config", new StringContent(
            JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json"));
        post.StatusCode.Should().Be(HttpStatusCode.OK);

        var get = await client.GetAsync("/web/config");
        get.StatusCode.Should().Be(HttpStatusCode.OK);
        var json = JsonDocument.Parse(await get.Content.ReadAsStringAsync()).RootElement;
        json.TryGetProperty("Radarr-RoundTrip", out var arr).Should().BeTrue();
        arr.GetProperty("ImportMode").GetString().Should().Be("Copy");
        arr.TryGetProperty("EntrySearch", out var entrySearch).Should().BeTrue("Search must serialize as EntrySearch");
        arr.TryGetProperty("Search", out _).Should().BeFalse();
        entrySearch.GetProperty("SearchMissing").GetBoolean().Should().BeFalse();
        entrySearch.GetProperty("DoUpgradeSearch").GetBoolean().Should().BeTrue();

        var cleanup = new
        {
            changes = new Dictionary<string, object?>
            {
                ["Radarr-RoundTrip"] = null
            }
        };
        await client.PostAsync("/web/config", new StringContent(
            JsonSerializer.Serialize(cleanup), Encoding.UTF8, "application/json"));
    }

    [Fact]
    public async Task PostApiConfig_WithChanges_PreservesUnchangedSections()
    {
        var client = _factory.CreateClientWithApiToken();
        var before = await client.GetAsync("/web/config");
        before.StatusCode.Should().Be(HttpStatusCode.OK);
        var beforeJson = JsonDocument.Parse(await before.Content.ReadAsStringAsync()).RootElement;
        beforeJson.TryGetProperty("WebUI", out _).Should().BeTrue();
        var originalPort = beforeJson.GetProperty("WebUI").GetProperty("Port").GetInt32();

        var payload = new
        {
            changes = new Dictionary<string, object>
            {
                ["Settings.LoopSleepTimer"] = 42
            }
        };
        var patchResponse = await client.PostAsJsonAsync("/api/config", payload);
        patchResponse.StatusCode.Should().Be(HttpStatusCode.OK);

        var after = await client.GetAsync("/web/config");
        var afterJson = JsonDocument.Parse(await after.Content.ReadAsStringAsync()).RootElement;
        afterJson.GetProperty("Settings").GetProperty("LoopSleepTimer").GetInt32().Should().Be(42);
        afterJson.GetProperty("WebUI").GetProperty("Port").GetInt32().Should().Be(originalPort);
    }

    [Fact]
    public async Task PostApiConfig_WithoutChanges_ReturnsBadRequest()
    {
        var client = _factory.CreateClientWithApiToken();
        var payload = new { Settings = new { LoopSleepTimer = 99 } };
        var response = await client.PostAsJsonAsync("/api/config", payload);
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var body = await response.Content.ReadAsStringAsync();
        body.Should().Contain("changes");
    }
}

/// <summary>
/// MT-4: GET /web/config must redact PasswordHash (return "[redacted]" not the bcrypt hash).
/// MT-5: POST /web/config with {"WebUI.PasswordHash":"[redacted]"} must NOT overwrite the real hash.
/// Uses LocalAuthWebApplicationFactory which has a real bcrypt hash configured.
/// </summary>
[Collection("HostWebLocalAuth")]
public class ConfigRedactionTests : IClassFixture<LocalAuthWebApplicationFactory>
{
    private readonly LocalAuthWebApplicationFactory _factory;

    public ConfigRedactionTests(LocalAuthWebApplicationFactory factory)
    {
        _factory = factory;
    }

    /// <summary>MT-4: PasswordHash in GET /web/config response must be "[redacted]".</summary>
    [Fact]
    public async Task GetConfig_RedactsPasswordHash()
    {
        _factory.SetConfigEnv();
        var client = _factory.CreateClientWithApiToken();
        var response = await client.GetAsync("/web/config");
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadAsStringAsync();
        var json = JsonDocument.Parse(body).RootElement;
        var webui = json.GetProperty("WebUI");
        webui.TryGetProperty("PasswordHash", out var hash).Should().BeTrue("PasswordHash must be present in config response");
        hash.GetString().Should().Be("[redacted]", "PasswordHash must be redacted in the config API response");
    }

    /// <summary>MT-5: POST /web/config with PasswordHash="[redacted]" must not overwrite the stored hash.</summary>
    [Fact]
    public async Task PostConfig_WithRedactedPasswordHash_DoesNotOverwriteRealHash()
    {
        _factory.SetConfigEnv();
        var client = _factory.CreateClientWithApiToken();

        // Confirm login works with the original password before attempting to wipe the hash
        var loginBefore = await client.PostAsJsonAsync("/web/login", new
        {
            username = LocalAuthWebApplicationFactory.TestUsername,
            password = LocalAuthWebApplicationFactory.TestPassword
        });
        loginBefore.StatusCode.Should().Be(HttpStatusCode.OK);

        // POST config with [redacted] placeholder — this should be silently ignored
        var payload = new { changes = new Dictionary<string, object> { ["WebUI.PasswordHash"] = "[redacted]" } };
        var patchResponse = await client.PostAsJsonAsync("/web/config", payload);
        patchResponse.StatusCode.Should().Be(HttpStatusCode.OK);

        // Login must still work — the real hash must not have been overwritten
        var loginAfter = await client.PostAsJsonAsync("/web/login", new
        {
            username = LocalAuthWebApplicationFactory.TestUsername,
            password = LocalAuthWebApplicationFactory.TestPassword
        });
        loginAfter.StatusCode.Should().Be(HttpStatusCode.OK, "login should still work because [redacted] must not overwrite the real hash");
    }

    /// <summary>
    /// Clearing PasswordHash via config API would re-open bootstrap set-password with WebUI.Token only.
    /// </summary>
    [Fact]
    public async Task PostConfig_WithEmptyPasswordHash_Returns403_AndPreservesLogin()
    {
        _factory.SetConfigEnv();
        var client = _factory.CreateClientWithApiToken();

        var payload = new { changes = new Dictionary<string, object> { ["WebUI.PasswordHash"] = "" } };
        var patchResponse = await client.PostAsJsonAsync("/web/config", payload);
        patchResponse.StatusCode.Should().Be(HttpStatusCode.Forbidden);

        var loginAfter = await client.PostAsJsonAsync("/web/login", new
        {
            username = LocalAuthWebApplicationFactory.TestUsername,
            password = LocalAuthWebApplicationFactory.TestPassword
        });
        loginAfter.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task PostApiConfig_WithEmptyPasswordHash_Returns403_AndPreservesLogin()
    {
        _factory.SetConfigEnv();
        var client = _factory.CreateClientWithApiToken();

        var payload = new { changes = new Dictionary<string, object> { ["WebUI.PasswordHash"] = "" } };
        var patchResponse = await client.PostAsJsonAsync("/api/config", payload);
        patchResponse.StatusCode.Should().Be(HttpStatusCode.Forbidden);

        var loginAfter = await client.PostAsJsonAsync("/web/login", new
        {
            username = LocalAuthWebApplicationFactory.TestUsername,
            password = LocalAuthWebApplicationFactory.TestPassword
        });
        loginAfter.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    /// <summary>
    /// Section-level WebUI replace bypasses dotted-key RejectPasswordHashConfigChange guard.
    /// Regression: closed PR #276; #271 only blocked WebUI.PasswordHash dotted keys.
    /// </summary>
    [Fact]
    public async Task PostConfig_WithWholeWebUISectionEmptyPasswordHash_Returns403_AndPreservesLogin()
    {
        _factory.SetConfigEnv();
        var client = _factory.CreateClientWithApiToken();

        var getResponse = await client.GetAsync("/web/config");
        var configJson = JsonDocument.Parse(await getResponse.Content.ReadAsStringAsync()).RootElement;
        var webui = new Dictionary<string, object?>();
        foreach (var webuiProp in configJson.GetProperty("WebUI").EnumerateObject())
        {
            webui[webuiProp.Name] = webuiProp.NameEquals("PasswordHash")
                ? ""
                : webuiProp.Value.ValueKind switch
                {
                    JsonValueKind.String => webuiProp.Value.GetString(),
                    JsonValueKind.Number => webuiProp.Value.GetInt32(),
                    JsonValueKind.True => true,
                    JsonValueKind.False => false,
                    _ => webuiProp.Value.Clone()
                };
        }

        var patchResponse = await client.PostAsJsonAsync("/web/config", new { changes = new Dictionary<string, object> { ["WebUI"] = webui } });
        patchResponse.StatusCode.Should().Be(HttpStatusCode.Forbidden);

        var loginAfter = await client.PostAsJsonAsync("/web/login", new
        {
            username = LocalAuthWebApplicationFactory.TestUsername,
            password = LocalAuthWebApplicationFactory.TestPassword
        });
        loginAfter.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task PostConfig_EmptyPasswordHash_CannotBeUsedForAccountTakeoverViaSetPassword()
    {
        _factory.SetConfigEnv();
        var client = _factory.CreateClientWithApiToken();

        var clearAttempt = await client.PostAsJsonAsync("/web/config", new
        {
            changes = new Dictionary<string, object> { ["WebUI.PasswordHash"] = "" }
        });
        clearAttempt.StatusCode.Should().Be(HttpStatusCode.Forbidden);

        var setPasswordAttempt = await client.PostAsJsonAsync("/web/auth/set-password", new
        {
            username = "attacker",
            password = "hijack123",
            setupToken = "test-api-token"
        });
        setPasswordAttempt.StatusCode.Should().Be(HttpStatusCode.Forbidden);

        var loginOriginal = await client.PostAsJsonAsync("/web/login", new
        {
            username = LocalAuthWebApplicationFactory.TestUsername,
            password = LocalAuthWebApplicationFactory.TestPassword
        });
        loginOriginal.StatusCode.Should().Be(HttpStatusCode.OK);
    }
}

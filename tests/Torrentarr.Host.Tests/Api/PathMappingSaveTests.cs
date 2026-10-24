using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Torrentarr.Core.Configuration;
using Torrentarr.Core.Services;
using Torrentarr.Infrastructure.Services;
using Xunit;

namespace Torrentarr.Host.Tests.Api;

[Collection("HostWeb")]
public sealed class PathMappingSaveTests : IClassFixture<TorrentarrWebApplicationFactory>
{
    private readonly TorrentarrWebApplicationFactory _factory;
    public PathMappingSaveTests(TorrentarrWebApplicationFactory factory) { _factory = factory; _factory.SetConfigEnv(); }

    [Theory]
    [InlineData("/web/config", false)]
    [InlineData("/web/config", true)]
    [InlineData("/api/config", false)]
    [InlineData("/api/config", true)]
    public async Task MappingEdits_PersistAndReloadDuringProcessing(string endpoint, bool canonical)
    {
        using var client = _factory.CreateClientWithApiToken();
        client.Timeout = TimeSpan.FromSeconds(10);
        var config = _factory.Services.GetRequiredService<TorrentarrConfig>();
        var loader = _factory.Services.GetRequiredService<ConfigurationLoader>();
        var name = canonical ? "mapping-canonical" : "qBit-mapping";
        var prefix = canonical ? "TorrentClient." + name : name;
        var root = Path.Combine(Path.GetTempPath(), "mapping-api-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var initial = new QBitConfig { Host = "fixture", Disabled = true };
        if (canonical) config.TorrentClients[name] = initial; else config.QBitInstances[name] = initial;
        config.TorrentClients["mapping-other"] = new() { Disabled = true, Maintenance = new() { PathMappings = [new() { ClientPath = "/other", LocalPath = root }] } };
        var reloader = _factory.Services.GetRequiredService<IConfigReloader>();
        reloader.Acknowledge("mapping-test", 0);
        var mappings = new PathMappingConfig[]
        {
            new() { ClientPath = "/downloads/", LocalPath = root },
            new() { ClientPath = "/downloads/movies", LocalPath = Path.Combine(root, "movie files") },
            new() { ClientPath = @"D:\Downloads\TV\", LocalPath = Path.Combine(root, "tv files") }
        };
        try
        {
            using var processing = ConfigMutationGuard.EnterProcessing(loader.ConfigPath);
            foreach (var expected in new[] { mappings.Take(1).ToArray(), mappings, mappings.Skip(1).ToArray(), Array.Empty<PathMappingConfig>() })
            {
                var response = await client.PostAsJsonAsync(endpoint, new
                {
                    changes = new Dictionary<string, object> { [prefix + ".Maintenance.PathMappings"] = expected }
                });
                response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
                var result = await response.Content.ReadFromJsonAsync<JsonElement>();
                result.GetProperty("application").GetProperty("state").GetString().Should().Be("pending");
                var saved = new ConfigurationLoader(loader.ConfigPath).Load(persistMigrations: false);
                saved.GetTorrentClient(name)!.Maintenance.PathMappings.Should().BeEquivalentTo(expected, options => options.WithStrictOrdering());
                config.GetTorrentClient(name)!.Maintenance.PathMappings.Should().BeEquivalentTo(expected, options => options.WithStrictOrdering());
                saved.TorrentClients["mapping-other"].Maintenance.PathMappings.Single().ClientPath.Should().Be("/other");
                if (expected.Length == 3)
                {
                    var paths = new PathMappingService();
                    paths.MapToLocal("/downloads/movies/title.mkv", saved.GetTorrentClient(name)!.Maintenance.PathMappings)
                        .Should().Be(Path.Combine(root, "movie files", "title.mkv"));
                    paths.MapToLocal(@"D:\Downloads\TV\show.mkv", saved.GetTorrentClient(name)!.Maintenance.PathMappings)
                        .Should().Be(Path.Combine(root, "tv files", "show.mkv"));
                }
                var read = await client.GetFromJsonAsync<JsonElement>(endpoint);
                var section = endpoint == "/api/config"
                    ? read.GetProperty(canonical ? "TorrentClients" : "QBitInstances").GetProperty(name)
                    : canonical ? read.GetProperty("TorrentClient").GetProperty(name) : read.GetProperty(name);
                section.GetProperty("Maintenance").GetProperty("PathMappings").GetArrayLength().Should().Be(expected.Length);
            }
        }
        finally
        {
            config.QBitInstances.Remove(name); config.TorrentClients.Remove(name); config.TorrentClients.Remove("mapping-other");
            Directory.Delete(root, recursive: true);
        }
    }

    [Theory]
    [InlineData("/web/config", false)]
    [InlineData("/api/config", true)]
    public async Task InvalidMapping_DoesNotChangeFileOrActiveConfiguration(string endpoint, bool canonical)
    {
        using var client = _factory.CreateClientWithApiToken();
        var config = _factory.Services.GetRequiredService<TorrentarrConfig>();
        var loader = _factory.Services.GetRequiredService<ConfigurationLoader>();
        var before = File.ReadAllText(loader.ConfigPath);
        var active = Newtonsoft.Json.JsonConvert.SerializeObject(config);
        var prefix = canonical ? "TorrentClient.mapping-invalid" : "qBit-mapping-invalid";
        var response = await client.PostAsJsonAsync(endpoint, new
        {
            changes = new Dictionary<string, object>
            {
                [prefix + ".Disabled"] = true,
                [prefix + ".Maintenance.PathMappings"] = new[] { new { ClientPath = "/downloads/../outside", LocalPath = Path.GetTempPath() } }
            }
        });
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        File.ReadAllText(loader.ConfigPath).Should().Be(before);
        Newtonsoft.Json.JsonConvert.SerializeObject(config).Should().Be(active);
    }

    [Theory]
    [InlineData("/web/config")]
    [InlineData("/api/config")]
    public async Task RenameDuringProcessing_Returns409Promptly(string endpoint)
    {
        using var client = _factory.CreateClientWithApiToken();
        client.Timeout = TimeSpan.FromSeconds(5);
        var loader = _factory.Services.GetRequiredService<ConfigurationLoader>();
        using var processing = ConfigMutationGuard.EnterProcessing(loader.ConfigPath);
        var response = await client.PostAsJsonAsync(endpoint, new { changes = new { }, renames = new { old = "new" } });
        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
    }

    [Theory]
    [InlineData("/web/config")]
    [InlineData("/api/config")]
    public async Task CompetingPublication_Returns409WithoutWriting(string endpoint)
    {
        using var client = _factory.CreateClientWithApiToken();
        client.Timeout = TimeSpan.FromSeconds(5);
        var loader = _factory.Services.GetRequiredService<ConfigurationLoader>();
        var before = File.ReadAllText(loader.ConfigPath);
        using var external = new FileStream(loader.ConfigPath + ".publish-lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        var response = await client.PostAsJsonAsync(endpoint, new { changes = new Dictionary<string, object> { ["Settings.LoopSleepTimer"] = 7 } });
        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
        File.ReadAllText(loader.ConfigPath).Should().Be(before);
    }

    [Fact]
    public async Task ConcurrentMappingSaves_DoNotOverwriteAnotherClientsChanges()
    {
        using var client = _factory.CreateClientWithApiToken();
        client.Timeout = TimeSpan.FromSeconds(5);
        var loader = _factory.Services.GetRequiredService<ConfigurationLoader>();
        var config = _factory.Services.GetRequiredService<TorrentarrConfig>();
        var names = new[] { "concurrent-one", "concurrent-two" };
        foreach (var name in names) config.TorrentClients[name] = new() { Disabled = true };
        Task<HttpResponseMessage> Save(string name) => client.PostAsJsonAsync("/web/config", new
        {
            changes = new Dictionary<string, object>
            {
                ["TorrentClient." + name + ".Maintenance.PathMappings"] =
                new[] { new { ClientPath = "/" + name, LocalPath = Path.GetTempPath() } }
            }
        });
        try
        {
            using var processing = ConfigMutationGuard.EnterProcessing(loader.ConfigPath);
            var responses = await Task.WhenAll(names.Select(Save));
            responses.Should().Contain(response => response.IsSuccessStatusCode);
            for (var i = 0; i < responses.Length; i++)
            {
                if (responses[i].StatusCode == HttpStatusCode.Conflict)
                {
                    using var retry = await Save(names[i]);
                    retry.StatusCode.Should().Be(HttpStatusCode.OK);
                }
                else responses[i].StatusCode.Should().Be(HttpStatusCode.OK);
                responses[i].Dispose();
            }
            var saved = new ConfigurationLoader(loader.ConfigPath).Load(persistMigrations: false);
            foreach (var name in names)
                saved.TorrentClients[name].Maintenance.PathMappings.Single().ClientPath.Should().Be("/" + name);
        }
        finally { foreach (var name in names) config.TorrentClients.Remove(name); }
    }

    [Fact]
    public async Task CancelledSave_DoesNotWrite()
    {
        using var client = _factory.CreateClientWithApiToken();
        var loader = _factory.Services.GetRequiredService<ConfigurationLoader>();
        var before = File.ReadAllText(loader.ConfigPath);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var save = () => client.PostAsJsonAsync("/web/config", new { changes = new { } }, cancellation.Token);
        await FluentActions.Awaiting(save).Should().ThrowAsync<OperationCanceledException>();
        File.ReadAllText(loader.ConfigPath).Should().Be(before);
    }
}

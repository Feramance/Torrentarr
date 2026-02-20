using FluentAssertions;
using Torrentarr.Core.Configuration;
using Torrentarr.Infrastructure.ApiClients.Arr;
using Xunit;

namespace Torrentarr.Infrastructure.Tests.ApiClients;

/// <summary>
/// Live integration tests for RadarrClient.
/// Gated with [Trait("Category","Live")] — excluded from CI.
/// Connection settings are read from the user's config file at runtime.
/// </summary>
[Trait("Category", "Live")]
public class RadarrClientLiveTests : IAsyncLifetime
{
    private RadarrClient? _client;
    private string? _skipReason;

    public Task InitializeAsync()
    {
        var (uri, key) = LoadLiveRadarrConfig();
        if (uri == null)
        {
            _skipReason = "No live Radarr config found — skipping live test.";
            return Task.CompletedTask;
        }

        _client = new RadarrClient(uri, key!);
        return Task.CompletedTask;
    }

    public Task DisposeAsync() => Task.CompletedTask;

    private static (string? uri, string? key) LoadLiveRadarrConfig()
    {
        try
        {
            var path = ConfigurationLoader.GetDefaultConfigPath();
            if (!File.Exists(path)) return (null, null);

            var config = new ConfigurationLoader(path).Load();
            var radarr = config.ArrInstances
                .FirstOrDefault(kvp => kvp.Value.Type == "radarr" &&
                                        !string.IsNullOrEmpty(kvp.Value.URI) &&
                                        kvp.Value.URI != "CHANGE_ME");
            return radarr.Value != null
                ? (radarr.Value.URI, radarr.Value.APIKey)
                : (null, null);
        }
        catch
        {
            return (null, null);
        }
    }

    [SkippableFact]
    public async Task GetSystemInfoAsync_ReturnsVersion()
    {
        Skip.If(_skipReason != null, _skipReason!);
        var info = await _client!.GetSystemInfoAsync();
        info.Should().NotBeNull();
        info.Version.Should().NotBeNullOrEmpty();
    }

    [SkippableFact]
    public async Task GetMoviesAsync_ReturnsListOrEmpty()
    {
        Skip.If(_skipReason != null, _skipReason!);
        var movies = await _client!.GetMoviesAsync();
        movies.Should().NotBeNull();
    }

    [SkippableFact]
    public async Task GetQualityProfilesAsync_ReturnsList()
    {
        Skip.If(_skipReason != null, _skipReason!);
        var profiles = await _client!.GetQualityProfilesAsync();
        profiles.Should().NotBeNull();
    }
}

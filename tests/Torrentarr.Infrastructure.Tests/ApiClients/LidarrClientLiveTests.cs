using FluentAssertions;
using Torrentarr.Core.Configuration;
using Torrentarr.Infrastructure.ApiClients.Arr;
using Xunit;

namespace Torrentarr.Infrastructure.Tests.ApiClients;

/// <summary>
/// Live integration tests for LidarrClient.
/// Gated with [Trait("Category","Live")] — excluded from CI.
/// Connection settings are read from the user's config file at runtime.
/// </summary>
[Trait("Category", "Live")]
public class LidarrClientLiveTests : IAsyncLifetime
{
    private LidarrClient? _client;
    private string? _skipReason;

    public Task InitializeAsync()
    {
        var (uri, key) = LoadLiveLidarrConfig();
        if (uri == null)
        {
            _skipReason = "No live Lidarr config found — skipping live test.";
            return Task.CompletedTask;
        }

        _client = new LidarrClient(uri, key!);
        return Task.CompletedTask;
    }

    public Task DisposeAsync() => Task.CompletedTask;

    private static (string? uri, string? key) LoadLiveLidarrConfig()
    {
        try
        {
            var path = ConfigurationLoader.GetDefaultConfigPath();
            if (!File.Exists(path)) return (null, null);

            var config = new ConfigurationLoader(path).Load();
            var lidarr = config.ArrInstances
                .FirstOrDefault(kvp => kvp.Value.Type == "lidarr" &&
                                        !string.IsNullOrEmpty(kvp.Value.URI) &&
                                        kvp.Value.URI != "CHANGE_ME");
            return lidarr.Value != null
                ? (lidarr.Value.URI, lidarr.Value.APIKey)
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
    public async Task GetArtistsAsync_ReturnsListOrEmpty()
    {
        Skip.If(_skipReason != null, _skipReason!);
        var artists = await _client!.GetArtistsAsync();
        artists.Should().NotBeNull();
    }
}

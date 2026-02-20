using FluentAssertions;
using Torrentarr.Core.Configuration;
using Torrentarr.Infrastructure.ApiClients.Arr;
using Xunit;

namespace Torrentarr.Infrastructure.Tests.ApiClients;

/// <summary>
/// Live integration tests for SonarrClient.
/// Gated with [Trait("Category","Live")] — excluded from CI.
/// Connection settings are read from the user's config file at runtime.
/// </summary>
[Trait("Category", "Live")]
public class SonarrClientLiveTests : IAsyncLifetime
{
    private SonarrClient? _client;
    private string? _skipReason;

    public Task InitializeAsync()
    {
        var (uri, key) = LoadLiveSonarrConfig();
        if (uri == null)
        {
            _skipReason = "No live Sonarr config found — skipping live test.";
            return Task.CompletedTask;
        }

        _client = new SonarrClient(uri, key!);
        return Task.CompletedTask;
    }

    public Task DisposeAsync() => Task.CompletedTask;

    private static (string? uri, string? key) LoadLiveSonarrConfig()
    {
        try
        {
            var path = ConfigurationLoader.GetDefaultConfigPath();
            if (!File.Exists(path)) return (null, null);

            var config = new ConfigurationLoader(path).Load();
            var sonarr = config.ArrInstances
                .FirstOrDefault(kvp => kvp.Value.Type == "sonarr" &&
                                        !string.IsNullOrEmpty(kvp.Value.URI) &&
                                        kvp.Value.URI != "CHANGE_ME");
            return sonarr.Value != null
                ? (sonarr.Value.URI, sonarr.Value.APIKey)
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
    public async Task GetSeriesAsync_ReturnsListOrEmpty()
    {
        Skip.If(_skipReason != null, _skipReason!);
        var series = await _client!.GetSeriesAsync();
        series.Should().NotBeNull();
    }
}

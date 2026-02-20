using FluentAssertions;
using Torrentarr.Core.Configuration;
using Torrentarr.Infrastructure.ApiClients.QBittorrent;
using Xunit;

namespace Torrentarr.Infrastructure.Tests.ApiClients;

/// <summary>
/// Live integration tests for QBittorrentClient.
/// These tests require a running qBittorrent instance and are gated by the [Trait("Category","Live")]
/// attribute so they are excluded from CI with:  dotnet test --filter "Category!=Live"
/// Connection settings are read from the user's config file at runtime.
/// </summary>
[Trait("Category", "Live")]
public class QBittorrentClientLiveTests : IAsyncLifetime
{
    private QBittorrentClient? _client;
    private string? _skipReason;

    public async Task InitializeAsync()
    {
        var config = LoadLiveConfig();
        if (config == null)
        {
            _skipReason = "No live qBit config found — skipping live test.";
            return;
        }

        var qbit = config.QBitInstances.GetValueOrDefault("qBit") ?? config.QBitInstances.Values.FirstOrDefault();
        if (qbit == null) { _skipReason = "No qBit instance found in config — skipping live test."; return; }

        _client = new QBittorrentClient(
            qbit.Host,
            qbit.Port,
            qbit.UserName,
            qbit.Password);

        var loggedIn = await _client.LoginAsync();
        if (!loggedIn)
            _skipReason = "Could not connect to qBittorrent — skipping live test.";
    }

    public Task DisposeAsync() => Task.CompletedTask;

    private static TorrentarrConfig? LoadLiveConfig()
    {
        try
        {
            var path = ConfigurationLoader.GetDefaultConfigPath();
            if (!File.Exists(path)) return null;

            var config = new ConfigurationLoader(path).Load();
            var primary = config.QBitInstances.GetValueOrDefault("qBit") ?? config.QBitInstances.Values.FirstOrDefault();
            return primary == null || primary.Host == "CHANGE_ME" ? null : config;
        }
        catch
        {
            return null;
        }
    }

    [SkippableFact]
    public async Task LoginAsync_Succeeds()
    {
        Skip.If(_skipReason != null, _skipReason!);
        _client.Should().NotBeNull();
        await Task.CompletedTask;
    }

    [SkippableFact]
    public async Task GetVersionAsync_ReturnsNonEmptyString()
    {
        Skip.If(_skipReason != null, _skipReason!);
        var version = await _client!.GetVersionAsync();
        version.Should().NotBeNullOrEmpty();
    }

    [SkippableFact]
    public async Task GetTorrentsAsync_ReturnsListWithoutError()
    {
        Skip.If(_skipReason != null, _skipReason!);
        var torrents = await _client!.GetTorrentsAsync();
        torrents.Should().NotBeNull();
    }

    [SkippableFact]
    public async Task GetCategoriesAsync_ReturnsDictionary()
    {
        Skip.If(_skipReason != null, _skipReason!);
        var categories = await _client!.GetCategoriesAsync();
        categories.Should().NotBeNull();
    }

    [SkippableFact]
    public async Task GetTagsAsync_ReturnsList()
    {
        Skip.If(_skipReason != null, _skipReason!);
        var tags = await _client!.GetTagsAsync();
        tags.Should().NotBeNull();
    }
}

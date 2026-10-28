using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Torrentarr.Core.Configuration;
using Torrentarr.Infrastructure.Database;
using Torrentarr.Infrastructure.Database.Models;
using Torrentarr.Infrastructure.Services;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Torrentarr.Infrastructure.ApiClients.Arr;
using Xunit;

namespace Torrentarr.Infrastructure.Tests.ApiClients;

public sealed class SonarrApiDiscoveryTests : IDisposable
{
    private readonly HttpListener _listener = new();
    private readonly Task _loop;
    private readonly ConcurrentQueue<(string Path, string Method, string Body)> _requests = new();
    private readonly string _url;
    private string _version = "v5";
    private int _discoveryStatus = 200;
    private int _statusCode = 200;
    private string? _discoveryJson;
    private string? _statusJson;
    private bool _failWrites;
    private bool _failSearchWrites;
    private string? _profilesJson;
    private bool _specialsSeasonMonitored;
    private bool _enumDeleteContract;
    private int _automaticSearches;
    private (bool Remove, bool Blocklist)? _deletion;
    private string? _queueJson;
    private readonly string _key = Guid.NewGuid().ToString();

    public SonarrApiDiscoveryTests()
    {
        var socket = new TcpListener(IPAddress.Loopback, 0);
        socket.Start();
        var port = ((IPEndPoint)socket.LocalEndpoint).Port;
        socket.Stop();
        _url = $"http://127.0.0.1:{port}/sonarr";
        _listener.Prefixes.Add($"http://127.0.0.1:{port}/");
        _listener.Start();
        _loop = Task.Run(ListenAsync);
    }

    [Theory]
    [InlineData("v3")]
    [InlineData("v5")]
    public async Task Client_RoutesAllOperations_AndPreservesProfileFields(string version)
    {
        _version = version;
        var client = new SonarrClient(_url, _key);
        (await client.GetSystemInfoAsync()).Version.Should().StartWith(version == "v3" ? "4." : "5.");
        await client.GetSeriesAsync();
        await client.GetSeriesAsync(1);
        await client.UpdateSeriesAsync(new SonarrSeries { Id = 1, QualityProfileId = 1 });
        (await client.GetEpisodesAsync(1)).Single().EpisodeFile!.Id.Should().Be(8);
        await client.GetWantedAsync();
        var queue = await client.GetQueueAsync();
        queue.Records.Single().EpisodeIds.Should().Equal(1, 2);
        queue.Records.Single().EpisodeId.Should().BeNull();
        queue.Records.Single().SizeLeft.Should().Be(100);
        await client.SearchSeriesAsync(1);
        (await client.SearchSeasonAsync(1, 2)).Should().BeTrue();
        await client.SearchEpisodeAsync(new List<int> { 1, 2 });
        await client.TriggerDownloadedEpisodesScanAsync("/downloads", "abc");
        await client.GetCommandsAsync();
        await client.GetEpisodeFileAsync(8);
        await client.GetEpisodeFilesAsync(1);
        await client.GetQualityProfilesAsync();
        await client.RssSyncAsync();
        await client.RefreshMonitoredDownloadsAsync();
        await client.RescanAsync();
        (await client.UpdateSeriesQualityProfileAsync(1, 9)).Should().BeTrue();
        var requests = _requests.ToArray();
        var seasonSearch = requests.Single(r => r.Method == "POST" && r.Body.Contains("SeasonSearch"));
        var seasonCommand = JObject.Parse(seasonSearch.Body);
        seasonCommand.Value<int>("seriesId").Should().Be(1);
        seasonCommand.Value<int>("seasonNumber").Should().Be(2);
        requests.Count(r => r.Path == "/sonarr/api").Should().Be(1);
        requests.Where(r => r.Path != "/sonarr/api").Should().OnlyContain(r => r.Path.StartsWith($"/sonarr/api/{version}/"));
        requests.Single(r => r.Path.Contains("/episode?")).Path.Should().Contain(version == "v5"
            ? "includeSubresources=episodeFile" : "includeEpisodeFile=true");
        requests.Single(r => r.Path.Contains("/queue?")).Path.Should().Contain("includeUnknownSeriesItems=true");
        var put = requests.Single(r => r.Method == "PUT" && r.Body.Contains("\"qualityProfileId\":9"));
        put.Body.Should().Contain("\"tags\":[7]").And.Contain("\"qualityProfileId\":9");
    }

    [Theory]
    [InlineData("v3", false, false)]
    [InlineData("v3", false, true)]
    [InlineData("v3", true, false)]
    [InlineData("v3", true, true)]
    [InlineData("v5", false, false)]
    [InlineData("v5", false, true)]
    [InlineData("v5", true, false)]
    [InlineData("v5", true, true)]
    public async Task Delete_MapsFlags_WithoutReplayingFailedMutation(string version, bool remove, bool blocklist)
    {
        _version = version;
        _failWrites = true;
        (await new SonarrClient(_url, _key).DeleteFromQueueAsync(1, remove, blocklist)).Should().BeFalse();
        var deletion = _requests.Single(r => r.Method == "DELETE");
        deletion.Path.Should().Contain(version == "v5"
            ? $"removalMethod={(remove ? "removeFromClient" : "ignore")}&blocklistMethod={(blocklist ? "blocklistAndSearch" : "doNotBlocklist")}"
            : $"removeFromClient={remove.ToString().ToLowerInvariant()}&blocklist={blocklist.ToString().ToLowerInvariant()}");
    }

    [Theory]
    [InlineData(false, false, false)]
    [InlineData(false, false, true)]
    [InlineData(false, true, false)]
    [InlineData(false, true, true)]
    [InlineData(true, false, false)]
    [InlineData(true, false, true)]
    [InlineData(true, true, false)]
    [InlineData(true, true, true)]
    public async Task Delete_PreservesIntentForBothReleasedV5Contracts(bool enums, bool remove, bool blocklist)
    {
        _enumDeleteContract = enums;
        _statusJson = enums ? "{\"version\":\"5.0.0.2608\"}" : "{\"version\":\"5.0.0.2569\"}";
        (await new SonarrClient(_url, _key).DeleteFromQueueAsync(1, remove, blocklist)).Should().BeTrue();
        _deletion.Should().Be((remove, blocklist));
        _automaticSearches.Should().Be(blocklist ? 1 : 0);
        _requests.Count(r => r.Method == "DELETE").Should().Be(1);
    }

    [Fact]
    public async Task SystemStatus_RemainsLiveWhenDiscoveryIsCached()
    {
        var client = new SonarrClient(_url, _key);
        await client.GetSystemInfoAsync();
        _statusCode = 401;
        var act = () => client.GetSystemInfoAsync();
        await act.Should().ThrowAsync<ArrApiException>();
        _requests.Count(r => r.Path == "/sonarr/api").Should().Be(1);
    }

    [Fact]
    public async Task Discovery_Coalesces_Refreshes_AndIsolatesCredentialsAndTls()
    {
        var clock = new TestClock();
        await Task.WhenAll(Enumerable.Range(0, 10).Select(_ => SonarrApiDiscovery.ResolveAsync(_url, _key, false, clock)));
        _requests.Count(r => r.Path.EndsWith("/api")).Should().Be(1);
        _version = "v3";
        clock.Now = clock.Now.AddMinutes(5);
        (await SonarrApiDiscovery.ResolveAsync(_url, _key, false, clock)).ApiVersion.Should().Be("v3");
        await SonarrApiDiscovery.ResolveAsync(_url, _key + "other", false, clock);
        await SonarrApiDiscovery.ResolveAsync(_url, _key, true, clock);
        await SonarrApiDiscovery.ResolveAsync(_url + "/other", _key, false, clock);
        _requests.Count(r => r.Path.EndsWith("/api")).Should().Be(5);
    }

    [Fact]
    public async Task Discovery_FailedRefreshDoesNotUseStaleSelection()
    {
        var clock = new TestClock();
        await SonarrApiDiscovery.ResolveAsync(_url, _key, false, clock);
        clock.Now = clock.Now.AddMinutes(5);
        _discoveryStatus = 401;
        var act = () => SonarrApiDiscovery.ResolveAsync(_url, _key, false, clock);
        await act.Should().ThrowAsync<ArrApiException>();
        _discoveryStatus = 200;
        _version = "v3";
        (await act()).ApiVersion.Should().Be("v3");
    }

    [Theory]
    [InlineData("v3")]
    [InlineData("v5")]
    public async Task Discovery_FallsBackFor404(string version)
    {
        _version = version;
        _discoveryStatus = 404;
        (await SonarrApiDiscovery.ResolveAsync(_url, _key, false)).ApiVersion.Should().Be(version);
        _requests.Should().Contain(r => r.Path.EndsWith("/v3/system/status"));
    }

    [Theory]
    [InlineData("v3", "")]
    [InlineData("v3", " \t\r\n")]
    [InlineData("v5", "")]
    [InlineData("v5", " \t\r\n")]
    public async Task Discovery_EmptySuccessUsesValidatedLegacyFallback(string version, string body)
    {
        _version = version;
        _discoveryJson = body;
        await new SonarrClient(_url, _key).GetSeriesAsync();
        (await SonarrApiDiscovery.ResolveAsync(_url, _key, false)).ApiVersion.Should().Be(version);
        _requests.Should().Contain(r => r.Path == "/sonarr/api/v3/system/status");
        _requests.Should().Contain(r => r.Path == $"/sonarr/api/{version}/series");
        _requests.Count(r => r.Path == "/sonarr/api").Should().Be(1);
    }

    [Theory]
    [InlineData(401, null)]
    [InlineData(403, null)]
    [InlineData(200, "<html>login</html>")]
    [InlineData(200, "{\"current\":\"v6\"}")]
    [InlineData(200, "{}")]
    [InlineData(200, "{\"current\":[]}")]
    [InlineData(200, "{\"current\":{}}")]
    public async Task Discovery_DoesNotFallbackOrCacheFailures(int status, string? json)
    {
        _discoveryStatus = status;
        _discoveryJson = json;
        var act = () => SonarrApiDiscovery.ResolveAsync(_url, _key, false);
        if (status == 200) await act.Should().ThrowAsync<JsonException>();
        else await act.Should().ThrowAsync<ArrApiException>();
        _requests.Should().HaveCount(1);
        _discoveryStatus = 200;
        _discoveryJson = null;
        (await act()).ApiVersion.Should().Be("v5");
    }

    [Theory]
    [InlineData(200, "{}")]
    [InlineData(200, "{\"version\":\"\"}")]
    [InlineData(404, "{\"version\":\"2.0.0\"}")]
    public async Task Discovery_RejectsMissingOrUnsupportedLegacyVersion(int discoveryStatus, string statusJson)
    {
        _discoveryStatus = discoveryStatus;
        _statusJson = statusJson;
        var act = () => SonarrApiDiscovery.ResolveAsync(_url, _key, false);
        await act.Should().ThrowAsync<JsonException>();
    }

    [Fact]
    public async Task Discovery_PropagatesCancellation()
    {
        using var ct = new CancellationTokenSource();
        ct.Cancel();
        var act = () => SonarrApiDiscovery.ResolveAsync(_url, _key, false, ct.Token);
        await act.Should().ThrowAsync<OperationCanceledException>();
        _requests.Should().BeEmpty();
    }

    [Fact]
    public void Queue_NormalizesLegacyScalars_WithoutChoosingAnArbitraryEpisode()
    {
        var item = JsonConvert.DeserializeObject<SonarrQueueItem>("{\"episodeId\":3,\"seasonNumber\":1}")!;
        item.NormalizeEpisodeIds();
        item.EpisodeIds.Should().Equal(3);
        item.SeasonNumbers.Should().Equal(1);
        item.EpisodeId.Should().Be(3);
        item = new SonarrQueueItem();
        item.NormalizeEpisodeIds();
        item.EpisodeId.Should().BeNull();
    }

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public async Task QueueServices_HandleEveryEpisodeWithoutDuplicatingQueueRows(bool forceMinimum, bool expected)
    {
        _queueJson = "{\"records\":[{\"id\":1,\"seriesId\":7,\"downloadId\":\"ABC\",\"episodeIds\":[1,2],\"customFormatScore\":10}]}";
        var cfg = new TorrentarrConfig();
        var arr = new ArrInstanceConfig { Type = "sonarr", URI = _url, APIKey = _key, Category = "tv" };
        arr.ReSearch = true;
        arr.Search.CustomFormatUnmetSearch = true;
        arr.Search.SearchBySeries = "false";
        arr.Search.ForceMinimumCustomFormat = forceMinimum;
        cfg.ArrInstances["Sonarr-test"] = arr;
        await using var db = new TorrentarrDbContext(new DbContextOptionsBuilder<TorrentarrDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        db.Episodes.AddRange(
            new EpisodeFilesModel { ArrId = 1, ArrInstance = "Sonarr-test", ArrSeriesId = 7, SeasonNumber = 1, Monitored = true, EpisodeFileId = 8, CustomFormatScore = 5 },
            new EpisodeFilesModel { ArrId = 2, ArrInstance = "Sonarr-test", ArrSeriesId = 7, SeasonNumber = 1, Monitored = true, EpisodeFileId = 9, CustomFormatScore = 20, MinCustomFormatScore = 5 });
        await db.SaveChangesAsync();
        var service = new ArrImportService(NullLogger<ArrImportService>.Instance, cfg,
            new QBittorrentConnectionManager(NullLogger<QBittorrentConnectionManager>.Instance), db);
        (await service.IsCustomFormatUnmetAsync("abc", "tv")).Should().Be(expected);
        (await service.BlocklistAndReSearchAsync("abc", "tv")).Should().BeTrue();
        _requests.Single(r => r.Method == "POST").Body.Should().Contain("\"episodeIds\":[1,2]");
        var sync = new ArrSyncService(NullLogger<ArrSyncService>.Instance, cfg, db, new DatabaseRestartCoordinator());
        await sync.SyncQueueAsync("Sonarr-test");
        await sync.SyncQueueAsync("Sonarr-test");
        var stored = await db.EpisodeQueue.SingleAsync();
        stored.QueueId.Should().Be(1);
        stored.EpisodeId.Should().BeNull();
    }

    [Theory]
    [InlineData("v3")]
    [InlineData("v5")]
    public async Task UnknownQueueItems_RemainAvailableAndCannotRemoveAnUnverifiedTorrent(string version)
    {
        _version = version;
        _queueJson = """{"records":[{"id":1,"downloadId":"ABC","status":"completed","trackedDownloadStatus":"warning","trackedDownloadState":"importPending","statusMessages":[{"title":"Import","messages":["unmatched download"]}]}]}""";
        var cfg = new TorrentarrConfig();
        var arr = new ArrInstanceConfig { Type = "sonarr", URI = _url, APIKey = _key, Category = "tv" };
        arr.ArrErrorCodesToBlocklist.Add("unmatched download");
        cfg.ArrInstances["Sonarr-test"] = arr;
        await using var db = new TorrentarrDbContext(new DbContextOptionsBuilder<TorrentarrDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        var service = new ArrImportService(NullLogger<ArrImportService>.Instance, cfg,
            new QBittorrentConnectionManager(NullLogger<QBittorrentConnectionManager>.Instance), db);
        (await service.BlocklistAndReSearchAsync("abc", "tv")).Should().BeTrue();
        var sync = new ArrSyncService(NullLogger<ArrSyncService>.Instance, cfg, db, new DatabaseRestartCoordinator());
        await sync.SyncQueueAsync("Sonarr-test");
        var stored = await db.EpisodeQueue.SingleAsync();
        stored.DownloadId.Should().Be("ABC");
        stored.SeriesId.Should().BeNull();
        _requests.Count(r => r.Method == "DELETE").Should().Be(1);
        _deletion.Should().Be((false, true));
    }

    [Theory]
    [InlineData("false", 2, 7, "EpisodeSearch")]
    [InlineData("true", 2, 7, "SeriesSearch")]
    [InlineData("smart", 2, 7, "EpisodeSearch")]
    [InlineData("smart", 1, 7, "EpisodeSearch")]
    [InlineData("false", 0, 7, null)]
    [InlineData("true", 0, 7, "SeriesSearch")]
    [InlineData("true", 2, null, null)]
    [InlineData("smart", 2, null, null)]
    [InlineData("smart", 0, null, null)]
    [InlineData("TRUE", 2, 7, "SeriesSearch")]
    public async Task BlocklistReSearch_RespectsSearchMode(string mode, int episodeCount, int? seriesId, string? command)
    {
        var episodeIds = Enumerable.Range(1, episodeCount).ToList();
        _queueJson = JsonConvert.SerializeObject(new { records = new[] { new { id = 1, downloadId = "ABC", seriesId, episodeIds } } });
        var cfg = new TorrentarrConfig();
        var arr = new ArrInstanceConfig { Type = "sonarr", URI = _url, APIKey = _key, Category = "tv", ReSearch = true };
        arr.Search.SearchBySeries = mode;
        cfg.ArrInstances["Sonarr-test"] = arr;
        await using var db = new TorrentarrDbContext(new DbContextOptionsBuilder<TorrentarrDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        var service = new ArrImportService(NullLogger<ArrImportService>.Instance, cfg,
            new QBittorrentConnectionManager(NullLogger<QBittorrentConnectionManager>.Instance), db);
        (await service.BlocklistAndReSearchAsync("abc", "tv")).Should().BeTrue();
        _automaticSearches.Should().Be(0);
        var searches = _requests.Where(r => r.Method == "POST").ToList();
        if (command == null)
        {
            searches.Should().BeEmpty();
            return;
        }
        var body = JObject.Parse(searches.Single().Body);
        body.Value<string>("name").Should().Be(command);
        if (command == "EpisodeSearch") body["episodeIds"]!.ToObject<List<int>>()!.Should().Equal(episodeIds);
        else body.Value<int?>("seriesId").Should().Be(seriesId);
    }

    [Theory]
    [InlineData("v3", false, false)]
    [InlineData("v3", false, true)]
    [InlineData("v5", false, false)]
    [InlineData("v5", false, true)]
    [InlineData("v5", true, false)]
    [InlineData("v5", true, true)]
    public async Task Blocklist_ReSearchIsTheOnlyTriggerForReplacementSearch(string version, bool enums, bool reSearch)
    {
        _version = version;
        _enumDeleteContract = enums;
        _queueJson = """{"records":[{"id":1,"downloadId":"ABC","seriesId":7,"episodeIds":[1,2]}]}""";
        var cfg = new TorrentarrConfig();
        var arr = new ArrInstanceConfig { Type = "sonarr", URI = _url, APIKey = _key, Category = "tv", ReSearch = reSearch };
        arr.Search.SearchBySeries = "false";
        cfg.ArrInstances["Sonarr-test"] = arr;
        await using var db = new TorrentarrDbContext(new DbContextOptionsBuilder<TorrentarrDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        var service = new ArrImportService(NullLogger<ArrImportService>.Instance, cfg,
            new QBittorrentConnectionManager(NullLogger<QBittorrentConnectionManager>.Instance), db);
        (await service.BlocklistAndReSearchAsync("abc", "tv")).Should().BeTrue();
        _deletion.Should().Be((false, true));
        _automaticSearches.Should().Be(0);
        var commands = _requests.Where(r => r.Method == "POST").ToList();
        commands.Count.Should().Be(reSearch ? 1 : 0);
        if (reSearch)
        {
            var body = JObject.Parse(commands.Single().Body);
            body.Value<string>("name").Should().Be("EpisodeSearch");
            body["episodeIds"]!.ToObject<List<int>>()!.Should().Equal(1, 2);
        }
    }

    [Theory]
    [InlineData("v3", false)]
    [InlineData("v3", true)]
    [InlineData("v5", false)]
    [InlineData("v5", true)]
    public async Task BlocklistSmart_UsesInventoryMajorityAndMarksCoveredNeeds(string version, bool seasonOnly)
    {
        _version = version;
        _queueJson = """{"records":[{"id":1,"downloadId":"ABC","seriesId":7,"episodeIds":[1,2,2]}]}""";
        var cfg = new TorrentarrConfig();
        var arr = new ArrInstanceConfig { Type = "sonarr", URI = _url, APIKey = _key, Category = "tv", ReSearch = true };
        cfg.ArrInstances["Sonarr-test"] = arr;
        await using var db = new TorrentarrDbContext(new DbContextOptionsBuilder<TorrentarrDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        db.Series.Add(new SeriesFilesModel { ArrId = 7, ArrInstance = "Sonarr-test", Monitored = true });
        for (var id = 1; id <= (seasonOnly ? 8 : 3); id++)
            db.Episodes.Add(new EpisodeFilesModel
            {
                ArrId = id,
                ArrInstance = "Sonarr-test",
                ArrSeriesId = 7,
                SeasonNumber = id <= 3 ? 1 : 2,
                Monitored = true,
                HasFile = true,
                QualityMet = true,
                CustomFormatMet = true,
                AirDateUtc = DateTime.UtcNow.AddYears(-id)
            });
        await db.SaveChangesAsync();
        var service = new ArrImportService(NullLogger<ArrImportService>.Instance, cfg,
            new QBittorrentConnectionManager(NullLogger<QBittorrentConnectionManager>.Instance), db);
        (await service.BlocklistAndReSearchAsync("abc", "tv")).Should().BeTrue();
        var command = _requests.Single(r => r.Method == "POST");
        command.Path.Should().Be($"/sonarr/api/{version}/command");
        var body = JObject.Parse(command.Body);
        body.Value<string>("name").Should().Be(seasonOnly ? "SeasonSearch" : "SeriesSearch");
        body.Value<int>("seriesId").Should().Be(7);
        if (seasonOnly) body.Value<int>("seasonNumber").Should().Be(1);
        (await db.Episodes.Where(e => e.Searched).Select(e => e.ArrId).ToListAsync()).Should().BeEquivalentTo(new[] { 1, 2 });
        _automaticSearches.Should().Be(0);
    }

    [Theory]
    [InlineData("v3")]
    [InlineData("v5")]
    public async Task SeasonSearch_FailedSubmissionIsNotReplayed(string version)
    {
        _version = version;
        _failSearchWrites = true;
        (await new SonarrClient(_url, _key).SearchSeasonAsync(7, 2)).Should().BeFalse();
        _requests.Count(r => r.Method == "POST").Should().Be(1);
    }

    [Theory]
    [InlineData("v3")]
    [InlineData("v5")]
    public async Task BroadSearch_SwitchesMissingProfileAcrossYearsBeforeSubmitting(string version)
    {
        _version = version;
        _profilesJson = """[{"id":1,"name":"Main"},{"id":2,"name":"Temp"}]""";
        var cfg = new TorrentarrConfig();
        var arr = new ArrInstanceConfig { Type = "sonarr", URI = _url, APIKey = _key };
        arr.Search.UseTempForMissing = true;
        arr.Search.QualityProfileMappings["Main"] = "Temp";
        arr.Search.QualityUnmetSearch = true;
        cfg.ArrInstances["Sonarr-test"] = arr;
        await using var db = new TorrentarrDbContext(new DbContextOptionsBuilder<TorrentarrDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        var series = new SeriesFilesModel { ArrId = 1, ArrInstance = "Sonarr-test", Monitored = true, QualityProfileId = 1 };
        db.Series.Add(series);
        for (var id = 1; id <= 3; id++)
            db.Episodes.Add(new EpisodeFilesModel
            {
                ArrId = id,
                ArrInstance = "Sonarr-test",
                ArrSeriesId = 1,
                SeasonNumber = 1,
                Monitored = true,
                HasFile = id != 1,
                QualityMet = id != 2,
                CustomFormatMet = true,
                AirDateUtc = DateTime.UtcNow.AddYears(-id)
            });
        await db.SaveChangesAsync();
        var coordinator = new DatabaseRestartCoordinator();
        var executor = new SearchExecutor(NullLogger<SearchExecutor>.Instance, cfg, db,
            new QualityProfileSwitcherService(NullLogger<QualityProfileSwitcherService>.Instance, db, coordinator), coordinator);
        var result = await executor.ExecuteSearchesAsync("Sonarr-test",
            [new Torrentarr.Core.Services.SearchCandidate { ArrId = 2, SeriesId = 1, SeasonNumber = 1, Type = "Episode", Reason = "Quality" }]);
        result.SearchesTriggered.Should().Be(1);
        result.SearchedIds.Should().BeEquivalentTo(new[] { 1, 2 });
        series.OriginalProfileId.Should().Be(1);
        series.CurrentProfileId.Should().Be(2);
        var writes = _requests.Where(r => r.Method is "PUT" or "POST").ToList();
        writes.Should().HaveCount(2);
        writes[0].Method.Should().Be("PUT");
        JObject.Parse(writes[0].Body).Value<int>("qualityProfileId").Should().Be(2);
        JObject.Parse(writes[1].Body).Value<string>("name").Should().Be("SeriesSearch");
    }

    [Theory]
    [InlineData("v3", "smart")]
    [InlineData("v3", "series")]
    [InlineData("v5", "smart")]
    [InlineData("v5", "series")]
    public async Task BlocklistSeriesSearch_CannotImplicitlySearchSpecials(string version, string mode)
    {
        _version = version;
        _specialsSeasonMonitored = true;
        _queueJson = """{"records":[{"id":1,"downloadId":"ABC","seriesId":7,"episodeIds":[1,2]}]}""";
        var cfg = new TorrentarrConfig();
        cfg.Settings.SearchLoopDelay = 1;
        var arr = new ArrInstanceConfig { Type = "sonarr", URI = _url, APIKey = _key, Category = "tv", ReSearch = true };
        arr.Search.SearchBySeries = mode;
        cfg.ArrInstances["Sonarr-test"] = arr;
        await using var db = new TorrentarrDbContext(new DbContextOptionsBuilder<TorrentarrDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        db.Series.Add(new SeriesFilesModel { ArrId = 7, ArrInstance = "Sonarr-test", Monitored = true });
        for (var id = 1; id <= 4; id++)
            db.Episodes.Add(new EpisodeFilesModel
            {
                ArrId = id,
                ArrInstance = "Sonarr-test",
                ArrSeriesId = 7,
                SeasonNumber = id == 4 ? 0 : 1,
                Monitored = id != 4,
                AirDateUtc = DateTime.UtcNow.AddYears(-1)
            });
        await db.SaveChangesAsync();
        var service = new ArrImportService(NullLogger<ArrImportService>.Instance, cfg,
            new QBittorrentConnectionManager(NullLogger<QBittorrentConnectionManager>.Instance), db);
        (await service.BlocklistAndReSearchAsync("abc", "tv")).Should().BeTrue();
        var commands = _requests.Where(r => r.Method == "POST").Select(r => JObject.Parse(r.Body)).ToList();
        commands.Should().HaveCount(mode == "series" ? 2 : 1);
        commands.Should().OnlyContain(c => c.Value<string>("name") == "SeasonSearch" && c.Value<int>("seasonNumber") != 0);
        (await db.Episodes.SingleAsync(e => e.ArrId == 4)).Searched.Should().BeFalse();
        _requests.Count(r => r.Path.EndsWith("/series")).Should().Be(1);
    }

    private async Task ListenAsync()
    {
        while (_listener.IsListening)
        {
            HttpListenerContext ctx;
            try { ctx = await _listener.GetContextAsync(); }
            catch (Exception) { break; }
            using var reader = new StreamReader(ctx.Request.InputStream);
            var body = await reader.ReadToEndAsync();
            var path = ctx.Request.Url!.PathAndQuery;
            _requests.Enqueue((path, ctx.Request.HttpMethod, body));
            var relative = path[(path.LastIndexOf("/api", StringComparison.Ordinal) + 4)..];
            var json = relative == "" ? _discoveryJson ?? $"{{\"current\":\"{_version}\"}}"
                : relative.EndsWith("/system/status") ? _statusJson ?? $"{{\"version\":\"{(_version == "v3" ? 4 : 5)}.0.0\"}}"
                : relative.Contains("/queue?") ? _queueJson ?? "{\"records\":[{\"id\":1,\"episodeIds\":[1,2],\"seasonNumbers\":[1],\"sizeLeft\":100}]}"
                : relative.Contains("/wanted/") ? "{\"records\":[]}"
                : relative.Contains("/episode?") && ctx.Request.QueryString["seriesId"] == "7" ? "[{\"id\":1,\"seriesId\":7,\"seasonNumber\":1,\"monitored\":true,\"airDateUtc\":\"2020-01-01T00:00:00Z\"},{\"id\":2,\"seriesId\":7,\"seasonNumber\":1,\"monitored\":true,\"airDateUtc\":\"2020-01-01T00:00:00Z\"}]"
                : relative.Contains("/episode?") ? "[{\"id\":1,\"episodeFile\":{\"id\":8}}]"
                : relative.EndsWith("/series/1") || relative.EndsWith("/series/7") ? JsonConvert.SerializeObject(new
                {
                    id = relative.EndsWith("/series/1") ? 1 : 7,
                    qualityProfileId = 1,
                    tags = new[] { 7 },
                    seasons = new[] { new { seasonNumber = 0, monitored = _specialsSeasonMonitored }, new { seasonNumber = 1, monitored = true }, new { seasonNumber = 2, monitored = true } }
                })
                : relative.EndsWith("/qualityprofile") && _profilesJson != null ? _profilesJson
                : relative.EndsWith("/series") ? JsonConvert.SerializeObject(new[] { 1, 7 }.Select(id => new
                {
                    id,
                    qualityProfileId = 1,
                    seasons = new[] { new { seasonNumber = 0, monitored = _specialsSeasonMonitored }, new { seasonNumber = 1, monitored = true }, new { seasonNumber = 2, monitored = true } }
                }))
                : relative.Contains("/episodefile?") || relative.EndsWith("/qualityprofile") || relative.EndsWith("/command") && ctx.Request.HttpMethod == "GET" ? "[]" : "{}";
            if (relative.Contains("/queue?") && ctx.Request.QueryString["includeUnknownSeriesItems"] == "false")
            {
                var queue = JObject.Parse(json);
                queue["records"] = new JArray(((JArray)queue["records"]!).Where(r => r["seriesId"]?.Type == JTokenType.Integer));
                json = queue.ToString(Formatting.None);
            }
            ctx.Response.StatusCode = relative == "" ? _discoveryStatus : relative.EndsWith("/system/status") ? _statusCode : (_failWrites && ctx.Request.HttpMethod == "DELETE" || _failSearchWrites && ctx.Request.HttpMethod == "POST") ? 400 : 200;
            if (ctx.Request.HttpMethod == "DELETE" && !_failWrites)
            {
                var query = ctx.Request.QueryString;
                if (_enumDeleteContract)
                {
                    if (query["removalMethod"] == null || query["blocklistMethod"] == null)
                        ctx.Response.StatusCode = 400;
                    else
                    {
                        _deletion = (query["removalMethod"] == "removeFromClient", query["blocklistMethod"] != "doNotBlocklist");
                        if (query["blocklistMethod"] == "blocklistAndSearch") _automaticSearches++;
                    }
                }
                else
                {
                    _deletion = (query["removeFromClient"] != "false", query["blocklist"] == "true");
                    if (query["blocklist"] == "true" && query["skipRedownload"] != "true") _automaticSearches++;
                }
            }
            var bytes = Encoding.UTF8.GetBytes(json);
            ctx.Response.ContentType = "application/json";
            ctx.Response.ContentLength64 = bytes.Length;
            await ctx.Response.OutputStream.WriteAsync(bytes);
            ctx.Response.Close();
        }
    }

    public void Dispose()
    {
        _listener.Close();
        _loop.Wait(TimeSpan.FromSeconds(2));
    }

    private sealed class TestClock : TimeProvider
    {
        internal DateTimeOffset Now = DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow() => Now;
    }
}

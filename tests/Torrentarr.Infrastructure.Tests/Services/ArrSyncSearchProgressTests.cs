using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Torrentarr.Core.Configuration;
using Torrentarr.Infrastructure.Database;
using Torrentarr.Infrastructure.Services;
using Xunit;

namespace Torrentarr.Infrastructure.Tests.Services;

public class ArrSyncSearchProgressTests
{
    [Theory]
    [InlineData("radarr", "moviesfilesmodel")]
    [InlineData("sonarr", "episodefilesmodel")]
    [InlineData("lidarr", "albumfilesmodel")]
    [InlineData("readarr", "bookfilesmodel")]
    public async Task SuccessfulSync_PreservesProgress_AndResetsChangedEligibility(string type, string table)
    {
        using var api = new ArrSyncStub();
        var config = new TorrentarrConfig();
        config.ArrInstances["Test"] = new ArrInstanceConfig { Type = type, URI = api.Url, Category = "test", APIKey = "test" };
        await using var db = new TorrentarrDbContext(new DbContextOptionsBuilder<TorrentarrDbContext>().UseSqlite("Data Source=:memory:").Options);
        await db.Database.OpenConnectionAsync();
        await db.Database.EnsureCreatedAsync();
        var sync = new ArrSyncService(NullLogger<ArrSyncService>.Instance, config, db, new DatabaseRestartCoordinator());
        await sync.SyncAsync("Test");
        if (type == "lidarr")
        {
            var track = await db.Tracks.SingleAsync();
            track.Title.Should().Be("Track");
            track.Monitored.Should().BeTrue();
            track.Duration.Should().Be(180);
        }
        var profileSql = "SELECT QualityProfileName AS Value FROM " + table;
        (await db.Database.SqlQueryRaw<string>(profileSql).SingleAsync()).Should().Be("Default");
        var updateSql = "UPDATE " + table + " SET searched = 1, upgrade = 1";
        var flagsSql = "SELECT searched + upgrade AS Value FROM " + table;
        (await db.Database.ExecuteSqlRawAsync(updateSql)).Should().Be(1);
        db.ChangeTracker.Clear();
        await sync.SyncAsync("Test");
        var flags = await db.Database.SqlQueryRaw<int>(flagsSql).SingleAsync();
        flags.Should().Be(2);
        api.Monitored = false;
        db.ChangeTracker.Clear();
        await sync.SyncAsync("Test");
        (await db.Database.SqlQueryRaw<int>(flagsSql).SingleAsync()).Should().Be(0);
        if (type == "lidarr")
            (await db.Tracks.SingleAsync()).Monitored.Should().BeFalse();
        api.Monitored = true;
        db.ChangeTracker.Clear();
        await sync.SyncAsync("Test");
        (await db.Database.SqlQueryRaw<int>(flagsSql).SingleAsync()).Should().Be(0);
    }

    [Theory]
    [InlineData("1", "integer")]
    [InlineData("A", "text")]
    public async Task LidarrTrackRequestFailure_PreservesExistingTracks(string trackNumber, string storedType)
    {
        using var api = new ArrSyncStub { TrackNumber = trackNumber };
        var config = new TorrentarrConfig();
        config.ArrInstances["Test"] = new ArrInstanceConfig { Type = "lidarr", URI = api.Url, Category = "test", APIKey = "test" };
        await using var db = new TorrentarrDbContext(new DbContextOptionsBuilder<TorrentarrDbContext>().UseSqlite("Data Source=:memory:").Options);
        await db.Database.OpenConnectionAsync();
        await db.Database.EnsureCreatedAsync();
        var sync = new ArrSyncService(NullLogger<ArrSyncService>.Instance, config, db, new DatabaseRestartCoordinator());
        await sync.SyncAsync("Test");
        db.ChangeTracker.Clear();
        (await db.Tracks.SingleAsync()).TrackNumber.Should().Be(trackNumber);
        (await db.Database.SqlQueryRaw<string>("SELECT typeof(tracknumber) AS Value FROM trackfilesmodel").SingleAsync()).Should().Be(storedType);
        (await db.Database.SqlQueryRaw<string>("SELECT type AS Value FROM pragma_table_info('trackfilesmodel') WHERE name='tracknumber'").SingleAsync()).Should().Be("INTEGER");
        api.FailTracks = true;
        db.ChangeTracker.Clear();
        await FluentActions.Awaiting(() => sync.SyncAsync("Test")).Should().ThrowAsync<Exception>();
        (await db.Tracks.SingleAsync()).Title.Should().Be("Track");
    }
}

internal sealed class ArrSyncStub : IDisposable
{
    private readonly HttpListener _listener = new();
    private readonly Task _serve;
    public string Url { get; }
    public bool Monitored { get; set; } = true;
    public bool FailTracks { get; set; }
    public string TrackNumber { get; set; } = "1";
    public ArrSyncStub()
    {
        using var port = new TcpListener(IPAddress.Loopback, 0);
        port.Start();
        Url = $"http://127.0.0.1:{((IPEndPoint)port.LocalEndpoint).Port}";
        port.Stop();
        _listener.Prefixes.Add(Url + "/");
        _listener.Start();
        _serve = ServeAsync();
    }
    private async Task ServeAsync()
    {
        while (_listener.IsListening)
        {
            HttpListenerContext request;
            try { request = await _listener.GetContextAsync(); }
            catch (HttpListenerException) { break; }
            catch (ObjectDisposedException) { break; }
            var resource = request.Request.Url!.AbsolutePath.Split('/').Last();
            if (resource == "track" && (FailTracks || request.Request.QueryString["artistId"] != "1"))
            {
                request.Response.StatusCode = FailTracks ? 500 : 400;
                request.Response.Close();
                continue;
            }
            object data = resource switch
            {
                "api" => new { current = "v3" },
                "status" => new { version = "4.0.0" },
                "qualityprofile" => new[] { new { id = 1, name = "Default" } },
                "movie" => new[] { new { id = 1, tmdbId = 1, title = "Movie", monitored = Monitored, year = 2000, qualityProfileId = 1, hasFile = false } },
                "series" => new[] { new { id = 1, tvdbId = 1, title = "Series", monitored = Monitored, qualityProfileId = 1 } },
                "episode" => new[] { new { id = 1, seriesId = 1, title = "Episode", monitored = Monitored, seasonNumber = 1, episodeNumber = 1, airDateUtc = "2000-01-01T00:00:00Z", hasFile = false } },
                "artist" => new[] { new { id = 1, foreignArtistId = "artist", artistName = "Artist", monitored = Monitored, qualityProfileId = 1 } },
                "album" => new[] { new { id = 1, foreignAlbumId = "album", title = "Album", artistId = 1, monitored = Monitored, qualityProfileId = 1, releaseDate = "2000-01-01T00:00:00Z" } },
                "track" => new[] { new { id = 1, albumId = 1, title = "Track", trackNumber = TrackNumber, hasFile = true, duration = 180000 } },
                "author" => new[] { new { id = 1, foreignAuthorId = "author", authorName = "Author", monitored = Monitored, qualityProfileId = 1 } },
                "book" => new[] { new { id = 1, foreignBookId = "book", title = "Book", authorId = 1, monitored = Monitored, qualityProfileId = 1, releaseDate = "2000-01-01T00:00:00Z" } },
                _ => Array.Empty<object>()
            };
            var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(data));
            request.Response.ContentType = "application/json";
            request.Response.ContentLength64 = bytes.Length;
            await request.Response.OutputStream.WriteAsync(bytes);
            request.Response.Close();
        }
    }
    public void Dispose() { _listener.Close(); _serve.GetAwaiter().GetResult(); }
}

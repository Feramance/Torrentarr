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
        api.Monitored = true;
        db.ChangeTracker.Clear();
        await sync.SyncAsync("Test");
        (await db.Database.SqlQueryRaw<int>(flagsSql).SingleAsync()).Should().Be(0);
    }
}

internal sealed class ArrSyncStub : IDisposable
{
    private readonly HttpListener _listener = new();
    private readonly Task _serve;
    public string Url { get; }
    public bool Monitored { get; set; } = true;
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
            object data = resource switch
            {
                "movie" => new[] { new { id = 1, tmdbId = 1, title = "Movie", monitored = Monitored, year = 2000, qualityProfileId = 1, hasFile = false } },
                "series" => new[] { new { id = 1, tvdbId = 1, title = "Series", monitored = Monitored, qualityProfileId = 1 } },
                "episode" => new[] { new { id = 1, seriesId = 1, title = "Episode", monitored = Monitored, seasonNumber = 1, episodeNumber = 1, airDateUtc = "2000-01-01T00:00:00Z", hasFile = false } },
                "artist" => new[] { new { id = 1, foreignArtistId = "artist", artistName = "Artist", monitored = Monitored, qualityProfileId = 1 } },
                "album" => new[] { new { id = 1, foreignAlbumId = "album", title = "Album", artistId = 1, monitored = Monitored, qualityProfileId = 1, releaseDate = "2000-01-01T00:00:00Z" } },
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

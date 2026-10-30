using System.Text.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Torrentarr.Core.Configuration;
using Torrentarr.Core.Interfaces;
using Torrentarr.Core.Models;
using Torrentarr.Core.Services;
using Torrentarr.Infrastructure.Database;
using Torrentarr.Infrastructure.Services;
using Xunit;

namespace Torrentarr.Infrastructure.Tests.Services;

public class EqualTorrentClientTests
{
    private static Mock<ITorrentClient> Client(string id, params TorrentInfo[] torrents)
    {
        var client = new Mock<ITorrentClient>();
        client.SetupGet(c => c.Identity).Returns(new TorrentClientIdentity(id, "fake"));
        client.SetupGet(c => c.Capabilities).Returns(TorrentClientCapabilities.QBittorrent);
        client.Setup(c => c.GetVersionAsync(It.IsAny<CancellationToken>())).ReturnsAsync(id + "-version");
        client.Setup(c => c.GetTagsAsync(It.IsAny<CancellationToken>())).ReturnsAsync(["qBitrr-allowed_seeding"]);
        client.Setup(c => c.GetTorrentTrackersAsync(It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync([]);
        client.Setup(c => c.AddTagsAsync(It.IsAny<List<string>>(), It.IsAny<List<string>>(), It.IsAny<CancellationToken>())).ReturnsAsync(true);
        client.Setup(c => c.RemoveTagsAsync(It.IsAny<List<string>>(), It.IsAny<List<string>>(), It.IsAny<CancellationToken>())).ReturnsAsync(true);
        client.Setup(c => c.AddTrackersAsync(It.IsAny<string>(), It.IsAny<List<string>>(), It.IsAny<CancellationToken>())).ReturnsAsync(true);
        client.Setup(c => c.SetSuperSeedingAsync(It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<CancellationToken>())).ReturnsAsync(true);
        client.Setup(c => c.GetTorrentsAsync(It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>())).ReturnsAsync(torrents.ToList());
        return client;
    }

    private static Mock<ITorrentClientRegistry> Registry(params (string Id, Mock<ITorrentClient> Client)[] clients)
    {
        var registry = new Mock<ITorrentClientRegistry>();
        var map = clients.ToDictionary(c => c.Id, c => c.Client.Object, StringComparer.OrdinalIgnoreCase);
        registry.Setup(r => r.GetAllClients()).Returns(map);
        registry.Setup(r => r.GetClient(It.IsAny<string>())).Returns((string id) => map.GetValueOrDefault(id));
        return registry;
    }

    [Theory]
    [InlineData("inventory")]
    [InlineData("tag-exception")]
    [InlineData("tag-rejected")]
    [InlineData("tracker")]
    [InlineData("creation-exception")]
    [InlineData("creation-rejected")]
    [InlineData("tracker-tag-rejected")]
    [InlineData("tracker-add-rejected")]
    [InlineData("super-seed-rejected")]
    public async Task SeedingMaintenanceCollectsFailuresWithoutSkippingIndependentWork(string failure)
    {
        var bad = new TorrentInfo { Hash = "bad", Category = "movies", Progress = 1 };
        var after = new TorrentInfo { Hash = "after", Category = "movies", Progress = 1 };
        var first = Client("one", bad, after);
        var healthy = Client("two", new TorrentInfo { Hash = "healthy", Category = "movies", Progress = 1 });
        if (failure == "inventory") first.Setup(c => c.GetTorrentsAsync(It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>())).ThrowsAsync(new IOException());
        if (failure == "tag-exception") first.Setup(c => c.AddTagsAsync(It.Is<List<string>>(h => h.Contains("bad")), It.IsAny<List<string>>(), It.IsAny<CancellationToken>())).ThrowsAsync(new IOException());
        if (failure == "tag-rejected") first.Setup(c => c.AddTagsAsync(It.Is<List<string>>(h => h.Contains("bad")), It.IsAny<List<string>>(), It.IsAny<CancellationToken>())).ReturnsAsync(false);
        if (failure == "tracker") first.Setup(c => c.GetTorrentTrackersAsync("bad", It.IsAny<CancellationToken>())).ThrowsAsync(new IOException());
        if (failure.StartsWith("creation"))
        {
            first.Setup(c => c.GetTagsAsync(It.IsAny<CancellationToken>())).ReturnsAsync([]);
            if (failure == "creation-exception") first.Setup(c => c.CreateTagsAsync(It.IsAny<List<string>>(), It.IsAny<CancellationToken>())).ThrowsAsync(new IOException());
            else first.Setup(c => c.CreateTagsAsync(It.IsAny<List<string>>(), It.IsAny<CancellationToken>())).ReturnsAsync(false);
        }
        if (failure is "tracker-tag-rejected" or "super-seed-rejected")
            first.Setup(c => c.GetTorrentTrackersAsync("bad", It.IsAny<CancellationToken>())).ReturnsAsync([new() { Url = "https://tracker.example/announce" }]);
        if (failure == "tracker-tag-rejected") first.Setup(c => c.AddTagsAsync(It.Is<List<string>>(h => h.Contains("bad")), It.Is<List<string>>(t => t.Contains("tracker-tag")), It.IsAny<CancellationToken>())).ReturnsAsync(false);
        if (failure == "tracker-add-rejected") first.Setup(c => c.AddTrackersAsync("bad", It.IsAny<List<string>>(), It.IsAny<CancellationToken>())).ReturnsAsync(false);
        if (failure == "super-seed-rejected") first.Setup(c => c.SetSuperSeedingAsync("bad", true, It.IsAny<CancellationToken>())).ReturnsAsync(false);
        var registry = Registry(("one", first), ("two", healthy));
        var config = new TorrentarrConfig();
        config.TorrentClients["one"] = new() { Host = "fixture", Trackers = [new() { Uri = "https://tracker.example/announce", AddTags = ["tracker-tag"], AddTrackerIfMissing = failure == "tracker-add-rejected", SuperSeedMode = failure == "super-seed-rejected" ? true : null }] };
        config.TorrentClients["two"] = new() { Host = "fixture" };
        using var db = new TorrentarrDbContext(new DbContextOptionsBuilder<TorrentarrDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        var seeding = new SeedingService(NullLogger<SeedingService>.Instance, db, config, registry.Object);
        await FluentActions.Awaiting(() => seeding.UpdateSeedingTagsAsync("movies")).Should().ThrowAsync<AggregateException>();
        healthy.Verify(c => c.AddTagsAsync(It.Is<List<string>>(h => h.Contains("healthy")), It.IsAny<List<string>>(), It.IsAny<CancellationToken>()), Times.Once);
        if (failure != "inventory") first.Verify(c => c.AddTagsAsync(It.Is<List<string>>(h => h.Contains("after")), It.IsAny<List<string>>(), It.IsAny<CancellationToken>()), Times.Once);
        first.Setup(c => c.GetTorrentsAsync(It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>())).ReturnsAsync([bad, after]);
        first.Setup(c => c.AddTagsAsync(It.Is<List<string>>(h => h.Contains("bad")), It.IsAny<List<string>>(), It.IsAny<CancellationToken>())).ReturnsAsync(true);
        first.Setup(c => c.GetTorrentTrackersAsync("bad", It.IsAny<CancellationToken>())).ReturnsAsync([]);
        first.Setup(c => c.CreateTagsAsync(It.IsAny<List<string>>(), It.IsAny<CancellationToken>())).ReturnsAsync(true);
        first.Setup(c => c.AddTrackersAsync("bad", It.IsAny<List<string>>(), It.IsAny<CancellationToken>())).ReturnsAsync(true);
        first.Setup(c => c.SetSuperSeedingAsync("bad", true, It.IsAny<CancellationToken>())).ReturnsAsync(true);
        // The repaired observation has no matching tracker, so the rejected optional tracker-tag write is no longer required.
        await seeding.UpdateSeedingTagsAsync("movies");
        first.Setup(c => c.GetTorrentsAsync(It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>())).ThrowsAsync(new OperationCanceledException());
        await FluentActions.Awaiting(() => seeding.UpdateSeedingTagsAsync("movies")).Should().ThrowAsync<OperationCanceledException>();
    }

    [Theory]
    [InlineData("qBit", false)]
    [InlineData("renamed", true)]
    public async Task StatusAndCategories_DoNotDependOnNameOrOrder(string id, bool reverse)
    {
        var config = new TorrentarrConfig();
        config.ArrInstances["Radarr"] = new() { Category = "movies" };
        var entries = new[] { id, "other" };
        if (reverse) Array.Reverse(entries);
        foreach (var name in entries)
            config.TorrentClients[name] = new()
            {
                Host = name,
                Port = name == id ? 8080 : 9090,
                ManagedCategories = ["movies", "seed"],
                MatchSubcategories = name == id,
                CategorySeeding = new() { MaxUploadRatio = name == id ? 2 : 3 }
            };
        config.TorrentClients["disabled"] = new() { Host = "disabled", Disabled = true };
        config.TorrentClients["placeholder"] = new() { Host = "CHANGE_ME" };
        var first = Client(id, new TorrentInfo { Hash = "abc", Category = "movies/4k", Ratio = 4, State = "uploading", Size = 10 });
        var second = Client("other", new TorrentInfo { Hash = "abc", Category = "movies", Ratio = 5, State = "uploading", Size = 20 });
        var registry = Registry((id, first), ("other", second));
        var status = JsonSerializer.SerializeToElement(await TorrentClientApiBuilder.StatusAsync(config, registry.Object));
        status.GetProperty(id).GetProperty("version").GetString().Should().Be(id + "-version");
        status.GetProperty("other").GetProperty("port").GetInt32().Should().Be(9090);
        status.GetProperty("disabled").GetProperty("alive").GetBoolean().Should().BeFalse();
        status.GetProperty("placeholder").GetProperty("alive").GetBoolean().Should().BeFalse();
        var rows = JsonSerializer.SerializeToElement(await TorrentClientApiBuilder.CategoriesAsync(config, registry.Object));
        rows.GetArrayLength().Should().Be(4);
        foreach (var row in rows.EnumerateArray().Where(r => r.GetProperty("category").GetString() == "movies"))
        {
            row.GetProperty("managedBy").GetString().Should().Be("arr");
            row.GetProperty("torrentCount").GetInt32().Should().Be(1);
            row.GetProperty("seedingConfig").GetProperty("MaxRatio").GetDouble()
                .Should().Be(row.GetProperty("instance").GetString() == id ? 2 : 3);
        }
    }

    [Fact]
    public async Task FailedClient_DoesNotBlockConnectivityOrProcessingOrCleanup()
    {
        var failed = Client("failed");
        failed.Setup(c => c.GetVersionAsync(It.IsAny<CancellationToken>())).ThrowsAsync(new HttpRequestException());
        failed.Setup(c => c.GetTorrentsAsync(It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>())).ThrowsAsync(new HttpRequestException());
        var healthy = Client("healthy", new TorrentInfo { Hash = "abc", Category = "movies", State = "uploading", Progress = 1 });
        var registry = Registry(("failed", failed), ("healthy", healthy));
        var config = new TorrentarrConfig();
        config.Settings.Tagless = true;
        config.TorrentClients["healthy"] = new() { Host = "fixture", ManagedCategories = ["movies"] };
        var connectivity = new ConnectivityService(NullLogger<ConnectivityService>.Instance, registry.Object, config);
        (await connectivity.IsQBittorrentReachableAsync()).Should().BeTrue();
        using var db = new TorrentarrDbContext(new DbContextOptionsBuilder<TorrentarrDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        var seeding = new SeedingService(NullLogger<SeedingService>.Instance, db, config, registry.Object);
        (await seeding.RemoveCompletedTorrentsAsync("movies")).TorrentsChecked.Should().Be(1);
        var processorSeeding = new Mock<ISeedingService>();
        processorSeeding.Setup(s => s.UpdateSeedingTagsAsync("movies", It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
        processorSeeding.Setup(s => s.RemoveCompletedTorrentsAsync("movies", It.IsAny<CancellationToken>())).ReturnsAsync(new RemovalResult());
        // Empty healthy inventory isolates the collection failure boundary from lifecycle processing.
        healthy.Setup(c => c.GetTorrentsAsync(It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>())).ReturnsAsync([]);
        var processor = new TorrentProcessor(NullLogger<TorrentProcessor>.Instance, registry.Object, db, config,
            new TorrentCacheService(NullLogger<TorrentCacheService>.Instance), new DatabaseRestartCoordinator(), seedingService: processorSeeding.Object);
        await processor.ProcessTorrentsAsync("movies");
        processorSeeding.Verify(s => s.UpdateSeedingTagsAsync("movies", It.IsAny<CancellationToken>()), Times.Once);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task SeedingTags_UseOwningClientWhenOtherClientFailsOrHasSameHash(bool offline)
    {
        var first = Client("one", new TorrentInfo { Hash = "abc", Category = "movies", Progress = 1 });
        var second = Client("two", new TorrentInfo { Hash = "abc", Category = "movies", Progress = 1 });
        if (offline)
            second.Setup(c => c.GetTorrentsAsync(It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
                .ThrowsAsync(new HttpRequestException());
        var registry = Registry(("two", second), ("one", first));
        using var db = new TorrentarrDbContext(new DbContextOptionsBuilder<TorrentarrDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        var config = new TorrentarrConfig();
        config.TorrentClients["one"] = new() { Host = "fixture" };
        config.TorrentClients["two"] = new() { Host = "fixture" };
        var seeding = new SeedingService(NullLogger<SeedingService>.Instance, db, config, registry.Object);
        if (offline) await FluentActions.Awaiting(() => seeding.UpdateSeedingTagsAsync("movies")).Should().ThrowAsync<AggregateException>();
        else await seeding.UpdateSeedingTagsAsync("movies");
        first.Verify(c => c.AddTagsAsync(It.Is<List<string>>(h => h.Contains("abc")),
            It.Is<List<string>>(tags => tags.Contains("qBitrr-allowed_seeding")), It.IsAny<CancellationToken>()), Times.Once);
        if (!offline)
            second.Verify(c => c.AddTagsAsync(It.Is<List<string>>(h => h.Contains("abc")),
                It.Is<List<string>>(tags => tags.Contains("qBitrr-allowed_seeding")), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task SeedingTags_ExcludeOwningClientsDatabasePausedTorrent(bool tagless)
    {
        var client = Client("named");
        client.SetupGet(c => c.Capabilities).Returns(TorrentClientCapabilities.QBittorrent with { Tags = tagless });
        client.Setup(c => c.GetTorrentsAsync(It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => [new TorrentInfo { Hash = "abc", Category = "movies", Progress = 1 }]);
        client.Setup(c => c.GetTorrentTrackersAsync(It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync([]);
        var registry = Registry(("named", client));
        var config = new TorrentarrConfig();
        config.Settings.Tagless = tagless;
        config.TorrentClients["named"] = new() { Host = "fixture" };
        using var db = new TorrentarrDbContext(new DbContextOptionsBuilder<TorrentarrDbContext>().UseSqlite("Data Source=:memory:").Options);
        await db.Database.OpenConnectionAsync();
        await db.Database.EnsureCreatedAsync();
        db.TorrentLibrary.Add(new() { Hash = "abc", Category = "movies", QbitInstance = "named", FreeSpacePaused = true });
        await db.SaveChangesAsync();
        var seeding = new SeedingService(NullLogger<SeedingService>.Instance, db, config, registry.Object);

        await seeding.UpdateSeedingTagsAsync("movies");

        (await db.TorrentLibrary.AsNoTracking().SingleAsync()).AllowedSeeding.Should().BeFalse();
        client.Verify(c => c.GetTorrentsAsync(It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task NamedLookup_OnlyReadsNamedClient_AndHashOnlyLookupRejectsAmbiguity()
    {
        var first = Client("one", new TorrentInfo { Hash = "abc" });
        var second = Client("two", new TorrentInfo { Hash = "ABC" });
        var registry = Registry(("one", first), ("two", second));
        var found = await TorrentClientLookup.FindAsync(registry.Object, "abc", "two", NullLogger.Instance, default);
        found!.ClientInstanceId.Should().Be("two");
        first.Verify(c => c.GetTorrentsAsync(It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Never);
        await FluentActions.Awaiting(() => TorrentClientLookup.FindAsync(registry.Object, "abc", null, NullLogger.Instance, default))
            .Should().ThrowAsync<InvalidOperationException>().WithMessage("*specify an instance*");
    }

    [Fact]
    public async Task IncompleteHashLookup_FailsSafely_ButNamedLookupAndCancellationRemainIndependent()
    {
        var first = Client("one");
        var second = Client("two", new TorrentInfo { Hash = "abc" });
        var registry = Registry(("one", first), ("two", second));
        first.Setup(c => c.GetTorrentsAsync(It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>())).ThrowsAsync(new HttpRequestException());
        (await TorrentClientLookup.FindAsync(registry.Object, "abc", "two", NullLogger.Instance, default))!.ClientInstanceId.Should().Be("two");
        await FluentActions.Awaiting(() => TorrentClientLookup.FindAsync(registry.Object, "abc", null, NullLogger.Instance, default))
            .Should().ThrowAsync<HttpRequestException>().WithMessage("*incomplete*");
        using var db = new TorrentarrDbContext(new DbContextOptionsBuilder<TorrentarrDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        var seeding = new SeedingService(NullLogger<SeedingService>.Instance, db, new TorrentarrConfig(), registry.Object);
        (await seeding.IsHitAndRunProtectedAsync("abc")).Should().BeTrue();
        first.Setup(c => c.GetTorrentsAsync(It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>())).ThrowsAsync(new OperationCanceledException());
        await FluentActions.Awaiting(() => TorrentClientLookup.FindAsync(registry.Object, "abc", null, NullLogger.Instance, default)).Should().ThrowAsync<OperationCanceledException>();
    }
}

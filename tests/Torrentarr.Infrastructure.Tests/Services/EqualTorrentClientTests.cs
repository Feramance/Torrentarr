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

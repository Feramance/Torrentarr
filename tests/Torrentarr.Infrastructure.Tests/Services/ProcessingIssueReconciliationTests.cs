using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Torrentarr.Core.Configuration;
using Torrentarr.Core.Interfaces;
using Torrentarr.Core.Models;
using Torrentarr.Infrastructure.Database;
using Torrentarr.Infrastructure.Database.Models;
using Torrentarr.Infrastructure.Services;
using Xunit;

namespace Torrentarr.Infrastructure.Tests.Services;

public sealed class ProcessingIssueReconciliationTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CompleteInventoryClosesRemovedTorrentIssues_AndFailedOrFilteredObservationsNeverDo(bool failsOnlyOnFullRead)
    {
        await using var connection = new SqliteConnection("Data Source=:memory:"); await connection.OpenAsync();
        await using var services = new ServiceCollection().AddDbContext<TorrentarrDbContext>(o => o.UseSqlite(connection)).BuildServiceProvider();
        using var scope = services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TorrentarrDbContext>(); await db.Database.EnsureCreatedAsync();
        foreach (var (client, hash, code, observed) in new[]
        {
            ("healthy", "gone", "torrent.processing", DateTime.UtcNow.AddMinutes(-1)),
            ("healthy", "moved", "torrent.processing", DateTime.UtcNow.AddMinutes(-1)),
            ("healthy", "newer-observation", "torrent.processing", DateTime.UtcNow.AddDays(1)),
            ("offline", "gone", "torrent.processing", DateTime.UtcNow.AddMinutes(-1)),
            ("unobserved", "gone", "torrent.processing", DateTime.UtcNow.AddMinutes(-1)),
            ("healthy", "gone", "import.scan", DateTime.UtcNow.AddMinutes(-1))
        })
            db.OperationalIssues.Add(new()
            {
                Code = code,
                Category = "automation",
                ClientInstance = client,
                ArrInstance = "Arr",
                SubjectType = "torrent",
                SubjectId = hash,
                LastObserved = observed,
                FirstObserved = observed
            });
        await db.SaveChangesAsync(); db.ChangeTracker.Clear();
        var cfg = new TorrentarrConfig(); cfg.Settings.Tagless = true;
        cfg.QBitInstances.Clear(); cfg.QBitInstances["healthy"] = new(); cfg.QBitInstances["offline"] = new();
        cfg.ArrInstances["Arr"] = new() { Type = "radarr", Category = "media" };
        var healthy = new Mock<ITorrentClient>();
        healthy.Setup(c => c.GetTorrentsAsync("media", It.IsAny<string?>(), It.IsAny<CancellationToken>())).ReturnsAsync([]);
        healthy.Setup(c => c.GetTorrentsAsync(null, It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([new TorrentInfo { Hash = "MOVED", Category = "other-owner" }]);
        var offline = new Mock<ITorrentClient>();
        offline.Setup(c => c.GetTorrentsAsync(It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>())).ThrowsAsync(new IOException());
        if (failsOnlyOnFullRead) offline.Setup(c => c.GetTorrentsAsync("media", It.IsAny<string?>(), It.IsAny<CancellationToken>())).ReturnsAsync([]);
        var clients = new Dictionary<string, ITorrentClient>(StringComparer.OrdinalIgnoreCase) { ["healthy"] = healthy.Object, ["offline"] = offline.Object };
        var registry = new Mock<ITorrentClientRegistry>(); registry.Setup(r => r.GetAllClients()).Returns(clients);
        registry.Setup(r => r.GetClient(It.IsAny<string>())).Returns((string id) => clients.GetValueOrDefault(id));
        using var reporter = new OperationalIssueReporter(services.GetRequiredService<IServiceScopeFactory>(), NullLogger<OperationalIssueReporter>.Instance, cfg);
        var processor = new TorrentProcessor(NullLogger<TorrentProcessor>.Instance, registry.Object, db, cfg,
            new TorrentCacheService(NullLogger<TorrentCacheService>.Instance), new(), issues: reporter);
        await FluentActions.Awaiting(() => processor.ProcessTorrentsAsync("media")).Should().ThrowAsync<AggregateException>();
        var rows = await db.OperationalIssues.AsNoTracking().Where(i => i.SubjectType == "torrent").ToListAsync();
        rows.Single(i => i.ClientInstance == "healthy" && i.SubjectId == "gone" && i.Code == "torrent.processing").ResolvedAt.Should().NotBeNull();
        rows.Where(i => !(i.ClientInstance == "healthy" && i.SubjectId == "gone" && i.Code == "torrent.processing")).Should().OnlyContain(i => i.ResolvedAt == null);
        offline.Verify(c => c.GetTorrentsAsync(null, null, It.IsAny<CancellationToken>()), failsOnlyOnFullRead ? Times.Once() : Times.Never());
        offline.Setup(c => c.GetTorrentsAsync(It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>())).ReturnsAsync([]);
        await processor.ProcessTorrentsAsync("media");
        (await db.OperationalIssues.AsNoTracking().SingleAsync(i => i.ClientInstance == "offline" && i.SubjectType == "torrent")).ResolvedAt.Should().NotBeNull();
        healthy.Verify(c => c.GetTorrentsAsync(null, null, It.IsAny<CancellationToken>()), Times.Exactly(2));
    }
}

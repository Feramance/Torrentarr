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

public sealed class TransferHistoryTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-11-04T12:00:00Z");

    [Fact]
    public async Task Query_BucketsRatesAndPreservesPartialAndMissingCoverageAcrossReopen()
    {
        using var connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();
        var options = new DbContextOptionsBuilder<TorrentarrDbContext>().UseSqlite(connection).Options;
        var minute = Now.ToUnixTimeSeconds() / 60;
        await using (var db = new TorrentarrDbContext(options))
        {
            db.Database.EnsureCreated();
            db.TransferSamples.AddRange(
                new TransferSample { ClientInstance = "one", Minute = minute - 5, DownloadRate = 100, UploadRate = 10 },
                new TransferSample { ClientInstance = "one", Minute = minute - 4, DownloadRate = 300, UploadRate = 30 },
                new TransferSample { ClientInstance = "one", Minute = minute, DownloadRate = 900 });
            await db.SaveChangesAsync();
        }
        await using var reopened = new TorrentarrDbContext(options);
        var result = await TransferHistoryApi.QueryAsync(reopened, ["one", "offline"], "24h", Now);
        result.Series[0].Points.Should().HaveCount(288);
        var last = result.Series[0].Points.Last();
        last.DownloadRate.Should().Be(200);
        last.UploadRate.Should().Be(20);
        last.Samples.Should().Be(2);
        last.ExpectedSamples.Should().Be(5);
        result.Series[0].Points.First().DownloadRate.Should().BeNull();
        result.Series[1].Points.Should().OnlyContain(p => p.DownloadRate == null && p.Samples == 0);
    }

    [Theory]
    [InlineData("1h", 60, 1)]
    [InlineData("24h", 288, 5)]
    [InlineData("7d", 336, 30)]
    public async Task Query_ReturnsBoundedAlignedRanges(string range, int count, int bucket)
    {
        await using var db = new TorrentarrDbContext(new DbContextOptionsBuilder<TorrentarrDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        var result = await TransferHistoryApi.QueryAsync(db, ["one"], range, Now);
        result.BucketMinutes.Should().Be(bucket);
        result.Series.Single().Points.Should().HaveCount(count);
        result.Series.Single().Points.Last().Timestamp.Should().BeBefore(Now);
    }

    [Fact]
    public async Task Collector_SkipsUnavailableAndDisabledClientsAndPrunesWithoutDuplicateMinutes()
    {
        using var connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();
        var services = new ServiceCollection();
        services.AddDbContext<TorrentarrDbContext>(o => o.UseSqlite(connection));
        using var provider = services.BuildServiceProvider();
        using (var scope = provider.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<TorrentarrDbContext>();
            db.Database.EnsureCreated();
            db.TransferSamples.Add(new() { ClientInstance = "one", Minute = Now.ToUnixTimeSeconds() / 60 - 10081 });
            await db.SaveChangesAsync();
        }
        var config = new TorrentarrConfig();
        foreach (var name in new[] { "one", "offline", "unsupported", "disabled" })
            config.TorrentClients[name] = new() { Host = "fixture", Disabled = name == "disabled" };
        var registry = new Mock<ITorrentClientRegistry>();
        var one = new Mock<ITorrentClient>();
        one.Setup(c => c.GetTransferInfoAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new TorrentTransferRecord { DownloadSpeed = 123, UploadSpeed = 45 });
        var offline = new Mock<ITorrentClient>();
        offline.Setup(c => c.GetTransferInfoAsync(It.IsAny<CancellationToken>())).ThrowsAsync(new IOException("fixture offline"));
        var unsupported = new Mock<ITorrentClient>();
        registry.Setup(r => r.GetClient("one")).Returns(one.Object);
        registry.Setup(r => r.GetClient("offline")).Returns(offline.Object);
        registry.Setup(r => r.GetClient("unsupported")).Returns(unsupported.Object);
        var collector = new TransferHistoryCollector(provider.GetRequiredService<IServiceScopeFactory>(), config, registry.Object,
            NullLogger<TransferHistoryCollector>.Instance);
        await collector.CollectAsync(Now, default);
        await collector.CollectAsync(Now.AddSeconds(20), default);
        using var check = provider.CreateScope();
        var saved = await check.ServiceProvider.GetRequiredService<TorrentarrDbContext>().TransferSamples.ToListAsync();
        saved.Should().ContainSingle().Which.DownloadRate.Should().Be(123);
        registry.Verify(r => r.GetClient("disabled"), Times.Never);
    }

    [Fact]
    public void Migration_CreatesHistoryTableOnExistingDatabaseAndIsRepeatable()
    {
        using var db = new TorrentarrDbContext(new DbContextOptionsBuilder<TorrentarrDbContext>()
            .UseSqlite("Data Source=:memory:").Options);
        db.Database.OpenConnection();
        db.Database.EnsureCreated();
        db.Database.ExecuteSqlRaw("DROP TABLE torrentarr_transfer_samples");
        ManualSqliteMigrations.Apply(db);
        db.TransferSamples.Add(new() { ClientInstance = "one", Minute = 123 });
        db.SaveChanges();
        ManualSqliteMigrations.Apply(db);
        db.TransferSamples.Single().Minute.Should().Be(123);
    }
}

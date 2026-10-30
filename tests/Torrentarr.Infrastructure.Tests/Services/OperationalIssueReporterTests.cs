using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Torrentarr.Core.Configuration;
using Torrentarr.Core.Services;
using Torrentarr.Infrastructure.Database;
using Torrentarr.Infrastructure.Services;
using Xunit;

namespace Torrentarr.Infrastructure.Tests.Services;

public sealed class OperationalIssueReporterTests
{
    [Fact]
    public async Task HealthyResolvesDoNotAcquireAWriteTransaction_ButActiveRecoveryStillWrites()
    {
        await using var connection = new Microsoft.Data.Sqlite.SqliteConnection("Data Source=:memory:"); await connection.OpenAsync();
        var commands = new WriteCounter();
        await using var services = new ServiceCollection().AddDbContext<TorrentarrDbContext>(o => o.UseSqlite(connection).AddInterceptors(commands)).BuildServiceProvider();
        using (var scope = services.CreateScope()) await scope.ServiceProvider.GetRequiredService<TorrentarrDbContext>().Database.EnsureCreatedAsync();
        commands.Writes = 0;
        using var reporter = new OperationalIssueReporter(services.GetRequiredService<IServiceScopeFactory>(), NullLogger<OperationalIssueReporter>.Instance, new());
        var issue = new OperationalIssueReport("test.health", "system", "Unavailable", "Work held", "Retry", ArrInstance: "Arr");
        await reporter.ResolveAsync(issue); await reporter.ResolveAsync(issue);
        commands.Writes.Should().Be(0);
        await reporter.ReportAsync(issue);
        commands.Writes = 0;
        await reporter.ResolveAsync(issue with { ArrInstance = "arr" });
        commands.Writes.Should().Be(1);
        commands.Writes = 0;
        await reporter.ResolveAsync(issue);
        commands.Writes.Should().Be(0);
        using var check = services.CreateScope();
        (await check.ServiceProvider.GetRequiredService<TorrentarrDbContext>().OperationalIssues.SingleAsync()).ResolvedAt.Should().NotBeNull();
    }

    private sealed class WriteCounter : Microsoft.EntityFrameworkCore.Diagnostics.DbCommandInterceptor
    {
        public int Writes;
        public override ValueTask<Microsoft.EntityFrameworkCore.Diagnostics.InterceptionResult<int>> NonQueryExecutingAsync(
            System.Data.Common.DbCommand command, Microsoft.EntityFrameworkCore.Diagnostics.CommandEventData eventData,
            Microsoft.EntityFrameworkCore.Diagnostics.InterceptionResult<int> result, CancellationToken ct = default)
        { Writes++; return ValueTask.FromResult(result); }
    }

    [Fact]
    public async Task IndependentReportersDeduplicatePersistResolveAndReopen()
    {
        var path = Path.Combine(Path.GetTempPath(), "issues-" + Guid.NewGuid().ToString("N") + ".db");
        await using var services = new ServiceCollection().AddDbContext<TorrentarrDbContext>(o => o.UseSqlite($"Data Source={path};Pooling=False")).BuildServiceProvider();
        using (var scope = services.CreateScope()) await scope.ServiceProvider.GetRequiredService<TorrentarrDbContext>().Database.EnsureCreatedAsync();
        OperationalIssueReporter Reporter() => new(services.GetRequiredService<IServiceScopeFactory>(), NullLogger<OperationalIssueReporter>.Instance, new());
        using var one = Reporter(); using var two = Reporter();
        var report = new OperationalIssueReport("test.failure", "automation", "Search skipped", "No unsafe update was sent.", "Check Arr.", ArrInstance: "Arr", SubjectId: "42");
        try
        {
            await Task.WhenAll(one.ReportAsync(report), two.ReportAsync(report));
            using (var scope = services.CreateScope())
            {
                var record = await scope.ServiceProvider.GetRequiredService<TorrentarrDbContext>().OperationalIssues.SingleAsync();
                record.Occurrences.Should().Be(2); record.ResolvedAt.Should().BeNull();
            }
            await two.ResolveAsync(report);
            using (var scope = services.CreateScope()) (await scope.ServiceProvider.GetRequiredService<TorrentarrDbContext>().OperationalIssues.SingleAsync()).ResolvedAt.Should().NotBeNull();
            using var restarted = Reporter(); await restarted.ReportAsync(report);
            using (var scope = services.CreateScope())
            {
                var record = await scope.ServiceProvider.GetRequiredService<TorrentarrDbContext>().OperationalIssues.SingleAsync();
                record.Occurrences.Should().Be(3); record.ResolvedAt.Should().BeNull();
            }
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task RetentionKeepsActiveIssues_AndRemovedInstancesAreExplicitlyRetired()
    {
        var path = Path.Combine(Path.GetTempPath(), "issues-retention-" + Guid.NewGuid().ToString("N") + ".db");
        await using var services = new ServiceCollection().AddDbContext<TorrentarrDbContext>(o => o.UseSqlite($"Data Source={path};Pooling=False")).BuildServiceProvider();
        using var reporter = new OperationalIssueReporter(services.GetRequiredService<IServiceScopeFactory>(), NullLogger<OperationalIssueReporter>.Instance, new());
        try
        {
            using (var scope = services.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<TorrentarrDbContext>(); await db.Database.EnsureCreatedAsync();
                foreach (var (code, resolved, instance) in new[] { ("old-resolved", (DateTime?)DateTime.UtcNow.AddDays(-31), ""), ("old-active", (DateTime?)null, ""), ("recent", (DateTime?)DateTime.UtcNow.AddDays(-1), ""), ("removed", (DateTime?)null, "Removed-Arr") })
                    db.OperationalIssues.Add(new() { Code = code, Category = "system", ArrInstance = instance, FirstObserved = DateTime.UtcNow.AddDays(-50), LastObserved = DateTime.UtcNow.AddDays(-40), ResolvedAt = resolved });
                await db.SaveChangesAsync();
            }
            await reporter.MaintainHistoryAsync(default);
            using (var scope = services.CreateScope())
            {
                var items = await scope.ServiceProvider.GetRequiredService<TorrentarrDbContext>().OperationalIssues.ToListAsync();
                items.Should().HaveCount(3); items.Single(i => i.Code == "old-active").ResolvedAt.Should().BeNull();
                items.Single(i => i.Code == "removed").Resolution.Should().Be("Instance removed from configuration");
            }
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task UpgradeAddsIssueTableWithoutChangingExistingTorrentState()
    {
        await using var connection = new Microsoft.Data.Sqlite.SqliteConnection("Data Source=:memory:"); await connection.OpenAsync();
        await using var db = new TorrentarrDbContext(new DbContextOptionsBuilder<TorrentarrDbContext>().UseSqlite(connection).Options);
        await db.Database.EnsureCreatedAsync();
        db.TorrentLibrary.Add(new() { Hash = "kept", QbitInstance = "client", Imported = true }); await db.SaveChangesAsync();
        await db.Database.ExecuteSqlRawAsync("DROP TABLE torrentarr_operational_issues");
        ManualSqliteMigrations.Apply(db); ManualSqliteMigrations.Apply(db);
        (await db.OperationalIssues.CountAsync()).Should().Be(0);
        (await db.TorrentLibrary.SingleAsync()).Imported.Should().BeTrue();
    }

    [Fact]
    public async Task IncidentThatRecoversDuringDatabaseOutageIsRetainedAsResolvedHistory()
    {
        var path = Path.Combine(Path.GetTempPath(), "issues-recovered-" + Guid.NewGuid().ToString("N") + ".db");
        await using var services = new ServiceCollection().AddDbContext<TorrentarrDbContext>(o => o.UseSqlite($"Data Source={path};Pooling=False")).BuildServiceProvider();
        using var reporter = new OperationalIssueReporter(services.GetRequiredService<IServiceScopeFactory>(), NullLogger<OperationalIssueReporter>.Instance, new());
        var issue = new OperationalIssueReport("profile.prepare", "automation", "Search skipped", "Original profile preserved", "Check Arr", SubjectId: "42");
        try
        {
            await reporter.ReportAsync(issue); await reporter.ResolveAsync(issue);
            reporter.Pending.Should().ContainSingle(p => p.Resolved && p.FirstObserved.HasValue);
            using (var scope = services.CreateScope()) await scope.ServiceProvider.GetRequiredService<TorrentarrDbContext>().Database.EnsureCreatedAsync();
            await reporter.FlushAsync(default);
            using (var scope = services.CreateScope())
            {
                var record = await scope.ServiceProvider.GetRequiredService<TorrentarrDbContext>().OperationalIssues.SingleAsync();
                record.Title.Should().Be("Search skipped"); record.ResolvedAt.Should().NotBeNull(); record.FirstObserved.Should().BeOnOrBefore(record.ResolvedAt!.Value);
            }
            reporter.Pending.Should().BeEmpty();
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task UnavailableDatabaseDoesNotThrow_BoundsBacklog_AndRetriesAfterMigration()
    {
        var path = Path.Combine(Path.GetTempPath(), "issues-pending-" + Guid.NewGuid().ToString("N") + ".db");
        await using var services = new ServiceCollection().AddDbContext<TorrentarrDbContext>(o => o.UseSqlite($"Data Source={path};Pooling=False")).BuildServiceProvider();
        using var reporter = new OperationalIssueReporter(services.GetRequiredService<IServiceScopeFactory>(), NullLogger<OperationalIssueReporter>.Instance, new());
        try
        {
            for (var i = 0; i < 105; i++) await reporter.ReportAsync(new("test", "system", "Unavailable", "Held back", "Retry", SubjectId: i.ToString()));
            reporter.Pending.Should().HaveCount(100);
            using (var scope = services.CreateScope()) await scope.ServiceProvider.GetRequiredService<TorrentarrDbContext>().Database.EnsureCreatedAsync();
            await reporter.FlushAsync(default); reporter.Pending.Should().BeEmpty();
            using (var scope = services.CreateScope()) (await scope.ServiceProvider.GetRequiredService<TorrentarrDbContext>().OperationalIssues.CountAsync()).Should().Be(100);
        }
        finally { File.Delete(path); }
    }
}

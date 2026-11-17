using System.Data.Common;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Torrentarr.Infrastructure.Database.Models;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Torrentarr.Infrastructure.Database;
using Xunit;

namespace Torrentarr.Infrastructure.Tests.Database;

public sealed class DatabaseRetryExtensionsTests
{
    [Theory]
    [InlineData(5)]
    [InlineData(6)]
    public async Task WrappedBusyErrors_RetryLocalWriteWithoutRepeatingSubmission(int code)
    {
        var error = new DbUpdateException("Save failed", new SqliteException("Busy database", code));
        using var db = new FailingContext(error, 2);
        (await db.SaveChangesWithRetryAsync()).Should().Be(1);
        db.Attempts.Should().Be(3);
    }

    [Fact]
    public async Task PermanentErrors_AreNotRetried()
    {
        using var db = new FailingContext(new DbUpdateException("Invalid data", new SqliteException("Constraint", 19)), 5);
        await FluentActions.Awaiting(() => db.SaveChangesWithRetryAsync()).Should().ThrowAsync<DbUpdateException>();
        db.Attempts.Should().Be(1);
    }

    [Fact]
    public async Task Cancellation_StopsBackoffWithoutAnotherWrite()
    {
        using var cancellation = new CancellationTokenSource();
        using var db = new FailingContext(new DbUpdateException("Save failed", new SqliteException("Busy", 5)), 5);
        var save = db.SaveChangesWithRetryAsync(cancellationToken: cancellation.Token);
        cancellation.Cancel();
        await FluentActions.Awaiting(() => save).Should().ThrowAsync<OperationCanceledException>();
        db.Attempts.Should().Be(1);
    }

    [Theory]
    [InlineData(5)]
    [InlineData(6)]
    public async Task BulkReset_RetriesBusyErrorsAndResetsBothSearchFlags(int code)
    {
        using var connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();
        var writes = new FailingBulkWrite(code, 2);
        using var db = BulkContext(connection, writes);
        await SeedAsync(db);
        (await db.Movies.Where(m => m.ArrInstance == "Radarr" && m.Searched)
            .ExecuteUpdateWithRetryAsync(s => s.SetProperty(m => m.Searched, false).SetProperty(m => m.Upgrade, false))).Should().Be(1);
        writes.Attempts.Should().Be(3);
        db.ChangeTracker.Clear();
        var movie = await db.Movies.SingleAsync();
        movie.Searched.Should().BeFalse();
        movie.Upgrade.Should().BeFalse();
    }

    [Theory]
    [InlineData(19, 1)]
    [InlineData(5, 3)]
    public async Task BulkReset_DoesNotRetryPermanentErrorsOrRetryIndefinitely(int code, int expected)
    {
        using var connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();
        var writes = new FailingBulkWrite(code, 99);
        using var db = BulkContext(connection, writes);
        await SeedAsync(db);
        await FluentActions.Awaiting(() => db.Movies.ExecuteUpdateWithRetryAsync(s => s.SetProperty(m => m.Searched, false)))
            .Should().ThrowAsync<SqliteException>();
        writes.Attempts.Should().Be(expected);
        db.ChangeTracker.Clear();
        (await db.Movies.SingleAsync()).Searched.Should().BeTrue();
    }

    [Fact]
    public async Task BulkReset_CancellationStopsBackoffBeforeAnotherWrite()
    {
        using var connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();
        using var cancellation = new CancellationTokenSource();
        var writes = new FailingBulkWrite(5, 99, cancellation);
        using var db = BulkContext(connection, writes);
        await SeedAsync(db);
        await FluentActions.Awaiting(() => db.Movies.ExecuteUpdateWithRetryAsync(s => s.SetProperty(m => m.Searched, false), cancellationToken: cancellation.Token))
            .Should().ThrowAsync<OperationCanceledException>();
        writes.Attempts.Should().Be(1);
    }

    private static TorrentarrDbContext BulkContext(SqliteConnection connection, FailingBulkWrite writes)
        => new(new DbContextOptionsBuilder<TorrentarrDbContext>().UseSqlite(connection).AddInterceptors(writes).Options);

    private static async Task SeedAsync(TorrentarrDbContext db)
    {
        db.Database.EnsureCreated();
        db.Movies.Add(new MoviesFilesModel { ArrInstance = "Radarr", TmdbId = 1, Searched = true, Upgrade = true });
        await db.SaveChangesAsync();
    }

    private sealed class FailingBulkWrite(int code, int failures, CancellationTokenSource? cancel = null) : DbCommandInterceptor
    {
        public int Attempts { get; private set; }
        public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(DbCommand command,
            CommandEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (command.CommandText.TrimStart().StartsWith("UPDATE", StringComparison.OrdinalIgnoreCase) && ++Attempts <= failures)
            {
                cancel?.Cancel();
                throw new SqliteException("Injected database write failure", code);
            }
            return ValueTask.FromResult(result);
        }
    }

    private sealed class FailingContext(Exception failure, int failures) : DbContext
    {
        public int Attempts { get; private set; }
        public override Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
            => ++Attempts <= failures ? Task.FromException<int>(failure) : Task.FromResult(1);
    }
}

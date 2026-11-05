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

    private sealed class FailingContext(Exception failure, int failures) : DbContext
    {
        public int Attempts { get; private set; }
        public override Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
            => ++Attempts <= failures ? Task.FromException<int>(failure) : Task.FromResult(1);
    }
}

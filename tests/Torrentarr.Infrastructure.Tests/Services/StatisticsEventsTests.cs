using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Torrentarr.Core.Configuration;
using Torrentarr.Core.Services;
using Torrentarr.Infrastructure.Database;
using Torrentarr.Infrastructure.Services;
using Xunit;

namespace Torrentarr.Infrastructure.Tests.Services;

public class StatisticsEventsTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Observer_DetectsCommitsFromAnotherConnection_AndCoalescesNotifications(bool withSupervisor)
    {
        var path = Path.Combine(Path.GetTempPath(), $"torrentarr-statistics-{Guid.NewGuid():N}.db");
        var connectionString = $"Data Source={path};Pooling=False";
        try
        {
            var services = new ServiceCollection();
            services.AddDbContext<TorrentarrDbContext>(options => options.UseSqlite(connectionString));
            services.AddSingleton<ProcessStateManager>();
            if (withSupervisor) services.AddSingleton(new WorkerProcessSupervisor(new TorrentarrConfig(), NullLogger<WorkerProcessSupervisor>.Instance, Mock.Of<IMediaValidationService>()));
            using var provider = services.BuildServiceProvider();
            await using var writer = new SqliteConnection(connectionString);
            await writer.OpenAsync();
            await using var command = writer.CreateCommand();
            command.CommandText = "CREATE TABLE observation (value INTEGER)";
            await command.ExecuteNonQueryAsync();
            using var events = new StatisticsEvents(provider.GetRequiredService<IServiceScopeFactory>(), NullLogger<StatisticsEvents>.Instance);
            var (id, reader) = events.Subscribe();
            (await reader.ReadAsync()).Should().Be(0);
            await events.StartAsync(default);
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var initial = await reader.ReadAsync(timeout.Token); // first runtime snapshot
            command.CommandText = "INSERT INTO observation VALUES (1)";
            await command.ExecuteNonQueryAsync();
            (await reader.ReadAsync(timeout.Token)).Should().BeGreaterThan(initial);
            events.Publish(); events.Publish(); events.Publish();
            (await reader.ReadAsync(timeout.Token)).Should().Be(events.Revision);
            events.Unsubscribe(id);
            await events.StopAsync(timeout.Token);
        }
        finally { File.Delete(path); }
    }
}

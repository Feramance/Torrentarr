using System.Collections.Concurrent;
using System.Text.Json;
using System.Threading.Channels;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Torrentarr.Infrastructure.Database;
using Torrentarr.Core.Interfaces;

namespace Torrentarr.Infrastructure.Services;

/// <summary>A single cross-process SQLite observer fans out coalesced invalidations.</summary>
public sealed class StatisticsEvents(IServiceScopeFactory scopes, ILogger<StatisticsEvents> logger) : BackgroundService
{
    private readonly ConcurrentDictionary<Guid, Channel<long>> _subscribers = new();
    private long _revision;
    public long Revision => Interlocked.Read(ref _revision);
    public (Guid Id, ChannelReader<long> Reader) Subscribe()
    {
        var channel = Channel.CreateBounded<long>(new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropOldest });
        var id = Guid.NewGuid();
        _subscribers[id] = channel;
        channel.Writer.TryWrite(Revision);
        return (id, channel.Reader);
    }
    public void Unsubscribe(Guid id)
    {
        if (_subscribers.TryRemove(id, out var channel)) channel.Writer.TryComplete();
    }
    public void Publish()
    {
        var revision = Interlocked.Increment(ref _revision);
        foreach (var channel in _subscribers.Values) channel.Writer.TryWrite(revision);
    }
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = scopes.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<TorrentarrDbContext>();
                await using var connection = new SqliteConnection(db.Database.GetConnectionString());
                await connection.OpenAsync(stoppingToken);
                await using var command = connection.CreateCommand();
                command.CommandText = "PRAGMA data_version";
                var previousVersion = Convert.ToInt64(await command.ExecuteScalarAsync(stoppingToken));
                string? previousRuntime = null;
                using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
                while (await timer.WaitForNextTickAsync(stoppingToken))
                {
                    var version = Convert.ToInt64(await command.ExecuteScalarAsync(stoppingToken));
                    var workers = scope.ServiceProvider.GetService<WorkerProcessSupervisor>();
                    var statuses = workers == null ? Enumerable.Empty<ProcessStatus>() : (await workers.GetProcessStatusAsync()).Values;
                    var runtime = JsonSerializer.Serialize(new
                    {
                        host = scope.ServiceProvider.GetService<ProcessStateManager>()?.GetAll().OrderBy(s => s.Name),
                        workers = statuses.OrderBy(s => s.Name).Select(s => new
                        {
                            s.Name,
                            s.IsAlive,
                            s.SearchesTriggered,
                            s.ItemsSearched,
                            s.SearchTimestamp,
                            s.CategoryCount,
                            s.QueueCount,
                            s.ObservedAt,
                            s.ObservationError,
                            s.Torrent,
                            s.Search
                        })
                    });
                    if (version != previousVersion || runtime != previousRuntime) Publish();
                    previousVersion = version;
                    previousRuntime = runtime;
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Statistics observation failed; retrying");
                try { await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken); }
                catch (OperationCanceledException) { break; }
            }
        }
    }
}

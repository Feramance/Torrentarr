using System.Net;
using System.Net.Sockets;
using System.Text;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Torrentarr.Core.Configuration;
using Torrentarr.Core.Services;
using Torrentarr.Infrastructure.Database;
using Torrentarr.Infrastructure.Database.Models;
using Torrentarr.Infrastructure.Services;
using Xunit;

namespace Torrentarr.Infrastructure.Tests.Services;

public sealed class IssueAlertDispatcherTests
{
    [Fact]
    public async Task ConcurrentReportersProduceOneOpening()
    {
        var connectionString = $"Data Source=issue-alert-{Guid.NewGuid():N};Mode=Memory;Cache=Shared;Pooling=False";
        await using var anchor = new SqliteConnection(connectionString);
        await anchor.OpenAsync();
        await using var services = new ServiceCollection().AddDbContext<TorrentarrDbContext>(o => o.UseSqlite(connectionString)).BuildServiceProvider();
        using var scope = services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TorrentarrDbContext>();
        await db.Database.EnsureCreatedAsync();
        ManualSqliteMigrations.ApplyIssueAlertTriggers(db);
        var scopes = services.GetRequiredService<IServiceScopeFactory>();
        var issue = new OperationalIssueReport("import.scan", "automation", "Blocked", "", "", ArrInstance: "Radarr", SubjectId: "file");
        using var first = new OperationalIssueReporter(scopes, NullLogger<OperationalIssueReporter>.Instance, new TorrentarrConfig());
        using var second = new OperationalIssueReporter(scopes, NullLogger<OperationalIssueReporter>.Instance, new TorrentarrConfig());
        await Task.WhenAll(Task.Run(() => first.ReportAsync(issue)), Task.Run(() => second.ReportAsync(issue)));
        await first.FlushAsync(default);
        await second.FlushAsync(default);

        Assert.Equal("opened", (await db.IssueAlertEvents.SingleAsync()).State);
        Assert.Empty(first.Pending);
        Assert.Empty(second.Pending);
    }

    [Fact]
    public async Task AlertsUsePersistedTransitionsAndRetryTheSameEventAfterRestart()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = new TorrentarrDbContext(new DbContextOptionsBuilder<TorrentarrDbContext>().UseSqlite(connection).Options);
        await db.Database.EnsureCreatedAsync();
        ManualSqliteMigrations.ApplyIssueAlertTriggers(db);

        var config = new TorrentarrConfig();
        config.Settings.IssueAlertsSecretUrl = "https://example.test/alert?token=secret";
        config.Settings.IssueAlertTypes = ["worker"];
        var now = DateTime.UtcNow;
        OperationalIssue Issue(string code, string id) => new()
        {
            Code = code,
            SubjectId = id,
            Title = "private https://tracker.example/passkey",
            ArrInstance = "Radarr",
            Severity = "warning",
            FirstObserved = now,
            LastObserved = now
        };

        var historical = Issue("worker.lifecycle", "historical");
        db.Add(historical);
        await db.SaveChangesAsync();
        var sent = new List<IssueAlertDispatcher.Payload>();
        var accepted = false;
        Task<bool> Send(Uri _, IssueAlertDispatcher.Payload payload, CancellationToken ct)
        {
            sent.Add(payload);
            return Task.FromResult(accepted);
        }
        IssueAlertDispatcher NewDispatcher() => new(null!, config, NullLogger<IssueAlertDispatcher>.Instance);
        var dispatcher = NewDispatcher();
        await dispatcher.DispatchAsync(db, config.Settings, now, Send, default);
        Assert.Empty(sent);

        config.Settings.IssueAlertsEnabled = true;
        await dispatcher.DispatchAsync(db, config.Settings, now.AddSeconds(1), Send, default);
        var issue = Issue("worker.lifecycle", "new");
        issue.FirstObserved = issue.LastObserved = now.AddSeconds(1);
        db.AddRange(issue, Issue("import.scan", "not-selected"), Issue("worker.lifecycle", "historical-late-write"));
        await db.SaveChangesAsync();
        issue.Occurrences += 3;
        await db.SaveChangesAsync();
        await dispatcher.DispatchAsync(db, config.Settings, now.AddSeconds(2), Send, default);
        Assert.Empty(sent);
        await dispatcher.DispatchAsync(db, config.Settings, now.AddSeconds(31), Send, default);
        var failed = Assert.Single(sent);
        Assert.Equal("opened", failed.State);
        Assert.Equal(issue.Id, failed.IssueId);
        Assert.Equal("Radarr", failed.Instance);
        Assert.DoesNotContain("private", System.Text.Json.JsonSerializer.Serialize(failed));

        accepted = true;
        using var restarted = NewDispatcher();
        await restarted.DispatchAsync(db, config.Settings, now.AddSeconds(32), Send, default);
        Assert.Single(sent);
        await restarted.DispatchAsync(db, config.Settings, now.AddSeconds(46), Send, default);
        Assert.Equal(failed.EventId, sent[1].EventId);

        historical.ResolvedAt = issue.ResolvedAt = now.AddSeconds(47);
        await db.SaveChangesAsync();
        await restarted.DispatchAsync(db, config.Settings, now.AddSeconds(48), Send, default);
        Assert.Equal(3, sent.Count);
        Assert.Equal("resolved", sent[2].State);
        Assert.Equal(issue.Id, sent[2].IssueId);

        var transient = Issue("worker.lifecycle", "transient");
        transient.FirstObserved = now.AddSeconds(48);
        transient.ResolvedAt = now.AddSeconds(50);
        db.Add(transient);
        await db.SaveChangesAsync();
        await restarted.DispatchAsync(db, config.Settings, now.AddSeconds(90), Send, default);
        Assert.Equal(3, sent.Count);
    }

    [Theory]
    [InlineData("307 Temporary Redirect")]
    [InlineData("503 Service Unavailable")]
    public async Task WebhookTreatsRedirectsAndErrorsAsDeliveryFailures(string status)
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var server = Task.Run(async () =>
        {
            using var peer = await listener.AcceptTcpClientAsync(timeout.Token);
            await using var stream = peer.GetStream();
            using var reader = new StreamReader(stream);
            while (await reader.ReadLineAsync(timeout.Token) is { Length: > 0 }) { }
            var response = $"HTTP/1.1 {status}\r\nLocation: http://127.0.0.1:{port}/redirected\r\nContent-Length: 0\r\nConnection: close\r\n\r\n";
            await stream.WriteAsync(Encoding.ASCII.GetBytes(response), timeout.Token);
        });
        var payload = new IssueAlertDispatcher.Payload("test", "test", "info", "", "test", "Fixture", null);
        Assert.False(await IssueAlertDispatcher.SendAsync(new Uri($"http://127.0.0.1:{port}/alert"), payload, timeout.Token));
        await server;
        Assert.False(listener.Pending());
    }

    [Fact]
    public async Task WebhookTimesOutWhenReceiverDoesNotRespond()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var payload = new IssueAlertDispatcher.Payload("test", "test", "info", "", "test", "Fixture", null);
        var send = IssueAlertDispatcher.SendAsync(new Uri($"http://127.0.0.1:{port}/timeout"), payload, timeout.Token);
        using var peer = await listener.AcceptTcpClientAsync(timeout.Token);
        Assert.False(await send);
    }
}

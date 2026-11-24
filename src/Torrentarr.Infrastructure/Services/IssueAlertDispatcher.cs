using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Torrentarr.Core.Configuration;
using Torrentarr.Core.Services;
using Torrentarr.Infrastructure.Database;
using Torrentarr.Infrastructure.Database.Models;

namespace Torrentarr.Infrastructure.Services;

public sealed class IssueAlertDispatcher(IServiceScopeFactory scopes, TorrentarrConfig config,
    ILogger<IssueAlertDispatcher> logger) : BackgroundService
{
    internal const string CursorKey = "issue-alert-dispatch";
    private static readonly TimeSpan WorkerGrace = TimeSpan.FromSeconds(30);
    private static readonly HttpClient Client = new(new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false })
    { Timeout = TimeSpan.FromSeconds(8) };
    private DateTime _pruneAfter;

    internal sealed class Cursor
    {
        public string Policy { get; set; } = "";
        public string Installation { get; set; } = "";
        public long ActivatedAfter { get; set; }
        public DateTime ActivatedAt { get; set; }
        public long Through { get; set; }
        public int Attempts { get; set; }
        public long AttemptEventId { get; set; }
        public DateTime RetryAt { get; set; }
    }

    internal sealed record Payload(string EventId, string Type, string Severity, string Instance, string State, string Summary, long? IssueId);
    internal static string TypeFor(string code) => code switch
    {
        "worker.lifecycle" => "worker",
        "automation.protection" => "tracker",
        _ => "import"
    };

    internal static async Task<bool> SendAsync(Uri destination, Payload payload, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, destination) { Content = JsonContent.Create(payload) };
        request.Headers.Add("Idempotency-Key", payload.EventId);
        try
        {
            using var result = await Client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
            return result.IsSuccessStatusCode;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception) { return false; }
    }

    internal async Task DispatchAsync(TorrentarrDbContext db, SettingsConfig settings, DateTime now,
        Func<Uri, Payload, CancellationToken, Task<bool>> send, CancellationToken ct)
    {
        var destination = settings.IssueAlertsEnabled ? ConfigValidationHelper.IssueAlertDestination(settings) : null;
        string[] types = settings.IssueAlertTypes?.Distinct().Order().ToArray() ?? [];
        var policy = destination == null || types.Length == 0 ? "disabled"
            : Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(new { Destination = destination.AbsoluteUri, Types = types })));
        var cursor = MaintenancePersistence.Read<Cursor>(db, CursorKey);
        if (cursor.Policy != policy)
        {
            cursor.Installation = cursor.Installation.Length == 0 ? Guid.NewGuid().ToString("N") : cursor.Installation;
            cursor.Policy = policy;
            cursor.ActivatedAfter = cursor.Through = await db.IssueAlertEvents.MaxAsync(e => (long?)e.Id, ct) ?? 0;
            cursor.ActivatedAt = now;
            cursor.Attempts = 0;
            cursor.AttemptEventId = 0;
            cursor.RetryAt = default;
            MaintenancePersistence.Write(db, CursorKey, cursor);
        }
        if (now >= _pruneAfter)
        {
            var cutoff = now.AddDays(-30);
            await db.IssueAlertEvents.Where(e => e.ObservedAt < cutoff && (e.State != "opened"
                || !db.OperationalIssues.Any(i => i.Id == e.IssueId && i.FirstObserved == e.OpenedAt && i.ResolvedAt == null)))
                .ExecuteDeleteAsync(ct);
            _pruneAfter = now.AddHours(1);
        }
        if (policy == "disabled" || destination == null || now < cursor.RetryAt) return;

        var events = await db.IssueAlertEvents.AsNoTracking().Where(e => e.Id > cursor.Through)
            .OrderBy(e => e.Id).Take(25).ToListAsync(ct);
        foreach (var entry in events)
        {
            if (!ReferenceEquals(settings, config.Settings)) break;
            if (cursor.AttemptEventId != entry.Id)
            {
                cursor.Attempts = 0;
                cursor.AttemptEventId = entry.Id;
            }
            var type = TypeFor(entry.Code);
            var eligible = types.Contains(type) && entry.OpenedAt >= cursor.ActivatedAt;
            if (eligible && entry.State == "resolved")
                eligible = entry.OpeningId > cursor.ActivatedAfter;
            if (eligible && type == "worker")
            {
                var deadline = entry.OpenedAt + WorkerGrace;
                if (entry.State == "opened")
                {
                    eligible = !await db.IssueAlertEvents.AnyAsync(e => e.OpeningId == entry.Id && e.ObservedAt < deadline, ct);
                    if (eligible && now < deadline) break;
                }
                else
                    eligible = entry.ObservedAt >= deadline;
            }
            if (eligible && cursor.Attempts < 4)
            {
                var summary = entry.State == "resolved" ? "Operational issue resolved." : type switch
                {
                    "worker" => "A worker is not making healthy progress.",
                    "tracker" => "A tracker obligation may be at risk. Review available protection evidence in Torrentarr.",
                    _ => "An Arr import is blocked or its progress could not be verified."
                };
                var severity = entry.Severity == "warning" ? "warning" : "error";
                var payload = new Payload($"{cursor.Installation}:{entry.Id}", type, severity,
                    DiagnosticText.Safe(entry.Instance), entry.State, summary, entry.IssueId);
                cursor.Attempts++;
                cursor.RetryAt = now.AddSeconds(15 * (1 << (cursor.Attempts - 1)));
                MaintenancePersistence.Write(db, CursorKey, cursor);
                if (!await send(destination, payload, ct))
                {
                    logger.LogWarning("Issue webhook event {EventId} failed on attempt {Attempt}", payload.EventId, cursor.Attempts);
                    if (cursor.Attempts < 4) break;
                }
            }
            cursor.Through = entry.Id;
            cursor.Attempts = 0;
            cursor.AttemptEventId = 0;
            cursor.RetryAt = default;
            MaintenancePersistence.Write(db, CursorKey, cursor);
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(5));
        do
        {
            try
            {
                using var scope = scopes.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<TorrentarrDbContext>();
                await DispatchAsync(db, config.Settings, DateTime.UtcNow, SendAsync, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception ex) { logger.LogWarning("Issue webhook dispatch unavailable ({ErrorType})", ex.GetType().Name); }
        } while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}

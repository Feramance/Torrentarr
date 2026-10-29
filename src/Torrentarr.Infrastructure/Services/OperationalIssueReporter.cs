using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Torrentarr.Core.Configuration;
using Torrentarr.Core.Services;
using Torrentarr.Infrastructure.Database;
using Torrentarr.Infrastructure.Database.Models;

namespace Torrentarr.Infrastructure.Services;

public sealed class OperationalIssueReporter(IServiceScopeFactory scopes, ILogger<OperationalIssueReporter> logger,
    TorrentarrConfig config) : BackgroundService, IOperationalIssueReporter
{
    private readonly Dictionary<string, PendingOperationalIssue> _pending = new();
    private readonly SemaphoreSlim _writes = new(1, 1);
    private readonly object _gate = new();
    public IReadOnlyList<PendingOperationalIssue> Pending { get { lock (_gate) return _pending.Values.ToArray(); } }
    private static string Key(OperationalIssueReport r) => System.Text.Json.JsonSerializer.Serialize(new[] { r.Code, r.ArrInstance.ToUpperInvariant(), r.ClientInstance.ToUpperInvariant(), r.SubjectType, r.SubjectId });
    public Task ReportAsync(OperationalIssueReport report, CancellationToken ct = default) => SubmitAsync(new(report, false, DateTime.UtcNow, config.Revision), ct);
    public Task ResolveAsync(OperationalIssueReport report, CancellationToken ct = default) => SubmitAsync(new(report, true, DateTime.UtcNow, config.Revision), ct);

    private async Task SubmitAsync(PendingOperationalIssue update, CancellationToken ct)
    {
        lock (_gate)
        {
            var key = Key(update.Report);
            var previous = _pending.GetValueOrDefault(key);
            update = update with { FirstObserved = previous?.FirstObserved ?? (update.Resolved ? null : update.ObservedAt) };
            if (_pending.Count < 100 || _pending.ContainsKey(key)) _pending[key] = update;
            else logger.LogWarning("Issue reporting backlog is full; additional issue updates remain in application logs");
        }
        await FlushAsync(ct);
    }

    internal async Task FlushAsync(CancellationToken ct)
    {
        try
        {
            using var configuration = config.BeginOperation();
            using var processing = ConfigMutationGuard.EnterProcessing();
            await _writes.WaitAsync(ct);
            try
            {
                foreach (var update in Pending)
                {
                    using var scope = scopes.CreateScope();
                    var db = scope.ServiceProvider.GetRequiredService<TorrentarrDbContext>();
                    var r = update.Report;
                    if (scope.ServiceProvider.GetService<IConfigReloader>() is { } reloader)
                        r = r with { ArrInstance = reloader.ResolveInstanceName(r.ArrInstance, update.ConfigurationRevision), ClientInstance = reloader.ResolveInstanceName(r.ClientInstance, update.ConfigurationRevision) };
                    var at = update.ObservedAt;
                    if (db.Database.IsRelational())
                    {
                        if (update.Resolved)
                        {
                            // A failure and recovery during the same outage still belong in resolved history.
                            if (update.FirstObserved.HasValue)
                                await db.Database.ExecuteSqlInterpolatedAsync($"INSERT OR IGNORE INTO torrentarr_operational_issues (Code, Category, ArrInstance, ClientInstance, SubjectType, SubjectId, Severity, Title, Impact, Remediation, Target, FirstObserved, LastObserved, ResolvedAt, Resolution, Occurrences) VALUES ({r.Code}, {r.Category}, {r.ArrInstance}, {r.ClientInstance}, {r.SubjectType}, {r.SubjectId}, {r.Severity}, {r.Title}, {r.Impact}, {r.Remediation}, {r.Target}, {update.FirstObserved.Value}, {at}, {at}, {"Verified recovery"}, 1)", ct);
                            await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE torrentarr_operational_issues SET ResolvedAt={at}, Resolution={"Verified recovery"}, LastObserved={at} WHERE Code={r.Code} AND ArrInstance={r.ArrInstance} COLLATE NOCASE AND ClientInstance={r.ClientInstance} COLLATE NOCASE AND SubjectType={r.SubjectType} AND SubjectId={r.SubjectId} AND (ResolvedAt IS NULL OR {update.FirstObserved.HasValue}) AND LastObserved <= {at}", ct);
                        }
                        else
                            await db.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO torrentarr_operational_issues (Code, Category, ArrInstance, ClientInstance, SubjectType, SubjectId, Severity, Title, Impact, Remediation, Target, FirstObserved, LastObserved, Occurrences) VALUES ({r.Code}, {r.Category}, {r.ArrInstance}, {r.ClientInstance}, {r.SubjectType}, {r.SubjectId}, {r.Severity}, {r.Title}, {r.Impact}, {r.Remediation}, {r.Target}, {update.FirstObserved ?? at}, {at}, 1) ON CONFLICT(Code, ArrInstance, ClientInstance, SubjectType, SubjectId) DO UPDATE SET LastObserved=excluded.LastObserved, ResolvedAt=NULL, Resolution=NULL, Occurrences=Occurrences+1, Severity=excluded.Severity, Title=excluded.Title, Impact=excluded.Impact, Remediation=excluded.Remediation, Target=excluded.Target WHERE LastObserved <= excluded.LastObserved", ct);
                    }
                    else
                    {
                        var item = await db.OperationalIssues.SingleOrDefaultAsync(i => i.Code == r.Code && i.ArrInstance == r.ArrInstance && i.ClientInstance == r.ClientInstance && i.SubjectType == r.SubjectType && i.SubjectId == r.SubjectId, ct);
                        if (item == null && (!update.Resolved || update.FirstObserved.HasValue))
                        {
                            item = new()
                            {
                                Code = r.Code,
                                Category = r.Category,
                                ArrInstance = r.ArrInstance,
                                ClientInstance = r.ClientInstance,
                                SubjectType = r.SubjectType,
                                SubjectId = r.SubjectId,
                                FirstObserved = update.FirstObserved ?? at,
                                Title = r.Title,
                                Impact = r.Impact,
                                Remediation = r.Remediation,
                                Severity = r.Severity,
                                Target = r.Target,
                                Occurrences = update.Resolved ? 1 : 0
                            };
                            db.Add(item);
                        }
                        if (item != null && item.LastObserved <= at)
                        {
                            item.LastObserved = at; item.ResolvedAt = update.Resolved ? at : null;
                            item.Resolution = update.Resolved ? "Verified recovery" : null;
                            if (!update.Resolved) { item.Occurrences++; item.Title = r.Title; item.Impact = r.Impact; item.Remediation = r.Remediation; item.Severity = r.Severity; item.Target = r.Target; }
                            await db.SaveChangesAsync(ct);
                        }
                    }
                    lock (_gate) { var key = Key(update.Report); if (_pending.GetValueOrDefault(key) == update) _pending.Remove(key); }
                }
            }
            finally { _writes.Release(); }
        }
        catch (Exception ex) { logger.LogWarning("Issue history could not be saved ({ErrorType}); reports retained for retry", ex.GetType().Name); }
    }

    private DateTime _nextCleanup;
    internal async Task MaintainHistoryAsync(CancellationToken ct)
    {
        using var configuration = config.BeginOperation();
        using var processing = ConfigMutationGuard.EnterProcessing();
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TorrentarrDbContext>();
        var now = DateTime.UtcNow;
        var arrs = config.ArrInstances.Keys.ToList();
        var clients = config.GetAllTorrentClients().Keys.ToList();
        var retired = await db.OperationalIssues.Where(i => i.ResolvedAt == null &&
            (i.ArrInstance != "" && !arrs.Contains(i.ArrInstance) || i.ClientInstance != "" && !clients.Contains(i.ClientInstance))).ToListAsync(ct);
        foreach (var issue in retired) { issue.ResolvedAt = now; issue.Resolution = "Instance removed from configuration"; }
        if (now >= _nextCleanup)
        {
            var cutoff = now.AddDays(-30);
            db.OperationalIssues.RemoveRange(await db.OperationalIssues.Where(i => i.ResolvedAt < cutoff).ToListAsync(ct));
        }
        await db.SaveChangesAsync(ct);
        if (now >= _nextCleanup) _nextCleanup = now.AddDays(1);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {

        while (!stoppingToken.IsCancellationRequested)
        {
            await FlushAsync(stoppingToken);
            try
            {
                await MaintainHistoryAsync(stoppingToken);
            }
            catch (Exception ex) when (!stoppingToken.IsCancellationRequested) { logger.LogWarning("Issue history maintenance unavailable ({ErrorType})", ex.GetType().Name); }
            try { await Task.Delay(TimeSpan.FromSeconds(15), stoppingToken); }
            catch (OperationCanceledException) { break; }
        }
    }
}

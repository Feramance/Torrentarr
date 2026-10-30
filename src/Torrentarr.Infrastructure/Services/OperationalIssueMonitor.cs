using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Torrentarr.Core.Configuration;
using Torrentarr.Core.Interfaces;
using Torrentarr.Core.Services;
using Torrentarr.Infrastructure.Database;

namespace Torrentarr.Infrastructure.Services;

/// <summary>Publishes existing structured service observations; an unavailable observation never clears an issue.</summary>
public sealed class OperationalIssueMonitor(IServiceScopeFactory scopes, IOperationalIssueReporter issues,
    TorrentarrConfig config, ILogger<OperationalIssueMonitor> logger) : BackgroundService
{
    private readonly Dictionary<string, string> _maintenanceRuns = new();

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try { await InspectAsync(ct); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
            catch (Exception ex) { logger.LogWarning("Operational observations unavailable ({ErrorType}); existing issues retained", ex.GetType().Name); }
            try { await Task.Delay(TimeSpan.FromSeconds(15), ct); } catch (OperationCanceledException) { break; }
        }
    }

    internal async Task InspectAsync(CancellationToken ct)
    {
        using var scope = scopes.CreateScope();
        var services = scope.ServiceProvider;
        using var operation = config.BeginOperation();
        if (services.GetService<IConfigReloader>() is { } reloader)
        {
            var state = reloader.GetStatus();
            if (state.State is "error" or "applied") await Observe(new("config.apply", "system", "Configuration could not be applied", "Some services may still use the previous configuration.", "Check configuration validation and component status.", Target: "config"), state.State == "error", ct);
        }
        if (services.GetService<IConnectivityService>() is { LastChecked: not null } network)
            await Observe(new("network.internet", "system", "Internet connectivity is unavailable", "Internet-dependent work is delayed.", "Check the host network, DNS and configured connectivity hosts."), !network.IsConnected, ct);
        if (services.GetService<ITorrentClientRegistry>() is { } clients)
            foreach (var (id, cfg) in config.GetAllTorrentClients().Where(p => !p.Value.Disabled && !string.IsNullOrWhiteSpace(p.Value.Host) && p.Value.Host != "CHANGE_ME"))
            {
                var reachable = false;
                try { reachable = clients.GetClient(id) is { } client && !string.IsNullOrWhiteSpace(await client.GetVersionAsync(ct)); }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                catch (Exception ex) { logger.LogWarning(ex, "Torrent client connectivity check failed for {Instance}", id); }
                await Observe(new("client.connection", "system", "Torrent client is disconnected", "Torrent management and ownership inspection are incomplete.", "Check the client address, credentials and network.", ClientInstance: id, Target: "config"), !reachable, ct);
            }
        if (services.GetService<WorkerProcessSupervisor>() is { } workers)
            foreach (var (id, state) in await workers.GetProcessStatusAsync())
            {
                // A running PID alone is not evidence of recovered worker progress.
                var healthy = state.IsAlive && state.LastHeartbeat.HasValue && state.Torrent?.LastSuccess != null && state.Search?.LastSuccess != null;
                var issue = new OperationalIssueReport("worker.lifecycle", "system", "Worker is not making healthy progress", "This Arr instance may not process torrents or searches.", "Check worker logs and connection settings; restart after correcting the cause.", ArrInstance: id, Target: "processes");
                if (!state.IsAlive) await issues.ReportAsync(issue, ct); else if (healthy) await issues.ResolveAsync(issue, ct);
            }
        if (services.GetService<IFreeSpaceService>() is { } storage)
            foreach (var disk in await storage.GetFreeSpaceStatsAsync(ct))
                await Observe(new("storage.reserve", "system", "Download storage needs attention", "Free-space protection may hold downloads paused.", "Restore access to the monitored directory or free space above its configured reserve.", ClientInstance: disk.ClientInstanceId, SubjectType: "storage", SubjectId: disk.Path, Severity: "warning", Target: "config"), disk.Error != null || disk.BelowThreshold, ct);
        if (services.GetService<TorrentAutomationService>() is { } automation)
        {
            var state = automation.GetStatus();
            var current = new List<OperationalIssueReport>();
            foreach (var tracker in state.TrackerIssues)
                current.Add(new("automation.tracker", "automation", "Tracker reports a torrent problem", "Tracker protection or torrent progress needs attention.", "Inspect tracker status and protection settings before taking action.", ClientInstance: tracker.ClientInstanceId, SubjectType: "torrent", SubjectId: tracker.TorrentHash, Severity: "warning", Target: "maintenance"));
            foreach (var recovery in state.Recovery.Where(r => r.Stage is not ("new" or "checked")))
                current.Add(new("automation.recovery", "automation", "Torrent recovery is pending", "The torrent is preserved while repair is pending or unresolved.", "Inspect the recovery status and storage access; do not remove protection evidence.", ClientInstance: recovery.ClientInstanceId, SubjectType: "torrent", SubjectId: recovery.TorrentHash, Severity: "warning", Target: "maintenance"));
            foreach (var group in state.Protection.Where(p => p.NativeStopLimitRisk || p.DeadlineRisk is "warning" or "critical" or "unachievable").GroupBy(p => new { p.ClientInstanceId, p.TorrentHash }))
                current.Add(new("automation.protection", "automation", "Tracker obligation may be at risk", "A tracker deadline or native stop limit can prevent the required seeding obligation from being met.", "Review tracker protection and seeding limits in Manage; preserve the torrent while obligations remain.", ClientInstance: group.Key.ClientInstanceId, SubjectType: "torrent", SubjectId: group.Key.TorrentHash,
                    Severity: group.Any(p => p.DeadlineRisk is "critical" or "unachievable") ? "error" : "warning", Target: "maintenance"));
            foreach (var inspection in state.Inspections)
                if (inspection.ConsecutiveFailures > 0 || inspection.LastSucceededAt.HasValue)
                    await Observe(new("automation.inspection", "system", "Torrent protection inspection is unavailable", "Previous protection evidence is retained; removal remains conservative.", "Check client connectivity and tracker access before retrying.", ClientInstance: inspection.ClientInstanceId, Target: "maintenance"), inspection.ConsecutiveFailures > 0, ct);
            foreach (var issue in current) await issues.ReportAsync(issue, ct);
            // Clear only clients whose latest complete protection inspection succeeded.
            var inspected = state.Inspections.Where(i => i.ConsecutiveFailures == 0 && i.LastSucceededAt.HasValue).Select(i => i.ClientInstanceId).ToList();
            var db = services.GetRequiredService<TorrentarrDbContext>();
            var previous = await db.OperationalIssues.AsNoTracking().Where(i => i.ResolvedAt == null && inspected.Contains(i.ClientInstance) && (i.Code == "automation.tracker" || i.Code == "automation.recovery" || i.Code == "automation.protection")).ToListAsync(ct);
            foreach (var old in previous.Where(i => state.Inspections.Any(s => s.ClientInstanceId == i.ClientInstance && s.LastSucceededAt > new DateTimeOffset(i.LastObserved, TimeSpan.Zero)) && !current.Any(r => r.Code == i.Code && r.ClientInstance == i.ClientInstance && r.SubjectId == i.SubjectId)))
                await issues.ResolveAsync(new(old.Code, old.Category, old.Title, old.Impact, old.Remediation, old.ArrInstance, old.ClientInstance, old.SubjectType, old.SubjectId, old.Severity, old.Target), ct);
        }
        if (services.GetService<IMaintenanceCoordinator>() is { } maintenance)
            foreach (var latest in maintenance.GetHistory().Where(r => r.CompletedAt.HasValue)
                .GroupBy(run => System.Text.Json.JsonSerializer.Serialize(run.ClientInstanceIds.Select(id => id.ToUpperInvariant()).Distinct().Order()))
                .Select(group => (Scope: group.Key, Run: group.MaxBy(run => run.CompletedAt)!)))
            {
                if (_maintenanceRuns.GetValueOrDefault(latest.Scope) == latest.Run.RunId || latest.Run.Result is not ("success" or "partial" or "failed")) continue;
                // History has aggregate counts, so report once for the selected set rather than blaming each client.
                var issue = new OperationalIssueReport("maintenance.run", "system", "Maintenance execution failed", "Some actions failed in the selected maintenance run; the aggregate result does not identify a failing client.",
                    "Review Manage history and its diagnostics, then retry the same client selection.", SubjectType: "maintenance", SubjectId: latest.Scope, Target: "maintenance");
                if (latest.Run.Result == "success" || latest.Run.Failed > 0)
                    await Observe(issue, latest.Run.Failed > 0, ct);
                // A verified successful run can also close records written by the former per-client reporter.
                if (latest.Run.Result == "success")
                    foreach (var client in latest.Run.ClientInstanceIds)
                        await issues.ResolveAsync(issue with { ClientInstance = client, SubjectType = "service", SubjectId = "" }, ct);
                _maintenanceRuns[latest.Scope] = latest.Run.RunId;
            }
    }

    private Task Observe(OperationalIssueReport issue, bool failed, CancellationToken ct)
        => failed ? issues.ReportAsync(issue, ct) : issues.ResolveAsync(issue, ct);
}

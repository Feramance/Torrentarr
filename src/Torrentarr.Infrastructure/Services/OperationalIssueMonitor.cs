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
    // Completion cursor per scope; equal timestamps retain IDs to avoid losing simultaneous runs.
    private readonly Dictionary<string, (DateTimeOffset CompletedAt, HashSet<string> RunIds)> _maintenanceRuns = new();

    private List<OperationalIssueReport> _configurationIssues = [];

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
            if (state.State is "error" or "applied") await Observe(new("config.apply", "system", "Configuration could not be applied", $"File={DiagnosticText.Safe(reloader.ConfigPath)}; accepted revision={state.AcceptedRevision}; state={state.State}; error={DiagnosticText.Safe(state.Error)}; components: {string.Join("; ", state.Components.Select(c => $"{c.Key}: applied={c.Value.AppliedRevision}, error={DiagnosticText.Safe(c.Value.Error)}"))}.", "Open Config and correct the indicated settings in the saved configuration file. Save and verify that all components acknowledge the accepted revision.", Target: "config"), state.State == "error", ct);
        }
        await InspectConfigurationAsync(services, ct);
        if (services.GetService<IConnectivityService>() is { LastChecked: not null } network)
            await Observe(new("network.internet", "system", "Internet connectivity is unavailable", $"Last connectivity check={network.LastChecked:O}; connected={network.IsConnected}. {network.FailureReason ?? "Client reachability and configured HTTP/TCP probes did not establish connectivity; no further probe detail was supplied."}", "Check the host network, DNS and configured connectivity hosts."), !network.IsConnected, ct);
        if (services.GetService<ITorrentClientRegistry>() is { } clients)
            foreach (var (id, cfg) in config.GetAllTorrentClients().Where(p => !p.Value.Disabled && !string.IsNullOrWhiteSpace(p.Value.Host) && p.Value.Host != "CHANGE_ME"))
            {
                var reachable = false;
                var evidence = $"No registered session for '{id}'. " + ((clients as QBittorrentConnectionManager)?.GetConnectionErrors().GetValueOrDefault(id) ?? "No connection failure detail was recorded.");
                try
                {
                    if (clients.GetClient(id) is { } client)
                    {
                        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                        timeout.CancelAfter(TimeSpan.FromSeconds(5));
                        var version = await client.GetVersionAsync(timeout.Token);
                        reachable = !string.IsNullOrWhiteSpace(version);
                        evidence = reachable ? $"Version probe succeeded: {version}" : "Version probe returned an empty response.";
                    }
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                catch (Exception ex) { evidence = DiagnosticText.Error(ex); logger.LogWarning("Torrent client connectivity check failed for {Instance}: {Evidence}", id, evidence); }
                await Observe(new("client.connection", "system", "Torrent client is disconnected", $"Client={id}; type={cfg.Type}; endpoint={DiagnosticText.Safe(cfg.Host)}:{cfg.Port}; operation=version probe; {evidence} Torrent management and ownership inspection are incomplete.", "Check the client address, credentials and network.", ClientInstance: id, Target: "config"), !reachable, ct);
            }
        if (services.GetService<WorkerProcessSupervisor>() is { } workers)
            foreach (var (id, state) in await workers.GetProcessStatusAsync())
            {
                var healthy = HasHealthyProgress(state, DateTimeOffset.UtcNow);
                var issue = new OperationalIssueReport("worker.lifecycle", "system", "Worker is not making healthy progress", $"Worker={id}; PID={state.ProcessId}; alive={state.IsAlive}; status={state.Status}; last exit={state.LastExitCode}; last failure={state.LastFailure ?? "none recorded"}; restarts={state.RestartCount}; started={state.StartTime:O}; heartbeat={state.LastHeartbeat:O}; config revision={state.ConfigRevision}. Torrent loop={state.Torrent?.Phase ?? "unavailable"}, last success={state.Torrent?.LastSuccess:O}; search loop={state.Search?.Phase ?? "unavailable"}, last success={state.Search?.LastSuccess:O}.", "Check worker logs and connection settings; restart after correcting the cause.", ArrInstance: id, Target: "processes");
                if (!state.IsAlive) await issues.ReportAsync(issue, ct); else if (healthy) await issues.ResolveAsync(issue, ct);
            }
        if (services.GetService<IFreeSpaceService>() is { } storage)
            foreach (var disk in await storage.GetFreeSpaceStatsAsync(ct))
                await Observe(new("storage.reserve", "system", "Download storage needs attention", $"Client={disk.ClientInstanceId}; path={disk.Path}; free={disk.FreeBytes} bytes; reserve={disk.ThresholdBytes} bytes; total={disk.TotalBytes} bytes; below threshold={disk.BelowThreshold}; inspection error={disk.Error ?? "none"}. Downloads may remain paused until the reserve is restored.", "Restore access to the monitored directory or free space above its configured reserve.", ClientInstance: disk.ClientInstanceId, SubjectType: "storage", SubjectId: disk.Path, Severity: "warning", Target: "config"), disk.Error != null || disk.BelowThreshold, ct);
        if (services.GetService<TorrentAutomationService>() is { } automation)
        {
            var state = automation.GetStatus();
            var current = new List<OperationalIssueReport>();
            foreach (var tracker in state.TrackerIssues)
                current.Add(new("automation.tracker", "automation", "Tracker reports a torrent problem", $"Torrent={tracker.TorrentName}; hash={tracker.TorrentHash}; client={tracker.ClientInstanceId}; added={tracker.AddedOn}; {tracker.Reason}", "Inspect tracker status and protection settings before taking action.", ClientInstance: tracker.ClientInstanceId, SubjectType: "torrent", SubjectId: tracker.TorrentHash, Severity: "warning", Target: "maintenance"));
            foreach (var recovery in state.Recovery.Where(r => r.Stage is not ("new" or "checked")))
                current.Add(new("automation.recovery", "automation", "Torrent recovery is pending", $"Torrent={recovery.TorrentName}; hash={recovery.TorrentHash}; operation={recovery.Operation}; stage={recovery.Stage}; attempts={recovery.Attempts}; failure since={recovery.FailureSince:O}; {recovery.Reason}", "Inspect the recovery status and storage access; do not remove protection evidence.", ClientInstance: recovery.ClientInstanceId, SubjectType: "torrent", SubjectId: recovery.TorrentHash, Severity: "warning", Target: "maintenance"));
            foreach (var group in state.Protection.Where(p => p.NativeStopLimitRisk || p.DeadlineRisk is "warning" or "critical" or "unachievable").GroupBy(p => new { p.ClientInstanceId, p.TorrentHash }))
                current.Add(new("automation.protection", "automation", "Tracker obligation may be at risk", $"Hash={group.Key.TorrentHash}; {string.Join("; ", group.Select(p => $"{p.TorrentName}: tracker={p.Tracker}, state={p.State}, {p.SeedAccountingDescription()}, deadline={p.Deadline:O}, recovery deadline={p.LatestRecoveryStart:O}, risk={p.DeadlineRisk}, native stop risk={p.NativeStopLimitRisk}. {p.Reason}"))}", "Review tracker protection and seeding limits in Manage; preserve the torrent while obligations remain.", ClientInstance: group.Key.ClientInstanceId, SubjectType: "torrent", SubjectId: group.Key.TorrentHash,
                    Severity: group.Any(p => p.DeadlineRisk is "critical" or "unachievable") ? "error" : "warning", Target: "maintenance"));
            foreach (var inspection in state.Inspections)
                if (inspection.ConsecutiveFailures > 0 || inspection.LastSucceededAt.HasValue)
                    await Observe(new("automation.inspection", "system", "Torrent protection inspection is unavailable", $"Client={inspection.ClientInstanceId}; consecutive inspection failures={inspection.ConsecutiveFailures}; last success={inspection.LastSucceededAt:O}; next attempt={inspection.NextAttempt:O}. Previous protection evidence is retained. Failure={inspection.LastError ?? "No failure detail supplied by this inspection implementation."}", "Check client connectivity and tracker access before retrying.", ClientInstance: inspection.ClientInstanceId, Target: "maintenance"), inspection.ConsecutiveFailures > 0, ct);
            foreach (var issue in current) await issues.ReportAsync(issue, ct);
            // Clear only clients whose latest complete protection inspection succeeded.
            var inspected = state.Inspections.Where(i => i.ConsecutiveFailures == 0 && i.LastSucceededAt.HasValue).Select(i => i.ClientInstanceId).ToList();
            var db = services.GetRequiredService<TorrentarrDbContext>();
            var previous = await db.OperationalIssues.AsNoTracking().Where(i => i.ResolvedAt == null && inspected.Contains(i.ClientInstance) && (i.Code == "automation.tracker" || i.Code == "automation.recovery" || i.Code == "automation.protection")).ToListAsync(ct);
            foreach (var old in previous.Where(i => state.Inspections.Any(s => s.ClientInstanceId == i.ClientInstance && s.LastSucceededAt > new DateTimeOffset(i.LastObserved, TimeSpan.Zero)) && !current.Any(r => r.Code == i.Code && r.ClientInstance == i.ClientInstance && r.SubjectId == i.SubjectId)))
                await issues.ResolveAsync(new(old.Code, old.Category, old.Title, old.Impact, old.Remediation, old.ArrInstance, old.ClientInstance, old.SubjectType, old.SubjectId, old.Severity, old.Target), ct);
        }
        if (services.GetService<IMaintenanceCoordinator>() is { } maintenance)
        {
            var history = maintenance.GetHistory().Where(r => r.CompletedAt.HasValue && r.Result is "success" or "partial" or "failed")
                .OrderBy(run => run.CompletedAt)
                .ThenBy(run => run.StartedAt)
                .Select(run => (Scope: System.Text.Json.JsonSerializer.Serialize(run.ClientInstanceIds.Select(id => id.ToUpperInvariant()).Distinct().Order()), Run: run)).ToArray();
            // Seed completion cursors before reporting: reporting writes a later wall-clock timestamp.
            if (services.GetService<TorrentarrDbContext>() is { } historyDb)
                foreach (var group in history.GroupBy(item => item.Scope).Where(group => !_maintenanceRuns.ContainsKey(group.Key)))
                {
                    var previous = await historyDb.OperationalIssues.AsNoTracking().SingleOrDefaultAsync(i =>
                        i.Code == "maintenance.run" && i.SubjectType == "maintenance" && i.SubjectId == group.Key
                        && i.ClientInstance == "" && i.ArrInstance == "", ct);
                    if (previous == null) continue;
                    var observedRuns = group.Where(item => item.Run.CompletedAt!.Value.UtcDateTime <= previous.LastObserved).ToArray();
                    if (observedRuns.Length == 0) continue;
                    var last = observedRuns[^1].Run;
                    var ids = observedRuns.Where(item => item.Run.CompletedAt == last.CompletedAt).Select(item => item.Run.RunId).ToHashSet();
                    // Recover an old active issue even when its report timestamp followed the successful run.
                    if (last.Result == "success" && previous.ResolvedAt == null) ids.Remove(last.RunId);
                    _maintenanceRuns[group.Key] = (last.CompletedAt!.Value, ids);
                }
            foreach (var latest in history)
            {
                var completedAt = latest.Run.CompletedAt!.Value;
                if (_maintenanceRuns.TryGetValue(latest.Scope, out var observed)
                    && (completedAt < observed.CompletedAt
                        || completedAt == observed.CompletedAt && observed.RunIds.Contains(latest.Run.RunId)))
                    continue;
                // History has aggregate counts, so report once for the selected set rather than blaming each client.
                var failureSummary = string.Join("; ", latest.Run.Errors.Distinct().Take(3));
                var issue = new OperationalIssueReport("maintenance.run", "system", "Maintenance execution failed",
                    latest.Run.Failed > 0
                        ? $"{latest.Run.Failed} action(s) failed for {string.Join(", ", latest.Run.ClientInstanceIds)}. {failureSummary}"
                        : "The selected maintenance run completed without failed actions.",
                    "Open Manage → Recent runs and expand the failed run to inspect each action, client and error before retrying.",
                    SubjectType: "maintenance", SubjectId: latest.Scope, Target: "maintenance");
                if (latest.Run.Result == "success" || latest.Run.Failed > 0)
                    await Observe(issue, latest.Run.Failed > 0, ct);
                // A verified successful run can also close records written by the former per-client reporter.
                if (latest.Run.Result == "success")
                    foreach (var client in latest.Run.ClientInstanceIds)
                        await issues.ResolveAsync(issue with { ClientInstance = client, SubjectType = "service", SubjectId = "" }, ct);
                if (_maintenanceRuns.TryGetValue(latest.Scope, out observed) && completedAt == observed.CompletedAt)
                    observed.RunIds.Add(latest.Run.RunId);
                else
                    _maintenanceRuns[latest.Scope] = (completedAt, new HashSet<string> { latest.Run.RunId });
            }
        }
    }

    private async Task InspectConfigurationAsync(IServiceProvider services, CancellationToken ct)
    {
        var current = new List<OperationalIssueReport>();
        var validation = ConfigValidationHelper.ValidateAll(config);
        if (!validation.Ok)
            current.Add(new("config.validation", "system", "Configuration needs correction",
                DiagnosticText.Safe(validation.Error), "Open Config and correct the indicated setting, then save and verify configuration activation.", Target: "config"));

        var clients = config.GetAllTorrentClients();
        var recycling = clients.Values.Any(c => !c.Disabled && c.Maintenance?.RecycleBin?.Enabled == true);
        var paths = services.GetService<IPathMappingService>();
        foreach (var (id, client) in clients)
        {
            if (!client.Disabled && (string.IsNullOrWhiteSpace(client.Type) || string.IsNullOrWhiteSpace(client.Host)
                || client.Host == "CHANGE_ME" || client.Port is < 1 or > 65535))
                current.Add(new("config.client", "system", "Torrent client configuration is incomplete",
                    "An enabled torrent client requires a Type, Host and port between 1 and 65535.",
                    "Open Config and complete this client's connection settings, or disable/remove an unused client.", ClientInstance: id, Target: "config"));
            if (client.Migration?.Notices is { Count: > 0 } notices)
                current.Add(new("config.migration", "system", "Migrated configuration needs review",
                    DiagnosticText.Safe(string.Join("; ", notices)),
                    "Open Config and review this client's migrated DownloadPath, FreeSpace and Maintenance settings. Verify ownership and local mounts before enabling protection. After reviewing and correcting the settings, clear the corresponding Migration.Notices entries in config.toml.",
                    ClientInstance: id, Severity: "warning", Target: "config"));
            if (client.Maintenance?.PathMappings is not { } mappings) continue;
            var errors = new List<string>();
            if (recycling && mappings.Count == 0 && !string.IsNullOrWhiteSpace(client.Host) && client.Host != "CHANGE_ME")
                errors.Add("Maintenance.PathMappings is missing. Recycling requires a complete file ownership inventory across all configured clients, even when client and local paths are identical.");
            if (paths != null) errors.AddRange(paths.Validate(mappings));
            if (errors.Count > 0)
                current.Add(new("config.storage", "system", "Client storage configuration needs correction",
                    DiagnosticText.Safe(string.Join("; ", errors)),
                    "Open Config and configure this client's Maintenance.PathMappings: ClientPath is visible to the torrent client; LocalPath is the same folder inside Torrentarr. Verify mounts and permissions, then save.",
                    ClientInstance: id, Severity: "warning", Target: "config"));
        }

        var previous = _configurationIssues.AsEnumerable();
        if (services.GetService<TorrentarrDbContext>() is { } db)
        {
            var persisted = await db.OperationalIssues.AsNoTracking().Where(i => i.ResolvedAt == null
                && (i.Code == "config.validation" || i.Code == "config.migration" || i.Code == "config.storage" || i.Code == "config.client")).ToListAsync(ct);
            previous = previous.Concat(persisted.Select(i => new OperationalIssueReport(i.Code, i.Category, i.Title,
                i.Impact, i.Remediation, i.ArrInstance, i.ClientInstance, i.SubjectType, i.SubjectId, i.Severity, i.Target)));
        }
        foreach (var old in previous.DistinctBy(i => (i.Code, i.ClientInstance)))
            if (!current.Any(i => i.Code == old.Code && i.ClientInstance == old.ClientInstance))
                await issues.ResolveAsync(old, ct);
        foreach (var issue in current) await issues.ReportAsync(issue, ct);
        _configurationIssues = current;
    }

    private Task Observe(OperationalIssueReport issue, bool failed, CancellationToken ct)
        => failed ? issues.ReportAsync(issue, ct) : issues.ResolveAsync(issue, ct);

    internal static bool HasHealthyProgress(ProcessStatus state, DateTimeOffset now)
        => state.IsAlive && state.LastHeartbeat is { } heartbeat && now - heartbeat < TimeSpan.FromSeconds(15)
            && Healthy(state.Torrent, now) && Healthy(state.Search, now);

    private static bool Healthy(Torrentarr.Core.Models.WorkerLoopProgress? loop, DateTimeOffset now)
        => loop != null && !loop.IsStalled(now) && loop.Phase is "Running" or "Sleeping" or "Disabled";
}

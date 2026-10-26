using System.Collections.Concurrent;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.EntityFrameworkCore;
using Torrentarr.Core.Configuration;
using Torrentarr.Core.Interfaces;
using Torrentarr.Core.Models;
using Torrentarr.Core.Services;
using Torrentarr.Infrastructure.Database;

namespace Torrentarr.Infrastructure.Services;

public sealed class TorrentAutomationService(TorrentarrConfig config, ITorrentClientRegistry registry,
    IServiceScopeFactory scopes, ILogger<TorrentAutomationService> logger, IConfigReloader? reloader = null) : BackgroundService
{
    private readonly TorrentarrConfig _config = config;
    private readonly ITorrentClientRegistry _registry = registry;
    private readonly IServiceScopeFactory _scopes = scopes;
    private readonly ILogger<TorrentAutomationService> _logger = logger;
    private readonly ConcurrentDictionary<string, List<ProtectionDecision>> _protection = new();
    private readonly ConcurrentDictionary<string, DateTimeOffset> _nextInspection = new();
    private readonly ConcurrentDictionary<string, int> _inspectionFailures = new();
    private readonly ConcurrentDictionary<string, (DateTimeOffset At, List<TorrentTracker> Trackers)> _trackerInspections = new();
    private readonly ConcurrentDictionary<string, DateTimeOffset> _nextFullInspection = new();
    private readonly ConcurrentDictionary<string, DateTimeOffset> _lastInspection = new();
    private long _inspectionRevision = -1;
    private long _appliedRevision;

    public TorrentAutomationStatus GetStatus()
    {
        using var scope = _scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TorrentarrDbContext>();
        var recovery = db.MaintenanceState.AsNoTracking().Where(s => s.Key.EndsWith("|recovery:ErrorRecovery")).ToList()
            .Select(s => (Parts: s.Key.Split('|'), State: Newtonsoft.Json.JsonConvert.DeserializeObject<RecoveryIncident>(s.Data)!))
            .Where(s => s.State.Stage != "new" || s.State.Reason.Length > 0)
            .Select(s => new MaintenanceRecoveryStatus
            {
                ClientInstanceId = _config.GetAllTorrentClients().Keys.FirstOrDefault(id => id.Equals(s.Parts[0], StringComparison.OrdinalIgnoreCase)) ?? s.Parts[0],
                TorrentHash = s.Parts[1],
                TorrentName = s.State.TorrentName,
                Stage = s.State.Stage,
                Attempts = s.State.Attempts,
                FailureSince = s.State.FailureSince,
                Operation = MaintenanceOperation.ErrorRecovery,
                Reason = s.State.Reason.Length > 0 ? s.State.Reason : "Keep and report. Unresolved recovery never authorizes removal."
            }).ToList();
        var trackerIssues = db.MaintenanceState.AsNoTracking().Where(s => s.Key.EndsWith("|tracker-issue")).ToList()
            .Select(s => Newtonsoft.Json.JsonConvert.DeserializeObject<TorrentAutomationIssue>(s.Data) ?? throw new InvalidDataException("Invalid tracker issue evidence.")).ToList();
        return new()
        {
            TrackerIssues = trackerIssues,
            Protection = _protection.Values.SelectMany(decisions => decisions).ToList(),
            Recovery = recovery,
            Inspections = _config.GetAllTorrentClients().Keys.Select(id => new MaintenanceInspectionStatus
            {
                ClientInstanceId = id,
                LastSucceededAt = _lastInspection.TryGetValue(id, out var at) ? at : null,
                NextAttempt = _nextInspection.TryGetValue(id, out var next) ? next : null,
                ConsecutiveFailures = _inspectionFailures.GetValueOrDefault(id)
            }).ToList()
        };
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try { await InspectProtectionAsync(ct); reloader?.Acknowledge("torrent-protection", _appliedRevision); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
            catch (Exception ex) { _logger.LogError(ex, "Protection inspection failed; protection remains conservative"); }
            try { await Task.Delay(TimeSpan.FromSeconds(15), ct); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
        }
    }

    private static HashSet<string> SplitTags(string tags) => tags.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToHashSet(StringComparer.OrdinalIgnoreCase);
    private async Task InspectProtectionAsync(CancellationToken ct)
    {
        using var configuration = _config.BeginOperation();
        using var processing = ConfigMutationGuard.EnterProcessing();
        if (_inspectionRevision != _config.Revision)
        { _nextInspection.Clear(); _nextFullInspection.Clear(); _trackerInspections.Clear(); _inspectionRevision = _config.Revision; }
        foreach (var (id, cfg) in _config.GetAllTorrentClients())
        {
            var client = _registry.GetClient(id);
            if (cfg.Disabled || client == null || _nextInspection.GetValueOrDefault(id) > DateTimeOffset.UtcNow) continue;
            var inspectionStarted = DateTimeOffset.UtcNow;
            try
            {
                using var scope = _scopes.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<TorrentarrDbContext>();
                var seeding = scope.ServiceProvider.GetRequiredService<ISeedingService>() as SeedingService;
                var urgent = _protection.Values.SelectMany(decisions => decisions).Any(d => d.ClientInstanceId == id && (d.DeadlineRisk is "warning" or "critical" or "unachievable" || d.NativeStopLimitRisk));
                var full = _nextFullInspection.GetValueOrDefault(id) <= DateTimeOffset.UtcNow;
                using var inspection = CancellationTokenSource.CreateLinkedTokenSource(ct);
                inspection.CancelAfter(TimeSpan.FromSeconds(55));
                var inspectionCt = inspection.Token;
                var torrents = await client.GetTorrentsAsync(cancellationToken: inspectionCt);
                var allHashes = torrents.Select(t => t.Hash).ToHashSet(StringComparer.OrdinalIgnoreCase);
                if (!full) torrents = torrents.Where(t => _protection.Values.SelectMany(decisions => decisions).Any(d => d.ClientInstanceId == id && d.TorrentHash == t.Hash
                    && (d.DeadlineRisk is "warning" or "critical" or "unachievable" || d.NativeStopLimitRisk))).ToList();
                torrents = torrents.OrderBy(t => _trackerInspections.GetValueOrDefault(id + "|" + t.Hash).At).ThenByDescending(t => _protection.Values.SelectMany(decisions => decisions).Any(d => d.ClientInstanceId == id && d.TorrentHash == t.Hash && d.DeadlineRisk != "none"))
                    .ThenByDescending(t => cfg.Maintenance.ErrorRecovery.States.Contains(t.State, StringComparer.OrdinalIgnoreCase)).ToList();
                var hashes = allHashes;
                foreach (var entry in _protection.Where(p => p.Value.Any(d => d.ClientInstanceId == id && !hashes.Contains(d.TorrentHash))).ToList()) _protection.TryRemove(entry.Key, out _);
                using var budget = new SemaphoreSlim(2);
                using var observations = new SemaphoreSlim(1);
                await Task.WhenAll(torrents.Select(async torrent =>
                {
                    torrent.ClientInstanceId = id;
                    var key = id + "|" + torrent.Hash;
                    var fresh = cfg.Trackers.Any(t => t.Protection != null) || _config.ArrInstances.Values.Any(a => CategoryOwnershipHelper.ArrOwnsTorrent(_config, a, torrent) && a.Torrent.Trackers.Any(t => t.Protection != null)) || cfg.Maintenance.ErrorRecovery.States.Contains(torrent.State, StringComparer.OrdinalIgnoreCase)
                        || _protection.Values.SelectMany(decisions => decisions).Any(d => d.ClientInstanceId == id && d.TorrentHash == torrent.Hash && d.DeadlineRisk != "none");
                    await budget.WaitAsync(inspectionCt);
                    try
                    {
                        List<TorrentTracker> trackers;
                        if (!fresh && _trackerInspections.TryGetValue(key, out var cached) && DateTimeOffset.UtcNow - cached.At < TimeSpan.FromMinutes(15))
                            trackers = cached.Trackers;
                        else
                        {
                            if (!torrent.IsPrivate.HasValue) torrent.IsPrivate = (await client.GetTorrentPropertiesAsync(torrent.Hash, inspectionCt))?.IsPrivate;
                            trackers = await client.GetTorrentTrackersAsync(torrent.Hash, inspectionCt);
                        }
                        // Persist completed observations before a slow or cancelled fetch ends the sweep.
                        await observations.WaitAsync(inspectionCt);
                        try
                        {
                            foreach (var operation in new[] { MaintenanceOperation.ErrorRecovery, MaintenanceOperation.UnregisteredCleanup, MaintenanceOperation.RepairPaused })
                            {
                                var failed = operation == MaintenanceOperation.ErrorRecovery ? MaintenanceRecovery.IsError(torrent, cfg.Maintenance)
                                    : operation == MaintenanceOperation.UnregisteredCleanup && trackers.Any(t => cfg.Maintenance.Unregistered.Messages.Any(m => t.Msg.Contains(m, StringComparison.OrdinalIgnoreCase)));
                                if (failed || MaintenanceRecovery.Read(db, torrent, operation.ToString()).Stage != "new")
                                    MaintenanceRecovery.Observe(db, torrent, operation.ToString(), failed, cfg.Maintenance.ErrorRecovery, DateTimeOffset.UtcNow);
                            }

                            if (seeding != null)
                            {
                                if (_config.Revision != _config.LatestRevision) throw new OperationCanceledException("Configuration changed during inspection.");
                                await seeding.ReconcileProtectionLimitsAsync(torrent, trackers, inspectionCt);
                            }
                            if (seeding != null)
                            {
                                var decisions = seeding.GetProtectionDecisions(torrent, trackers, observe: true);
                                foreach (var decision in decisions)
                                {
                                    if (decision.State != "eligible" && (torrent.IsStopped || torrent.State.Contains("paused", StringComparison.OrdinalIgnoreCase)))
                                    {
                                        var owner = MaintenancePersistence.Read<PauseOwnership>(db, MaintenancePersistence.Key(id, torrent.Hash, "routine-pause"));
                                        if (!owner.Owned || owner.AddedOn != torrent.AddedOn) decision.Reason += " User/unknown pause ownership blocks automatic resume.";
                                    }
                                    urgent |= decision.DeadlineRisk is "warning" or "critical" or "unachievable" || decision.NativeStopLimitRisk;
                                }
                                _protection[key] = decisions;
                            }
                            if ((cfg.Trackers.Any(t => t.Protection != null) || _config.ArrInstances.Values.Any(a => CategoryOwnershipHelper.ArrOwnsTorrent(_config, a, torrent) && a.Torrent.Trackers.Any(t => t.Protection != null))) && torrent.Progress >= 1 && (torrent.IsStopped || torrent.State.Contains("paused", StringComparison.OrdinalIgnoreCase))
                                && !SplitTags(torrent.Tags).Contains("qBitrr-free_space_paused") && !SplitTags(torrent.Tags).Contains("qBitrr-ignored"))
                            {
                                var pauseKey = MaintenancePersistence.Key(id, torrent.Hash, "routine-pause");
                                var pause = MaintenancePersistence.Read<PauseOwnership>(db, pauseKey);
                                if (pause.Owned && pause.AddedOn == torrent.AddedOn
                                    && _protection.Values.SelectMany(decisions => decisions).Any(d => d.ClientInstanceId == id && d.TorrentHash == torrent.Hash && d.State != "eligible")
                                    && await client.ResumeTorrentsAsync([torrent.Hash], inspectionCt))
                                    MaintenancePersistence.Write(db, pauseKey, new PauseOwnership());
                            }
                            _trackerInspections[key] = (DateTimeOffset.UtcNow, trackers);
                        }
                        finally { observations.Release(); }
                    }
                    finally { budget.Release(); }
                }));
                if (full) _nextFullInspection[id] = inspectionStarted.AddMinutes(5);
                _lastInspection[id] = DateTimeOffset.UtcNow;
                _inspectionFailures[id] = 0;
                _nextInspection[id] = inspectionStarted.AddMinutes(urgent ? 1 : 5);
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                var failures = _inspectionFailures.AddOrUpdate(id, 1, (_, n) => Math.Min(6, n + 1));
                _nextInspection[id] = DateTimeOffset.UtcNow.AddMinutes(Math.Min(30, Math.Pow(2, failures - 1)));
                _logger.LogWarning(ex, "Protection inspection delayed for {Instance}; existing protection retained", id);
            }
        }
        _appliedRevision = _config.Revision;
    }

}

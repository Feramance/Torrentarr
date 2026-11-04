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
    private readonly ConcurrentDictionary<string, string> _inspectionErrors = new();
    private readonly ConcurrentDictionary<string, (DateTimeOffset At, List<TorrentTracker> Trackers, bool? IsPrivate)> _trackerInspections = new();
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
                ConsecutiveFailures = _inspectionFailures.GetValueOrDefault(id),
                LastError = _inspectionErrors.GetValueOrDefault(id)
            }).ToList()
        };
    }

    internal TimeProvider Clock { get; set; } = TimeProvider.System;

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try { await InspectProtectionAsync(ct); reloader?.Acknowledge("torrent-protection", _appliedRevision); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
            catch (Exception ex) { _logger.LogError(ex, "Protection inspection failed; protection remains conservative"); }
            try { await Task.Delay(TimeSpan.FromSeconds(5), Clock, ct); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
        }
    }

    private static HashSet<string> SplitTags(string tags) => tags.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToHashSet(StringComparer.OrdinalIgnoreCase);
    internal async Task InspectProtectionAsync(CancellationToken ct)
    {
        using var configuration = _config.BeginOperation();
        using var processing = ConfigMutationGuard.EnterProcessing();
        if (_inspectionRevision != _config.Revision)
        { _nextInspection.Clear(); _nextFullInspection.Clear(); _trackerInspections.Clear(); _inspectionRevision = _config.Revision; }
        foreach (var (id, cfg) in _config.GetAllTorrentClients())
        {
            var client = _registry.GetClient(id);
            if (cfg.Disabled || client == null || _nextInspection.GetValueOrDefault(id) > Clock.GetUtcNow())
            {
                _logger.LogTrace("Protection inspection deferred for {ClientInstance}: disabled={Disabled}; connected={Connected}; nextAttempt={NextAttempt:O}", id, cfg.Disabled, client != null, _nextInspection.GetValueOrDefault(id));
                continue;
            }
            using var inspectionScope = _logger.BeginScope(new Dictionary<string, object> { ["Operation"] = "ProtectionInspection", ["ClientInstance"] = id, ["CycleId"] = Guid.NewGuid().ToString("N") });
            var started = System.Diagnostics.Stopwatch.GetTimestamp();
            _logger.LogInformation("Protection inspection started for {ClientInstance}", id);
            var inspectionStarted = Clock.GetUtcNow();
            try
            {
                using var scope = _scopes.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<TorrentarrDbContext>();
                var seeding = scope.ServiceProvider.GetRequiredService<ISeedingService>() as SeedingService;
                var urgent = _protection.Values.SelectMany(decisions => decisions).Any(d => d.ClientInstanceId == id && NeedsFreshInspection(d));
                var full = _nextFullInspection.GetValueOrDefault(id) <= Clock.GetUtcNow();
                using var inspection = CancellationTokenSource.CreateLinkedTokenSource(ct);
                inspection.CancelAfter(TimeSpan.FromSeconds(55));
                var inspectionCt = inspection.Token;
                var torrents = await client.GetTorrentsAsync(cancellationToken: inspectionCt);
                var allHashes = torrents.Select(t => t.Hash).ToHashSet(StringComparer.OrdinalIgnoreCase);
                if (!full) torrents = torrents.Where(t => _protection.Values.SelectMany(decisions => decisions).Any(d => d.ClientInstanceId == id && d.TorrentHash == t.Hash
                    && NeedsFreshInspection(d))).ToList();
                torrents = torrents.OrderBy(t => _trackerInspections.GetValueOrDefault(id + "|" + t.Hash).At).ThenByDescending(t => _protection.Values.SelectMany(decisions => decisions).Any(d => d.ClientInstanceId == id && d.TorrentHash == t.Hash && d.DeadlineRisk != "none"))
                    .ThenByDescending(t => cfg.Maintenance.ErrorRecovery.States.Contains(t.State, StringComparer.OrdinalIgnoreCase)).ToList();
                var hashes = allHashes;
                foreach (var entry in _protection.Where(p => p.Value.Any(d => d.ClientInstanceId == id && !hashes.Contains(d.TorrentHash))).ToList()) _protection.TryRemove(entry.Key, out _);
                var prefix = id.ToUpperInvariant() + "|";
                var oldTrackerIssues = await db.MaintenanceState.Where(s => s.Key.StartsWith(prefix) && s.Key.EndsWith("|tracker-issue")).ToListAsync(inspectionCt);
                var removed = oldTrackerIssues.Where(s => !allHashes.Contains(s.Key.Split('|')[1])).ToList();
                if (removed.Count > 0)
                {
                    db.MaintenanceState.RemoveRange(removed);
                    await db.SaveChangesAsync(inspectionCt);
                }
                using var budget = new SemaphoreSlim(2);
                using var observations = new SemaphoreSlim(1);
                await Task.WhenAll(torrents.Select(async torrent =>
                {
                    torrent.ClientInstanceId = id;
                    var key = id + "|" + torrent.Hash;
                    var fresh = cfg.Trackers.Any(t => t.Protection != null) || _config.ArrInstances.Values.Any(a => CategoryOwnershipHelper.ArrOwnsTorrent(_config, a, torrent) && a.Torrent.Trackers.Any(t => t.Protection != null)) || cfg.Maintenance.ErrorRecovery.States.Contains(torrent.State, StringComparer.OrdinalIgnoreCase)
                        || _protection.Values.SelectMany(decisions => decisions).Any(d => d.ClientInstanceId == id && d.TorrentHash == torrent.Hash && NeedsFreshInspection(d));
                    await budget.WaitAsync(inspectionCt);
                    try
                    {
                        List<TorrentTracker> trackers;
                        var fetchedAt = Clock.GetUtcNow();
                        if (!fresh && _trackerInspections.TryGetValue(key, out var cached) && Clock.GetUtcNow() - cached.At < TimeSpan.FromMinutes(15))
                        {
                            _logger.LogTrace("Protection tracker cache hit for {Hash}: fetchedAt={FetchedAt:O}; expiresAt={ExpiresAt:O}", torrent.Hash, cached.At, cached.At.AddMinutes(15));
                            trackers = cached.Trackers;
                            torrent.IsPrivate ??= cached.IsPrivate;
                            fetchedAt = cached.At;
                        }
                        else
                        {
                            _logger.LogTrace("Protection tracker observation requested for {Hash}: freshRequired={FreshRequired}; privacyKnown={PrivacyKnown}", torrent.Hash, fresh, torrent.IsPrivate.HasValue);
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
                                    MaintenanceRecovery.Observe(db, torrent, operation.ToString(), failed, cfg.Maintenance.ErrorRecovery, Clock.GetUtcNow());
                            }

                            if (seeding != null)
                            {
                                if (_config.Revision != _config.LatestRevision) throw new OperationCanceledException("Configuration changed during inspection.");
                                await seeding.ReconcileProtectionLimitsAsync(torrent, trackers, inspectionCt);
                            }
                            if (seeding != null)
                            {
                                await seeding.ObserveTrackerIssueAsync(torrent, trackers, inspectionCt);
                                var decisions = seeding.GetProtectionDecisions(torrent, trackers, observe: true);
                                foreach (var decision in decisions)
                                {
                                    if (decision.State != "eligible" && (torrent.IsStopped || torrent.State.Contains("paused", StringComparison.OrdinalIgnoreCase)))
                                    {
                                        var owner = MaintenancePersistence.Read<PauseOwnership>(db, MaintenancePersistence.Key(id, torrent.Hash, "routine-pause"));
                                        if (!owner.Owned || owner.AddedOn != torrent.AddedOn) decision.Reason += " User/unknown pause ownership blocks automatic resume.";
                                    }
                                    urgent |= NeedsFreshInspection(decision);
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
                            _trackerInspections[key] = (fetchedAt, trackers, torrent.IsPrivate);
                        }
                        finally { observations.Release(); }
                    }
                    finally { budget.Release(); }
                }));
                _lastInspection[id] = Clock.GetUtcNow();
                _inspectionFailures[id] = 0;
                _inspectionErrors.TryRemove(id, out _);
                var interval = TimeSpan.FromSeconds(cfg.TorrentScanIntervalSeconds);
                var elapsed = Clock.GetUtcNow() - inspectionStarted;
                _nextInspection[id] = inspectionStarted + interval * (elapsed.Ticks / interval.Ticks + 1);
                _nextFullInspection[id] = _nextInspection[id];
                _logger.LogInformation("Protection inspection completed: full={Full}; inventory={InventoryCount}; inspected={InspectedCount}; urgent={Urgent}; elapsed={ElapsedMs:F1}ms; nextAttempt={NextAttempt:O}",
                    full, allHashes.Count, torrents.Count, urgent, System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds, _nextInspection[id]);
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                _inspectionErrors[id] = DiagnosticText.Error(ex);
                var failures = _inspectionFailures.AddOrUpdate(id, 1, (_, n) => Math.Min(6, n + 1));
                _nextInspection[id] = Clock.GetUtcNow().AddMinutes(Math.Min(30, Math.Pow(2, failures - 1)));
                _logger.LogWarning("Protection inspection delayed for {Instance}; existing protection retained. {Error}", id, _inspectionErrors[id]);
            }
        }
        _appliedRevision = _config.Revision;
    }

    internal static bool NeedsFreshInspection(ProtectionDecision decision)
        => decision.State == "unknown" || decision.DeadlineRisk is "warning" or "critical" or "unachievable" || decision.NativeStopLimitRisk;

}

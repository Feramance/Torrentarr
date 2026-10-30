using System.Collections.Concurrent;
using System.IO.Enumeration;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using Microsoft.EntityFrameworkCore;
using Torrentarr.Infrastructure.Database;
using Torrentarr.Core.Configuration;
using Torrentarr.Core.Interfaces;
using Torrentarr.Core.Models;
using Torrentarr.Core.Services;

namespace Torrentarr.Infrastructure.Services;

public sealed class MaintenanceCoordinator : BackgroundService, IMaintenanceCoordinator
{
    private readonly TorrentarrConfig _config;
    private readonly ITorrentClientRegistry _registry;
    private readonly ITorrentInventoryService _inventory;
    private readonly IPathMappingService _paths;
    private readonly IHardlinkInspector _hardlinks;
    private readonly ISafeDeletionService _deletion;
    private readonly ILogger<MaintenanceCoordinator> _logger;
    private readonly IServiceScopeFactory _scopes;
    private readonly IConfigReloader? _reloader;
    private readonly ConcurrentDictionary<string, MaintenancePlan> _plans = new();
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _instanceLocks = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<MaintenanceRunSummary> _history = new();
    private readonly object _stateLock = new();
    private CancellationTokenSource? _activeCancellation;
    private MaintenanceRunSummary? _current;
    private MaintenanceRunSummary? _last;
    private readonly TorrentAutomationService? _automation;

    public MaintenanceCoordinator(
        TorrentarrConfig config,
        ITorrentClientRegistry registry,
        ITorrentInventoryService inventory,
        IPathMappingService paths,
        IHardlinkInspector hardlinks,
        ISafeDeletionService deletion,
        ILogger<MaintenanceCoordinator> logger, IServiceScopeFactory scopes, IConfigReloader? reloader = null, TorrentAutomationService? automation = null)
    {
        _config = config;
        _registry = registry;
        _inventory = inventory;
        _paths = paths;
        _hardlinks = hardlinks;
        _deletion = deletion;
        _logger = logger;
        _scopes = scopes;
        _reloader = reloader;
        _automation = automation;
        LoadHistory();
    }

    public static readonly MaintenanceOperation[] CleanupOperations = [MaintenanceOperation.OrphanScan, MaintenanceOperation.RecycleRetention];

    public MaintenanceStatus GetStatus()
    {
        var automation = _automation?.GetStatus();
        lock (_stateLock)
        {
            return new MaintenanceStatus
            {
                ConfigurationFingerprint = Fingerprint(),
                Operations = _config.GetAllTorrentClients().SelectMany(pair => Enum.GetValues<MaintenanceOperation>().Select(op => new MaintenanceOperationStatus
                {
                    ClientInstanceId = pair.Key,
                    Operation = op,
                    Enabled = MaintenanceRequirements.Enabled(op, pair.Value.Maintenance),
                    ExecutionMode = MaintenancePolicies.Mode(pair.Value.Maintenance, op),
                    Schedule = MaintenancePolicies.Schedule(pair.Value.Maintenance, op),
                    Authorized = CanRunAutomatically(pair.Key, op),
                    NextRun = CanRunAutomatically(pair.Key, op)
                        ? CronSchedule.Next(MaintenancePolicies.Schedule(pair.Value.Maintenance, op), DateTimeOffset.UtcNow)?.ToString("O") : null
                })).ToList(),
                Protection = automation?.Protection ?? [],
                Inspections = automation?.Inspections ?? [],
                Recovery = automation?.Recovery ?? [],
                Running = _current != null,
                CurrentRun = _current,
                LastRun = _last,
                Armed = _config.GetAllTorrentClients().ToDictionary(
                    pair => pair.Key, _ => false, StringComparer.OrdinalIgnoreCase),
                ArmedOperations = _config.GetAllTorrentClients().Keys.ToDictionary(id => id,
                    _ => new List<MaintenanceOperation>(), StringComparer.OrdinalIgnoreCase),
                NextScheduledRun = _config.GetAllTorrentClients().ToDictionary(
                    pair => pair.Key,
                    pair => pair.Value.Maintenance.Enabled && CanRunAutomatically(pair.Key)
                        ? CronSchedule.Next(pair.Value.Maintenance.Schedule, DateTimeOffset.UtcNow)?.ToString("O")
                        : null,
                    StringComparer.OrdinalIgnoreCase)
            };
        }
    }

    public async Task<MaintenancePlan> PreviewAsync(MaintenancePreviewRequest request, CancellationToken ct = default)
    {
        using var configuration = _config.BeginOperation();
        using var processing = ConfigMutationGuard.EnterProcessing();
        PrunePlans();
        using var scope = _scopes.CreateScope();
        var seeding = scope.ServiceProvider.GetRequiredService<ISeedingService>();
        var effective = _config;
        var inventoryService = _inventory;
        if (request.MaintenanceOverrides.Count > 0)
        {
            effective = JsonConvert.DeserializeObject<TorrentarrConfig>(JsonConvert.SerializeObject(_config))!;
            foreach (var (id, value) in request.MaintenanceOverrides)
            {
                if (!request.ClientInstanceIds.Contains(id, StringComparer.OrdinalIgnoreCase) || effective.GetTorrentClient(id) is not { } clientConfig)
                    throw new InvalidOperationException("Draft overrides must belong to selected configured clients.");
                var validation = MaintenanceConfigValidation.Validate(value);
                if (validation != null) throw new InvalidOperationException(validation);
                clientConfig.Maintenance = value;
            }
            inventoryService = new TorrentInventoryService(_registry, effective, _paths, scope.ServiceProvider.GetRequiredService<ILogger<TorrentInventoryService>>());
        }
        var allConfigs = effective.GetAllTorrentClients();
        var ids = request.ClientInstanceIds.Count > 0
            ? request.ClientInstanceIds.Distinct(StringComparer.OrdinalIgnoreCase).ToList()
            : allConfigs.Where(p => p.Value.Maintenance.Enabled).Select(p => p.Key).ToList();
        var operations = request.Operations.Count > 0 ? request.Operations.Distinct().ToList() : Enum.GetValues<MaintenanceOperation>().ToList();
        var plan = new MaintenancePlan
        {
            IsDraft = request.MaintenanceOverrides.Count > 0,
            ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(Math.Max(1, ids.Select(id => allConfigs.GetValueOrDefault(id)?.Maintenance.PlanTtlMinutes ?? 30).DefaultIfEmpty(30).Min())),
            ConfigurationFingerprint = Fingerprint(),
            ClientInstanceIds = ids,
            Operations = operations
        };
        foreach (var id in ids.Where(id => !allConfigs.ContainsKey(id)))
            MaintenanceDiagnostic.Add(plan.Diagnostics, "unknown-client", id, $"Unknown torrent client '{id}'.",
                "Select a configured torrent client and preview again.", severity: "error");
        if (operations.Any(op => !Enum.IsDefined(op)))
            MaintenanceDiagnostic.Add(plan.Diagnostics, "unknown-operation", "", "Unknown maintenance operation.",
                "Select supported maintenance operations and preview again.", severity: "error");
        plan.CriticalErrors.AddRange(plan.Diagnostics.Where(d => d.Severity == "error").Select(d => d.Message));
        if (plan.CriticalErrors.Count > 0) return SavePlan(plan);

        var operationRequirements = ids.SelectMany(id => operations.Select(op => (Id: id, Operation: op)))
            .ToDictionary(key => key, key => MaintenanceRequirements.Enabled(key.Operation, allConfigs[key.Id].Maintenance)
                ? (key.Operation == MaintenanceOperation.OrphanScan && allConfigs[key.Id].Maintenance.Orphans.MaxFilesPerRun == 0 ? InventoryRequirements.None : MaintenanceRequirements.For(key.Operation, allConfigs[key.Id].Maintenance)) : InventoryRequirements.None);
        var requirements = ids.ToDictionary(id => id, id => operations.Aggregate(InventoryRequirements.None,
            (required, op) => required | operationRequirements[(id, op)]), StringComparer.OrdinalIgnoreCase);
        // Inspect metadata before deciding whether unregistered cleanup needs file ownership.
        var metadata = requirements.ToDictionary(p => p.Key, p => p.Value & (InventoryRequirements.Metadata | InventoryRequirements.Trackers), StringComparer.OrdinalIgnoreCase);
        var inventory = await inventoryService.CollectAsync(metadata, ct);
        foreach (var id in ids)
        {
            var cfg = allConfigs[id];
            var items = inventory.Torrents.Where(i => i.Torrent.ClientInstanceId.Equals(id, StringComparison.OrdinalIgnoreCase)
                && IsInScope(i.Torrent, cfg, effective) && i.TrackersComplete).ToList();
            foreach (var op in operations.Where(op => op is MaintenanceOperation.UnregisteredCleanup or MaintenanceOperation.ErrorRecovery
                && MaintenanceRequirements.Enabled(op, cfg.Maintenance)))
            {
                var needsContent = (op == MaintenanceOperation.UnregisteredCleanup ? PlanUnregistered(id, cfg, items, seeding)
                    : PlanRecovery(id, cfg, _registry.GetClient(id)!, items, seeding, false)).Any(a => a.Kind == MaintenanceActionKind.RecycleContent && a.BlockedReason == null);
                if (!needsContent) continue;
                var files = InventoryRequirements.Files | InventoryRequirements.LocalFiles | InventoryRequirements.GlobalOwnership;
                requirements[id] |= files;
                operationRequirements[(id, op)] |= files;
            }
        }
        var global = requirements.Values.Any(r => r.HasFlag(InventoryRequirements.GlobalOwnership));
        if (global)
        {
            foreach (var (id, cfg) in allConfigs.Where(p => !string.IsNullOrWhiteSpace(p.Value.Host) && p.Value.Host != "CHANGE_ME"))
                requirements[id] = requirements.GetValueOrDefault(id) | InventoryRequirements.Metadata | InventoryRequirements.Files | InventoryRequirements.LocalFiles | InventoryRequirements.GlobalOwnership;
        }
        if (requirements.Values.Any(r => r.HasFlag(InventoryRequirements.Files))) inventory = await inventoryService.CollectAsync(requirements, ct);
        plan.Diagnostics.AddRange(inventory.Diagnostics);
        foreach (var diagnostic in plan.Diagnostics)
        {
            diagnostic.Operations = operations.Where(op => ids.Any(id =>
                MaintenanceRequirements.Enabled(op, allConfigs[id].Maintenance)
                && (diagnostic.ClientInstanceId.Equals(id, StringComparison.OrdinalIgnoreCase)
                    || global && operationRequirements[(id, op)].HasFlag(InventoryRequirements.GlobalOwnership))
                && DiagnosticAffects(diagnostic.Code, operationRequirements[(id, op)]))).ToList();
        }
        foreach (var id in ids)
        {
            var cfg = allConfigs[id];
            var client = _registry.GetClient(id);
            var allItems = inventory.Torrents.Where(t => t.Torrent.ClientInstanceId.Equals(id, StringComparison.OrdinalIgnoreCase)).ToList();
            var scoped = allItems.Where(t => IsInScope(t.Torrent, cfg, effective)).ToList();
            foreach (var operation in operations)
            {
                ct.ThrowIfCancellationRequested();
                var result = new MaintenanceOperationResult { ClientInstanceId = id, Operation = operation };
                plan.OperationResults.Add(result);
                if (!MaintenanceRequirements.Enabled(operation, cfg.Maintenance)) { result.State = "disabled"; result.Reason = "Operation is disabled or has no configured rules."; continue; }
                if (operation == MaintenanceOperation.OrphanScan && cfg.Maintenance.Orphans.MaxFilesPerRun == 0)
                { result.Reason = "Orphan action budget is zero."; continue; }
                var required = MaintenanceRequirements.For(operation, cfg.Maintenance);
                if (cfg.Disabled || (required != InventoryRequirements.None && (client == null || !inventory.InspectedClients.Contains(id))))
                {
                    result.State = "blocked"; result.Reason = "Torrent client is unavailable or torrent listing failed."; continue;
                }
                if (operation == MaintenanceOperation.OrphanScan && (!inventory.IsComplete || cfg.Maintenance.PathMappings.Count == 0 || !cfg.Maintenance.RecycleBin.Enabled || cfg.Maintenance.Orphans.ScanRoots.Count == 0))
                {
                    result.State = "blocked";
                    var missing = new List<string>();
                    if (!inventory.IsComplete)
                    {
                        var failures = inventory.Diagnostics.Where(d => d.Severity == "error" || d.Code is
                            "ownership-not-inspected" or "client-unavailable" or "client-disabled" or
                            "torrent-list-failed" or "file-enumeration-unsupported" or "file-list-failed" or
                            "empty-file-list" or "missing-mapping" or "invalid-mapping" or
                            "unmatched-prefix" or "unsafe-path" or "inaccessible-local-root")
                            .Select(d => $"{d.ClientInstanceId}: {d.Code}: {d.Message}").Distinct().Take(8).ToArray();
                        missing.Add(failures.Length > 0
                            ? $"global torrent ownership inventory is incomplete ({string.Join("; ", failures)})"
                            : "global torrent ownership inventory is incomplete; inspect client and file enumeration diagnostics");
                    }
                    if (cfg.Maintenance.PathMappings.Count == 0) missing.Add("Maintenance.PathMappings has no entries");
                    if (!cfg.Maintenance.RecycleBin.Enabled) missing.Add("Maintenance.RecycleBin.Enabled is false");
                    if (cfg.Maintenance.Orphans.ScanRoots.Count == 0) missing.Add("Maintenance.Orphans.ScanRoots has no entries");
                    result.Reason = $"Orphan scan blocked for {id}: {string.Join("; ", missing)}.";
                    AddIssue("orphan-prerequisite", result.Reason); continue;
                }
                if (operation == MaintenanceOperation.HardlinkAudit && cfg.Maintenance.Hardlinks.CountLinksInsideRoot)
                {
                    result.State = "blocked"; result.Reason = "Root-scoped hardlink inspection is not supported.";
                    AddIssue("inspection-unsupported", result.Reason); continue;
                }
                if (required.HasFlag(InventoryRequirements.LocalFiles) && cfg.Maintenance.PathMappings.Count == 0)
                {
                    result.State = "blocked"; result.Uninspectable = scoped.Count;
                    result.Reason = "This operation requires explicit local path mappings.";
                    AddIssue("missing-mapping", result.Reason); continue;
                }
                if (operation == MaintenanceOperation.PrivateTagging && !client!.Capabilities.PrivateState)
                {
                    result.State = "blocked"; result.Reason = "Client cannot inspect torrent privacy.";
                    AddIssue("inspection-unsupported", result.Reason); continue;
                }
                var candidates = scoped.Where(i => IsOperationScope(i.Torrent, cfg.Maintenance, operation)).ToList();
                if (operation == MaintenanceOperation.HardlinkAudit)
                    candidates = candidates.Where(i => (cfg.Maintenance.Hardlinks.Categories.Count == 0 || cfg.Maintenance.Hardlinks.Categories.Contains(i.Torrent.Category, StringComparer.OrdinalIgnoreCase))
                        && !cfg.Maintenance.Hardlinks.ExcludedTags.Any(SplitTags(i.Torrent.Tags).Contains)).ToList();
                result.Excluded = allItems.Count - candidates.Count;
                var inspectable = candidates.Where(i => (!required.HasFlag(InventoryRequirements.Trackers) || i.TrackersComplete)
                    && (!required.HasFlag(InventoryRequirements.Files) || i.FilesComplete)
                    && (!required.HasFlag(InventoryRequirements.LocalFiles) || i.LocalFilesComplete)
                    && (operation != MaintenanceOperation.PrivateTagging || i.Torrent.IsPrivate.HasValue)).ToList();
                result.Uninspectable = candidates.Count - inspectable.Count;
                result.Examined = inspectable.Count;
                var uninspectableContent = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                if (operation == MaintenanceOperation.PrivateTagging)
                    foreach (var item in candidates.Where(i => !i.Torrent.IsPrivate.HasValue))
                        MaintenanceDiagnostic.Add(plan.Diagnostics, "privacy-unknown", id, "Torrent privacy could not be determined.",
                            "Check that the client exposes private torrent state.", new(item.Torrent.Hash, item.Torrent.Name, null, null), operations: [operation]);
                try
                {
                    IEnumerable<MaintenanceAction> actions = operation switch
                    {
                        MaintenanceOperation.OrphanScan => PlanOrphans(id, cfg, inventory, result, plan.InspectionCursors, ct),
                        MaintenanceOperation.HardlinkAudit => await PlanHardlinksAsync(id, cfg, client!, inspectable, ct, plan.Diagnostics, result, plan.InspectionCursors),
                        MaintenanceOperation.UnregisteredCleanup => PlanUnregistered(id, cfg, inspectable, seeding),
                        MaintenanceOperation.CategoryReconcile => PlanCategories(id, cfg, client!, inspectable),
                        MaintenanceOperation.AutomaticManagement => PlanAutomaticManagement(id, cfg, client!, inspectable),
                        MaintenanceOperation.PrivateTagging => PlanPrivateTags(id, cfg, client!, inspectable),
                        MaintenanceOperation.TrackerErrorTagging => PlanTrackerErrorTags(id, cfg, client!, inspectable),
                        MaintenanceOperation.RepairPaused => PlanRepair(id, cfg, client!, inspectable),
                        MaintenanceOperation.ErrorRecovery => PlanRecovery(id, cfg, client!, inspectable, seeding, false),
                        MaintenanceOperation.RecycleRetention => PlanRetention(id, cfg, result, plan.Diagnostics),
                        _ => []
                    };
                    foreach (var action in actions.Take(MaintenancePolicies.Get(cfg.Maintenance, operation)?.MaxActionsPerRun ?? int.MaxValue))
                    {
                        action.Operation = operation;
                        action.Arguments["operation"] = operation.ToString();
                        if (action.Kind == MaintenanceActionKind.RecycleContent)
                        {
                            var candidate = inspectable.FirstOrDefault(i => i.Torrent.Hash == action.TorrentHash);
                            if (candidate == null || !await seeding.CanDeleteTorrentAsync(candidate.Torrent, "maintenance preview", ct))
                                action.BlockedReason ??= "Removal protections, stalled grace, or seeding requirements are unmet or unknown.";
                        }
                        if (action.Kind is MaintenanceActionKind.SetCategory or MaintenanceActionKind.SetAutomaticManagement)
                        {
                            var candidate = inspectable.FirstOrDefault(i => i.Torrent.Hash == action.TorrentHash);
                            if (candidate == null || !seeding.HnrAllowsDelete(candidate.Torrent, candidate.Trackers))
                                action.BlockedReason ??= "Hit & Run obligations are unmet or unknown; preserve seeding.";
                        }

                        if (MaintenanceRequirements.MovesContent(action.Kind))
                        {
                            var item = inventory.Torrents.FirstOrDefault(i => i.Torrent.ClientInstanceId.Equals(id, StringComparison.OrdinalIgnoreCase) && i.Torrent.Hash == action.TorrentHash);
                            var blocked = !inventory.IsComplete ? "File ownership inventory is incomplete."
                                : cfg.Maintenance.PathMappings.Count == 0 ? "Maintenance requires explicit local path mappings."
                                : !cfg.Maintenance.RecycleBin.Enabled ? "Maintenance recycling is disabled."
                                : item != null && !item.LocalFilesComplete ? "Torrent files cannot be inspected locally." : null;
                            action.BlockedReason ??= blocked;
                            if (item != null && (!inventory.IsComplete || !item.LocalFilesComplete) && uninspectableContent.Add(item.Torrent.Hash))
                            { result.Uninspectable++; result.Examined = Math.Max(0, result.Examined - 1); }
                        }
                        plan.Actions.Add(action);
                    }
                    if (result.State == "blocked" && result.Reason != null) AddIssue("operation-prerequisite", result.Reason);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    result.State = "blocked"; result.Reason = "Local inspection failed; no actions from this operation will run.";
                    plan.Actions.RemoveAll(a => a.ClientInstanceId.Equals(id, StringComparison.OrdinalIgnoreCase) && a.Operation == operation);
                    AddIssue("local-inspection-failed", result.Reason);
                }
                void AddIssue(string code, string message) => MaintenanceDiagnostic.Add(plan.Diagnostics, code, id, message,
                    "Check Maintenance settings, path mappings, and filesystem access in Config, then preview again.", operations: [operation]);
            }
        }
        NormalizeActions(plan);
        foreach (var result in plan.OperationResults)
        {
            var actions = plan.Actions.Where(a => a.ClientInstanceId.Equals(result.ClientInstanceId, StringComparison.OrdinalIgnoreCase) && a.Operation == result.Operation).ToList();
            result.Actionable = actions.Count(a => a.BlockedReason == null);
            result.BlockedActions = actions.Count - result.Actionable;
            if (result.State is "disabled" or "blocked") continue;
            result.State = result.Uninspectable > 0 || result.BlockedActions > 0
                ? result.Actionable > 0 || result.Examined > 0 && result.BlockedActions == 0 ? "partial" : "blocked" : "ready";
            if (result.Uninspectable > 0) result.Reason = "Some torrents could not be inspected; see diagnostics.";
            result.Reason ??= actions.FirstOrDefault(a => a.BlockedReason != null)?.BlockedReason;
        }
        plan.Warnings.AddRange(plan.Diagnostics.Select(d => $"[{d.ClientInstanceId}] {d.Message} ({d.AffectedFiles} files, {d.AffectedTorrents} torrents)"));
        return SavePlan(plan);
    }

    private MaintenancePlan SavePlan(MaintenancePlan plan)
    {
        if (!plan.IsDraft) _plans[plan.PlanId] = plan;
        return plan;
    }

    private static bool DiagnosticAffects(string code, InventoryRequirements required) => code switch
    {
        "tracker-list-failed" => required.HasFlag(InventoryRequirements.Trackers),
        "file-list-failed" or "empty-file-list" or "file-enumeration-unsupported" => required.HasFlag(InventoryRequirements.Files),
        "missing-mapping" or "invalid-mapping" or "unmatched-prefix" or "missing-file" or "unsafe-path" or "inaccessible-local-root" => required.HasFlag(InventoryRequirements.LocalFiles),
        _ => required != InventoryRequirements.None
    };

    private IEnumerable<MaintenanceAction> PlanRetention(string id, TorrentClientInstanceConfig cfg, MaintenanceOperationResult? result = null, List<MaintenanceDiagnostic>? diagnostics = null)
    {
        result ??= new() { ClientInstanceId = id, Operation = MaintenanceOperation.RecycleRetention };
        diagnostics ??= [];
        var actions = new List<MaintenanceAction>();
        var remaining = cfg.Maintenance.RecycleBin.MaxFilesDeletedPerRun;
        foreach (var entry in _deletion.InspectRecycleEntries(id))
        {
            result.Examined++;
            if (entry.State == "legacy")
            {
                actions.Add(new()
                {
                    ClientInstanceId = id,
                    Operation = MaintenanceOperation.RecycleRetention,
                    Kind = MaintenanceActionKind.RegisterRecycleEntry,
                    Path = entry.Destination,
                    Reason = "Begin a full retention period for legacy recycled content; no files will be deleted."
                });
                continue;
            }
            if (entry.State != "committed")
            {
                result.State = "partial"; result.Uninspectable++;
                result.Reason = "Incomplete recycling or ambiguous ownership requires manual recovery; files are preserved.";
                MaintenanceDiagnostic.Add(diagnostics, "recycle-recovery-required", id, result.Reason,
                    "Preserve both paths and verify the torrent client's state before manual recovery.",
                    new(entry.TorrentHash, null, entry.Source, entry.Destination), operations: [MaintenanceOperation.RecycleRetention]);
                continue;
            }
            if ((DateTimeOffset.UtcNow - entry.RecycledAt).TotalDays < cfg.Maintenance.RecycleBin.RetentionDays || remaining == 0) continue;
            var files = _deletion.InspectRecycleFiles(entry).Take(remaining).ToList();
            remaining -= files.Count;
            if (files.Count == 0) continue;
            actions.Add(new()
            {
                ClientInstanceId = id,
                Operation = MaintenanceOperation.RecycleRetention,
                Kind = MaintenanceActionKind.DeleteExpiredRecycleItem,
                Path = entry.Destination,
                Bytes = files.Sum(f => f.Bytes),
                Destructive = true,
                Arguments = new() { ["entry"] = entry.Key, ["files"] = JsonConvert.SerializeObject(files) },
                Reason = $"Permanently delete {files.Count} reviewed files after their recycling retention period."
            });
        }
        return actions;
    }

    private static void NormalizeActions(MaintenancePlan plan)
    {
        string Property(MaintenanceAction a) => a.Kind switch
        {
            MaintenanceActionKind.AddTag or MaintenanceActionKind.RemoveTag => "tag:" + a.Arguments.GetValueOrDefault("tag"),
            MaintenanceActionKind.Resume or MaintenanceActionKind.Recheck => "run-state",
            MaintenanceActionKind.RecycleContent => "removal",
            _ => a.Kind.ToString()
        };
        string Target(MaintenanceAction a) => $"{a.ClientInstanceId.ToUpperInvariant()}|{a.TorrentHash?.ToUpperInvariant() ?? a.Path}|{Property(a)}";
        string Value(MaintenanceAction a) => a.Kind + "|" + string.Join("|", a.Arguments.Where(p => p.Key != "operation" && p.Key != "tracker").OrderBy(p => p.Key).Select(p => $"{p.Key}={p.Value}"));
        var seen = new HashSet<string>();
        plan.Actions.RemoveAll(a => a.BlockedReason == null && !seen.Add(Target(a) + "|" + Value(a)));
        foreach (var group in plan.Actions.Where(a => a.BlockedReason == null).GroupBy(Target))
        {
            if (group.Select(Value).Distinct().Count() < 2) continue;
            foreach (var action in group) action.BlockedReason = "Conflicting maintenance mutations target the same property.";
            var first = group.First();
            MaintenanceDiagnostic.Add(plan.Diagnostics, "conflicting-actions", first.ClientInstanceId,
                "Conflicting maintenance actions were blocked.", "Resolve overlapping category rules in Config.",
                new(first.TorrentHash, first.TorrentName, null, first.Path), operations: group.Select(a => a.Operation));
        }
    }

    public Task<MaintenanceRunSummary> ApplyAsync(string planId, CancellationToken ct = default) => ApplyReviewedAsync(planId, false, ct);
    public Task<MaintenanceRunSummary> ApplyOnceAsync(string planId, CancellationToken ct = default) => ApplyReviewedAsync(planId, true, ct);
    private async Task<MaintenanceRunSummary> ApplyReviewedAsync(string planId, bool once, CancellationToken ct)
    {
        if (!_plans.TryGetValue(planId, out var plan)) throw new KeyNotFoundException("Maintenance plan was not found.");
        lock (plan)
        {
            if (plan.IsDraft) throw new InvalidOperationException("Draft previews cannot execute.");
            if (plan.Consumed) throw new InvalidOperationException("Maintenance plan has already been consumed.");
            if (plan.ExpiresAt <= DateTimeOffset.UtcNow) throw new InvalidOperationException("Maintenance plan has expired.");
            if (plan.CriticalErrors.Count > 0) throw new InvalidOperationException("Maintenance plan contains critical validation errors.");
            if (!once && plan.Actions.Any(a => a.BlockedReason == null && !CanRunAutomatically(a.ClientInstanceId, a.Operation)))
                throw new InvalidOperationException("Use a reviewed one-time apply for Manual tools, or save Automatic mode.");
            if (!CryptographicOperations.FixedTimeEquals(
                    Encoding.UTF8.GetBytes(plan.ConfigurationFingerprint),
                    Encoding.UTF8.GetBytes(Fingerprint())))
                throw new InvalidOperationException("Maintenance configuration changed after preview.");
            plan.Consumed = true;
        }
        return await ApplyPlanAsync(plan, once ? "once" : "apply", ct, once);
    }

    private bool CanRunAutomatically(string id, MaintenanceOperation? operation = null)
    {
        var cfg = _config.GetTorrentClient(id);
        if (_config.Revision != _config.LatestRevision || cfg?.Maintenance.Enabled != true || cfg.Disabled) return false;
        if (operation == null) return Enum.GetValues<MaintenanceOperation>().Any(op => CanRunAutomatically(id, op));
        if (!MaintenanceRequirements.Enabled(operation.Value, cfg.Maintenance)) return false;
        return MaintenancePolicies.Mode(cfg.Maintenance, operation.Value) == "automatic";
    }

    public async Task<MaintenanceRunSummary> RunAsync(IEnumerable<string>? instanceIds = null, string trigger = "manual", CancellationToken ct = default,
        IEnumerable<MaintenanceOperation>? operations = null)
    {
        var ids = instanceIds?.Distinct(StringComparer.OrdinalIgnoreCase).ToList()
            ?? _config.GetAllTorrentClients().Keys.Where(id => CanRunAutomatically(id)).ToList();
        var requested = operations?.Distinct().ToHashSet();
        if (requested?.Any(op => !Enum.IsDefined(op)) == true) throw new InvalidOperationException("Unknown maintenance operation.");
        foreach (var id in ids)
        {
            if (!CanRunAutomatically(id)) throw new InvalidOperationException($"Automatic maintenance is not enabled for '{id}'.");
        }
        var combined = new MaintenancePlan { ClientInstanceIds = ids, ConfigurationFingerprint = Fingerprint() };
        foreach (var id in ids)
        {
            if (!CanRunAutomatically(id)) throw new InvalidOperationException($"Automatic maintenance is no longer enabled for '{id}'.");
            var allowed = Enum.GetValues<MaintenanceOperation>().Where(op => CanRunAutomatically(id, op)).ToHashSet();
            var selected = (requested is { Count: > 0 } ? requested.Intersect(allowed) : allowed).ToList();
            foreach (var operation in (requested ?? []).Except(allowed))
                combined.OperationResults.Add(new()
                {
                    ClientInstanceId = id,
                    Operation = operation,
                    State = MaintenanceRequirements.Enabled(operation, _config.GetTorrentClient(id)!.Maintenance) ? "blocked" : "disabled",
                    Reason = "Operation is not enabled in Automatic mode."
                });
            if (selected.Count == 0) continue;
            var plan = await PreviewAsync(new MaintenancePreviewRequest { ClientInstanceIds = [id], Operations = selected }, ct);
            if (plan.CriticalErrors.Count > 0) throw new InvalidOperationException(string.Join("; ", plan.CriticalErrors));
            plan.Consumed = true;
            foreach (var cursor in plan.InspectionCursors) combined.InspectionCursors[cursor.Key] = cursor.Value;
            combined.Actions.AddRange(plan.Actions);
            combined.OperationResults.AddRange(plan.OperationResults);
            combined.Diagnostics.AddRange(plan.Diagnostics);
        }
        NormalizeActions(combined);
        return await ApplyPlanAsync(combined, trigger, ct);
    }

    public void Cancel()
    {
        lock (_stateLock) _activeCancellation?.Cancel();
    }

    public IReadOnlyList<MaintenanceRunSummary> GetHistory(int limit = 50)
    {
        lock (_stateLock) return _history.OrderByDescending(h => h.StartedAt).Take(Math.Clamp(limit, 1, 500)).ToList();
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {

        DateTime? lastMinute = null;
        long lastRevision = -1;
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var now = DateTime.UtcNow;
                var minute = new DateTime(now.Year, now.Month, now.Day, now.Hour, now.Minute, 0, DateTimeKind.Utc);
                var revision = _config.Revision;
                // Saving settings must not launch a tool in the current schedule minute.
                if (lastRevision >= 0 && lastRevision != revision) lastMinute = minute;
                lastRevision = revision;
                if (lastMinute != minute)
                {
                    lastMinute = minute;
                    await RunDueAsync(new DateTimeOffset(minute, TimeSpan.Zero), stoppingToken, revision);
                }
                _reloader?.Acknowledge("storage-cleanup", lastRevision);
                if (_reloader != null) await _reloader.WaitForChangeAsync(lastRevision, TimeSpan.FromSeconds(15), stoppingToken);
                else await Task.Delay(TimeSpan.FromSeconds(15), stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Scheduled maintenance run failed");
            }
        }

    }



    internal async Task RunDueAsync(DateTimeOffset now, CancellationToken ct = default, long? expectedRevision = null)
    {
        using var configuration = _config.BeginOperation();
        using var processing = ConfigMutationGuard.EnterProcessing();
        if (expectedRevision.HasValue && expectedRevision != _config.Revision) return;
        var minute = new DateTimeOffset(now.UtcDateTime.AddTicks(-(now.UtcDateTime.Ticks % TimeSpan.TicksPerMinute)), TimeSpan.Zero);
        foreach (var (id, cfg) in _config.GetAllTorrentClients())
        {
            if (cfg.Disabled || !cfg.Maintenance.Enabled) continue;
            var due = Enum.GetValues<MaintenanceOperation>().Where(op => CanRunAutomatically(id, op)
                && CronSchedule.Matches(MaintenancePolicies.Schedule(cfg.Maintenance, op), minute.UtcDateTime)).ToList();
            if (due.Count == 0) continue;
            using var scope = _scopes.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<TorrentarrDbContext>();
            using (var transaction = db.Database.BeginTransaction())
            {
                due = due.Where(op =>
                {
                    var key = MaintenancePersistence.Key(id, op.ToString(), "scheduled-minute");
                    if (MaintenancePersistence.Read<ScanCursor>(db, key).Position == minute.ToString("O")) return false;
                    MaintenancePersistence.Write(db, key, new ScanCursor { Position = minute.ToString("O") });
                    return true;
                }).ToList();
                transaction.Commit();
            }
            if (due.Count > 0) await RunAsync([id], "scheduled", ct, due);
        }
    }

    private async Task<MaintenanceRunSummary> ApplyPlanAsync(MaintenancePlan plan, string trigger, CancellationToken outerCt, bool once = false)
    {
        using var configuration = _config.BeginOperation();
        using var processing = ConfigMutationGuard.EnterProcessing();
        var locks = plan.ClientInstanceIds.OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
            .Select(id => _instanceLocks.GetOrAdd(id, _ => new SemaphoreSlim(1, 1))).ToList();
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(outerCt);
        var run = new MaintenanceRunSummary
        {
            Trigger = trigger,
            ClientInstanceIds = plan.ClientInstanceIds,
            Planned = plan.CandidateCount,
            Blocked = plan.BlockedCount,
            OperationResults = plan.OperationResults,
            Diagnostics = plan.Diagnostics
        };
        lock (_stateLock)
        {
            if (_current != null) throw new InvalidOperationException("A maintenance run is already active.");
            _current = run;
            _activeCancellation = linked;
        }

        var acquired = new List<SemaphoreSlim>(locks.Count);
        try
        {
            foreach (var gate in locks)
            {
                await gate.WaitAsync(outerCt);
                acquired.Add(gate);
            }
        }
        catch
        {
            lock (_stateLock)
            {
                if (ReferenceEquals(_current, run))
                {
                    _current = null;
                    _activeCancellation = null;
                }
            }
            foreach (var gate in acquired) gate.Release();
            throw;
        }

        try
        {
            using var scope = _scopes.CreateScope();
            var seeding = scope.ServiceProvider.GetRequiredService<ISeedingService>();
            var inventory = new TorrentInventory { OwnershipRequested = false };
            var purgedByClient = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            var movedByClient = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            var rechecksByClient = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            var unregisteredByTracker = new Dictionary<(string Client, string Tracker), int>();
            foreach (var action in plan.Actions)
            {
                linked.Token.ThrowIfCancellationRequested();
                if (action.BlockedReason != null)
                {
                    continue;
                }
                try
                {
                    var cfg = _config.GetTorrentClient(action.ClientInstanceId);
                    if (_config.Revision != _config.LatestRevision || cfg?.Maintenance.Enabled != true || !MaintenanceRequirements.Enabled(action.Operation, cfg.Maintenance) || (!once && !CanRunAutomatically(action.ClientInstanceId, action.Operation))
                        || plan.ConfigurationFingerprint != Fingerprint())
                        throw new OperationCanceledException("Automatic maintenance is no longer enabled or configuration changed.");
                    if (action.Kind is MaintenanceActionKind.QuarantineFile or MaintenanceActionKind.RecycleContent)
                    {
                        inventory = await CollectOwnershipAsync(action, linked.Token);
                    }
                    else if (action.TorrentHash != null)
                        inventory = await _inventory.CollectAsync(new Dictionary<string, InventoryRequirements>
                        {
                            [action.ClientInstanceId] = MaintenanceRequirements.For(action.Operation, cfg.Maintenance)
                                & ~InventoryRequirements.GlobalOwnership
                        }, linked.Token, action.TorrentHash);
                    var staleReason = await RevalidateActionAsync(action, inventory, seeding, linked.Token);
                    if (action.Kind is MaintenanceActionKind.RecycleContent
                        && movedByClient.GetValueOrDefault(action.ClientInstanceId) >= cfg.Maintenance.RecycleBin.MaxTorrentMovesPerRun)
                        staleReason = "Per-run content move limit reached.";
                    if (action.Kind == MaintenanceActionKind.Recheck && rechecksByClient.GetValueOrDefault(action.ClientInstanceId) >= cfg.Maintenance.ErrorRecovery.MaxPerRun)
                        staleReason = "Per-run new recheck limit reached.";
                    var trackerKey = (action.ClientInstanceId.ToUpperInvariant(), action.Arguments.GetValueOrDefault("tracker", ""));
                    if (trackerKey.Item2.Length > 0 && unregisteredByTracker.GetValueOrDefault(trackerKey) >= cfg.Maintenance.Unregistered.MaxPerTrackerPerRun)
                        staleReason = "Per-tracker cleanup limit reached.";
                    if (staleReason != null)
                    {
                        run.Skipped++; run.SkippedReasons.Add($"{action.TorrentName ?? action.Path}: {staleReason}");
                        continue;
                    }
                    SafeDeletionResult result;
                    if (action.Kind == MaintenanceActionKind.DeleteExpiredRecycleItem)
                    {
                        var files = JsonConvert.DeserializeObject<List<RecycleFileSnapshot>>(action.Arguments["files"])!;
                        if (purgedByClient.GetValueOrDefault(action.ClientInstanceId) + files.Count > cfg.Maintenance.RecycleBin.MaxFilesDeletedPerRun)
                            throw new InvalidOperationException("Per-run purge limit reached.");
                        run.BytesReclaimed += await _deletion.PurgeRecycleEntryAsync(action.ClientInstanceId, action.Arguments["entry"], files, linked.Token);
                        purgedByClient[action.ClientInstanceId] = purgedByClient.GetValueOrDefault(action.ClientInstanceId) + files.Count;
                        result = new(true, false, 0);
                    }
                    else result = await ApplyActionAsync(action, inventory, linked.Token);
                    if (result.Success)
                    {
                        run.Applied++;
                        if (action.Kind == MaintenanceActionKind.Recheck) rechecksByClient[action.ClientInstanceId] = rechecksByClient.GetValueOrDefault(action.ClientInstanceId) + 1;
                        if (trackerKey.Item2.Length > 0) unregisteredByTracker[trackerKey] = unregisteredByTracker.GetValueOrDefault(trackerKey) + 1;
                        if (action.Kind is MaintenanceActionKind.RecycleContent)
                            movedByClient[action.ClientInstanceId] = movedByClient.GetValueOrDefault(action.ClientInstanceId) + 1;
                        run.BytesMoved += result.BytesMoved;
                        if (action.Kind is MaintenanceActionKind.SetCategory or MaintenanceActionKind.SetAutomaticManagement or MaintenanceActionKind.RecycleContent)
                            inventory = new TorrentInventory { OwnershipRequested = false };
                    }
                    else
                    {
                        run.Failed++;
                        run.Errors.Add($"{action.ClientInstanceId}: {action.Kind} ({action.TorrentName ?? action.TorrentHash ?? action.Path ?? action.Id}): {result.Error ?? "Torrent client rejected the operation."}");
                    }
                    linked.Token.ThrowIfCancellationRequested();
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    run.Failed++;
                    run.Errors.Add($"{action.ClientInstanceId}: {action.Kind} for {action.TorrentName ?? action.TorrentHash ?? action.Path ?? action.Id}: {ex.Message}");
                    _logger.LogError(ex, "Maintenance action {ActionId} failed", action.Id);
                }
            }
            using (var cursorScope = _scopes.CreateScope())
            {
                var db = cursorScope.ServiceProvider.GetRequiredService<TorrentarrDbContext>();
                foreach (var cursor in plan.InspectionCursors) MaintenancePersistence.Write(db, cursor.Key, new ScanCursor { Position = cursor.Value });
            }
            run.Result = run.Failed > 0 || run.Skipped > 0 || run.Blocked > 0 || plan.OperationResults.Any(r => r.State is "blocked" or "partial") ? "partial" : "success";
        }
        catch (OperationCanceledException)
        {
            run.Result = "cancelled";
        }
        finally
        {
            run.CompletedAt = DateTimeOffset.UtcNow;
            lock (_stateLock)
            {
                _last = run;
                _history.Add(run);
                if (_history.Count > 500) _history.RemoveRange(0, _history.Count - 500);
                _current = null;
                _activeCancellation = null;
            }
            AppendHistory(run);
            foreach (var gate in acquired) gate.Release();
        }
        return run;
    }

    private Task<TorrentInventory> CollectOwnershipAsync(MaintenanceAction action, CancellationToken ct)
    {
        var requirements = _config.GetAllTorrentClients().Where(p => !string.IsNullOrWhiteSpace(p.Value.Host) && p.Value.Host != "CHANGE_ME")
            .ToDictionary(p => p.Key, p => InventoryRequirements.Metadata | InventoryRequirements.Files | InventoryRequirements.LocalFiles | InventoryRequirements.GlobalOwnership
                | (p.Key.Equals(action.ClientInstanceId, StringComparison.OrdinalIgnoreCase)
                    ? MaintenanceRequirements.For(action.Operation, p.Value.Maintenance) & InventoryRequirements.Trackers : InventoryRequirements.None), StringComparer.OrdinalIgnoreCase);
        return _inventory.CollectAsync(requirements, ct);
    }

    private async Task<SafeDeletionResult> ApplyActionAsync(MaintenanceAction action, TorrentInventory inventory, CancellationToken ct)
    {
        if (action.Kind == MaintenanceActionKind.RegisterRecycleEntry)
        {
            _deletion.RegisterLegacyRecycleEntry(action.ClientInstanceId, action.Path!);
            return new(true, true, 0);
        }
        if (action.Kind == MaintenanceActionKind.DeleteExpiredRecycleItem)
        {
            var files = JsonConvert.DeserializeObject<List<RecycleFileSnapshot>>(action.Arguments["files"])!;
            await _deletion.PurgeRecycleEntryAsync(action.ClientInstanceId, action.Arguments["entry"], files, ct);
            return new(true, false, 0);
        }
        var client = _registry.GetClient(action.ClientInstanceId)
            ?? throw new InvalidOperationException("Torrent client disconnected before apply.");
        switch (action.Kind)
        {
            case MaintenanceActionKind.QuarantineFile:
                return await _deletion.QuarantineFileAsync(action.ClientInstanceId, action.Path!, ct);
            case MaintenanceActionKind.AddTag:
            case MaintenanceActionKind.RemoveTag:
                var tagTorrent = inventory.Torrents.Single(t => t.Torrent.ClientInstanceId == action.ClientInstanceId && t.Torrent.Hash == action.TorrentHash).Torrent;
                var adding = action.Kind == MaintenanceActionKind.AddTag;
                if (!adding && !OwnsTag(action.ClientInstanceId, action.TorrentHash!, action.Arguments["tag"], tagTorrent.AddedOn))
                    return new(false, true, 0, "Tag is not owned by Torrentarr.");
                var success = adding ? await client.AddTagsAsync([action.TorrentHash!], [action.Arguments["tag"]], ct)
                    : await client.RemoveTagsAsync([action.TorrentHash!], [action.Arguments["tag"]], ct);
                if (success)
                {
                    using var tagScope = _scopes.CreateScope();
                    MaintenancePersistence.Write(tagScope.ServiceProvider.GetRequiredService<TorrentarrDbContext>(),
                        MaintenancePersistence.Key(action.ClientInstanceId, action.TorrentHash!, "tag:" + action.Arguments["tag"].ToUpperInvariant()), new TagOwnership { Owned = adding, AddedOn = tagTorrent.AddedOn });
                }
                return Result(success);
            case MaintenanceActionKind.SetCategory:
                return Result(await client.SetCategoryAsync([action.TorrentHash!], action.Arguments["category"], ct));
            case MaintenanceActionKind.SetAutomaticManagement:
                return Result(await client.SetAutomaticManagementAsync([action.TorrentHash!], true, ct));
            case MaintenanceActionKind.Recheck:
                {
                    using var recoveryScope = _scopes.CreateScope();
                    var db = recoveryScope.ServiceProvider.GetRequiredService<TorrentarrDbContext>();
                    var torrent = inventory.Torrents.Single(t => t.Torrent.ClientInstanceId == action.ClientInstanceId && t.Torrent.Hash == action.TorrentHash).Torrent;
                    return Result(await MaintenanceRecovery.RequestAsync(db, client, torrent, action.Operation.ToString(), _config.GetTorrentClient(action.ClientInstanceId)!.Maintenance.ErrorRecovery, ct));
                }
            case MaintenanceActionKind.Resume:
                return Result(await client.ResumeTorrentsAsync([action.TorrentHash!], ct));
            case MaintenanceActionKind.RecycleContent:
                return await _deletion.RemoveMaintenanceTorrentAsync(action.ClientInstanceId, action.TorrentHash!, true, inventory, ct: ct);
            default:
                return new SafeDeletionResult(false, true, 0, $"Unsupported maintenance action {action.Kind}.");
        }
    }

    private async Task<string?> RevalidateActionAsync(MaintenanceAction action, TorrentInventory inventory, ISeedingService seeding, CancellationToken ct)
    {
        var client = _registry.GetClient(action.ClientInstanceId);
        var cfg = _config.GetTorrentClient(action.ClientInstanceId);
        if (cfg == null || cfg.Disabled || client == null && action.Kind is not (MaintenanceActionKind.DeleteExpiredRecycleItem or MaintenanceActionKind.RegisterRecycleEntry)) return "Torrent client is unavailable.";
        if (action.Kind is MaintenanceActionKind.DeleteExpiredRecycleItem or MaintenanceActionKind.RegisterRecycleEntry)
        {
            if (!cfg.Maintenance.RecycleBin.Enabled || !cfg.Maintenance.RecycleBin.RetentionEnabled) return "Recycle retention is disabled.";
            return action.Path == null || _paths.ValidateLocalPath(action.Path, Directory.Exists(action.Path) ? action.Path : Path.GetDirectoryName(action.Path)!, File.Exists(action.Path)) != null
                ? "Recycle root is inaccessible or unsafe." : null;
        }
        if (client == null) return "Torrent client is unavailable.";
        if (action.Kind is MaintenanceActionKind.QuarantineFile or MaintenanceActionKind.RecycleContent)
        {
            if (!inventory.IsComplete) return "File ownership inventory is incomplete.";
            if (cfg.Maintenance.PathMappings.Count == 0 || !cfg.Maintenance.RecycleBin.Enabled)
                return "Mapped maintenance recycling is not enabled.";
        }
        if (action.Kind == MaintenanceActionKind.QuarantineFile && action.Path != null)
        {
            var normalized = Path.GetFullPath(action.Path);
            var comparer = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
            if (inventory.LocalPathReferences.Keys.Any(path => path.Equals(normalized, comparer)
                || path.StartsWith(normalized.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, comparer)))
                return "File is no longer orphaned.";
            if (!MaintenanceRequirements.Enabled(MaintenanceOperation.OrphanScan, cfg.Maintenance) || !File.Exists(normalized) || IsAutomationStorage(normalized) || _deletion.GetRecoveryPaths().Any(p => _paths.IsSafeDescendant(normalized, p))
                || File.GetLastWriteTimeUtc(normalized) > DateTime.UtcNow.AddMinutes(-cfg.Maintenance.Orphans.MinimumAgeMinutes))
                return "Orphan eligibility changed after preview.";
            var mapping = cfg.Maintenance.PathMappings.FirstOrDefault(m => cfg.Maintenance.Orphans.ScanRoots.Contains(m.LocalPath) && _paths.IsSafeDescendant(normalized, m.LocalPath));
            if (mapping == null || cfg.Maintenance.Orphans.ExcludePatterns.Any(pattern => FileSystemName.MatchesSimpleExpression(
                pattern, Path.GetRelativePath(mapping.LocalPath, normalized), OperatingSystem.IsWindows())))
                return "Orphan path is excluded or outside configured roots.";
        }
        if (action.TorrentHash != null)
        {
            var item = inventory.Torrents.FirstOrDefault(i => i.Torrent.ClientInstanceId.Equals(action.ClientInstanceId, StringComparison.OrdinalIgnoreCase)
                && i.Torrent.Hash.Equals(action.TorrentHash, StringComparison.OrdinalIgnoreCase));
            if (item == null || !IsInScope(item.Torrent, cfg) || !IsOperationScope(item.Torrent, cfg.Maintenance, action.Operation)) return "Torrent no longer exists or is outside maintenance scope.";
            if (action.Kind == MaintenanceActionKind.RecycleContent
                && !await seeding.CanDeleteTorrentAsync(item.Torrent, "maintenance apply", ct))
                return "Removal protections, stalled grace, or seeding requirements are unmet or unknown.";
            if (action.Kind is MaintenanceActionKind.RecycleContent or MaintenanceActionKind.SetAutomaticManagement or MaintenanceActionKind.SetCategory)
            {
                if (!item.TrackersComplete || !seeding.HnrAllowsDelete(item.Torrent, item.Trackers))
                    return "Hit & Run obligations are unmet or unknown; preserve seeding.";
            }
            if (action.Kind == MaintenanceActionKind.Recheck && item.Torrent.IsSeeding && !MaintenanceRecovery.IsError(item.Torrent, cfg.Maintenance)
                && (!item.TrackersComplete || !seeding.HnrAllowsDelete(item.Torrent, item.Trackers)))
                return "Protected seeding must not be interrupted for a tracker-only issue.";
            var required = MaintenanceRequirements.For(action.Operation, cfg.Maintenance);
            if (required.HasFlag(InventoryRequirements.Trackers) && !item.TrackersComplete) return "Tracker inspection is incomplete.";
            if (required.HasFlag(InventoryRequirements.Files) && !item.FilesComplete) return "Torrent file enumeration is incomplete.";
            if ((required.HasFlag(InventoryRequirements.LocalFiles) || MaintenanceRequirements.MovesContent(action.Kind)) && !item.LocalFilesComplete)
                return "Local file inspection is incomplete.";
            if (!Enum.TryParse<MaintenanceOperation>(action.Arguments.GetValueOrDefault("operation"), out var operation))
                return "Action has no valid preview operation.";
            IEnumerable<MaintenanceAction> candidates = operation switch
            {
                MaintenanceOperation.HardlinkAudit when MaintenanceRequirements.Enabled(MaintenanceOperation.HardlinkAudit, cfg.Maintenance) => await PlanHardlinksAsync(action.ClientInstanceId, cfg, client, [item], ct),
                MaintenanceOperation.UnregisteredCleanup when MaintenanceRequirements.Enabled(MaintenanceOperation.UnregisteredCleanup, cfg.Maintenance) => PlanUnregistered(action.ClientInstanceId, cfg, [item], seeding),
                MaintenanceOperation.CategoryReconcile => PlanCategories(action.ClientInstanceId, cfg, client, [item]),
                MaintenanceOperation.AutomaticManagement => PlanAutomaticManagement(action.ClientInstanceId, cfg, client, [item]),
                MaintenanceOperation.PrivateTagging => PlanPrivateTags(action.ClientInstanceId, cfg, client, [item]),
                MaintenanceOperation.TrackerErrorTagging => PlanTrackerErrorTags(action.ClientInstanceId, cfg, client, [item]),
                MaintenanceOperation.RepairPaused when MaintenanceRequirements.Enabled(MaintenanceOperation.RepairPaused, cfg.Maintenance) => PlanRepair(action.ClientInstanceId, cfg, client, [item]),
                MaintenanceOperation.ErrorRecovery => PlanRecovery(action.ClientInstanceId, cfg, client, [item], seeding, false),
                _ => []
            };
            var candidate = candidates.FirstOrDefault(c => c.Kind == action.Kind
                && c.Arguments.Count == action.Arguments.Count - 1
                && c.Arguments.All(p => action.Arguments.GetValueOrDefault(p.Key) == p.Value));
            if (candidate == null) return "Action eligibility changed after preview.";
            if (candidate.BlockedReason != null) return candidate.BlockedReason;
        }
        foreach (var capability in action.RequiredCapabilities)
        {
            var supported = capability.ToLowerInvariant() switch
            {
                "tags" => client.Capabilities.Tags,
                "categories" => client.Capabilities.Categories,
                "recheck" => client.Capabilities.Recheck,
                "automaticmanagement" => client.Capabilities.AutomaticManagement,
                "contentdeletion" => client.Capabilities.ContentDeletion,
                _ => false
            };
            if (!supported) return $"Required capability '{capability}' is no longer available.";
        }
        return null;
    }

    private static SafeDeletionResult Result(bool success)
        => success ? new(true, true, 0) : new(false, true, 0, "Torrent client rejected the operation.");

    private bool IsAutomationStorage(string path)
    {
        foreach (var cfg in _config.GetAllTorrentClients().Values)
        {
            var storage = cfg.Maintenance;
            var root = storage.RecycleBin.Path.Length > 0 ? storage.RecycleBin.Path
                : storage.PathMappings.Count > 0 ? Path.Combine(storage.PathMappings[0].LocalPath, ".torrentarr-recycle") : null;
            if (root != null && _paths.IsSafeDescendant(path, root)) return true;
            if (storage.RecycleBin.ResumeDataPath.Length > 0 && _paths.IsSafeDescendant(path, storage.RecycleBin.ResumeDataPath)) return true;
        }
        var data = ConfigurationLoader.GetDataDirectoryPath();
        if (_paths.IsSafeDescendant(path, Path.Combine(data, "logs"))) return true;
        return new[] { "torrentarr.db", "torrentarr.db-wal", "torrentarr.db-shm", "maintenance-history.jsonl", "active-config.json", ".torrentarr-host.lock" }
            .Any(name => Path.GetFullPath(path) == Path.Combine(data, name));
    }

    private IEnumerable<MaintenanceAction> PlanOrphans(string id, TorrentClientInstanceConfig cfg, TorrentInventory inventory, MaintenanceOperationResult? result = null, Dictionary<string, string>? cursors = null, CancellationToken ct = default)
    {
        if (!inventory.IsComplete) yield break;
        if (result != null) { result.Examined = 0; result.Excluded = 0; }
        var referenced = inventory.LocalPathReferences.Keys.ToHashSet(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        var yielded = 0;
        var inspectedPaths = new HashSet<string>(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        var inspected = 0;
        var recoveryPaths = _deletion.GetRecoveryPaths();
        using var cursorScope = _scopes.CreateScope();
        var cursorDb = cursorScope.ServiceProvider.GetRequiredService<TorrentarrDbContext>();
        var cursorKey = MaintenancePersistence.Key(id, "", "cursor:OrphanScan");
        var previous = MaintenancePersistence.Read<ScanCursor>(cursorDb, cursorKey).Position;
        if (cursors != null) cursors[cursorKey] = "";
        var options = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            AttributesToSkip = FileAttributes.ReparsePoint,
            IgnoreInaccessible = false,
            ReturnSpecialDirectories = false
        };
        var files = cfg.Maintenance.PathMappings.Where(m => cfg.Maintenance.Orphans.ScanRoots.Contains(m.LocalPath)).SelectMany(mapping =>
        {
            if (!Directory.Exists(mapping.LocalPath)) throw new IOException("Orphan root is missing or inaccessible.");
            if (_paths.ValidateLocalPath(mapping.LocalPath, mapping.LocalPath) != null) throw new IOException("Orphan root is inaccessible or unsafe.");
            return Directory.EnumerateFiles(mapping.LocalPath, "*", options).Select(file => { ct.ThrowIfCancellationRequested(); return (File: file, Mapping: mapping); });
        });
        // shortcut: ordering enumerates names across all roots; use a filesystem index for very large roots.
        foreach (var (file, mapping) in files.OrderBy(entry => entry.File, StringComparer.Ordinal))
        {
            ct.ThrowIfCancellationRequested();
            if (string.CompareOrdinal(file, previous) <= 0) continue;
            if (yielded >= cfg.Maintenance.Orphans.MaxFilesPerRun || inspected++ >= cfg.Maintenance.Orphans.MaxInspectedFilesPerRun) yield break;
            if (cursors != null) cursors[cursorKey] = file;
            if (!inspectedPaths.Add(Path.GetFullPath(file))) continue;
            if (result != null) result.Examined++;
            if (_paths.ValidateLocalPath(file, mapping.LocalPath, requireFile: true) != null) throw new IOException("Orphan file is inaccessible or unsafe.");
            if (IsAutomationStorage(file) || recoveryPaths.Any(p => _paths.IsSafeDescendant(file, p))) continue;
            var recycleRoot = cfg.Maintenance.RecycleBin.Path;
            if (string.IsNullOrWhiteSpace(recycleRoot) && cfg.Maintenance.PathMappings.Count > 0)
                recycleRoot = Path.Combine(cfg.Maintenance.PathMappings[0].LocalPath, ".torrentarr-recycle");
            if (!string.IsNullOrWhiteSpace(recycleRoot) && _paths.IsSafeDescendant(file, recycleRoot)) continue;
            if (referenced.Contains(Path.GetFullPath(file))) continue;
            var relative = Path.GetRelativePath(mapping.LocalPath, file);
            if (cfg.Maintenance.Orphans.ExcludePatterns.Any(pattern => FileSystemName.MatchesSimpleExpression(pattern, relative, OperatingSystem.IsWindows()))) continue;
            if ((DateTime.UtcNow - File.GetLastWriteTimeUtc(file)).TotalMinutes < cfg.Maintenance.Orphans.MinimumAgeMinutes) continue;
            if ((File.GetAttributes(file) & FileAttributes.ReparsePoint) != 0) continue;
            yielded++;
            yield return new MaintenanceAction
            {
                Kind = MaintenanceActionKind.QuarantineFile,
                ClientInstanceId = id,
                Path = file,
                Bytes = new FileInfo(file).Length,
                Reason = "File is not referenced by any connected torrent.",
                Destructive = true
            };
        }
        if (cursors != null) cursors[cursorKey] = "";
    }

    private async Task<IEnumerable<MaintenanceAction>> PlanHardlinksAsync(
        string id, TorrentClientInstanceConfig cfg, ITorrentClient client,
        IEnumerable<TorrentInventoryItem> items, CancellationToken ct, List<MaintenanceDiagnostic>? diagnostics = null, MaintenanceOperationResult? result = null, Dictionary<string, string>? cursors = null)
    {
        var actions = new List<MaintenanceAction>();
        using var cursorScope = _scopes.CreateScope();
        var cursorDb = cursorScope.ServiceProvider.GetRequiredService<TorrentarrDbContext>();
        var cursorKey = MaintenancePersistence.Key(id, "", "cursor:HardlinkAudit");
        var previous = MaintenancePersistence.Read<ScanCursor>(cursorDb, cursorKey).Position;
        var sorted = items.OrderBy(item => item.Torrent.Hash, StringComparer.Ordinal).ToList();
        var ordered = sorted.Where(item => string.CompareOrdinal(item.Torrent.Hash, previous) > 0).Concat(sorted.Where(item => string.CompareOrdinal(item.Torrent.Hash, previous) <= 0));
        foreach (var item in ordered.Take(cfg.Maintenance.Hardlinks.MaxInspectedTorrentsPerRun))
        {
            if (cursors != null) cursors[cursorKey] = item.Torrent.Hash;
            if (cfg.Maintenance.Hardlinks.Categories.Count > 0
                && !cfg.Maintenance.Hardlinks.Categories.Contains(item.Torrent.Category, StringComparer.OrdinalIgnoreCase)) continue;
            var tags = SplitTags(item.Torrent.Tags);
            if (cfg.Maintenance.Hardlinks.ExcludedTags.Any(tags.Contains)) continue;
            var largest = item.Files.Where(f => f.LocalPath != null).OrderByDescending(f => f.File.Size).FirstOrDefault();
            if (tags.Contains(cfg.Maintenance.Hardlinks.Tag) && !cfg.Maintenance.Hardlinks.RemoveClearedTag) continue;
            int? links = null;
            if (largest?.LocalPath != null)
            {
                try { links = await _hardlinks.GetLinkCountAsync(largest.LocalPath, ct); }
                catch (Exception ex) when (ex is not OperationCanceledException) { _logger.LogWarning("Hardlink inspection failed for client {InstanceId}", id); }
            }
            if (links == null)
            {
                if (result != null) { result.Uninspectable++; result.Examined--; }
                if (diagnostics != null) MaintenanceDiagnostic.Add(diagnostics, "hardlink-inspection-failed", id,
                    "Hardlink count could not be determined.", "Check file access and platform hardlink inspection support.",
                    new(item.Torrent.Hash, item.Torrent.Name, largest?.ClientPath, largest?.LocalPath), operations: [MaintenanceOperation.HardlinkAudit]);
            }
            if (links is > 1)
            {
                if (tags.Contains(cfg.Maintenance.Hardlinks.Tag) && cfg.Maintenance.Hardlinks.RemoveClearedTag && OwnsTag(id, item.Torrent.Hash, cfg.Maintenance.Hardlinks.Tag, item.Torrent.AddedOn))
                {
                    var remove = TagAction(id, item, cfg.Maintenance.Hardlinks.Tag, "Additional hardlinks found; remove Torrentarr-owned tag.", null);
                    actions.Add(new MaintenanceAction
                    {
                        Kind = MaintenanceActionKind.RemoveTag,
                        ClientInstanceId = id,
                        TorrentHash = item.Torrent.Hash,
                        TorrentName = item.Torrent.Name,
                        Reason = remove.Reason,
                        Arguments = remove.Arguments,
                        RequiredCapabilities = ["tags"]
                    });
                }
                continue;
            }
            if (tags.Contains(cfg.Maintenance.Hardlinks.Tag)) continue;
            actions.Add(new MaintenanceAction
            {
                Kind = MaintenanceActionKind.AddTag,
                ClientInstanceId = id,
                TorrentHash = item.Torrent.Hash,
                TorrentName = item.Torrent.Name,
                Path = largest?.LocalPath,
                Reason = links == null ? "Hardlink count could not be determined." : "Largest torrent file has no additional hardlinks.",
                Arguments = new() { ["tag"] = cfg.Maintenance.Hardlinks.Tag },
                RequiredCapabilities = ["tags"],
                BlockedReason = !client.Capabilities.Tags ? "Client does not support tags."
                    : links == null ? "Hardlink inspection failed."
                    : cfg.Maintenance.Hardlinks.CountLinksInsideRoot
                        ? "Root-scoped hardlink inspection is not supported by this platform."
                        : null
            });
        }
        return actions;
    }

    private IEnumerable<MaintenanceAction> PlanUnregistered(string id, TorrentClientInstanceConfig cfg, IEnumerable<TorrentInventoryItem> items, ISeedingService seeding)
        => PlanRecovery(id, cfg, _registry.GetClient(id)!, items, seeding, true);

    private IEnumerable<MaintenanceAction> PlanRecovery(string id, TorrentClientInstanceConfig cfg, ITorrentClient client,
        IEnumerable<TorrentInventoryItem> items, ISeedingService seeding, bool unregistered)
    {
        using var scope = _scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TorrentarrDbContext>();
        var config = cfg.Maintenance.ErrorRecovery;
        var operation = unregistered ? MaintenanceOperation.UnregisteredCleanup : MaintenanceOperation.ErrorRecovery;
        var trackerCounts = new Dictionary<string, int>();
        var count = 0;
        var rechecks = 0;
        var recycles = 0;
        foreach (var item in items)
        {
            if (!unregistered && count >= config.MaxPerRun) yield break;
            var tracker = item.Trackers.FirstOrDefault(t => cfg.Maintenance.Unregistered.Messages.Any(m => t.Msg.Contains(m, StringComparison.OrdinalIgnoreCase)));
            if (unregistered ? tracker == null : !MaintenanceRecovery.IsError(item.Torrent, cfg.Maintenance)) continue;
            if (item.Torrent.AddedOn <= 0 || DateTimeOffset.UtcNow - DateTimeOffset.FromUnixTimeSeconds(item.Torrent.AddedOn)
                < TimeSpan.FromMinutes(Math.Max(cfg.Maintenance.MinimumTorrentAgeMinutes, unregistered ? cfg.Maintenance.Unregistered.GraceMinutes : 0))) continue;
            if (unregistered && cfg.Maintenance.Unregistered.CompletedOnly && item.Torrent.Progress < 1) continue;
            var healthy = MaintenanceRecovery.HealthyTracker(item.Trackers);
            if (healthy && unregistered && !MaintenanceRecovery.IsError(item.Torrent, cfg.Maintenance)) continue;
            var state = MaintenanceRecovery.Read(db, item.Torrent, operation.ToString());
            var recycle = !healthy && MaintenanceRecovery.CanRecycle(state, config, DateTimeOffset.UtcNow) && (unregistered || config.Outcome == "recycle");
            var retry = state.Attempts < config.MaxRepairAttempts && state.Stage is "new" or "rejected" or "checked" or "recurring"
                && (state.RequestedAt == null || DateTimeOffset.UtcNow - state.RequestedAt >= TimeSpan.FromMinutes(config.RetryMinutes));
            if (!retry && !recycle) continue;
            if (recycle && unregistered && recycles >= cfg.Maintenance.RecycleBin.MaxTorrentMovesPerRun
                || !recycle && rechecks >= config.MaxPerRun) continue;
            var trackerKey = tracker == null ? "" : Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(tracker.Url)));
            if (unregistered && trackerCounts.GetValueOrDefault(trackerKey) >= cfg.Maintenance.Unregistered.MaxPerTrackerPerRun) continue;

            if (recycle) recycles++; else rechecks++;
            count++; trackerCounts[trackerKey] = trackerCounts.GetValueOrDefault(trackerKey) + 1;
            yield return new MaintenanceAction
            {
                Kind = recycle ? MaintenanceActionKind.RecycleContent : MaintenanceActionKind.Recheck,
                ClientInstanceId = id,
                TorrentHash = item.Torrent.Hash,
                TorrentName = item.Torrent.Name,
                Reason = recycle ? "Confirmed recurring failure after completed repair and sustained-error delay." : "Repair first: check files before any escalation.",
                RequiredCapabilities = recycle ? [] : ["recheck"],
                Destructive = recycle,
                BlockedReason = recycle && !seeding.HnrAllowsDelete(item.Torrent, item.Trackers) ? "Hit & Run obligations remain unmet or unknown."
                    : !recycle && !client.Capabilities.Recheck ? "Client cannot recheck files." : null,
                Arguments = unregistered ? new() { ["tracker"] = trackerKey } : new()
            };
        }
    }

    private static IEnumerable<MaintenanceAction> PlanCategories(string id, TorrentClientInstanceConfig cfg, ITorrentClient client, IEnumerable<TorrentInventoryItem> items)
    {
        foreach (var item in items)
        {
            var mapping = cfg.Maintenance.CategoryMappings
                .Where(m => !string.IsNullOrWhiteSpace(m.ClientPath))
                .OrderByDescending(m => m.ClientPath.Length)
                .FirstOrDefault(m => PathBoundaryMatch(item.Torrent.SavePath, m.ClientPath));
            if (mapping != null && !item.Torrent.Category.Equals(mapping.Category, StringComparison.OrdinalIgnoreCase))
                yield return CategoryAction(id, item, mapping.Category, "Save path maps to a different category.",
                    client.Capabilities.Categories ? null : "Client does not support categories.");
            var transition = cfg.Maintenance.CategoryTransitions.FirstOrDefault(t => t.From.Equals(item.Torrent.Category, StringComparison.OrdinalIgnoreCase));
            if (transition != null && item.Torrent.Progress >= 1 && item.Torrent.CompletionOn > 0
                && DateTimeOffset.UtcNow - DateTimeOffset.FromUnixTimeSeconds(item.Torrent.CompletionOn) >= TimeSpan.FromMinutes(transition.DelayMinutes))
                yield return CategoryAction(id, item, transition.To, "Completed torrent reached its category-transition delay.",
                    client.Capabilities.Categories ? null : "Client does not support categories.");
            var assignment = cfg.Maintenance.TrackerAssignments.FirstOrDefault(rule =>
                !string.IsNullOrWhiteSpace(rule.TrackerContains)
                && item.Trackers.Any(t => t.Url.Contains(rule.TrackerContains, StringComparison.OrdinalIgnoreCase)));
            if (assignment != null)
            {
                if (!string.IsNullOrWhiteSpace(assignment.Category)
                    && !item.Torrent.Category.Equals(assignment.Category, StringComparison.OrdinalIgnoreCase))
                    yield return CategoryAction(id, item, assignment.Category, "Tracker assignment selected this category.", client.Capabilities.Categories ? null : "Client does not support categories.");
                var existing = SplitTags(item.Torrent.Tags);
                foreach (var tag in assignment.Tags.Where(tag => !existing.Contains(tag)))
                    yield return TagAction(id, item, tag, "Tracker assignment selected this tag.",
                        client.Capabilities.Tags ? null : "Client does not support tags.");
            }
        }
    }

    private static MaintenanceAction CategoryAction(string id, TorrentInventoryItem item, string category, string reason, string? blockedReason = null) => new()
    {
        Kind = MaintenanceActionKind.SetCategory,
        ClientInstanceId = id,
        TorrentHash = item.Torrent.Hash,
        TorrentName = item.Torrent.Name,
        Reason = reason,
        Arguments = new() { ["category"] = category },
        RequiredCapabilities = ["categories"],
        BlockedReason = blockedReason
    };

    private static IEnumerable<MaintenanceAction> PlanAutomaticManagement(
        string id, TorrentClientInstanceConfig cfg, ITorrentClient client, IEnumerable<TorrentInventoryItem> items)
    {
        if (!MaintenanceRequirements.Enabled(MaintenanceOperation.AutomaticManagement, cfg.Maintenance)) yield break;
        foreach (var item in items)
        {
            var tags = SplitTags(item.Torrent.Tags);
            if (item.Torrent.AutoManaged == true || cfg.Maintenance.AutomaticManagement.IgnoredTags.Any(tags.Contains)) continue;
            yield return new MaintenanceAction
            {
                Kind = MaintenanceActionKind.SetAutomaticManagement,
                ClientInstanceId = id,
                TorrentHash = item.Torrent.Hash,
                TorrentName = item.Torrent.Name,
                Reason = "Automatic torrent management is enforced by configuration.",
                RequiredCapabilities = ["automaticManagement"],
                BlockedReason = client.Capabilities.AutomaticManagement ? null : "Client does not support automatic management."
            };
        }
    }

    private static IEnumerable<MaintenanceAction> PlanPrivateTags(
        string id, TorrentClientInstanceConfig cfg, ITorrentClient client, IEnumerable<TorrentInventoryItem> items)
    {
        var tag = cfg.Maintenance.AutomaticManagement.PrivateTag;
        if (string.IsNullOrWhiteSpace(tag)) yield break;
        foreach (var item in items.Where(i => i.Torrent.IsPrivate == true && !SplitTags(i.Torrent.Tags).Contains(tag)))
            yield return TagAction(id, item, tag, "Torrent is private.", client.Capabilities.PrivateState && client.Capabilities.Tags ? null : "Client cannot inspect private state or apply tags.");
    }

    private IEnumerable<MaintenanceAction> PlanTrackerErrorTags(
        string id, TorrentClientInstanceConfig cfg, ITorrentClient client, IEnumerable<TorrentInventoryItem> items)
    {
        var tag = cfg.Maintenance.AutomaticManagement.TrackerErrorTag;
        if (string.IsNullOrWhiteSpace(tag)) yield break;
        foreach (var item in items.Where(i => !SplitTags(i.Torrent.Tags).Contains(tag) && i.Trackers.Count > 0 && i.Trackers.All(t => t.Status == 0 || !string.IsNullOrWhiteSpace(t.Msg))))
            yield return TagAction(id, item, tag, "No tracker currently reports a healthy state.", client.Capabilities.Tags ? null : "Client does not support tags.");
        if (cfg.Maintenance.AutomaticManagement.RemoveClearedTrackerErrorTag)
            foreach (var item in items.Where(i => SplitTags(i.Torrent.Tags).Contains(tag) && MaintenanceRecovery.HealthyTracker(i.Trackers) && OwnsTag(id, i.Torrent.Hash, tag, i.Torrent.AddedOn)))
                yield return new MaintenanceAction
                {
                    Kind = MaintenanceActionKind.RemoveTag,
                    ClientInstanceId = id,
                    TorrentHash = item.Torrent.Hash,
                    TorrentName = item.Torrent.Name,
                    Arguments = new() { ["tag"] = tag },
                    Reason = "Tracker recovered; remove Torrentarr-owned issue tag.",
                    RequiredCapabilities = ["tags"],
                    BlockedReason = client.Capabilities.Tags ? null : "Client does not support tags."
                };
    }

    private static MaintenanceAction TagAction(string id, TorrentInventoryItem item, string tag, string reason, string? blocked) => new()
    {
        Kind = MaintenanceActionKind.AddTag,
        ClientInstanceId = id,
        TorrentHash = item.Torrent.Hash,
        TorrentName = item.Torrent.Name,
        Reason = reason,
        Arguments = new() { ["tag"] = tag },
        RequiredCapabilities = ["tags"],
        BlockedReason = blocked
    };

    private static IEnumerable<MaintenanceAction> PlanRepair(
        string id, TorrentClientInstanceConfig cfg, ITorrentClient client, IEnumerable<TorrentInventoryItem> items)
    {
        foreach (var item in items.Where(i => (i.Torrent.IsStopped || i.Torrent.State.Contains("paused", StringComparison.OrdinalIgnoreCase))
                     && (i.Torrent.Progress >= 1 ? cfg.Maintenance.Repair.ResumeCompleted : cfg.Maintenance.Repair.RecheckIncomplete))
                     .OrderBy(i => i.Torrent.Size).Take(cfg.Maintenance.Repair.MaxPerRun))
            yield return new MaintenanceAction
            {
                Kind = item.Torrent.Progress >= 1 ? MaintenanceActionKind.Resume : MaintenanceActionKind.Recheck,
                ClientInstanceId = id,
                TorrentHash = item.Torrent.Hash,
                TorrentName = item.Torrent.Name,
                Reason = item.Torrent.Progress >= 1 ? "Paused torrent is complete." : "Paused torrent requires a data recheck.",
                RequiredCapabilities = item.Torrent.Progress >= 1 ? [] : ["recheck"],
                BlockedReason = item.Torrent.Progress < 1 && !client.Capabilities.Recheck ? "Client does not support recheck." : null
            };
    }

    private bool OwnsTag(string id, string hash, string tag, long addedOn)
    {
        using var scope = _scopes.CreateScope();
        var ownership = MaintenancePersistence.Read<TagOwnership>(scope.ServiceProvider.GetRequiredService<TorrentarrDbContext>(),
            MaintenancePersistence.Key(id, hash, "tag:" + tag.ToUpperInvariant()));
        return ownership.Owned && ownership.AddedOn == addedOn;
    }

    private static bool IsOperationScope(TorrentInfo torrent, MaintenanceConfig cfg, MaintenanceOperation operation)
    {
        var policy = MaintenancePolicies.Get(cfg, operation);
        return policy == null || ((policy.IncludedCategories.Count == 0 || policy.IncludedCategories.Contains(torrent.Category, StringComparer.OrdinalIgnoreCase))
            && !policy.ExcludedCategories.Contains(torrent.Category, StringComparer.OrdinalIgnoreCase)
            && !policy.ExcludedTags.Any(SplitTags(torrent.Tags).Contains));
    }



    private bool IsInScope(TorrentInfo torrent, TorrentClientInstanceConfig cfg, TorrentarrConfig? effective = null)
    {
        if (torrent.AddedOn <= 0 || torrent.AddedOn > DateTimeOffset.UtcNow.ToUnixTimeSeconds() || DateTimeOffset.UtcNow - DateTimeOffset.FromUnixTimeSeconds(torrent.AddedOn)
            < TimeSpan.FromSeconds(Math.Max(cfg.Maintenance.MinimumTorrentAgeMinutes * 60L, (effective ?? _config).Settings.IgnoreTorrentsYoungerThan))) return false;
        if (cfg.Maintenance.ExcludedCategories.Contains(torrent.Category, StringComparer.OrdinalIgnoreCase)) return false;
        var tags = SplitTags(torrent.Tags);
        if (cfg.Maintenance.ExcludedTags.Any(tags.Contains)) return false;
        return cfg.Maintenance.Scope.ToLowerInvariant() switch
        {
            "all" => true,
            "explicit" => cfg.Maintenance.ExplicitCategories.Contains(torrent.Category, StringComparer.OrdinalIgnoreCase),
            _ => CategoryPathHelper.MatchesConfigured(torrent.Category, cfg.ManagedCategories, cfg.MatchSubcategories) != null
                || (effective ?? _config).ArrInstances.Values.Any(a =>
                    CategoryPathHelper.MatchesConfigured(torrent.Category, [a.Category], cfg.MatchSubcategories) != null)
        };
    }

    private static bool PathBoundaryMatch(string candidate, string prefix)
    {
        if (string.IsNullOrWhiteSpace(prefix)) return false;
        var fullCandidate = candidate.Replace('\\', '/').TrimEnd('/');
        var fullPrefix = prefix.Replace('\\', '/').TrimEnd('/');
        return fullCandidate.Equals(fullPrefix, StringComparison.OrdinalIgnoreCase)
            || fullCandidate.StartsWith(fullPrefix + "/", StringComparison.OrdinalIgnoreCase);
    }

    private static HashSet<string> SplitTags(string tags)
        => tags.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

    private string Fingerprint()
    {
        var selected = _config.GetAllTorrentClients().Keys.OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(id => id, id =>
            {
                var token = Newtonsoft.Json.Linq.JObject.FromObject(
                    _config.GetTorrentClient(id) ?? new TorrentClientInstanceConfig());
                (token[nameof(TorrentClientInstanceConfig.Maintenance)] as Newtonsoft.Json.Linq.JObject)?.Remove(nameof(MaintenanceConfig.Armed));
                return token;
            });
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(new { Clients = selected, Arr = _config.ArrInstances }))));
    }

    private void PrunePlans()
    {
        foreach (var (id, plan) in _plans.Where(p => p.Value.ExpiresAt <= DateTimeOffset.UtcNow || p.Value.Consumed).ToList())
            _plans.TryRemove(id, out _);
    }

    private static string HistoryPath => Path.Combine(ConfigurationLoader.GetDataDirectoryPath(), "maintenance-history.jsonl");

    private void AppendHistory(MaintenanceRunSummary run)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(HistoryPath)!);
            File.AppendAllText(HistoryPath, JsonConvert.SerializeObject(run) + Environment.NewLine);
            var info = new FileInfo(HistoryPath);
            if (info.Length > 5 * 1024 * 1024)
                File.WriteAllLines(HistoryPath, File.ReadLines(HistoryPath).TakeLast(500).ToList());
        }
        catch (Exception ex) { _logger.LogWarning(ex, "Unable to persist maintenance history"); }
    }

    private void LoadHistory()
    {
        try
        {
            if (!File.Exists(HistoryPath)) return;
            foreach (var line in File.ReadLines(HistoryPath).TakeLast(500))
            {
                var item = JsonConvert.DeserializeObject<MaintenanceRunSummary>(line);
                if (item != null) _history.Add(item);
            }
            _last = _history.LastOrDefault();
        }
        catch (Exception ex) { _logger.LogWarning(ex, "Unable to load maintenance history"); }
    }
}

using System.Collections.Concurrent;
using System.IO.Enumeration;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
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
    private readonly IMaintenanceNotificationService _notifications;
    private readonly ILogger<MaintenanceCoordinator> _logger;
    private readonly ConcurrentDictionary<string, MaintenancePlan> _plans = new();
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _instanceLocks = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<MaintenanceRunSummary> _history = new();
    private readonly object _stateLock = new();
    private CancellationTokenSource? _activeCancellation;
    private MaintenanceRunSummary? _current;
    private MaintenanceRunSummary? _last;

    public MaintenanceCoordinator(
        TorrentarrConfig config,
        ITorrentClientRegistry registry,
        ITorrentInventoryService inventory,
        IPathMappingService paths,
        IHardlinkInspector hardlinks,
        ISafeDeletionService deletion,
        IMaintenanceNotificationService notifications,
        ILogger<MaintenanceCoordinator> logger)
    {
        _config = config;
        _registry = registry;
        _inventory = inventory;
        _paths = paths;
        _hardlinks = hardlinks;
        _deletion = deletion;
        _notifications = notifications;
        _logger = logger;
        LoadHistory();
    }

    public MaintenanceStatus GetStatus()
    {
        lock (_stateLock)
        {
            return new MaintenanceStatus
            {
                Running = _current != null,
                CurrentRun = _current,
                LastRun = _last,
                Armed = _config.GetAllTorrentClients().ToDictionary(
                    pair => pair.Key, pair => pair.Value.Maintenance.Armed, StringComparer.OrdinalIgnoreCase),
                NextScheduledRun = _config.GetAllTorrentClients().ToDictionary(
                    pair => pair.Key,
                    pair => pair.Value.Maintenance.Enabled
                        ? CronSchedule.Next(pair.Value.Maintenance.Schedule, DateTimeOffset.UtcNow)?.ToString("O")
                        : null,
                    StringComparer.OrdinalIgnoreCase)
            };
        }
    }

    public async Task<MaintenancePlan> PreviewAsync(MaintenancePreviewRequest request, CancellationToken ct = default)
    {
        PrunePlans();
        var allConfigs = _config.GetAllTorrentClients();
        var ids = request.ClientInstanceIds.Count > 0
            ? request.ClientInstanceIds.Distinct(StringComparer.OrdinalIgnoreCase).ToList()
            : allConfigs.Where(p => p.Value.Maintenance.Enabled).Select(p => p.Key).ToList();
        var operations = request.Operations.Count > 0
            ? request.Operations.Distinct().ToList()
            : Enum.GetValues<MaintenanceOperation>().ToList();
        var expiryMinutes = ids.Select(id => allConfigs.GetValueOrDefault(id)?.Maintenance.PlanTtlMinutes ?? 30).DefaultIfEmpty(30).Min();
        var actions = new List<MaintenanceAction>();
        var warnings = new List<string>();
        var critical = new List<string>();

        foreach (var id in ids)
        {
            if (!allConfigs.TryGetValue(id, out var cfg))
            {
                critical.Add($"Unknown torrent client '{id}'.");
                continue;
            }
            critical.AddRange(_paths.Validate(cfg.Maintenance.PathMappings).Select(e => $"[{id}] {e}"));
            if (_registry.GetClient(id) == null) critical.Add($"[{id}] Torrent client is disconnected.");
        }

        // References must always include every connected client: selected IDs limit
        // actions, not ownership discovery for shared download roots.
        var inventory = await _inventory.BuildAsync(
            operations.Contains(MaintenanceOperation.OrphanScan) ? null : ids, ct);
        warnings.AddRange(inventory.Warnings);
        if (operations.Contains(MaintenanceOperation.OrphanScan))
        {
            foreach (var id in allConfigs.Keys)
                if (_registry.GetClient(id) == null)
                    critical.Add($"[{id}] Torrent client is disconnected; orphan actions are blocked.");
            critical.AddRange(inventory.Warnings.Select(w => $"Orphan inventory incomplete: {w}"));
        }
        foreach (var id in ids)
        {
            if (!allConfigs.TryGetValue(id, out var cfg)) continue;
            var client = _registry.GetClient(id);
            if (client == null) continue;
            var items = inventory.Torrents.Where(t =>
                t.Torrent.ClientInstanceId.Equals(id, StringComparison.OrdinalIgnoreCase)
                && IsInScope(t.Torrent, cfg)).ToList();

            if (operations.Contains(MaintenanceOperation.OrphanScan) && cfg.Maintenance.Orphans.Enabled)
                actions.AddRange(PlanOrphans(id, cfg, inventory));
            if (operations.Contains(MaintenanceOperation.HardlinkAudit) && cfg.Maintenance.Hardlinks.Enabled)
                actions.AddRange(await PlanHardlinksAsync(id, cfg, client, items, ct));
            if (operations.Contains(MaintenanceOperation.UnregisteredCleanup) && cfg.Maintenance.Unregistered.Enabled)
                actions.AddRange(PlanUnregistered(id, cfg, items));
            if (operations.Contains(MaintenanceOperation.CategoryReconcile))
                actions.AddRange(PlanCategories(id, cfg, client, items));
            if (operations.Contains(MaintenanceOperation.AutomaticManagement))
                actions.AddRange(PlanAutomaticManagement(id, cfg, client, items));
            if (operations.Contains(MaintenanceOperation.PrivateTagging))
                actions.AddRange(PlanPrivateTags(id, cfg, client, items));
            if (operations.Contains(MaintenanceOperation.TrackerErrorTagging))
                actions.AddRange(PlanTrackerErrorTags(id, cfg, client, items));
            if (operations.Contains(MaintenanceOperation.RepairPaused) && cfg.Maintenance.Repair.Enabled)
                actions.AddRange(PlanRepair(id, cfg, client, items));
            if (operations.Contains(MaintenanceOperation.SharePolicy))
                actions.AddRange(PlanSharePolicies(id, cfg, client, items, inventory));
            if (operations.Contains(MaintenanceOperation.RecycleRetention) && cfg.Maintenance.RecycleBin.Enabled)
                actions.Add(new MaintenanceAction
                {
                    Kind = MaintenanceActionKind.DeleteExpiredRecycleItem,
                    ClientInstanceId = id,
                    Reason = "Recycle-bin retention cleanup.",
                    Destructive = true
                });
        }

        var plan = new MaintenancePlan
        {
            ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(Math.Max(1, expiryMinutes)),
            ConfigurationFingerprint = Fingerprint(ids),
            ClientInstanceIds = ids,
            Operations = operations,
            Actions = actions,
            Warnings = warnings,
            CriticalErrors = critical
        };
        _plans[plan.PlanId] = plan;
        _notifications.Publish(new("preview-completed", DateTimeOffset.UtcNow, null,
            $"Maintenance preview contains {plan.CandidateCount} actionable and {plan.BlockedCount} blocked actions.",
            new { plan.PlanId, plan.ClientInstanceIds, plan.CandidateCount, plan.BlockedCount, plan.CandidateBytes }));
        return plan;
    }

    public async Task<MaintenanceRunSummary> ApplyAsync(string planId, CancellationToken ct = default)
    {
        if (!_plans.TryGetValue(planId, out var plan)) throw new KeyNotFoundException("Maintenance plan was not found.");
        if (plan.Consumed) throw new InvalidOperationException("Maintenance plan has already been consumed.");
        if (plan.ExpiresAt <= DateTimeOffset.UtcNow) throw new InvalidOperationException("Maintenance plan has expired.");
        if (plan.CriticalErrors.Count > 0) throw new InvalidOperationException("Maintenance plan contains critical validation errors.");
        if (plan.ClientInstanceIds.Any(id => !(_config.GetTorrentClient(id)?.Maintenance.Armed ?? false)))
            throw new InvalidOperationException("Maintenance must be explicitly armed after preview before applying mutations.");
        if (!CryptographicOperations.FixedTimeEquals(
                Encoding.UTF8.GetBytes(plan.ConfigurationFingerprint),
                Encoding.UTF8.GetBytes(Fingerprint(plan.ClientInstanceIds))))
            throw new InvalidOperationException("Maintenance configuration changed after preview.");
        plan.Consumed = true;
        return await ApplyPlanAsync(plan, "apply", ct);
    }

    public Task ArmAsync(MaintenanceArmRequest request, CancellationToken ct = default)
    {
        if (!_plans.TryGetValue(request.PlanId, out var plan)
            || plan.Consumed
            || plan.ExpiresAt <= DateTimeOffset.UtcNow
            || plan.CriticalErrors.Count > 0
            || plan.ConfigurationFingerprint != Fingerprint(plan.ClientInstanceIds))
            throw new InvalidOperationException("A current successful preview is required before arming maintenance.");
        var selected = request.ClientInstanceIds.Count > 0 ? request.ClientInstanceIds : plan.ClientInstanceIds;
        var outside = selected.Where(id => !plan.ClientInstanceIds.Contains(id, StringComparer.OrdinalIgnoreCase)).ToList();
        if (outside.Count > 0)
            throw new InvalidOperationException($"Clients were not part of the preview: {string.Join(", ", outside)}");
        foreach (var id in selected)
        {
            var cfg = _config.GetTorrentClient(id) ?? throw new KeyNotFoundException($"Unknown torrent client '{id}'.");
            if (!cfg.Maintenance.Enabled)
                throw new InvalidOperationException($"Maintenance is disabled for '{id}'.");
            cfg.Maintenance.Armed = true;
        }
        return Task.CompletedTask;
    }

    public void Disarm(IEnumerable<string> instanceIds)
    {
        foreach (var id in instanceIds)
        {
            var cfg = _config.GetTorrentClient(id);
            if (cfg != null) cfg.Maintenance.Armed = false;
        }
    }

    public async Task<MaintenanceRunSummary> RunAsync(IEnumerable<string>? instanceIds = null, string trigger = "manual", CancellationToken ct = default)
    {
        var ids = instanceIds?.Distinct(StringComparer.OrdinalIgnoreCase).ToList()
            ?? _config.GetAllTorrentClients().Where(p => p.Value.Maintenance.Enabled && p.Value.Maintenance.Armed).Select(p => p.Key).ToList();
        foreach (var id in ids)
        {
            var cfg = _config.GetTorrentClient(id);
            if (cfg == null || !cfg.Maintenance.Enabled || !cfg.Maintenance.Armed)
                throw new InvalidOperationException($"Maintenance is not enabled and armed for '{id}'.");
        }
        var plan = await PreviewAsync(new MaintenancePreviewRequest { ClientInstanceIds = ids }, ct);
        if (plan.CriticalErrors.Count > 0) throw new InvalidOperationException(string.Join("; ", plan.CriticalErrors));
        plan.Consumed = true;
        return await ApplyPlanAsync(plan, trigger, ct);
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
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var now = DateTime.UtcNow;
                var minute = new DateTime(now.Year, now.Month, now.Day, now.Hour, now.Minute, 0, DateTimeKind.Utc);
                if (lastMinute != minute)
                {
                    lastMinute = minute;
                    var due = _config.GetAllTorrentClients()
                        .Where(pair => pair.Value.Maintenance.Enabled && pair.Value.Maintenance.Armed
                            && CronSchedule.Matches(pair.Value.Maintenance.Schedule, minute))
                        .Select(pair => pair.Key).ToList();
                    if (due.Count > 0) await RunAsync(due, "scheduled", stoppingToken);
                }
                await Task.Delay(TimeSpan.FromSeconds(15), stoppingToken);
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

    private async Task<MaintenanceRunSummary> ApplyPlanAsync(MaintenancePlan plan, string trigger, CancellationToken outerCt)
    {
        var locks = plan.ClientInstanceIds.OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
            .Select(id => _instanceLocks.GetOrAdd(id, _ => new SemaphoreSlim(1, 1))).ToList();
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(outerCt);
        var run = new MaintenanceRunSummary
        {
            Trigger = trigger,
            ClientInstanceIds = plan.ClientInstanceIds,
            Planned = plan.CandidateCount
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
            foreach (var action in plan.Actions)
            {
                linked.Token.ThrowIfCancellationRequested();
                if (action.BlockedReason != null)
                {
                    run.Skipped++;
                    _notifications.Publish(new("skipped-unsafe-action", DateTimeOffset.UtcNow, action.ClientInstanceId, action.BlockedReason, action));
                    continue;
                }
                try
                {
                    var staleReason = await RevalidateActionAsync(action, linked.Token);
                    if (staleReason != null)
                    {
                        run.Skipped++;
                        _notifications.Publish(new("skipped-unsafe-action", DateTimeOffset.UtcNow, action.ClientInstanceId, staleReason, action));
                        continue;
                    }
                    var result = await ApplyActionAsync(action, linked.Token);
                    if (result.Success)
                    {
                        run.Applied++;
                        run.BytesMoved += result.BytesMoved;
                        _notifications.Publish(new(EventType(action), DateTimeOffset.UtcNow, action.ClientInstanceId, action.Reason, action));
                    }
                    else
                    {
                        run.Failed++;
                        run.Errors.Add(result.Error ?? $"Action {action.Id} failed.");
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    run.Failed++;
                    run.Errors.Add($"{action.Kind} for {action.TorrentName ?? action.Path}: {ex.Message}");
                    _logger.LogError(ex, "Maintenance action {ActionId} failed", action.Id);
                    _notifications.Publish(new("operation-failure", DateTimeOffset.UtcNow, action.ClientInstanceId, ex.Message, action));
                }
            }
            run.Result = run.Failed > 0 ? "partial" : "success";
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
            _notifications.Publish(new("apply-completed", DateTimeOffset.UtcNow, null,
                $"Maintenance run completed with result {run.Result}.", run));
            foreach (var gate in acquired) gate.Release();
        }
        return run;
    }

    private async Task<SafeDeletionResult> ApplyActionAsync(MaintenanceAction action, CancellationToken ct)
    {
        var client = _registry.GetClient(action.ClientInstanceId)
            ?? throw new InvalidOperationException("Torrent client disconnected before apply.");
        switch (action.Kind)
        {
            case MaintenanceActionKind.QuarantineFile:
                return await _deletion.QuarantineFileAsync(action.ClientInstanceId, action.Path!, ct);
            case MaintenanceActionKind.AddTag:
                return Result(await client.AddTagsAsync([action.TorrentHash!], [action.Arguments["tag"]], ct));
            case MaintenanceActionKind.RemoveTag:
                return Result(await client.RemoveTagsAsync([action.TorrentHash!], [action.Arguments["tag"]], ct));
            case MaintenanceActionKind.SetCategory:
                return Result(await client.SetCategoryAsync([action.TorrentHash!], action.Arguments["category"], ct));
            case MaintenanceActionKind.SetAutomaticManagement:
                return Result(await client.SetAutomaticManagementAsync([action.TorrentHash!], true, ct));
            case MaintenanceActionKind.Recheck:
                return Result(await client.RecheckTorrentsAsync([action.TorrentHash!], ct));
            case MaintenanceActionKind.Resume:
                return Result(await client.ResumeTorrentsAsync([action.TorrentHash!], ct));
            case MaintenanceActionKind.Stop:
                return Result(await client.PauseTorrentsAsync([action.TorrentHash!], ct));
            case MaintenanceActionKind.RemoveTorrent:
                return await _deletion.RemoveTorrentAsync(action.ClientInstanceId, action.TorrentHash!, false, ct: ct);
            case MaintenanceActionKind.RecycleContent:
                return await _deletion.RemoveTorrentAsync(action.ClientInstanceId, action.TorrentHash!, true, ct: ct);
            case MaintenanceActionKind.DeleteContent:
                return await _deletion.RemoveTorrentAsync(action.ClientInstanceId, action.TorrentHash!, true, permanent: true, ct: ct);
            case MaintenanceActionKind.SetUploadLimit:
                return Result(await client.SetUploadLimitAsync(action.TorrentHash!, long.Parse(action.Arguments["limitKiB"]) * 1024, ct));
            case MaintenanceActionKind.EnableSuperSeeding:
                return Result(await client.SetSuperSeedingAsync(action.TorrentHash!, true, ct));
            case MaintenanceActionKind.DeleteExpiredRecycleItem:
                await _deletion.CleanupExpiredAsync(action.ClientInstanceId, ct);
                return new SafeDeletionResult(true, false, 0);
            default:
                return new SafeDeletionResult(false, true, 0, $"Unsupported maintenance action {action.Kind}.");
        }
    }

    private async Task<string?> RevalidateActionAsync(MaintenanceAction action, CancellationToken ct)
    {
        var client = _registry.GetClient(action.ClientInstanceId);
        if (client == null) return "Torrent client disconnected after preview.";
        if (action.Kind == MaintenanceActionKind.QuarantineFile && action.Path != null)
        {
            var inventory = await _inventory.BuildAsync(null, ct);
            var normalized = Path.GetFullPath(action.Path);
            if (inventory.LocalPathReferences.Keys.Any(path =>
                    path.Equals(normalized, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal)
                    || path.StartsWith(normalized.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar,
                        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal)))
                return "File is no longer orphaned; a torrent now references this path.";
        }
        if (action.TorrentHash != null)
        {
            var current = await client.GetTorrentsAsync(cancellationToken: ct);
            var torrent = current.FirstOrDefault(t => t.Hash.Equals(action.TorrentHash, StringComparison.OrdinalIgnoreCase));
            if (torrent == null)
                return "Torrent state changed after preview; the torrent no longer exists.";
            if (action.Destructive)
            {
                torrent.ClientInstanceId = action.ClientInstanceId;
                var cfg = _config.GetTorrentClient(action.ClientInstanceId);
                if (cfg == null) return "Torrent client configuration was removed after preview.";
                if (torrent.AddedOn <= 0 || DateTimeOffset.UtcNow - DateTimeOffset.FromUnixTimeSeconds(torrent.AddedOn)
                    < TimeSpan.FromMinutes(cfg.Maintenance.MinimumTorrentAgeMinutes))
                    return "Torrent is younger than the configured maintenance safety window.";
                var inventory = await _inventory.BuildAsync([action.ClientInstanceId], ct);
                var item = inventory.Torrents.FirstOrDefault(i => i.Torrent.Hash.Equals(action.TorrentHash, StringComparison.OrdinalIgnoreCase));
                if (item == null) return "Torrent inventory changed after preview.";
                var hnr = HnrBlocked(cfg, item);
                if (hnr != null) return hnr;
            }
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
                "superseeding" => client.Capabilities.SuperSeeding,
                "pertorrentlimits" => client.Capabilities.PerTorrentLimits,
                _ => false
            };
            if (!supported) return $"Required capability '{capability}' is no longer available.";
        }
        return null;
    }

    private static SafeDeletionResult Result(bool success)
        => success ? new(true, true, 0) : new(false, true, 0, "Torrent client rejected the operation.");

    private static string EventType(MaintenanceAction action) => action.Kind switch
    {
        MaintenanceActionKind.QuarantineFile or MaintenanceActionKind.RecycleContent => "quarantine",
        MaintenanceActionKind.DeleteContent or MaintenanceActionKind.DeleteExpiredRecycleItem => "permanent-deletion",
        MaintenanceActionKind.SetCategory or MaintenanceActionKind.AddTag or MaintenanceActionKind.RemoveTag => "category-tag-change",
        MaintenanceActionKind.Recheck or MaintenanceActionKind.Resume => "repair",
        _ => "action-completed"
    };

    private IEnumerable<MaintenanceAction> PlanOrphans(string id, TorrentClientInstanceConfig cfg, TorrentInventory inventory)
    {
        var referenced = inventory.LocalPathReferences.Keys.ToHashSet(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        var yielded = 0;
        foreach (var mapping in cfg.Maintenance.PathMappings)
        {
            if (!Directory.Exists(mapping.LocalPath)) continue;
            foreach (var file in Directory.EnumerateFiles(mapping.LocalPath, "*", SearchOption.AllDirectories))
            {
                if (yielded >= cfg.Maintenance.Orphans.MaxFilesPerRun) yield break;
                var recycleRoot = cfg.Maintenance.RecycleBin.Path;
                if (string.IsNullOrWhiteSpace(recycleRoot) && cfg.Maintenance.PathMappings.Count > 0)
                    recycleRoot = Path.Combine(cfg.Maintenance.PathMappings[0].LocalPath, ".torrentarr-recycle");
                if (!string.IsNullOrWhiteSpace(recycleRoot) && _paths.IsSafeDescendant(file, recycleRoot)) continue;
                if (referenced.Contains(Path.GetFullPath(file))) continue;
                var relative = Path.GetRelativePath(mapping.LocalPath, file);
                if (cfg.Maintenance.Orphans.ExcludePatterns.Any(pattern => FileSystemName.MatchesSimpleExpression(pattern, relative, OperatingSystem.IsWindows()))) continue;
                if (File.GetLastWriteTimeUtc(file) > DateTime.UtcNow.AddMinutes(-cfg.Maintenance.Orphans.MinimumAgeMinutes)) continue;
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
        }
    }

    private async Task<IEnumerable<MaintenanceAction>> PlanHardlinksAsync(
        string id, TorrentClientInstanceConfig cfg, ITorrentClient client,
        IEnumerable<TorrentInventoryItem> items, CancellationToken ct)
    {
        var actions = new List<MaintenanceAction>();
        foreach (var item in items)
        {
            if (cfg.Maintenance.Hardlinks.Categories.Count > 0
                && !cfg.Maintenance.Hardlinks.Categories.Contains(item.Torrent.Category, StringComparer.OrdinalIgnoreCase)) continue;
            var tags = SplitTags(item.Torrent.Tags);
            if (cfg.Maintenance.Hardlinks.ExcludedTags.Any(tags.Contains)) continue;
            var largest = item.Files.Where(f => f.LocalPath != null).OrderByDescending(f => f.File.Size).FirstOrDefault();
            if (largest?.LocalPath == null) continue;
            var links = await _hardlinks.GetLinkCountAsync(largest.LocalPath, ct);
            if (links is > 1) continue;
            actions.Add(new MaintenanceAction
            {
                Kind = MaintenanceActionKind.AddTag,
                ClientInstanceId = id,
                TorrentHash = item.Torrent.Hash,
                TorrentName = item.Torrent.Name,
                Path = largest.LocalPath,
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

    private static IEnumerable<MaintenanceAction> PlanUnregistered(string id, TorrentClientInstanceConfig cfg, IEnumerable<TorrentInventoryItem> items)
    {
        var perTracker = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var total = 0;
        foreach (var item in items)
        {
            if (cfg.Maintenance.Unregistered.CompletedOnly && item.Torrent.Progress < 1) continue;
            if (HnrBlocked(cfg, item) != null) continue;
            if (item.Torrent.AddedOn > 0 && DateTimeOffset.UtcNow - DateTimeOffset.FromUnixTimeSeconds(item.Torrent.AddedOn) < TimeSpan.FromMinutes(cfg.Maintenance.Unregistered.GraceMinutes)) continue;
            var tracker = item.Trackers.FirstOrDefault(t => cfg.Maintenance.Unregistered.Messages.Any(message => t.Msg.Contains(message, StringComparison.OrdinalIgnoreCase)));
            if (tracker == null) continue;
            perTracker.TryGetValue(tracker.Url, out var count);
            if (count >= cfg.Maintenance.Unregistered.MaxPerTrackerPerRun) continue;
            if (total >= cfg.Maintenance.RecycleBin.MaxTorrentMovesPerRun) yield break;
            perTracker[tracker.Url] = count + 1;
            total++;
            yield return new MaintenanceAction
            {
                Kind = MaintenanceActionKind.RecycleContent,
                ClientInstanceId = id,
                TorrentHash = item.Torrent.Hash,
                TorrentName = item.Torrent.Name,
                Bytes = item.Files.Sum(f => f.File.Size),
                Reason = $"Tracker reports torrent as unregistered: {tracker.Msg}",
                RequiredCapabilities = ["contentDeletion"],
                Destructive = true
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
                    yield return CategoryAction(id, item, assignment.Category, "Tracker assignment selected this category.");
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
        if (!cfg.Maintenance.AutomaticManagement.Enabled) yield break;
        foreach (var item in items)
        {
            var tags = SplitTags(item.Torrent.Tags);
            if (cfg.Maintenance.AutomaticManagement.IgnoredTags.Any(tags.Contains)) continue;
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

    private static IEnumerable<MaintenanceAction> PlanTrackerErrorTags(
        string id, TorrentClientInstanceConfig cfg, ITorrentClient client, IEnumerable<TorrentInventoryItem> items)
    {
        var tag = cfg.Maintenance.AutomaticManagement.TrackerErrorTag;
        if (string.IsNullOrWhiteSpace(tag)) yield break;
        foreach (var item in items.Where(i => i.Trackers.Count > 0 && i.Trackers.All(t => t.Status == 0 || !string.IsNullOrWhiteSpace(t.Msg))))
            yield return TagAction(id, item, tag, "No tracker currently reports a healthy state.", client.Capabilities.Tags ? null : "Client does not support tags.");
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
        foreach (var item in items.Where(i => i.Torrent.IsStopped || i.Torrent.State.Contains("paused", StringComparison.OrdinalIgnoreCase))
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

    private static IEnumerable<MaintenanceAction> PlanSharePolicies(
        string id, TorrentClientInstanceConfig cfg, ITorrentClient client,
        IEnumerable<TorrentInventoryItem> items, TorrentInventory inventory)
    {
        var destructivePlanned = 0;
        foreach (var item in items)
        {
            var policy = cfg.Maintenance.SharePolicies.Where(p => Matches(p, item)).OrderByDescending(p => p.Priority).FirstOrDefault();
            if (policy == null || !ReachedMaximum(policy, item.Torrent)) continue;
            var shared = item.Files.Any(f => f.LocalPath != null
                && inventory.LocalPathReferences.TryGetValue(Path.GetFullPath(f.LocalPath), out var owners)
                && owners.Count > 1);
            var safety = HnrBlocked(cfg, item) ?? (policy.MinimumSeedCount > item.Torrent.CompletePeers
                ? $"Seed count {item.Torrent.CompletePeers} is below minimum {policy.MinimumSeedCount}."
                : item.Torrent.SeedingTime < policy.MinimumSeedingMinutes * 60L
                    ? "Minimum seeding time has not been reached."
                    : null);
            var kind = policy.Action.ToLowerInvariant() switch
            {
                "remove" => MaintenanceActionKind.RemoveTorrent,
                "recycle" => MaintenanceActionKind.RecycleContent,
                "delete" => MaintenanceActionKind.DeleteContent,
                "superseed" => MaintenanceActionKind.EnableSuperSeeding,
                "throttle" => MaintenanceActionKind.SetUploadLimit,
                _ => MaintenanceActionKind.Stop
            };
            var blocked = safety;
            if (kind == MaintenanceActionKind.EnableSuperSeeding && !client.Capabilities.SuperSeeding) blocked = "Client does not support super-seeding.";
            if (kind == MaintenanceActionKind.SetUploadLimit && !client.Capabilities.PerTorrentLimits) blocked = "Client does not support per-torrent limits.";
            if (kind is MaintenanceActionKind.RecycleContent or MaintenanceActionKind.DeleteContent)
            {
                if (destructivePlanned >= cfg.Maintenance.RecycleBin.MaxTorrentMovesPerRun)
                    blocked = $"Per-run content move limit ({cfg.Maintenance.RecycleBin.MaxTorrentMovesPerRun}) reached.";
                else if (blocked == null)
                    destructivePlanned++;
            }
            if (safety != null && client.Capabilities.Tags && !string.IsNullOrWhiteSpace(policy.WaitingTag)
                && !SplitTags(item.Torrent.Tags).Contains(policy.WaitingTag))
                yield return TagAction(id, item, policy.WaitingTag, safety, null);
            yield return new MaintenanceAction
            {
                Kind = kind,
                ClientInstanceId = id,
                TorrentHash = item.Torrent.Hash,
                TorrentName = item.Torrent.Name,
                Reason = $"Matched share policy '{policy.Name}'." + (shared ? " Shared content will be preserved." : ""),
                Arguments = policy.UploadLimitKiB.HasValue ? new() { ["limitKiB"] = policy.UploadLimitKiB.Value.ToString() } : new(),
                Bytes = item.Files.Sum(f => f.File.Size),
                Destructive = kind is MaintenanceActionKind.RecycleContent or MaintenanceActionKind.DeleteContent,
                BlockedReason = kind == MaintenanceActionKind.SetUploadLimit && !policy.UploadLimitKiB.HasValue
                    ? "Throttle policy requires UploadLimitKiB."
                    : blocked
            };
        }
    }

    private bool IsInScope(TorrentInfo torrent, TorrentClientInstanceConfig cfg)
    {
        if (torrent.AddedOn <= 0 || DateTimeOffset.UtcNow - DateTimeOffset.FromUnixTimeSeconds(torrent.AddedOn)
            < TimeSpan.FromMinutes(cfg.Maintenance.MinimumTorrentAgeMinutes)) return false;
        if (cfg.Maintenance.ExcludedCategories.Contains(torrent.Category, StringComparer.OrdinalIgnoreCase)) return false;
        var tags = SplitTags(torrent.Tags);
        if (cfg.Maintenance.ExcludedTags.Any(tags.Contains)) return false;
        return cfg.Maintenance.Scope.ToLowerInvariant() switch
        {
            "all" => true,
            "explicit" => cfg.Maintenance.ExplicitCategories.Contains(torrent.Category, StringComparer.OrdinalIgnoreCase),
            _ => CategoryPathHelper.MatchesConfigured(torrent.Category, cfg.ManagedCategories, cfg.MatchSubcategories) != null
                || _config.ArrInstances.Values.Any(a =>
                    CategoryPathHelper.MatchesConfigured(torrent.Category, [a.Category], cfg.MatchSubcategories) != null)
        };
    }

    private static bool PathBoundaryMatch(string candidate, string prefix)
    {
        if (string.IsNullOrWhiteSpace(prefix)) return false;
        var fullCandidate = Path.GetFullPath(candidate);
        var fullPrefix = Path.GetFullPath(prefix).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return fullCandidate.Equals(fullPrefix, StringComparison.OrdinalIgnoreCase)
            || fullCandidate.StartsWith(fullPrefix + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
            || fullCandidate.StartsWith(fullPrefix + Path.AltDirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    private static HashSet<string> SplitTags(string tags)
        => tags.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

    private static bool Matches(SharePolicyConfig policy, TorrentInventoryItem item)
    {
        var tags = SplitTags(item.Torrent.Tags);
        if (policy.Categories.Count > 0 && !policy.Categories.Contains(item.Torrent.Category, StringComparer.OrdinalIgnoreCase)) return false;
        if (policy.Tags.Count > 0 && !policy.Tags.All(tags.Contains)) return false;
        if (policy.Trackers.Count > 0 && !item.Trackers.Any(t => policy.Trackers.Any(p => t.Url.Contains(p, StringComparison.OrdinalIgnoreCase)))) return false;
        if (policy.CompletionState.Equals("completed", StringComparison.OrdinalIgnoreCase) && item.Torrent.Progress < 1) return false;
        if (policy.CompletionState.Equals("incomplete", StringComparison.OrdinalIgnoreCase) && item.Torrent.Progress >= 1) return false;
        if (policy.Privacy.Equals("private", StringComparison.OrdinalIgnoreCase) && item.Torrent.IsPrivate != true) return false;
        if (policy.Privacy.Equals("public", StringComparison.OrdinalIgnoreCase) && item.Torrent.IsPrivate != false) return false;
        return true;
    }

    private static bool ReachedMaximum(SharePolicyConfig policy, TorrentInfo torrent)
    {
        if (policy.MaximumRatio.HasValue && torrent.Ratio >= policy.MaximumRatio.Value) return true;
        if (policy.MaximumSeedingMinutes.HasValue && torrent.SeedingTime >= policy.MaximumSeedingMinutes.Value * 60L) return true;
        return policy.MaximumInactiveMinutes.HasValue && torrent.LastActivity > 0
            && DateTimeOffset.UtcNow - DateTimeOffset.FromUnixTimeSeconds(torrent.LastActivity) >= TimeSpan.FromMinutes(policy.MaximumInactiveMinutes.Value);
    }

    private static string? HnrBlocked(TorrentClientInstanceConfig cfg, TorrentInventoryItem item)
    {
        var effective = SeedingService.ApplyCategoryOverride(cfg.CategorySeeding, item.Torrent.Category, cfg.MatchSubcategories);
        if (effective.HitAndRunMode is not null && !effective.HitAndRunMode.Equals("disabled", StringComparison.OrdinalIgnoreCase)
            && item.Torrent.Progress < effective.HitAndRunMinimumDownloadPercent / 100.0
            && item.Torrent.Ratio < effective.HitAndRunPartialSeedRatio)
            return "Partial-download H&R obligations are not met.";
        var rules = cfg.Trackers.Count > 0 ? cfg.Trackers : new List<TrackerConfig>();
        if (rules.Count == 0)
        {
            if (effective.HitAndRunMode is not null && !effective.HitAndRunMode.Equals("disabled", StringComparison.OrdinalIgnoreCase))
            {
                var ratioMet = effective.MinSeedRatio <= 0 || item.Torrent.Ratio >= effective.MinSeedRatio;
                var timeMet = effective.MinSeedingTimeDays <= 0 || item.Torrent.SeedingTime >= effective.MinSeedingTimeDays * 86400L;
                var met = effective.HitAndRunMode.Equals("and", StringComparison.OrdinalIgnoreCase) ? ratioMet && timeMet : ratioMet || timeMet;
                if (!met) return "Category H&R obligations are not met.";
            }
            return null;
        }
        foreach (var tracker in rules.Where(t => item.Trackers.Any(actual => actual.Url.Contains(t.Uri, StringComparison.OrdinalIgnoreCase))))
        {
            if (string.IsNullOrWhiteSpace(tracker.HitAndRunMode) || tracker.HitAndRunMode.Equals("disabled", StringComparison.OrdinalIgnoreCase)) continue;
            var ratioMet = !tracker.MinSeedRatio.HasValue || item.Torrent.Ratio >= tracker.MinSeedRatio.Value;
            var timeMet = !tracker.MinSeedingTimeDays.HasValue || item.Torrent.SeedingTime >= tracker.MinSeedingTimeDays.Value * 86400L;
            var met = tracker.HitAndRunMode.Equals("and", StringComparison.OrdinalIgnoreCase) ? ratioMet && timeMet : ratioMet || timeMet;
            if (!met) return $"H&R obligations for tracker '{tracker.Name ?? tracker.Uri}' are not met.";
        }
        return null;
    }

    private string Fingerprint(IEnumerable<string> ids)
    {
        var selected = ids.OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(id => id, id =>
            {
                var token = Newtonsoft.Json.Linq.JObject.FromObject(
                    _config.GetTorrentClient(id)?.Maintenance ?? new MaintenanceConfig());
                token.Remove(nameof(MaintenanceConfig.Armed));
                return token;
            });
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(selected))));
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

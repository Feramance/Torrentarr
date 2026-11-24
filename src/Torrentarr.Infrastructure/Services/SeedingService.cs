using Torrentarr.Core.Configuration;
using Torrentarr.Core.Interfaces;
using Torrentarr.Core.Models;
using Torrentarr.Core.Services;
using Torrentarr.Infrastructure.ApiClients.QBittorrent;
using Torrentarr.Infrastructure.Database;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Torrentarr.Infrastructure.Services;

/// <summary>
/// Service for managing torrent seeding rules and Hit &amp; Run protection.
/// Matches qBitrr's qbit_category_manager.py implementation exactly.
/// </summary>
public class SeedingService : ISeedingService
{
    private const string AllowedSeedingTag = "qBitrr-allowed_seeding";
    private const string FreeSpacePausedTag = "qBitrr-free_space_paused";
    private const string IgnoredTag = "qBitrr-ignored";
    private const string HnrActiveTag = "qBitrr-hnr_active";

    private static readonly HashSet<string> DeadTrackerKeywords = new(StringComparer.OrdinalIgnoreCase)
    {
        "unregistered torrent",
        "torrent not registered",
        "info hash is not authorized",
        "torrent is not authorized",
        "torrent not found"
    };

    /// <summary>Returns true when a tracker status message indicates the torrent is unregistered/dead on that tracker.</summary>
    internal static bool TrackerMessageIndicatesDead(string? message)
    {
        if (string.IsNullOrWhiteSpace(message))
            return false;
        var messageText = message.ToLowerInvariant();
        return DeadTrackerKeywords.Any(keyword => messageText.Contains(keyword));
    }

    private readonly ILogger<SeedingService> _logger;
    private readonly TorrentarrDbContext _dbContext;
    private readonly TorrentarrConfig _config;
    private readonly ITorrentClientRegistry _qbitManager;
    private readonly StalledUploadTracker _stalledUploads;
    private readonly TimeProvider _clock;

    public SeedingService(
        ILogger<SeedingService> logger,
        TorrentarrDbContext dbContext,
        TorrentarrConfig config,
        ITorrentClientRegistry qbitManager,
        StalledUploadTracker? stalledUploads = null, TimeProvider? clock = null)
    {
        _logger = logger;
        _dbContext = dbContext;
        _config = config;
        _qbitManager = qbitManager;
        _stalledUploads = stalledUploads ?? new StalledUploadTracker();
        _clock = clock ?? TimeProvider.System;
    }

    /// <summary>Returns the seeding config for the owning qBit instance with category overrides applied.</summary>
    private CategorySeedingConfig GetSeedingConfig(TorrentInfo torrent)
    {
        var qbit = _config.GetTorrentClient(torrent.QBitInstanceName);
        var baseConfig = qbit?.CategorySeeding ?? new CategorySeedingConfig();
        return ApplyCategoryOverride(baseConfig, torrent.Category, qbit?.MatchSubcategories ?? false);
    }

    public static CategorySeedingConfig ApplyCategoryOverride(
        CategorySeedingConfig baseConfig,
        string? category,
        bool matchSubcategories)
    {
        var categoryOverride = CategoryPathHelper.FindSeedingOverride(baseConfig, category, matchSubcategories);
        if (categoryOverride == null)
            return baseConfig;

        return new CategorySeedingConfig
        {
            MinimumReportedSeeders = categoryOverride.MinimumReportedSeeders ?? baseConfig.MinimumReportedSeeders,
            MinimumUploadInactivitySeconds = categoryOverride.MinimumUploadInactivitySeconds ?? baseConfig.MinimumUploadInactivitySeconds,
            DownloadRateLimitPerTorrent = categoryOverride.DownloadRateLimitPerTorrent ?? baseConfig.DownloadRateLimitPerTorrent,
            UploadRateLimitPerTorrent = categoryOverride.UploadRateLimitPerTorrent ?? baseConfig.UploadRateLimitPerTorrent,
            MaxUploadRatio = categoryOverride.MaxUploadRatio ?? baseConfig.MaxUploadRatio,
            MaxSeedingTime = categoryOverride.MaxSeedingTime ?? baseConfig.MaxSeedingTime,
            RemoveTorrent = baseConfig.RemoveTorrent == -1 ? -1 : categoryOverride.RemoveTorrent ?? baseConfig.RemoveTorrent,
            HitAndRunMode = categoryOverride.HitAndRunMode ?? baseConfig.HitAndRunMode,
            MinSeedRatio = categoryOverride.MinSeedRatio ?? baseConfig.MinSeedRatio,
            MinSeedingTimeDays = categoryOverride.MinSeedingTimeDays ?? baseConfig.MinSeedingTimeDays,
            HitAndRunMinimumDownloadPercent = categoryOverride.HitAndRunMinimumDownloadPercent ?? baseConfig.HitAndRunMinimumDownloadPercent,
            HitAndRunPartialSeedRatio = categoryOverride.HitAndRunPartialSeedRatio ?? baseConfig.HitAndRunPartialSeedRatio,
            TrackerUpdateBuffer = categoryOverride.TrackerUpdateBuffer ?? baseConfig.TrackerUpdateBuffer,
            StalledDelay = categoryOverride.StalledDelay ?? baseConfig.StalledDelay,
            IgnoreTorrentsYoungerThan = categoryOverride.IgnoreTorrentsYoungerThan ?? baseConfig.IgnoreTorrentsYoungerThan,
            Categories = baseConfig.Categories
        };
    }

    /// <summary>
    /// §3.3: Returns the merged tracker list: qBit-level as base, Arr-level overrides on host collision.
    /// Matches qBitrr's _merge_trackers() — Arr-level wins when both define the same host.
    /// </summary>
    private List<TrackerConfig> GetTrackerList(TorrentInfo torrent)
    {
        var clientTrackers = TrackerRules.ForClient(_config, torrent.ClientInstanceId).ToList();
        var integration = _config.ArrInstances.FirstOrDefault(pair =>
            CategoryOwnershipHelper.ArrOwnsTorrent(_config, pair.Value, torrent));
        var overrides = integration.Value == null ? [] : TrackerRules.ForIntegration(_config, integration.Key, integration.Value).ToList();

        var merged = new Dictionary<string, TrackerConfig>(StringComparer.OrdinalIgnoreCase);
        foreach (var tracker in overrides.OrderByDescending(t => t.Priority).Concat(clientTrackers.OrderByDescending(t => t.Priority)))
            merged.TryAdd(ExtractTrackerHost(tracker.Uri), tracker);
        return merged.Values.ToList();
    }

    /// <summary>Returns the connected client for whichever qBit instance the torrent belongs to.</summary>
    private ITorrentClient? GetClient(TorrentInfo torrent)
        => _qbitManager.GetClient(torrent.QBitInstanceName);

    public Task<bool> MeetsSeedingRequirementsAsync(string hash, string category, CancellationToken cancellationToken = default)
        => MeetsSeedingRequirementsAsync(hash, category, null, cancellationToken);

    private async Task<bool> MeetsSeedingRequirementsAsync(string hash, string category, string? instanceId, CancellationToken cancellationToken)
    {
        _logger.LogInformation("Checking seeding requirements for hash {Hash} in category {Category}", hash, category);

        var stats = await GetSeedingStatsAsync(hash, instanceId, cancellationToken);

        if (stats == null)
        {
            _logger.LogInformation("No seeding stats found for {Hash} - returning false", hash);
            return false;
        }

        _logger.LogInformation("Seeding stats for {Hash}: Ratio={Ratio}, SeedingTime={Time}s, MeetsTime={MeetsTime}, MeetsRatio={MeetsRatio}",
            hash, stats.Ratio, stats.SeedingTimeSeconds, stats.MeetsTimeRequirement, stats.MeetsRatioRequirement);

        // This optional legacy policy is separate from the effective qBittorrent category
        // overrides and tracker-specific H&R rules.
        var categoryConfig = _config.Settings.CategorySeedingRules?.FirstOrDefault(r => r.Category == category);
        _logger.LogInformation("Optional legacy category rule for {Category}: {Status} (qBit/category and tracker policies are evaluated independently)",
            category, categoryConfig != null ? "configured" : "not configured");

        if (categoryConfig != null)
        {
            if (categoryConfig.MinimumSeedingTime > 0)
            {
                var seedingTime = TimeSpan.FromSeconds(stats.SeedingTimeSeconds);
                var requiredTime = TimeSpan.FromMinutes(categoryConfig.MinimumSeedingTime);

                _logger.LogInformation("Checking minimum seeding time: {Current} vs required {Required}", seedingTime, requiredTime);

                if (seedingTime < requiredTime)
                {
                    _logger.LogInformation("Torrent {Hash} has not met minimum seeding time ({Current} < {Required})",
                        hash, seedingTime, requiredTime);
                    return false;
                }
            }

            if (categoryConfig.MinimumRatio > 0 && stats.Ratio < categoryConfig.MinimumRatio)
            {
                _logger.LogInformation("Torrent {Hash} has not met minimum ratio ({Current} < {Required})",
                    hash, stats.Ratio, categoryConfig.MinimumRatio);
                return false;
            }
        }

        foreach (var trackerReq in stats.TrackerRequirements)
        {
            _logger.LogInformation("Tracker requirement for {Hash}: {Requirement}", hash, trackerReq);
        }

        var result = stats.MeetsTimeRequirement && stats.MeetsRatioRequirement;
        _logger.LogInformation("Seeding requirements result for {Hash}: {Result}", hash, result);

        return result;
    }

    public Task<SeedingStats?> GetSeedingStatsAsync(string hash, CancellationToken cancellationToken = default)
        => GetSeedingStatsAsync(hash, null, cancellationToken);

    private async Task<SeedingStats?> GetSeedingStatsAsync(string hash, string? instanceId, CancellationToken cancellationToken)
    {
        var torrent = await TorrentClientLookup.FindAsync(_qbitManager, hash, instanceId, _logger, cancellationToken);

        if (torrent == null)
        {
            return null;
        }

        IReadOnlyList<TorrentTracker> announces = [];
        if (_config.Settings.TrackerRules is { Count: > 0 })
        {
            var client = GetClient(torrent) ?? throw new InvalidOperationException("Tracker evidence is unavailable.");
            announces = await client.GetTorrentTrackersAsync(torrent.Hash, cancellationToken);
        }
        return GetSeedingStats(torrent, announces);
    }

    private SeedingStats GetSeedingStats(TorrentInfo torrent, IReadOnlyList<TorrentTracker> announces)
    {
        var stats = new SeedingStats
        {
            Hash = torrent.Hash,
            Ratio = torrent.Ratio,
            SeedingTimeSeconds = torrent.SeedingTime,
            UploadedBytes = torrent.Uploaded,
            DownloadedBytes = torrent.Downloaded,
            CompletionTime = DateTime.UtcNow.AddSeconds(-torrent.SeedingTime)
        };

        var categoryConfig = _config.Settings.CategorySeedingRules?.FirstOrDefault(r => r.Category == torrent.Category);

        if (categoryConfig != null)
        {
            stats.MeetsTimeRequirement = categoryConfig.MinimumSeedingTime == 0 ||
                stats.SeedingTimeSeconds >= categoryConfig.MinimumSeedingTime * 60;

            stats.MeetsRatioRequirement = categoryConfig.MinimumRatio == 0 ||
                stats.Ratio >= categoryConfig.MinimumRatio;
        }
        else
        {
            stats.MeetsTimeRequirement = true;
            stats.MeetsRatioRequirement = true;
        }

        var trackerConfigs = _config.Settings.TrackerRules?.Where(rule => announces.Any(t => t.Tier >= 0
            && TrackerHostMatches(ExtractTrackerHost(t.Url), ExtractTrackerHost(rule.TrackerUrl))));
        if (trackerConfigs != null)
        {
            foreach (var tracker in trackerConfigs)
            {
                var meetsTrackerTime = tracker.MinimumSeedingTime == 0 ||
                    stats.SeedingTimeSeconds >= tracker.MinimumSeedingTime * 60;

                var meetsTrackerRatio = tracker.MinimumRatio == 0 ||
                    stats.Ratio >= tracker.MinimumRatio;

                if (!meetsTrackerTime)
                    stats.MeetsTimeRequirement = false;
                if (!meetsTrackerRatio)
                    stats.MeetsRatioRequirement = false;

                stats.TrackerRequirements.Add(
                    $"{TrackerLogHost(tracker.TrackerUrl)}: Ratio {tracker.MinimumRatio}, Time {tracker.MinimumSeedingTime}min");
            }
        }

        return stats;
    }

    public async Task<bool> IsHitAndRunProtectedAsync(string hash, CancellationToken cancellationToken = default)
    {
        try
        {
            var torrent = await TorrentClientLookup.FindAsync(_qbitManager, hash, null, _logger, cancellationToken);

            if (torrent == null)
            {
                return true;
            }

            return !await HnrAllowsDeleteAsync(torrent, "seeding interruption", cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException and not InvalidOperationException)
        {
            _logger.LogError(ex, "Error checking H&R protection for torrent {Hash}", hash);
            return true;
        }
    }

    /// <summary>
    /// Get the highest-priority matching tracker config for a torrent.
    /// Matches qBitrr's _get_tracker_config() exactly.
    /// Supports subdomain/apex matching (e.g., tracker.torrentleech.org matches torrentleech.org config).
    /// </summary>
    public async Task<TrackerConfig?> GetTrackerConfigAsync(TorrentInfo torrent, CancellationToken cancellationToken = default)
    {
        var trackers = GetTrackerList(torrent);
        if (!trackers.Any()) return null;

        var client = GetClient(torrent);
        if (client == null) return null;

        try
        {
            var torrentTrackers = await client.GetTorrentTrackersAsync(torrent.Hash, cancellationToken);
            return SelectTrackerConfig(trackers, torrentTrackers);
        }
        catch (Exception ex) when (ex is not OperationCanceledException && ex is not HttpRequestException { StatusCode: System.Net.HttpStatusCode.NotFound })
        {
            _logger.LogWarning(ex, "Error getting tracker config for torrent {Hash}", torrent.Hash);
            return null;
        }
    }

    private static TrackerConfig? SelectTrackerConfig(IEnumerable<TrackerConfig> trackers, IReadOnlyList<TorrentTracker> torrentTrackers)
    {
        var torrentHosts = torrentTrackers
            .Where(t => t.Tier >= 0 && !string.IsNullOrEmpty(t.Url))
            .Select(t => ExtractTrackerHost(t.Url))
            .Where(h => !string.IsNullOrEmpty(h))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        TrackerConfig? best = null;
        var bestPriority = int.MinValue;

        foreach (var trackerCfg in trackers)
        {
            if (trackerCfg == null) continue;

            var uri = (trackerCfg.Uri ?? "").Trim().TrimEnd('/');
            var priority = trackerCfg.Priority;
            var cfgHost = ExtractTrackerHost(uri);

            if (string.IsNullOrEmpty(cfgHost)) continue;

            if ((best == null || priority > bestPriority) && torrentHosts.Any(host => TrackerHostMatches(host, cfgHost)))
            {
                best = trackerCfg;
                bestPriority = priority;
            }
        }

        return best;
    }

    public static bool MatchesTracker(TrackerConfig rule, IReadOnlyList<TorrentTracker> trackers) =>
        trackers.Any(t => t.Tier >= 0 && TrackerHostMatches(ExtractTrackerHost(t.Url), ExtractTrackerHost(rule.Uri)));

    private static bool TrackerHostMatches(string host, string configuredHost) =>
        TrackerRules.HostMatches(host, configuredHost);

    public bool HnrAllowsDelete(TorrentInfo torrent, IReadOnlyList<TorrentTracker> torrentTrackers)
    {
        if (_config.Revision != _config.LatestRevision || !double.IsFinite(torrent.Progress)
            || torrent.Progress is < 0 or > 1 || !double.IsFinite(torrent.Ratio) || torrent.Ratio < 0 || torrent.SeedingTime < 0)
            return false;
        return GetProtectionDecisions(torrent, torrentTrackers).All(d => d.State == "eligible");
    }

    public List<ProtectionDecision> GetProtectionDecisions(TorrentInfo torrent, IReadOnlyList<TorrentTracker> torrentTrackers, bool observe = false)
    {
        // Negative tiers are qBittorrent's synthetic DHT/PeX/LSD entries, not tracker obligations.
        torrentTrackers = torrentTrackers.Where(t => t.Tier >= 0).ToList();
        // All configured obligations apply, including different policies for the same tracker.
        var obligations = TrackerRules.ForTorrent(_config, torrent);
        var matched = obligations.Where(cfg => MatchesTracker(cfg, torrentTrackers)).ToList();
        var decisions = new List<ProtectionDecision>();
        var now = _clock.GetUtcNow();
        var maximumGap = TimeSpan.FromSeconds(2 * (_config.GetTorrentClient(torrent.ClientInstanceId)?.TorrentScanIntervalSeconds ?? 30));
        var peakKey = MaintenancePersistence.Key(torrent.ClientInstanceId, torrent.Hash, "protection-progress");
        var tracksProgress = matched.Any(rule => rule.Protection != null || IsHnREnabled(rule.HitAndRunMode))
            || IsHnREnabled(GetSeedingConfig(torrent).HitAndRunMode);
        var peak = tracksProgress ? MaintenancePersistence.Read<ProtectionObservation>(_dbContext, peakKey) : new();
        var progress = Math.Max(torrent.Progress, peak.AddedOn == torrent.AddedOn ? peak.MaximumDownloadPercent / 100 : 0);
        if (observe && double.IsFinite(progress) && progress is >= 0 and <= 1 &&
            tracksProgress &&
            (peak.AddedOn != torrent.AddedOn || progress * 100 > peak.MaximumDownloadPercent))
            MaintenancePersistence.Write(_dbContext, peakKey, new ProtectionObservation { AddedOn = torrent.AddedOn, MaximumDownloadPercent = progress * 100 });
        foreach (var cfg in matched)
        {
            var effective = GetEffectiveSeedingConfig(torrent, cfg);
            if (cfg.Protection is not { RequireLegacyClearance: false } && !IsHnREnabled(effective.HitAndRunMode))
            {
                decisions.Add(new()
                {
                    ClientInstanceId = torrent.ClientInstanceId,
                    TorrentHash = torrent.Hash,
                    TorrentName = torrent.Name,
                    Tracker = TrackerLogHost(cfg.Uri),
                    State = "eligible",
                    Reason = "Configured tracker has no active clearance requirements."
                });
                continue;
            }
            var host = ExtractTrackerHost(cfg.Uri);
            var healthy = torrentTrackers.Any(t => TrackerHostMatches(ExtractTrackerHost(t.Url), host)
                && t.Status == 2 && string.IsNullOrWhiteSpace(t.Msg));
            ProtectionDecision decision;
            if (cfg.Protection is { RequireLegacyClearance: false } policy)
            {
                var policyFingerprint = Newtonsoft.Json.JsonConvert.SerializeObject(policy);
                var key = ObservationKey(torrent, host, policyFingerprint);
                using var transaction = observe ? _dbContext.Database.BeginTransaction() : null;
                var observation = MaintenancePersistence.Read<ProtectionObservation>(_dbContext, key);
                if (observe)
                {
                    if (observation.LastObserved is not { } last || now < last || now - last > maximumGap || torrent.Ratio < observation.ClientRatio)
                        observation.EligibleSince = null;
                    if (observation.PolicyFingerprint != policyFingerprint) observation.EligibleSince = null;
                    observation.PolicyFingerprint = policyFingerprint;
                    TrackerProtectionEvaluator.Observe(observation, torrent, healthy, now, maximumGap);
                    observation.ClientRatio = torrent.Ratio;
                    if (peak.AddedOn == torrent.AddedOn)
                        observation.MaximumDownloadPercent = Math.Max(observation.MaximumDownloadPercent, peak.MaximumDownloadPercent);
                    var checking = TrackerProtectionEvaluator.Evaluate(torrent, policy, observation, now, healthy, checkingRequirements: true);
                    observation.EligibleSince = checking.State == "eligible" && healthy ? observation.EligibleSince ?? now : null;
                    MaintenancePersistence.Write(_dbContext, key, observation);
                    transaction!.Commit();
                }
                if (peak.AddedOn == torrent.AddedOn)
                    observation.MaximumDownloadPercent = Math.Max(observation.MaximumDownloadPercent, peak.MaximumDownloadPercent);
                decision = TrackerProtectionEvaluator.Evaluate(torrent, policy, observation, now, healthy, maximumGap);
            }
            else
            {
                decision = ConfirmHnr(torrent, effective, host, healthy, now, observe, cfg.Protection?.AccountingGraceMinutes ?? 60, progress);
            }
            decision.ClientInstanceId = torrent.ClientInstanceId; decision.TorrentHash = torrent.Hash; decision.TorrentName = torrent.Name; decision.Tracker = TrackerLogHost(cfg.Uri);
            decisions.Add(decision);
        }
        var unmatchedHosts = torrentTrackers.Select(t => ExtractTrackerHost(t.Url)).Distinct()
            .Where(host => !matched.Any(c => TrackerHostMatches(host, ExtractTrackerHost(c.Uri)))).ToArray();
        if (matched.Count == 0 || torrent.IsPrivate != false && unmatchedHosts.Length > 0)
        {
            var category = GetEffectiveSeedingConfig(torrent, null);
            var knownPublic = torrent.IsPrivate == false;
            var legacy = IsHnREnabled(category.HitAndRunMode);
            var categoryConfigured = knownPublic && matched.Count == 0 && legacy;
            var healthy = torrentTrackers.Any(t => t.Status == 2 && string.IsNullOrWhiteSpace(t.Msg));
            var publicWithoutObligations = knownPublic && !legacy;
            var unidentified = !unmatchedHosts.Any(host => host.Length > 0);
            var decision = categoryConfigured ? ConfirmHnr(torrent, category, "", healthy, now, observe, 60, progress) : new ProtectionDecision
            {
                Tracker = string.Join(", ", unmatchedHosts.Where(host => host.Length > 0)),
                State = publicWithoutObligations ? "eligible" : "unknown",
                UnknownReason = publicWithoutObligations ? null : unidentified ? "no-tracker-reported" : "no-matching-tracker-rule",
                Reason = publicWithoutObligations ? "Public torrent without configured HnR obligations."
                    : unidentified ? "The torrent client returned no identifiable tracker hostname for this decision."
                    : "No configured tracker rule applies to the listed hostname and this torrent."
            };
            decision.ClientInstanceId = torrent.ClientInstanceId;
            decision.TorrentHash = torrent.Hash;
            decision.TorrentName = torrent.Name;
            decisions.Add(decision);
        }
        foreach (var decision in decisions.Where(d => d.State != "eligible"))
        {
            decision.NativeStopLimitRisk = torrent.RatioLimit != -1 || torrent.SeedingTimeLimit != -1 || (torrent.InactiveSeedingTimeLimit.HasValue && torrent.InactiveSeedingTimeLimit != -1);
            if (decision.NativeStopLimitRisk) decision.Reason += " Native stop limits may interrupt seeding; changes require verification.";
        }
        foreach (var decision in decisions)
            _logger.LogTrace("Tracker protection decision for {Hash}: tracker={Tracker}; state={State}; progress={Progress:P2}; ratio={Ratio:F4}; seed={SeedTime}s; observedSeed={ObservedSeed}s; remainingSeed={RemainingSeed}s; deadline={Deadline:O}; risk={Risk}; nativeStopRisk={NativeStopRisk}; reason={Reason}",
                torrent.Hash, decision.Tracker, decision.State, torrent.Progress, torrent.Ratio, torrent.SeedingTime, decision.ObservedSeedSeconds,
                decision.RemainingSeedSeconds, decision.Deadline, decision.DeadlineRisk, decision.NativeStopLimitRisk, decision.Reason);
        return decisions;
    }

    private async Task RemoveAnnounceUrlsAsync(ITorrentClient client, TorrentInfo torrent,
        IReadOnlyCollection<string> requested, CancellationToken ct)
    {
        if (!client.Capabilities.TrackerMutation || requested.Count == 0) return;
        var current = (await client.GetTorrentsAsync(cancellationToken: ct))
            .FirstOrDefault(t => t.Hash.Equals(torrent.Hash, StringComparison.OrdinalIgnoreCase));
        if (current == null || !SameRemovalCandidate(torrent, current)) return;
        current.ClientInstanceId = torrent.ClientInstanceId;
        current.IsPrivate ??= (await client.GetTorrentPropertiesAsync(current.Hash, ct))?.IsPrivate;
        var announces = (await client.GetTorrentTrackersAsync(current.Hash, ct))
            .Where(t => t.Tier >= 0 && ExtractTrackerHost(t.Url).Length != 0).ToList();
        if (!HnrAllowsDelete(current, announces)) return;
        var remaining = announces.Select(t => t.Url).ToHashSet(StringComparer.Ordinal);
        var remove = new List<string>();
        foreach (var tracker in announces)
        {
            if (!requested.Contains(tracker.Url) || !remaining.Contains(tracker.Url) ||
                PreservesPrivateAnnounceUrl(current, tracker, remaining)) continue;
            remaining.Remove(tracker.Url);
            remove.Add(tracker.Url);
        }
        if (remove.Count != 0 && !await client.RemoveTrackersAsync(current.Hash, remove, ct))
            throw new IOException("Torrent client rejected tracker removal.");
    }

    /// <summary>
    /// Check if the H&R-enabled tracker reports the torrent as unregistered/dead.
    /// Matches qBitrr's _hnr_tracker_is_dead() exactly.
    /// </summary>
    public async Task<bool> IsTrackerDeadAsync(TorrentInfo torrent, TrackerConfig config, CancellationToken cancellationToken = default)
    {
        var client = GetClient(torrent);
        if (client == null) return false;

        var uri = (config.Uri ?? "").Trim().TrimEnd('/');
        var cfgHost = ExtractTrackerHost(uri);

        if (string.IsNullOrEmpty(cfgHost))
        {
            return false;
        }

        try
        {
            var torrentTrackers = await client.GetTorrentTrackersAsync(torrent.Hash, cancellationToken);

            foreach (var tracker in torrentTrackers)
            {
                var trackerUrl = (tracker.Url ?? "").TrimEnd('/');
                if (!TrackerHostMatches(ExtractTrackerHost(trackerUrl), cfgHost))
                {
                    continue;
                }

                if (TrackerMessageIndicatesDead(tracker.Msg))
                {
                    return true;
                }
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Error checking if tracker is dead for torrent {Hash}", torrent.Hash);
        }

        return false;
    }

    /// <summary>
    /// Check if Hit and Run obligations are met for this torrent.
    /// Matches qBitrr's _hnr_safe_to_remove() exactly.
    /// </summary>
    public Task<bool> IsHnRSafeToRemoveAsync(TorrentInfo torrent, TrackerConfig config, CancellationToken cancellationToken = default)
        => Task.FromResult(IsHnRSafeToRemove(torrent, config));

    private static string ObservationKey(TorrentInfo torrent, string host, string fingerprint)
    {
        var id = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(fingerprint)));
        return MaintenancePersistence.Key(torrent.ClientInstanceId, torrent.Hash, "protection:" + host + ":" + id);
    }

    private ProtectionDecision ConfirmHnr(TorrentInfo torrent, CategorySeedingConfig config, string host,
        bool healthy, DateTimeOffset now, bool observe, int graceMinutes, double peakProgress)
    {
        var decision = HnrDecision(torrent, config, healthy, peakProgress);
        var valid = double.IsFinite(torrent.Ratio) && torrent.Ratio >= 0
            && double.IsFinite(torrent.Progress) && torrent.Progress is >= 0 and <= 1 && torrent.SeedingTime >= 0;
        var exempt = valid && peakProgress < config.HitAndRunMinimumDownloadPercent / 100.0;
        var qualifies = valid && !exempt && decision.State == "eligible";
        var fingerprint = "hnr-confirmation:" + Newtonsoft.Json.JsonConvert.SerializeObject(new
        {
            config.HitAndRunMode,
            config.MinSeedRatio,
            config.MinSeedingTimeDays,
            config.HitAndRunMinimumDownloadPercent,
            config.HitAndRunPartialSeedRatio,
            config.TrackerUpdateBuffer,
            graceMinutes
        });
        var key = ObservationKey(torrent, host, fingerprint);
        var maximumGap = TimeSpan.FromSeconds(2 * (_config.GetTorrentClient(torrent.ClientInstanceId)?.TorrentScanIntervalSeconds ?? 30));
        var grace = TimeSpan.FromMinutes(graceMinutes);
        ProtectionObservation observation;
        if (observe)
        {
            using var transaction = _dbContext.Database.BeginTransaction();
            observation = MaintenancePersistence.Read<ProtectionObservation>(_dbContext, key);
            TrackerProtectionEvaluator.ConfirmLegacyClearance(observation, torrent, qualifies, now, maximumGap, grace, observe: true);
            MaintenancePersistence.Write(_dbContext, key, observation);
            transaction.Commit();
        }
        else observation = MaintenancePersistence.Read<ProtectionObservation>(_dbContext, key);

        if (exempt)
        {
            decision.State = "eligible";
            decision.UnknownReason = null;
            decision.Reason += " Download threshold has not been reached.";
        }
        else if (decision.State == "eligible")
        {
            var confirmed = TrackerProtectionEvaluator.ConfirmLegacyClearance(observation, torrent, qualifies, now, maximumGap, grace, observe: false);
            decision.State = confirmed ? "eligible" : "protected";
            var elapsed = observation.EligibleSince is { } since && observation.LastObserved is { } last && now - last <= maximumGap
                ? last - since : TimeSpan.Zero;
            decision.Reason += confirmed ? " Clearance confirmed by healthy observations."
                : $" Waiting for healthy confirmation ({Math.Max(0, Math.Ceiling((grace - elapsed).TotalMinutes))} minutes of accounting grace remaining).";
        }
        return decision;
    }

    private static ProtectionDecision HnrDecision(TorrentInfo torrent, CategorySeedingConfig config, bool healthy, double peakProgress)
    {
        var safe = IsHnRSafeToRemove(torrent, new TrackerConfig
        {
            HitAndRunMode = config.HitAndRunMode,
            MinSeedRatio = config.MinSeedRatio,
            MinSeedingTimeDays = config.MinSeedingTimeDays,
            HitAndRunMinimumDownloadPercent = config.HitAndRunMinimumDownloadPercent,
            HitAndRunPartialSeedRatio = config.HitAndRunPartialSeedRatio,
            TrackerUpdateBuffer = config.TrackerUpdateBuffer
        }, peakProgress);
        var requiredSeconds = (long)config.MinSeedingTimeDays * 86400;
        var timeApplies = IsHnREnabled(config.HitAndRunMode) && config.HitAndRunMode != "ratio" && torrent.Progress >= 1
            && torrent.Progress >= config.HitAndRunMinimumDownloadPercent / 100.0 && requiredSeconds > 0;
        return new()
        {
            State = safe && healthy ? "eligible" : healthy ? "protected" : "unknown",
            UnknownReason = healthy ? null : "tracker-accounting-unconfirmed",
            LocalEstimate = false,
            ObservedSeedSeconds = torrent.SeedingTime,
            RemainingSeedSeconds = timeApplies ? Math.Max(0, requiredSeconds + (double)config.TrackerUpdateBuffer - torrent.SeedingTime) : 0,
            Reason = $"Hit & Run mode={config.HitAndRunMode}; ratio={torrent.Ratio:F3}/{config.MinSeedRatio}; client-reported seed={torrent.SeedingTime}s/{requiredSeconds}s; tracker update buffer={config.TrackerUpdateBuffer}s; minimum download={config.HitAndRunMinimumDownloadPercent}%; partial ratio={config.HitAndRunPartialSeedRatio}; tracker healthy={healthy}; requirements met={safe}."
                + (!healthy && safe ? " Waiting for a healthy tracker response to confirm accounting." : "")
        };
    }

    private static bool IsHnRSafeToRemove(TorrentInfo torrent, TrackerConfig config, double? peakProgress = null)
    {
        if (!double.IsFinite(torrent.Ratio) || !double.IsFinite(torrent.Progress)) return false;
        var clearMode = (config.HitAndRunMode ?? "disabled").Trim().ToLowerInvariant();
        if (clearMode == "disabled")
        {
            return true;
        }

        var minRatio = config.MinSeedRatio ?? 1.0;
        var minTimeDays = config.MinSeedingTimeDays ?? 0;
        var minTimeSeconds = (long)minTimeDays * 86400;
        var minDownloadPercent = (config.HitAndRunMinimumDownloadPercent ?? 10) / 100.0;
        var partialRatio = config.HitAndRunPartialSeedRatio ?? 1.0;
        var bufferSeconds = config.TrackerUpdateBuffer ?? 0;

        var progress = torrent.Progress;
        var isPartial = progress < 1.0;
        var effectiveSeedingTime = torrent.SeedingTime - bufferSeconds;

        // Negligible download (<threshold% progress), no HnR obligation
        if ((peakProgress ?? progress) < minDownloadPercent)
        {
            return true;
        }

        // Partial download: ratio only check
        if (isPartial)
        {
            return clearMode != "time" && torrent.Ratio >= partialRatio;
        }

        var ratioMet = minRatio > 0 && torrent.Ratio >= minRatio;
        var timeMet = minTimeSeconds > 0 && effectiveSeedingTime >= minTimeSeconds;

        if (clearMode == "ratio") return ratioMet;
        if (clearMode == "time") return timeMet;

        if (clearMode == "and")
        {
            // Both ratio AND time must be met
            if (minRatio > 0 && minTimeSeconds > 0) return ratioMet && timeMet;
            if (minRatio > 0) return ratioMet;
            if (minTimeSeconds > 0) return timeMet;
            return true;
        }

        // "or" mode: either ratio OR time clears HnR
        if (minRatio > 0 && minTimeSeconds > 0) return ratioMet || timeMet;
        if (minRatio > 0) return ratioMet;
        if (minTimeSeconds > 0) return timeMet;

        return true;
    }

    /// <summary>
    /// Check if HnR obligations allow deleting this torrent.
    /// Uses matched tracker config when available, otherwise category seeding (same resolution as
    /// <see cref="IsHitAndRunProtectedAsync"/>). Returns true if deletion is allowed.
    /// </summary>
    public async Task<bool> HnrAllowsDeleteAsync(TorrentInfo torrent, string reason, CancellationToken cancellationToken = default)
    {
        var client = GetClient(torrent);
        if (client == null || torrent.AddedOn <= 0 || torrent.AddedOn > DateTimeOffset.UtcNow.ToUnixTimeSeconds()
            || DateTimeOffset.UtcNow.ToUnixTimeSeconds() - torrent.AddedOn < _config.Settings.IgnoreTorrentsYoungerThan)
        {
            _logger.LogDebug("HnR check blocked {Reason} of {Hash} on {Client}: client unavailable or torrent age is unverified/too young", reason, torrent.Hash, torrent.ClientInstanceId);
            return false;
        }
        if (!torrent.IsPrivate.HasValue)
            torrent.IsPrivate = (await client.GetTorrentPropertiesAsync(torrent.Hash, cancellationToken))?.IsPrivate;
        var trackerEvidence = await client.GetTorrentTrackersAsync(torrent.Hash, cancellationToken);
        var allowed = HnrAllowsDelete(torrent, trackerEvidence);
        if (!allowed) _logger.LogDebug("HnR protection blocked {Reason} of {Hash}: {Evidence}", reason, torrent.Hash,
            DiagnosticText.Safe(string.Join("; ", GetProtectionDecisions(torrent, trackerEvidence).Where(d => d.State != "eligible").Select(d => $"{d.Tracker}: {d.State}; {d.Reason}"))));
        if (allowed) _logger.LogDebug("HnR obligations cleared for {Reason} of {Hash} on {Client}", reason, torrent.Hash, torrent.ClientInstanceId);
        return allowed;
    }

    public Task<bool> CanDeleteTorrentAsync(TorrentInfo torrent, string reason, CancellationToken cancellationToken = default)
        => CanDeleteTorrentForIntentAsync(torrent, TorrentRemovalIntent.SeedingLimit, reason, cancellationToken);

    public async Task<bool> CanDeleteTorrentForIntentAsync(TorrentInfo torrent, TorrentRemovalIntent intent,
        string reason, CancellationToken cancellationToken = default)
        => (await EvaluateRemovalAsync(torrent, intent, cancellationToken)).Allowed;

    public async Task<RemovalDecision> EvaluateRemovalAsync(TorrentInfo torrent, TorrentRemovalIntent intent, CancellationToken ct = default)
    {
        var blockers = new List<RemovalBlocker>();
        void Block(string code, string evidence, string next)
        {
            blockers.Add(new(code, DiagnosticText.Safe(evidence), next));
            _logger.LogDebug("Removal blocked for [{Name}] ({Hash}) on {Client}, category={Category}, intent={Intent}: {Code}; {Evidence}",
                torrent.Name, torrent.Hash, torrent.ClientInstanceId, torrent.Category, intent, code, DiagnosticText.Safe(evidence));
        }
        var cfg = _config.GetTorrentClient(torrent.ClientInstanceId);
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        if (cfg?.Disabled != false) Block("client.disabled", $"Client '{torrent.ClientInstanceId}' is {(cfg == null ? "not configured" : "disabled")}", "Enable/configure this client before requesting removal.");
        if (_config.Revision != _config.LatestRevision) Block("config.pending", $"Applied revision {_config.Revision}; accepted revision {_config.LatestRevision}", "Wait for configuration activation to complete.");
        if (torrent.AddedOn <= 0 || torrent.AddedOn > now) Block("torrent.added-on", $"Invalid AddedOn={torrent.AddedOn}; now={now}", "Refresh torrent metadata from the client.");
        if (!double.IsFinite(torrent.Progress) || torrent.Progress is < 0 or > 1 || !double.IsFinite(torrent.Ratio) || torrent.Ratio < 0 || torrent.SeedingTime < 0)
            Block("torrent.invalid-evidence", $"Progress={torrent.Progress}, ratio={torrent.Ratio}, seedingTime={torrent.SeedingTime}s", "Refresh invalid client statistics; removal remains blocked until evidence is valid.");
        try
        {
            IReadOnlyList<TorrentTracker> trackers = [];
            var client = GetClient(torrent);
            if (client == null) Block("client.disconnected", $"Client '{torrent.ClientInstanceId}' has no connected session", "Reconnect this client in configuration.");
            else
            {
                if (!torrent.IsPrivate.HasValue) torrent.IsPrivate = (await client.GetTorrentPropertiesAsync(torrent.Hash, ct))?.IsPrivate;
                trackers = await client.GetTorrentTrackersAsync(torrent.Hash, ct);
                foreach (var decision in GetProtectionDecisions(torrent, trackers).Where(d => d.State != "eligible"))
                {
                    var code = "hnr." + decision.State;
                    if (decision.NativeStopLimitRisk) code += ".native-stop-risk";
                    if (decision.DeadlineRisk is "warning" or "critical" or "unachievable") code += ".deadline-" + decision.DeadlineRisk;
                    Block(code, $"Tracker={decision.Tracker}; state={decision.State}; {decision.Reason} {decision.SeedAccountingDescription()}; ratio={torrent.Ratio:F3}; progress={torrent.Progress:P1}; native ratioLimit={torrent.RatioLimit}, timeLimit={torrent.SeedingTimeLimit}; native stop risk={decision.NativeStopLimitRisk}; deadline risk={decision.DeadlineRisk}",
                        "Meet this tracker/category's configured obligations and verify healthy tracker accounting.");
                }
            }
            var tags = torrent.Tags.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
            if (tags.Contains(IgnoredTag))
                Block("torrent.protected-tag", $"Blocking tag '{IgnoredTag}' is present", "Review the ignore policy before removing its tag.");
            if (tags.Contains(FreeSpacePausedTag))
                Block("torrent.free-space-paused", $"Blocking tag '{FreeSpacePausedTag}' is present", "Wait for the free-space policy to resume the torrent.");
            if (torrent.SuperSeeding || torrent.State.Equals("forcedUP", StringComparison.OrdinalIgnoreCase))
                Block("torrent.protected-state", $"SuperSeeding={torrent.SuperSeeding}; state={torrent.State}", "Disable forced/super-seeding intentionally before removal.");
            var arr = _config.ArrInstances.Values.FirstOrDefault(a => CategoryOwnershipHelper.ArrOwnsTorrent(_config, a, torrent));
            var (delay, age) = TorrentProcessor.ResolveStalledSettings(_config, torrent, arr);
            age = Math.Max(age, GetSeedingConfig(torrent).IgnoreTorrentsYoungerThan);
            var requiredAge = Math.Max(age, _config.Settings.IgnoreTorrentsYoungerThan);
            if (age < 0 || now - torrent.AddedOn < requiredAge)
                Block("torrent.minimum-age", $"Age={now - torrent.AddedOn}s; effective IgnoreTorrentsYoungerThan={requiredAge}s; category/Arr value={age}s", "Wait until the minimum age is met or correct the effective age policy.");
            if (intent is not (TorrentRemovalIntent.SeedingLimit or TorrentRemovalIntent.StalledDownload
                or TorrentRemovalIntent.RejectedByAllowlist or TorrentRemovalIntent.RejectedByCustomFormat or TorrentRemovalIntent.OrdinaryRetirement))
            {
                Block("removal.unsupported-intent", $"Unknown removal intent '{intent}'", "Use a supported removal intent.");
                return new(blockers);
            }
            var policy = GetEffectiveSeedingConfig(torrent, SelectTrackerConfig(GetTrackerList(torrent), trackers));
            if (policy.RemoveTorrent is not (>= 1 and <= 4) ||
                TrackerRules.ForTorrent(_config, torrent).Any(rule => rule.RemoveTorrent == -1 && MatchesTracker(rule, trackers)))
                Block("seeding.removal-disabled", $"Removal is disabled by an applicable policy (effective mode={policy.RemoveTorrent}).", "Review the applicable removal modes.");
            if (torrent.Progress >= 1)
            {
                if (intent == TorrentRemovalIntent.StalledDownload)
                    Block("stalled.completed", $"Progress={torrent.Progress:P2}", "Use completed-torrent seeding policy.");
                var stats = GetSeedingStats(torrent, trackers);
                if (!stats.MeetsTimeRequirement || !stats.MeetsRatioRequirement)
                    Block("seeding.minimum", "The ordinary minimum seeding requirements are unmet.", "Meet the minimum seeding requirements.");
                if (!RemovalLimitsMet(torrent, policy))
                    Block("seeding.limit", $"RemoveTorrent={policy.RemoveTorrent}; ratio={torrent.Ratio:F3}/{policy.MaxUploadRatio}; seed={torrent.SeedingTime}/{policy.MaxSeedingTime}s; state={torrent.State}", "Meet the effective ratio/time removal limits.");
                if (intent == TorrentRemovalIntent.OrdinaryRetirement && client != null && blockers.Count == 0)
                {
                    var current = (await client.GetTorrentsAsync(cancellationToken: ct))
                        .FirstOrDefault(t => t.Hash.Equals(torrent.Hash, StringComparison.OrdinalIgnoreCase));
                    if (current == null || current.AmountLeft != 0 || !SameRemovalCandidate(torrent, current, torrent.ObservedRetirementState)
                        || !double.IsFinite(current.Ratio) || current.Ratio < torrent.Ratio || current.SeedingTime < torrent.SeedingTime)
                        Block("retirement.changed", "The completed torrent changed before final retirement authorization.", "Wait for a fresh evaluation.");
                    else
                    {
                        current.ClientInstanceId = torrent.ClientInstanceId;
                        current.ObservedAtUnixSeconds = _clock.GetUtcNow().ToUnixTimeSeconds();
                        if (!RemovalLimitsMet(current, policy, torrent.State))
                            Block("seeding.limit", "The current ratio/time evidence does not meet the retirement limits.", "Wait until the effective limits are met.");
                        foreach (var guard in await RetirementGuard.EvaluateAsync(_dbContext, _config, current,
                            current.ObservedAtUnixSeconds, ct))
                            Block(guard.Code, guard.Reason, guard.Remediation);
                    }
                }
                return new(blockers);
            }
            if (intent == TorrentRemovalIntent.OrdinaryRetirement)
            {
                Block("retirement.incomplete", "The torrent is incomplete.", "Allow it to finish before retirement.");
                return new(blockers);
            }
            var metadata = torrent.State.Equals("metaDL", StringComparison.OrdinalIgnoreCase) || torrent.State.Equals("forcedMetaDL", StringComparison.OrdinalIgnoreCase);
            var stalled = torrent.State.Equals("stalledDL", StringComparison.OrdinalIgnoreCase);
            var unavailable = (torrent.State.Equals("downloading", StringComparison.OrdinalIgnoreCase) || torrent.State.Equals("forcedDL", StringComparison.OrdinalIgnoreCase)) && torrent.Availability < 1;
            if (intent == TorrentRemovalIntent.StalledDownload && !metadata && !stalled && !unavailable)
                Block("stalled.trigger-cleared", $"State={torrent.State}; availability={torrent.Availability}; progress={torrent.Progress:P1}", "Wait for a fresh stalled-download evaluation; the original trigger no longer applies.");
            var reference = metadata ? torrent.AddedOn : torrent.LastActivity;
            if (delay < 0 || reference <= 0 || reference > now || now - reference < Math.Max(age, delay * 60L))
                Block("stalled.grace", $"StalledDelay={delay}min; reference={(metadata ? "AddedOn" : "LastActivity")}={reference}; idle={now - reference}s; required={Math.Max(age, delay * 60L)}s", "Wait for the configured stalled grace; a negative delay disables stalled removal.");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex) { Block("removal.inspection-failed", DiagnosticText.Error(ex), "Restore client/storage access and retry inspection; missing evidence cannot authorize deletion."); }
        return new(blockers);
    }

    internal static string DescribeRemovalChanges(TorrentInfo expected, TorrentInfo current)
    {
        var fields = new (string Name, object? Before, object? After)[]
        {
            ("AddedOn", expected.AddedOn, current.AddedOn), ("Category", expected.Category, current.Category),
            ("State", expected.State, current.State), ("Progress", expected.Progress, current.Progress),
            ("LastActivity", expected.LastActivity, current.LastActivity), ("SavePath", expected.SavePath, current.SavePath),
            ("ContentPath", expected.ContentPath, current.ContentPath)
        };
        return string.Join("; ", fields.Where(f => !Equals(f.Before, f.After)).Select(f => $"{f.Name}: '{f.Before}' -> '{f.After}'"));
    }

    internal static bool SameRemovalCandidate(TorrentInfo expected, TorrentInfo current, string? expectedState = null)
        => expected.AddedOn == current.AddedOn && expected.Category == current.Category
            && (expectedState ?? expected.State) == current.State && expected.Progress == current.Progress && expected.LastActivity == current.LastActivity
            && expected.SavePath == current.SavePath && expected.ContentPath == current.ContentPath;

    /// <summary>
    /// Check if torrent meets removal conditions based on RemoveMode.
    /// Matches qBitrr's _should_remove_torrent() / torrent_limit_check (5.14.4 stalledUP clock).
    /// RemoveMode: -1=Never, 1=Ratio only, 2=Time only, 3=OR, 4=AND
    /// When HnR is enabled, returns false until real seeding_time / ratio obligations are met.
    /// Callers should trust this result and not repeat <see cref="HnrAllowsDeleteAsync"/>.
    /// </summary>
    public async Task<bool> SeedingLimitsMetAsync(TorrentInfo torrent, CancellationToken cancellationToken = default)
    {
        if (!double.IsFinite(torrent.Ratio) || torrent.Ratio < 0 || !double.IsFinite(torrent.Progress)
            || torrent.Progress is < 0 or > 1 || torrent.SeedingTime < 0 || !IsUploadingState(torrent.State)) return false;
        _stalledUploads.Touch(torrent.ClientInstanceId, torrent.Hash, IsStalledUploadState(torrent.State));
        return RemovalLimitsMet(torrent, GetEffectiveSeedingConfig(torrent, await GetTrackerConfigAsync(torrent, cancellationToken)));
    }

    public async Task<bool> ShouldRemoveTorrentAsync(TorrentInfo torrent, CancellationToken cancellationToken = default)
    {
        if (MaintenanceRecovery.BlocksAutomaticRemoval(_dbContext, torrent, _config.GetTorrentClient(torrent.ClientInstanceId)?.Maintenance.ErrorRecovery ?? new ErrorRecoveryConfig()))
        {
            _logger.LogDebug("Seeding removal withheld for {Hash}: unresolved error recovery", torrent.Hash);
            return false;
        }
        _stalledUploads.Touch(torrent.QBitInstanceName, torrent.Hash, IsStalledUploadState(torrent.State));

        var trackerConfig = await GetTrackerConfigAsync(torrent, cancellationToken);
        var seedingConfig = GetEffectiveSeedingConfig(torrent, trackerConfig);
        if (!IsUploadingState(torrent.State))
        {
            _logger.LogDebug("Seeding removal withheld for {Hash}: state={State} is not uploading", torrent.Hash, torrent.State);
            return false;
        }
        if (RetirementGuard.Intent(_config, torrent) == TorrentRemovalIntent.OrdinaryRetirement)
        {
            if (torrent.Progress < 1 || torrent.AmountLeft != 0) return false;
            var guards = await RetirementGuard.EvaluateAsync(_dbContext, _config, torrent,
                _clock.GetUtcNow().ToUnixTimeSeconds(), cancellationToken);
            foreach (var guard in guards)
                _logger.LogDebug("Retirement withheld for {Hash} on {Client}: {Reason}", torrent.Hash, torrent.ClientInstanceId, guard.Reason);
            if (guards.Count > 0) return false;
        }
        if (!RemovalLimitsMet(torrent, seedingConfig)) return false;
        var allowed = await HnrAllowsDeleteAsync(torrent, "seeding limit", cancellationToken);
        _logger.LogDebug("Seeding removal decision for {Hash}: limits met; HnR clearance={Allowed}", torrent.Hash, allowed);
        return allowed;
    }

    private bool RemovalLimitsMet(TorrentInfo torrent, CategorySeedingConfig policy, string? stateOverride = null)
    {
        if (!double.IsFinite(policy.MaxUploadRatio)) return false;
        var ratioMet = policy.MaxUploadRatio > 0 && torrent.Ratio >= policy.MaxUploadRatio;
        var state = stateOverride ?? torrent.State;
        var timeMet = policy.MaxSeedingTime > 0 && (torrent.SeedingTime >= policy.MaxSeedingTime
            || (IsStalledUploadState(state) && _stalledUploads.IdleMeets(torrent.ClientInstanceId, torrent.Hash, policy.MaxSeedingTime)));
        var met = policy.RemoveTorrent switch
        {
            1 => ratioMet,
            2 => timeMet,
            3 => ratioMet || timeMet,
            4 => ratioMet && timeMet,
            _ => false
        };
        _logger.LogDebug("Seeding limit decision for {Hash}: mode={Mode}; ratio={Ratio:F4}/{RequiredRatio}; ratioMet={RatioMet}; seed={SeedTime}s/{RequiredTime}s; timeMet={TimeMet}; state={State}; removalLimitsMet={Met}",
            torrent.Hash, policy.RemoveTorrent, torrent.Ratio, policy.MaxUploadRatio, ratioMet, torrent.SeedingTime, policy.MaxSeedingTime, timeMet, state, met);
        return met;
    }

    /// <summary>
    /// Check if a torrent state indicates uploading/seeding.
    /// Matches qBitrr's is_uploading check.
    /// </summary>
    public static bool IsUploadingState(string state)
    {
        if (string.IsNullOrEmpty(state)) return false;
        var s = state.ToLowerInvariant();
        return s is "stalledup" or "pausedup" or "stoppedup" or "queuedup" or "forcedup" ||
               s.Contains("uploading") ||
               s.Contains("stalledupload") ||
               s.Contains("queuedupload") ||
               s.Contains("pausedupload") ||
               s.Contains("forcedupload");
    }

    /// <summary>
    /// qBittorrent stalled upload only (<c>stalledUP</c>). Queued/paused/forced/active seed reset the clock.
    /// </summary>
    public static bool IsStalledUploadState(string state)
    {
        if (string.IsNullOrEmpty(state)) return false;
        var s = state.ToLowerInvariant();
        return s.Contains("stalledupload") || s == "stalledup";
    }

    /// <summary>
    /// Check if a torrent state indicates downloading.
    /// Matches qBitrr's is_downloading check.
    /// </summary>
    public static bool IsDownloadingState(string state)
    {
        if (string.IsNullOrEmpty(state)) return false;
        var s = state.ToLowerInvariant();
        return s.Contains("downloading") ||
               s.Contains("stalleddownload") ||
               s.Contains("queueddownload") ||
               s.Contains("pauseddownload") ||
               s.Contains("forceddownload") ||
               s.Contains("metadata");
    }

    /// <summary>
    /// Check if a torrent state indicates stopped (not paused).
    /// </summary>
    public static bool IsStoppedState(string state)
    {
        if (string.IsNullOrEmpty(state)) return false;
        var s = state.ToLowerInvariant();
        return s == "stoppeddownload" ||
               s == "stoppedupload" ||
               s.Contains("stopped");
    }

    private CategorySeedingConfig GetEffectiveSeedingConfig(TorrentInfo torrent, TrackerConfig? tracker)
    {
        var category = GetSeedingConfig(torrent);
        var arr = _config.ArrInstances.Values
            .FirstOrDefault(a => CategoryOwnershipHelper.ArrOwnsTorrent(_config, a, torrent));
        var effective = SeedingLimitMerge.Merge(category, arr?.Torrent.SeedingMode, tracker);
        _logger.LogTrace("Seeding policy for {Hash}: precedence=category -> positive Arr override -> positive tracker override; category={Category}; tracker={Tracker}; " +
            "categoryRatio={CategoryRatio}; categoryTime={CategoryTime}s; ArrRatio={ArrRatio}; ArrTime={ArrTime}s; trackerRatio={TrackerRatio}; trackerTime={TrackerTime}s; " +
            "effectiveRatio={EffectiveRatio}; effectiveTime={EffectiveTime}s; removalMode={Mode}; HnRMode={HnrMode}; minimumRatio={MinRatio}; minimumSeedDays={MinDays}; " +
            "clientRatioLimit={ClientRatio}; clientSeedLimit={ClientTime}min; clientInactiveLimit={InactiveTime}min",
            torrent.Hash, torrent.Category, tracker == null ? "category fallback" : TrackerLogHost(tracker.Uri),
            category.MaxUploadRatio, category.MaxSeedingTime, arr?.Torrent.SeedingMode?.MaxUploadRatio, arr?.Torrent.SeedingMode?.MaxSeedingTime,
            tracker?.MaxUploadRatio, tracker?.MaxSeedingTime, effective.MaxUploadRatio, effective.MaxSeedingTime, effective.RemoveTorrent,
            effective.HitAndRunMode, effective.MinSeedRatio, effective.MinSeedingTimeDays, torrent.RatioLimit, torrent.SeedingTimeLimit, torrent.InactiveSeedingTimeLimit);
        return effective;
    }

    /// <summary>
    /// Check if HitAndRunMode is enabled (not null and not "disabled").
    /// </summary>
    private static bool IsHnREnabled(string? mode)
    {
        if (string.IsNullOrEmpty(mode)) return false;
        var lower = mode.Trim().ToLowerInvariant();
        return lower is "and" or "or" or "ratio" or "time";
    }

    // Presentation only: policy matching keeps its existing normalization.
    private static string TrackerLogHost(string? url)
    {
        if (Uri.TryCreate(url, UriKind.Absolute, out var uri) && !string.IsNullOrEmpty(uri.Host)) return uri.Host;
        return Uri.TryCreate("https://" + url, UriKind.Absolute, out uri) && !string.IsNullOrEmpty(uri.Host)
            ? uri.Host : "unknown tracker";
    }

    /// <summary>
    /// Extract the host from a tracker URL for matching.
    /// Matches qBitrr's _extract_tracker_host() function.
    /// </summary>
    public static string ExtractTrackerHost(string url) => TrackerRules.Host(url);


    public Task<RemovalResult> RemoveCompletedTorrentsAsync(string category, CancellationToken cancellationToken = default)
        => RemoveCompletedTorrentsAsync(category, null, cancellationToken);

    public async Task<RemovalResult> RemoveCompletedTorrentsAsync(string category, IReadOnlySet<string>? clientIds, CancellationToken cancellationToken = default)
    {
        var result = new RemovalResult();

        var allClients = _qbitManager.GetAllClients().Where(pair => clientIds == null || clientIds.Contains(pair.Key))
            .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.OrdinalIgnoreCase);
        if (allClients.Count == 0)
        {
            result.Errors.Add("No qBittorrent clients available");
            return result;
        }

        // Gather completed torrents from all qBit instances for this category
        var completedByInstance = new List<(string instanceName, ITorrentClient client, TorrentInfo torrent)>();
        foreach (var (instanceName, client) in allClients)
        {
            List<TorrentInfo> torrents;
            try { torrents = await client.GetTorrentsAsync(category, cancellationToken: cancellationToken); }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "Unable to read torrent client {Instance}", instanceName);
                result.Errors.Add($"{instanceName}: torrent enumeration failed.");
                await RetirementGuard.ForgetAsync(_dbContext, _config, instanceName, null, null, cancellationToken);
                continue;
            }
            var observedAt = _clock.GetUtcNow().ToUnixTimeSeconds();
            await RetirementGuard.ForgetAsync(_dbContext, _config, instanceName, category, torrents, cancellationToken);
            foreach (var t in torrents)
            {
                if (t.State.Contains("up", StringComparison.OrdinalIgnoreCase) ||
                    (t.State.Contains("paused", StringComparison.OrdinalIgnoreCase) && t.Progress >= 1.0))
                {
                    t.QBitInstanceName = instanceName;
                    t.ObservedAtUnixSeconds = observedAt;
                    completedByInstance.Add((instanceName, client, t));
                }
            }
        }

        result.TorrentsChecked = completedByInstance.Count;
        _logger.LogInformation("H&R Check: Processing {Count} completed torrents in category {Category}", completedByInstance.Count, category);

        foreach (var (instanceName, client, torrent) in completedByInstance)
        {
            using var logScope = _logger.BeginScope(new Dictionary<string, object>
            {
                ["ClientInstance"] = instanceName,
                ["TorrentCategory"] = torrent.Category,
                ["TorrentHash"] = torrent.Hash
            });
            try
            {
                _logger.LogInformation("H&R Check: [{Name}] | Ratio[{Ratio:F2}] | SeedingTime[{SeedingTime}s] | State[{State}] | Hash[{Hash}]",
                    torrent.Name, torrent.Ratio, torrent.SeedingTime, torrent.State, torrent.Hash);

                var shouldRemove = await ShouldRemoveTorrentAsync(torrent, cancellationToken);
                if (!shouldRemove)
                {
                    _logger.LogInformation("H&R: Keeping torrent [{Name}] - does not meet removal criteria | Ratio[{Ratio:F2}] | SeedingTime[{SeedingTime}s]",
                        torrent.Name, torrent.Ratio, torrent.SeedingTime);
                    result.TorrentsProtected++;
                    continue;
                }

                var imported = await _dbContext.TorrentLibrary
                    .AnyAsync(t => t.Hash == torrent.Hash && t.QbitInstance == instanceName && t.Imported
                        && t.AutoDeletePendingAddedOn == null, cancellationToken);

                var current = (await client.GetTorrentsAsync(cancellationToken: cancellationToken))
                    .FirstOrDefault(t => t.Hash.Equals(torrent.Hash, StringComparison.OrdinalIgnoreCase));
                if (current != null) current.ClientInstanceId = instanceName;
                var sameCandidate = imported && current != null && SameRemovalCandidate(torrent, current);
                var intent = RetirementGuard.Intent(_config, torrent);
                if (sameCandidate && await CanDeleteTorrentForIntentAsync(current!, intent, "final seeding removal", cancellationToken)
                    && !FailedTorrentReceipt.BlocksRemoval(_dbContext, current!)
                    && !ImportRescueReceipt.BlocksRemoval(_dbContext, current!, intent))
                {
                    _logger.LogInformation("Seeding removal requested for {Hash}: deleteFiles=false; imported=true; freshCandidate=true", torrent.Hash);
                    var deleted = await client.DeleteTorrentsAsync(new List<string> { torrent.Hash }, deleteFiles: false, cancellationToken);
                    _logger.LogInformation("Seeding removal result for {Hash}: accepted={Accepted}", torrent.Hash, deleted);
                    if (deleted)
                    {
                        _stalledUploads.Evict(instanceName, torrent.Hash);
                        result.TorrentsRemoved++;
                        result.RemovedHashes.Add(torrent.Hash);
                        _logger.LogInformation("H&R Remove: [{Name}] | Reason[Removal criteria met] | Ratio[{Ratio:F2}] | SeedingTime[{SeedingTime}s] | Hash[{Hash}]",
                            torrent.Name, torrent.Ratio, torrent.SeedingTime, torrent.Hash);
                    }
                }
                else
                {
                    var reason = !imported ? "import or post-import cleanup is pending" : current == null ? "torrent absent from fresh inventory"
                        : !sameCandidate ? "torrent metadata changed since evaluation" : "final protection clearance denied";
                    _logger.LogInformation("H&R: Keeping torrent [{Name}] - {Reason} | Hash[{Hash}]", torrent.Name, reason, torrent.Hash);
                    result.TorrentsProtected++;
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogError(ex, "[{Instance}] Error processing torrent {Hash} for removal", instanceName, torrent.Hash);
                result.Errors.Add($"{torrent.Name}: {ex.Message}");
            }
        }

        return result;
    }

    public Task UpdateSeedingTagsAsync(string category, CancellationToken cancellationToken = default)
        => UpdateSeedingTagsAsync(category, null, cancellationToken);

    public async Task UpdateSeedingTagsAsync(string category, IReadOnlySet<string>? clientIds, CancellationToken cancellationToken = default)
    {
        var failures = new List<Exception>();
        foreach (var (instanceName, client) in _qbitManager.GetAllClients().Where(pair => clientIds == null || clientIds.Contains(pair.Key)))
        {
            if (!_config.Settings.Tagless)
                try { await EnsureTagsExistAsync(client, cancellationToken); }
                catch (Exception ex) when (ex is not OperationCanceledException) { failures.Add(ex); }
            try
            {
                var torrents = await client.GetTorrentsAsync(category, cancellationToken: cancellationToken);
                var skip = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                async Task RecordFailureAsync(TorrentInfo torrent, string operation, Exception error)
                {
                    if (error is HttpRequestException { StatusCode: System.Net.HttpStatusCode.NotFound })
                    {
                        skip.Add(torrent.Hash);
                        try
                        {
                            var current = await client.GetTorrentsAsync(cancellationToken: cancellationToken);
                            if (!current.Any(t => t.Hash.Equals(torrent.Hash, StringComparison.OrdinalIgnoreCase)))
                            {
                                _stalledUploads.Evict(instanceName, torrent.Hash);
                                _logger.LogInformation("Torrent {Hash} disappeared from {Instance}; skipping stale seeding inventory", torrent.Hash, instanceName);
                                return;
                            }
                        }
                        catch (Exception refreshError) when (refreshError is not OperationCanceledException)
                        {
                            error = new AggregateException("Unable to verify torrent disappearance against fresh inventory.", error, refreshError);
                        }
                    }
                    failures.Add(error);
                    _logger.LogError(error, "Error in {Operation} for torrent {Hash} on {Instance}", operation, torrent.Hash, instanceName);
                }

                foreach (var torrent in torrents) torrent.QBitInstanceName = instanceName;
                var completedTorrents = torrents.Where(t =>
                    t.Progress >= 1.0 &&
                    !HasTag(t, IgnoredTag) &&
                    !HasTag(t, FreeSpacePausedTag)
                ).ToList();

                foreach (var torrent in completedTorrents)
                {
                    try
                    {
                        await UpdateSingleTorrentSeedingTagAsync(client, torrent, cancellationToken);
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        await RecordFailureAsync(torrent, "seeding tag update", ex);
                    }
                }

                // §3.1 / §3.2: Tracker actions + message scanning — run on all torrents
                foreach (var torrent in torrents)
                {
                    if (skip.Contains(torrent.Hash)) continue;
                    try
                    {
                        await ApplyTrackerActionsAsync(client, torrent, cancellationToken);
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        await RecordFailureAsync(torrent, "tracker actions", ex);
                    }
                    if (skip.Contains(torrent.Hash)) continue;
                    try
                    {
                        await ProcessTrackerMessagesAsync(client, torrent, cancellationToken);
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        await RecordFailureAsync(torrent, "tracker message processing", ex);
                    }
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                failures.Add(ex);
                _logger.LogError(ex, "Error updating seeding tags for instance '{Instance}' category {Category}", instanceName, category);
            }
        }
        if (failures.Count > 0) throw new AggregateException("Some seeding maintenance could not be completed.", failures);
    }

    internal bool HasConfiguredProtection(TorrentInfo torrent) =>
        TrackerRules.ForTorrent(_config, torrent)
            .Any(t => t.Protection != null || IsHnREnabled(t.HitAndRunMode)) || IsHnREnabled(GetEffectiveSeedingConfig(torrent, null).HitAndRunMode);

    internal Task ReconcileProtectionLimitsAsync(TorrentInfo torrent, IReadOnlyList<TorrentTracker> trackers, CancellationToken ct) =>
        ReconcileProtectionLimitsFromDecisionsAsync(torrent, GetProtectionDecisions(torrent, trackers, observe: true), ct);

    internal async Task ReconcileProtectionLimitsFromDecisionsAsync(TorrentInfo torrent, IReadOnlyList<ProtectionDecision> decisions, CancellationToken ct)
    {
        var client = GetClient(torrent);
        if (client == null || !client.Capabilities.PerTorrentLimits || _config.Revision != _config.LatestRevision) return;
        var key = MaintenancePersistence.Key(torrent.ClientInstanceId, torrent.Hash, "native-limits");
        var state = MaintenancePersistence.Read<ProtectionObservation>(_dbContext, key);
        if (state.AddedOn != torrent.AddedOn) state = new();
        var configured = torrent.IsPrivate != false || HasConfiguredProtection(torrent);
        if (!configured && !state.PreviousRatioLimit.HasValue) return;
        var blocked = configured && decisions.Any(d => d.State != "eligible");
        var unlimited = torrent.RatioLimit == -1 && torrent.SeedingTimeLimit == -1
            && (torrent.InactiveSeedingTimeLimit == null || torrent.InactiveSeedingTimeLimit == -1);
        async Task<bool> Set(double ratio, long time, long? inactive) => inactive.HasValue
            ? await client.SetShareLimitsAsync(torrent.Hash, ratio, time, inactive.Value, ct)
            : await client.SetShareLimitsAsync(torrent.Hash, ratio, time, ct);
        if (blocked && !unlimited)
        {
            state.AddedOn = torrent.AddedOn;
            state.PreviousRatioLimit = torrent.RatioLimit;
            state.PreviousTimeLimit = torrent.SeedingTimeLimit;
            state.PreviousInactiveTimeLimit = torrent.InactiveSeedingTimeLimit;
            MaintenancePersistence.Write(_dbContext, key, state);
            if (!await Set(-1, -1, torrent.InactiveSeedingTimeLimit.HasValue ? -1 : null))
                _logger.LogWarning("Unable to clear conflicting native stop limits for {Hash}; protection remains active", torrent.Hash);
            else
            {
                torrent.RatioLimit = -1;
                torrent.SeedingTimeLimit = -1;
                if (torrent.InactiveSeedingTimeLimit.HasValue) torrent.InactiveSeedingTimeLimit = -1;
            }
        }
        else if (!blocked && state.PreviousRatioLimit.HasValue)
        {
            // Restore only values still owned by Torrentarr, preserving later user changes.
            if (!unlimited || await Set(state.PreviousRatioLimit.Value, state.PreviousTimeLimit ?? -1, state.PreviousInactiveTimeLimit))
                MaintenancePersistence.Write(_dbContext, key, new ProtectionObservation());
        }
    }

    /// <summary>
    /// Apply rate limits to a torrent.
    /// Note: qBitrr removed set_share_limits from tracker processing - limits are set per-torrent.
    /// </summary>
    public async Task ApplySeedingLimitsAsync(TorrentInfo torrent, CancellationToken cancellationToken = default)
    {
        var client = GetClient(torrent);
        if (client == null || !client.Capabilities.PerTorrentLimits) return;

        var trackerConfigs = GetTrackerList(torrent);
        IReadOnlyList<TorrentTracker> trackerEvidence = [];
        if (!torrent.IsPrivate.HasValue)
        {
            try
            {
                torrent.IsPrivate = (await client.GetTorrentPropertiesAsync(torrent.Hash, cancellationToken))?.IsPrivate;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "Error getting privacy evidence for torrent {Hash}", torrent.Hash);
            }
        }
        if (trackerConfigs.Count > 0 || torrent.IsPrivate != false || HasConfiguredProtection(torrent))
        {
            try
            {
                trackerEvidence = await client.GetTorrentTrackersAsync(torrent.Hash, cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "Error getting tracker evidence for torrent {Hash}", torrent.Hash);
            }
        }
        var trackerConfig = SelectTrackerConfig(trackerConfigs, trackerEvidence);
        var config = GetEffectiveSeedingConfig(torrent, trackerConfig);

        var dlLimit = config.DownloadRateLimitPerTorrent;
        if (dlLimit >= 0)
        {
            try
            {
                var limitBytes = dlLimit > 0 ? dlLimit * 1024 : -1;
                var accepted = await client.SetDownloadLimitAsync(torrent.Hash, limitBytes, cancellationToken);
                _logger.LogInformation("Download limit requested for '{Name}': {Limit} KB/s, accepted={Accepted}", torrent.Name, dlLimit, accepted);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogError(ex, "Failed to set download limit for '{Name}'", torrent.Name);
            }
        }

        var ulLimit = config.UploadRateLimitPerTorrent;
        if (ulLimit >= 0)
        {
            try
            {
                var limitBytes = ulLimit > 0 ? ulLimit * 1024 : -1;
                var accepted = await client.SetUploadLimitAsync(torrent.Hash, limitBytes, cancellationToken);
                _logger.LogInformation("Upload limit requested for '{Name}': {Limit} KB/s, accepted={Accepted}", torrent.Name, ulLimit, accepted);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogError(ex, "Failed to set upload limit for '{Name}'", torrent.Name);
            }
        }
    }

    private async Task UpdateSingleTorrentSeedingTagAsync(
        ITorrentClient client,
        TorrentInfo torrent,
        CancellationToken cancellationToken)
    {
        var meetsRequirements = await MeetsSeedingRequirementsAsync(torrent.Hash, torrent.Category, torrent.QBitInstanceName, cancellationToken);
        var hasAllowedSeedingTag = HasTag(torrent, AllowedSeedingTag);

        if (meetsRequirements && !hasAllowedSeedingTag)
        {
            if (_config.Settings.Tagless || _qbitManager.GetClient(torrent.QBitInstanceName)?.Capabilities.Tags == false)
            {
                await _dbContext.TorrentLibrary
                    .Where(t => t.Hash == torrent.Hash && t.QbitInstance == torrent.QBitInstanceName)
                    .ExecuteUpdateAsync(s => s.SetProperty(t => t.AllowedSeeding, true), cancellationToken);
            }
            else
            {
                if (!await client.AddTagsAsync(
                    new List<string> { torrent.Hash },
                    new List<string> { AllowedSeedingTag },
                    cancellationToken)) throw new IOException("Torrent client rejected the seeding tag update.");
            }
            _logger.LogInformation("AllowedSeeding set for torrent {Name} (Tagless={Tagless})", torrent.Name, _config.Settings.Tagless);
        }
        else if (!meetsRequirements && hasAllowedSeedingTag)
        {
            if (_config.Settings.Tagless || _qbitManager.GetClient(torrent.QBitInstanceName)?.Capabilities.Tags == false)
            {
                await _dbContext.TorrentLibrary
                    .Where(t => t.Hash == torrent.Hash && t.QbitInstance == torrent.QBitInstanceName)
                    .ExecuteUpdateAsync(s => s.SetProperty(t => t.AllowedSeeding, false), cancellationToken);
            }
            else
            {
                if (!await client.RemoveTagsAsync(
                    new List<string> { torrent.Hash },
                    new List<string> { AllowedSeedingTag },
                    cancellationToken)) throw new IOException("Torrent client rejected the seeding tag update.");
            }
            _logger.LogInformation("AllowedSeeding cleared for torrent {Name} (Tagless={Tagless})", torrent.Name, _config.Settings.Tagless);
        }
    }

    private bool HasTag(TorrentInfo torrent, string tag)
    {
        // §1.6 Tagless mode: map tag names to TorrentLibrary DB columns.
        // qBitrr still honors the live qBit "qBitrr-ignored" tag in Tagless mode.
        if (_config.Settings.Tagless || _qbitManager.GetClient(torrent.QBitInstanceName)?.Capabilities.Tags == false)
        {
            if (tag == IgnoredTag)
                return HasQbitTag(torrent, IgnoredTag);
            var dbEntry = _dbContext.TorrentLibrary.AsNoTracking()
                .FirstOrDefault(t => t.Hash == torrent.Hash && t.QbitInstance == torrent.QBitInstanceName);
            if (dbEntry == null) return false;
            return tag switch
            {
                AllowedSeedingTag => dbEntry.AllowedSeeding,
                FreeSpacePausedTag => dbEntry.FreeSpacePaused,
                _ => false
            };
        }

        return HasQbitTag(torrent, tag);
    }

    private static bool HasQbitTag(TorrentInfo torrent, string tag)
    {
        if (string.IsNullOrEmpty(torrent.Tags))
            return false;

        var tags = torrent.Tags.Split(',', StringSplitOptions.RemoveEmptyEntries)
            .Select(t => t.Trim())
            .ToList();

        return tags.Contains(tag, StringComparer.OrdinalIgnoreCase);
    }

    private async Task EnsureTagsExistAsync(ITorrentClient client, CancellationToken cancellationToken)
    {
        if (!client.Capabilities.Tags) return;
        try
        {
            var existingTags = await client.GetTagsAsync(cancellationToken);
            var tagsToCreate = new List<string>();

            if (!existingTags.Contains(AllowedSeedingTag, StringComparer.OrdinalIgnoreCase))
                tagsToCreate.Add(AllowedSeedingTag);

            if (tagsToCreate.Count > 0)
            {
                if (!await client.CreateTagsAsync(tagsToCreate, cancellationToken)) throw new IOException("Torrent client rejected tag creation.");
                _logger.LogInformation("Created tags: {Tags}", string.Join(", ", tagsToCreate));
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Failed to ensure tags exist");
            throw;
        }
    }

    /// <summary>Pre-create tracker tags on the clients where the rules apply.</summary>
    public async Task<bool> EnsureAllTrackerTagsExistAsync(CancellationToken cancellationToken = default)
    {
        var succeeded = true;
        foreach (var (id, client) in _qbitManager.GetAllClients())
        {
            if (!client.Capabilities.Tags) continue;
            var tags = TrackerRules.ForClient(_config, id).Concat(_config.ArrInstances
                    .Where(pair => CategoryOwnershipHelper.ArrOwnsTorrent(_config, pair.Value,
                        new TorrentInfo { ClientInstanceId = id, Category = pair.Value.Category }))
                    .SelectMany(pair => TrackerRules.ForIntegration(_config, pair.Key, pair.Value)))
                .SelectMany(rule => rule.AddTags).Where(tag => !string.IsNullOrWhiteSpace(tag))
                .Select(tag => tag.Trim()).ToHashSet(StringComparer.OrdinalIgnoreCase);
            if (tags.Count == 0) continue;
            try
            {
                var existing = await client.GetTagsAsync(cancellationToken);
                var toCreate = tags.Where(t => !existing.Contains(t, StringComparer.OrdinalIgnoreCase)).ToList();
                if (toCreate.Count > 0)
                {
                    if (await client.CreateTagsAsync(toCreate, cancellationToken))
                        _logger.LogInformation("Pre-created tracker tags on qBit: {Tags}", string.Join(", ", toCreate));
                    else
                        succeeded = false;
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                succeeded = false;
                _logger.LogWarning(ex, "Failed to pre-create tracker tags");
            }
        }
        return succeeded;
    }

    /// <summary>
    /// Public entry point for tracker actions, callable from TorrentProcessor pre-step.
    /// In qBitrr this runs for EVERY torrent BEFORE the state machine.
    /// </summary>
    public async Task ApplyTrackerActionsForTorrentAsync(TorrentInfo torrent, CancellationToken cancellationToken = default)
    {
        var client = _qbitManager.GetClient(torrent.QBitInstanceName);
        if (client == null) return;

        try
        {
            await ApplyTrackerActionsAsync(client, torrent, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Error applying tracker actions for {Hash}", torrent.Hash);
        }

        try
        {
            await ProcessTrackerMessagesAsync(client, torrent, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Error processing tracker messages for {Hash}", torrent.Hash);
        }
    }

    /// <inheritdoc />
    public async Task<int> GetQueueSortAnnouncePriorityAsync(TorrentInfo torrent, CancellationToken cancellationToken = default)
    {
        try
        {
            var cfg = await GetTrackerConfigAsync(torrent, cancellationToken);
            return cfg?.Priority ?? -100;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug(ex, "Queue sort announce priority failed for {Hash}", torrent.Hash);
            return -100;
        }
    }

    /// <inheritdoc />
    public async Task<int> GetTorrentQueueSortPriorityAsync(
        TorrentInfo torrent,
        IReadOnlyDictionary<string, int> tagToPriorityMax,
        CancellationToken cancellationToken = default)
    {
        var announcePri = await GetQueueSortAnnouncePriorityAsync(torrent, cancellationToken);
        if (tagToPriorityMax == null || tagToPriorityMax.Count == 0)
            return announcePri;

        var present = ParseQBitTagListForQueueSort(torrent.Tags);
        var matched = new List<int>();
        foreach (var tag in present)
        {
            if (tagToPriorityMax.TryGetValue(tag, out var p))
                matched.Add(p);
        }

        if (matched.Count == 0)
            return announcePri;

        var tagPri = matched.Max();
        if (tagPri <= -100)
            return announcePri;
        return Math.Max(tagPri, announcePri);
    }

    private static List<string> ParseQBitTagListForQueueSort(string? tags)
    {
        if (string.IsNullOrWhiteSpace(tags)) return [];
        return tags.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
    }

    /// <summary>
    /// §3.1: Apply per-tracker-config actions (RemoveIfExists, AddTrackerIfMissing, AddTags).
    /// Iterates the merged tracker list and applies each action for the trackers that match/don't match.
    /// </summary>
    private async Task ApplyTrackerActionsAsync(
        ITorrentClient client,
        TorrentInfo torrent,
        CancellationToken ct)
    {
        var trackers = GetTrackerList(torrent);
        var actionTrackers = trackers.Where(t =>
            t.RemoveIfExists || t.AddTrackerIfMissing || t.AddTags.Count > 0 || t.SuperSeedMode.HasValue).ToList();
        if (actionTrackers.Count == 0) return;

        var torrentTrackers = await client.GetTorrentTrackersAsync(torrent.Hash, ct);

        var existingTagSet = (torrent.Tags ?? "")
            .Split(',', StringSplitOptions.RemoveEmptyEntries)
            .Select(t => t.Trim())
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var tagsToAdd = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        bool? superSeedMode = null;

        foreach (var cfg in actionTrackers)
        {
            var cfgHost = ExtractTrackerHost(cfg.Uri ?? "");
            if (string.IsNullOrEmpty(cfgHost)) continue;

            // Find torrent tracker entries that match this config's host (direct or subdomain)
            var matchingTrackers = torrentTrackers.Where(t => t.Tier >= 0 &&
                TrackerHostMatches(ExtractTrackerHost(t.Url), cfgHost)).ToList();
            var isPresent = matchingTrackers.Count > 0;
            if (cfg.RemoveIfExists && isPresent)
                await RemoveAnnounceUrlsAsync(client, torrent, matchingTrackers.Select(t => t.Url).ToList(), ct);

            // AddTrackerIfMissing: torrent lacks this tracker → inject it
            if (client.Capabilities.TrackerMutation && cfg.AddTrackerIfMissing && !cfg.RemoveIfExists && !isPresent && !string.IsNullOrEmpty(cfg.Uri))
            {
                _logger.LogInformation("AddTrackerIfMissing: adding tracker host {Host} to [{Name}]", TrackerLogHost(cfg.Uri), torrent.Name);
                if (!await client.AddTrackersAsync(torrent.Hash, new List<string> { cfg.Uri }, ct)) throw new IOException("Torrent client rejected tracker addition.");
                _logger.LogInformation("Client accepted tracker addition for {Hash} on {Client}", torrent.Hash, torrent.ClientInstanceId);
            }

            // AddTags: active tracker match → queue user-defined tags
            if (cfg.AddTags.Count > 0 && isPresent)
            {
                foreach (var tag in cfg.AddTags)
                    tagsToAdd.Add(tag);
            }

            if (isPresent && cfg.SuperSeedMode.HasValue) superSeedMode ??= cfg.SuperSeedMode.Value;
        }
        if (client.Capabilities.SuperSeeding && superSeedMode.HasValue && torrent.SuperSeeding != superSeedMode.Value)
        {
            if (!await client.SetSuperSeedingAsync(torrent.Hash, superSeedMode.Value, ct)) throw new IOException("Torrent client rejected super-seeding update.");
            torrent.SuperSeeding = superSeedMode.Value;
            _logger.LogInformation("Client accepted super-seeding update for {Hash} on {Client}: enabled={Enabled}", torrent.Hash, torrent.ClientInstanceId, superSeedMode.Value);
        }

        // Apply new tags (only add, never remove — qBitrr pattern)
        var newTags = tagsToAdd.Where(t => !existingTagSet.Contains(t)).ToList();
        if (client.Capabilities.Tags && newTags.Count > 0)
        {
            _logger.LogInformation("AddTags: adding {Tags} to [{Name}]", string.Join(", ", newTags), torrent.Name);
            if (!await client.AddTagsAsync(new List<string> { torrent.Hash }, newTags, ct)) throw new IOException("Torrent client rejected tracker tag update.");
            _logger.LogInformation("Client accepted tracker tag update for {Hash} on {Client}", torrent.Hash, torrent.ClientInstanceId);
        }
    }

    /// <summary>
    /// §3.2: Check each tracker's status message against RemoveTrackerWithMessage list.
    /// If matched: remove the tracker only after protection clearance; RemoveDeadTrackers=true reports and preserves.
    /// Checks both Arr-level SeedingMode and TorrentConfig lists.
    /// </summary>
    internal async Task ObserveTrackerIssueAsync(TorrentInfo torrent, IReadOnlyList<TorrentTracker> trackers, CancellationToken ct)
    {
        var issueKey = MaintenancePersistence.Key(torrent.ClientInstanceId, torrent.Hash, "tracker-issue");
        var client = _config.GetTorrentClient(torrent.ClientInstanceId);
        var matchesCleanup = client != null && trackers.Any(t => MaintenanceRecovery.MatchesUnregistered(t, client.Maintenance.Unregistered));
        var rejected = matchesCleanup || trackers.Any(t => TrackerMessageIndicatesDead(t.Msg));
        var healthy = MaintenanceRecovery.HealthyTracker(trackers);
        var parking = client != null && IsInAutomaticCleanupScope(torrent, client)
            && MaintenanceRecovery.ShouldParkIncomplete(torrent, client.Maintenance.Unregistered);
        if (parking && !healthy && TorrentPolicyHelper.IsPausedDownloadStateForFreeSpace(torrent.State))
        {
            var previous = await _dbContext.MaintenanceState.AsNoTracking().Where(s => s.Key == issueKey)
                .Select(s => s.Data).SingleOrDefaultAsync(ct);
            var lastRejection = previous == null ? null : Newtonsoft.Json.JsonConvert.DeserializeObject<TorrentAutomationIssue>(previous);
            if (matchesCleanup || lastRejection?.AddedOn == torrent.AddedOn
                && trackers.Where(t => t.Tier >= 0).All(t => string.IsNullOrWhiteSpace(t.Msg)))
            {
                var detail = matchesCleanup ? "The tracker rejection is still present." : "Last confirmed tracker rejection retained. Waiting for fresh tracker evidence.";
                MaintenancePersistence.Write(_dbContext, issueKey, new TorrentAutomationIssue(torrent.ClientInstanceId,
                    torrent.Hash, torrent.Name, $"Parked incomplete torrent. {detail}", torrent.AddedOn)
                { RequiresAttention = false });
                return;
            }
        }
        if (rejected && !healthy)
        {
            var (requiresAttention, reason) = parking && matchesCleanup && MaintenanceRecovery.CanParkDownload(torrent)
                && client!.Maintenance.Unregistered.MaxPerTrackerPerRun > 0
                ? (false, HnrAllowsDelete(torrent, trackers)
                    ? "Automatic parking is waiting for the cleanup schedule or grace period."
                    : "Tracker protection blocks parking. Torrentarr is preserving the torrent's current state.")
                : DescribeTrackerRejection(torrent, client, matchesCleanup);
            MaintenancePersistence.Write(_dbContext, issueKey, new TorrentAutomationIssue(torrent.ClientInstanceId, torrent.Hash, torrent.Name,
                reason, torrent.AddedOn)
            { RequiresAttention = requiresAttention });
        }
        else if (healthy) await MaintenancePersistence.DeleteAsync(_dbContext, issueKey, ct);
    }

    private bool IsInAutomaticCleanupScope(TorrentInfo torrent, TorrentClientInstanceConfig client)
        => MaintenanceCoordinator.IsAutomatic(_config, client, MaintenanceOperation.UnregisteredCleanup)
            && MaintenanceCoordinator.IsCategoryAndTagInScope(torrent, client, _config)
            && MaintenanceCoordinator.IsOperationScope(torrent, client.Maintenance, MaintenanceOperation.UnregisteredCleanup)
            && torrent.AddedOn > 0 && torrent.AddedOn <= _clock.GetUtcNow().ToUnixTimeSeconds();

    private (bool RequiresAttention, string Reason) DescribeTrackerRejection(
        TorrentInfo torrent, TorrentClientInstanceConfig? client, bool matchesCleanup)
    {
        var now = _clock.GetUtcNow();
        if (client == null || !matchesCleanup || !IsInAutomaticCleanupScope(torrent, client)
            || client.Maintenance.Unregistered.CompletedOnly && torrent.Progress < 1
            || MaintenanceRecovery.ShouldParkIncomplete(torrent, client.Maintenance.Unregistered))
            return (true, "Tracker rejected this torrent and no authorized automatic cleanup rule applies.");

        var maintenance = client.Maintenance;
        var recovery = maintenance.ErrorRecovery;
        var state = MaintenanceRecovery.Read(_dbContext, torrent, nameof(MaintenanceOperation.UnregisteredCleanup));
        if (state.Stage == "rejected")
            return (true, "The torrent client rejected the automatic cleanup recheck.");
        if (state.Stage == "unconfirmed" || state.CompletedAt == null && state.RequestedAt is { } requested
            && now - requested > TimeSpan.FromMinutes(recovery.RecheckTimeoutMinutes))
            return (true, "The automatic cleanup recheck could not be confirmed.");

        var capabilities = _qbitManager.GetClient(torrent.ClientInstanceId)?.Capabilities ?? TorrentClientCapabilities.None;
        var canRecheck = capabilities.Recheck && recovery.MaxPerRun > 0 && state.Attempts < recovery.MaxRepairAttempts;
        var recheckInProgress = state.Stage is "requested" or "checking";
        var canCleanUp = maintenance.RecycleBin.Enabled
            ? maintenance.RecycleBin.MaxTorrentMovesPerRun > 0
            : capabilities.ContentDeletion;
        var hasNextStep = state.Stage == "recurring" ? canCleanUp : canRecheck || recheckInProgress;
        if (maintenance.Unregistered.MaxPerTrackerPerRun <= 0 || !hasNextStep)
            return (true, "The tracker still rejects this torrent and no supported automatic cleanup step remains.");

        var grace = Math.Max(Math.Max(maintenance.MinimumTorrentAgeMinutes, maintenance.Unregistered.GraceMinutes) * 60L,
            _config.Settings.IgnoreTorrentsYoungerThan);
        if (now.ToUnixTimeSeconds() - torrent.AddedOn < grace)
            return (false, "Automatic cleanup is waiting for the configured torrent age or grace period.");
        return (false, $"Automatic cleanup is handling this tracker rejection. Recovery stage: {state.Stage}.");
    }

    private bool PreservesPrivateAnnounceUrl(
        TorrentInfo torrent,
        TorrentTracker tracker,
        IReadOnlyCollection<string> remainingAnnounceUrls)
    {
        if (torrent.IsPrivate == false) return false;
        var host = ExtractTrackerHost(tracker.Url);
        if (TrackerRules.ForTorrent(_config, torrent).Any(c => TrackerHostMatches(host, ExtractTrackerHost(c.Uri))))
            return true;
        return remainingAnnounceUrls.Count <= 1;
    }

    internal async Task ProcessTrackerMessagesAsync(
        ITorrentClient client,
        TorrentInfo torrent,
        CancellationToken ct)
    {
        var trackers = await client.GetTorrentTrackersAsync(torrent.Hash, ct);
        await ObserveTrackerIssueAsync(torrent, trackers, ct);
        // Determine effective RemoveTrackerWithMessage and RemoveDeadTrackers from Arr config
        var arrCfg = _config.ArrInstances.Values
            .FirstOrDefault(a => CategoryOwnershipHelper.ArrOwnsTorrent(_config, a, torrent));

        List<string> keywords;
        bool removeDead;

        if (arrCfg != null)
        {
            // Prefer SeedingMode list (has defaults); fall back to Torrent-level list
            var seedingModeKws = arrCfg.Torrent.SeedingMode?.RemoveTrackerWithMessage;
            var torrentKws = arrCfg.Torrent.RemoveTrackerWithMessage;
            keywords = seedingModeKws ?? torrentKws ?? SeedingModeConfig.DefaultTrackerMessages.ToList();
            removeDead = arrCfg.Torrent.SeedingMode?.RemoveDeadTrackers ?? arrCfg.Torrent.RemoveDeadTrackers ?? false;
        }
        else
        {
            // No Arr config — nothing to do
            return;
        }

        if (keywords.Count == 0) return;


        foreach (var tracker in trackers)
        {
            var msg = tracker.Msg ?? "";
            if (string.IsNullOrEmpty(msg)) continue;

            var matched = keywords.FirstOrDefault(kw =>
                !string.IsNullOrWhiteSpace(kw) && msg.Contains(kw, StringComparison.OrdinalIgnoreCase));

            if (matched == null) continue;

            if (removeDead)
            {
                _logger.LogWarning("Tracker failure for {Hash}: preserving torrent; tracker rejection never authorizes automatic removal", torrent.Hash);
                continue;
            }
            await RemoveAnnounceUrlsAsync(client, torrent, [tracker.Url], ct);
        }
    }
}

using Torrentarr.Core.Configuration;

namespace Torrentarr.Core.Models;

public sealed class ProtectionDecision
{
    public string ClientInstanceId { get; set; } = "";
    public string TorrentHash { get; set; } = "";
    public string TorrentName { get; set; } = "";
    public string Tracker { get; set; } = "";
    public string State { get; set; } = "unknown";
    public string? UnknownReason { get; set; }
    public string Reason { get; set; } = "Protection evidence is unavailable.";
    public double ObservedSeedSeconds { get; set; }
    public double RemainingSeedSeconds { get; set; }
    public DateTimeOffset? Deadline { get; set; }
    public DateTimeOffset? LatestRecoveryStart { get; set; }
    public DateTimeOffset? LatestCompletion { get; set; }
    public DateTimeOffset? EstimatedCompletion { get; set; }
    public string CompletionRisk { get; set; } = "none";
    public string DeadlineRisk { get; set; } = "none";
    public bool LocalEstimate { get; set; } = true;
    public bool NativeStopLimitRisk { get; set; }

    public string SeedAccountingDescription() => $"{(LocalEstimate ? "Locally observed" : "Client-reported")} seed={ObservedSeedSeconds:F0}s; remaining time requirement={RemainingSeedSeconds:F0}s (ratio/accounting requirements may still apply)";
}

public sealed class ProtectionObservation
{
    public long AddedOn { get; set; }
    public DateTimeOffset? LastObserved { get; set; }
    public double SeedSeconds { get; set; }
    public bool HealthySeeding { get; set; }
    public long ClientSeedSeconds { get; set; }
    public double ClientRatio { get; set; }
    public long CompletionOn { get; set; }
    public double MaximumDownloadPercent { get; set; }
    public string PolicyFingerprint { get; set; } = "";
    public DateTimeOffset? EligibleSince { get; set; }
    public double? PreviousRatioLimit { get; set; }
    public long? PreviousTimeLimit { get; set; }
    public long? PreviousInactiveTimeLimit { get; set; }
}

public static class TrackerProtectionEvaluator
{
    public static bool ConfirmLegacyClearance(ProtectionObservation observation, TorrentInfo torrent,
        bool qualifies, DateTimeOffset now, TimeSpan maximumGap, TimeSpan grace, bool observe)
    {
        var previous = observation.LastObserved;
        var continuous = previous is { } last && now >= last && now - last <= maximumGap
            && EvidenceMatches(observation, torrent) && torrent.Ratio >= observation.ClientRatio;
        if (observe)
        {
            observation.EligibleSince = qualifies
                ? (continuous ? observation.EligibleSince : null) ?? now
                : null;
            observation.AddedOn = torrent.AddedOn;
            observation.CompletionOn = torrent.CompletionOn;
            observation.ClientSeedSeconds = torrent.SeedingTime;
            observation.ClientRatio = double.IsFinite(torrent.Ratio) ? torrent.Ratio : 0;
            observation.LastObserved = now;
            continuous = true;
        }
        return qualifies && continuous && observation.EligibleSince is { } since
            && observation.LastObserved is { } confirmed && confirmed > since && confirmed - since >= grace;
    }

    public static ProtectionDecision Evaluate(TorrentInfo torrent, TrackerProtectionConfig policy,
        ProtectionObservation observation, DateTimeOffset now, bool healthy, TimeSpan? maximumGap = null, bool checkingRequirements = false)
    {
        if (!EvidenceMatches(observation, torrent))
            observation = new() { MaximumDownloadPercent = observation.AddedOn == torrent.AddedOn ? observation.MaximumDownloadPercent : 0 };
        var threshold = policy.Policy is "onlyencodes" or "torrentleech-power" ? 10 : policy.DownloadThresholdPercent;
        var inclusive = policy.Policy != "onlyencodes" && policy.ThresholdInclusive;
        var partialPolicy = policy.Policy == "onlyencodes" ? "preserve" : policy.Policy == "torrentleech-power" ? "ratio" : policy.PartialPolicy;
        var requiredHours = policy.Policy switch { "onlyencodes" => 48, "torrentleech-power" => 192, _ => policy.MinimumSeedHours };
        var required = (requiredHours + policy.AdditionalSeedHours) * 3600;
        var result = new ProtectionDecision
        {
            ClientInstanceId = torrent.ClientInstanceId,
            TorrentHash = torrent.Hash,
            TorrentName = torrent.Name,
            ObservedSeedSeconds = observation.SeedSeconds,
            RemainingSeedSeconds = Math.Max(0, required - observation.SeedSeconds)
        };
        if (!double.IsFinite(torrent.Progress) || torrent.Progress < 0 || torrent.Progress > 1)
            return result;
        var percent = Math.Max(torrent.Progress * 100, observation.MaximumDownloadPercent);
        var hasObligation = inclusive ? percent >= threshold : percent > threshold;
        var partial = torrent.Progress < 1;
        // Crossing the threshold once creates an obligation even if a later recheck lowers progress.
        hasObligation |= observation.SeedSeconds > 0 || observation.CompletionOn > 0;
        var ratioMet = policy.AllowClientRatioClearance && double.IsFinite(torrent.Ratio) && torrent.Ratio >= (policy.Policy == "torrentleech-power" ? 1 : policy.MinimumRatio);
        var seedMet = !partial && observation.SeedSeconds >= required;
        var meets = !hasObligation || (partial ? partialPolicy == "ratio" && ratioMet
            : policy.Policy == "onlyencodes" ? seedMet : seedMet || ratioMet);
        if (meets && (ratioMet && policy.Policy != "onlyencodes" || !hasObligation))
            result.RemainingSeedSeconds = 0;
        var confirmedAt = maximumGap.HasValue ? observation.LastObserved : now;
        var continuous = !maximumGap.HasValue || confirmedAt is { } last && now >= last
            && now - last <= maximumGap.Value && observation.EligibleSince < last
            && torrent.Ratio >= observation.ClientRatio;
        result.State = "protected";
        result.Reason = partial ? "Partial download cannot clear through elapsed seeding time." : "Mandatory observed seeding or accounting requirements remain unmet.";
        if (!hasObligation) { result.State = "eligible"; result.Reason = "Download threshold has not been reached."; }
        else if (meets && healthy && (checkingRequirements || continuous && observation.EligibleSince is { } since && confirmedAt >= since.AddMinutes(policy.AccountingGraceMinutes)))
        { result.State = "eligible"; result.Reason = "Configured requirements and accounting grace met using conservative local observations."; }
        else if (!healthy)
        {
            result.State = "unknown";
            result.UnknownReason = "tracker-accounting-unconfirmed";
            result.Reason = meets
                ? "Local requirements are met. Waiting for a healthy tracker response to confirm accounting."
                : "Local requirements remain unmet, and the tracker is not reporting healthy accounting.";
        }
        var hours = policy.FulfillmentHours ?? (policy.Policy == "onlyencodes" ? 168 : (double?)null);
        var start = policy.DeadlineStart == "completed" ? torrent.CompletionOn : torrent.AddedOn;
        if (hours.HasValue && start > 0 && start <= now.ToUnixTimeSeconds())
        {
            result.Deadline = DateTimeOffset.FromUnixTimeSeconds(start).AddHours(hours.Value);
            result.LatestCompletion = result.Deadline.Value.AddSeconds(-required).AddMinutes(-policy.AccountingGraceMinutes).AddHours(-policy.RecoveryReserveHours);
            var remainingGrace = TimeSpan.FromMinutes(policy.AccountingGraceMinutes);
            if (meets && continuous && observation.EligibleSince is { } eligibleSince && confirmedAt is { } confirmed)
                remainingGrace = TimeSpan.FromTicks(Math.Max(0, (remainingGrace - (confirmed - eligibleSince)).Ticks));
            result.LatestRecoveryStart = result.Deadline.Value.AddSeconds(-result.RemainingSeedSeconds)
                .Subtract(remainingGrace).AddHours(-policy.RecoveryReserveHours);
            if (hasObligation && result.State != "eligible")
                result.DeadlineRisk = now.AddSeconds(result.RemainingSeedSeconds).Add(remainingGrace) > result.Deadline
                    ? "unachievable" : now >= result.LatestRecoveryStart ? "critical"
                    : now.AddHours(24) >= result.LatestRecoveryStart ? "warning" : "none";
            if (partial && hasObligation && result.State != "eligible" && !meets)
            {
                result.CompletionRisk = "unknown-eta";
                if (torrent.Eta > 0 && torrent.Eta < TimeSpan.FromDays(36500).TotalSeconds)
                {
                    result.EstimatedCompletion = now.AddSeconds(torrent.Eta);
                    result.CompletionRisk = result.EstimatedCompletion <= result.LatestCompletion ? "feasible" : "late";
                    if (result.EstimatedCompletion.Value.AddSeconds(required).AddMinutes(policy.AccountingGraceMinutes) > result.Deadline)
                        result.DeadlineRisk = "unachievable";
                    else if (result.CompletionRisk == "late" && result.DeadlineRisk == "none") result.DeadlineRisk = "warning";
                }
                if (result.CompletionRisk == "unknown-eta" && result.DeadlineRisk == "none") result.DeadlineRisk = "unknown-eta";
            }
        }
        result.Reason += $" Policy={policy.Policy}; maximum downloaded={percent:F2}%; threshold={(inclusive ? ">=" : ">")}{threshold}%; partial={partial}; partial policy={partialPolicy}; ratio={torrent.Ratio:F3}, ratio clearance enabled={policy.AllowClientRatioClearance}, required ratio={(policy.Policy == "torrentleech-power" ? 1 : policy.MinimumRatio)}; observed seed={observation.SeedSeconds:F0}s, required={required:F0}s; healthy tracker={healthy}; accounting grace={policy.AccountingGraceMinutes}min, eligible since={observation.EligibleSince:O}.";
        return result;
    }

    public static void Observe(ProtectionObservation observation, TorrentInfo torrent, bool healthy, DateTimeOffset now, TimeSpan maximumGap)
    {
        if (!EvidenceMatches(observation, torrent))
        { observation.SeedSeconds = 0; observation.EligibleSince = null; observation.HealthySeeding = false; observation.LastObserved = null; }
        if (observation.AddedOn != torrent.AddedOn) observation.MaximumDownloadPercent = 0;
        observation.AddedOn = torrent.AddedOn;
        var seeding = healthy && torrent.Progress >= 1 && torrent.CompletionOn > 0 && torrent.CompletionOn <= now.ToUnixTimeSeconds()
            && torrent.State.ToLowerInvariant() is "uploading" or "stalledup" or "forcedup" or "seeding";
        if (seeding && observation.HealthySeeding && observation.LastObserved is { } last)
        {
            var elapsed = now - last;
            if (elapsed >= TimeSpan.Zero && elapsed <= maximumGap)
                observation.SeedSeconds += Math.Max(0, Math.Min(elapsed.TotalSeconds, torrent.SeedingTime - observation.ClientSeedSeconds));
        }
        if (double.IsFinite(torrent.Progress)) observation.MaximumDownloadPercent = Math.Max(observation.MaximumDownloadPercent, torrent.Progress * 100);
        observation.CompletionOn = torrent.CompletionOn;
        observation.ClientSeedSeconds = torrent.SeedingTime;
        if (!healthy) observation.EligibleSince = null;
        observation.HealthySeeding = seeding;
        observation.LastObserved = now;
    }
    private static bool EvidenceMatches(ProtectionObservation observation, TorrentInfo torrent)
        => observation.AddedOn == torrent.AddedOn && observation.CompletionOn == torrent.CompletionOn
            && torrent.SeedingTime >= observation.ClientSeedSeconds;

}

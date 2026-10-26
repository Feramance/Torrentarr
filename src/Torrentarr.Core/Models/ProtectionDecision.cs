using Torrentarr.Core.Configuration;

namespace Torrentarr.Core.Models;

public sealed class ProtectionDecision
{
    public string ClientInstanceId { get; set; } = "";
    public string TorrentHash { get; set; } = "";
    public string TorrentName { get; set; } = "";
    public string Tracker { get; set; } = "";
    public string State { get; set; } = "unknown";
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
}

public sealed class ProtectionObservation
{
    public long AddedOn { get; set; }
    public DateTimeOffset? LastObserved { get; set; }
    public double SeedSeconds { get; set; }
    public bool HealthySeeding { get; set; }
    public long ClientSeedSeconds { get; set; }
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
    public static ProtectionDecision Evaluate(TorrentInfo torrent, TrackerProtectionConfig policy,
        ProtectionObservation observation, DateTimeOffset now, bool healthy)
    {
        var threshold = policy.Policy is "onlyencodes" or "torrentleech-power" ? 10 : policy.DownloadThresholdPercent;
        var inclusive = policy.Policy != "onlyencodes" && policy.ThresholdInclusive;
        var partialPolicy = policy.Policy == "onlyencodes" ? "preserve" : policy.Policy == "torrentleech-power" ? "ratio" : policy.PartialPolicy;
        var requiredHours = policy.Policy switch { "onlyencodes" => 48, "torrentleech-power" => 192, _ => policy.MinimumSeedHours };
        var required = requiredHours * 3600;
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
        var seedMet = !partial && observation.SeedSeconds >= required + policy.AdditionalSeedHours * 3600;
        var meets = !hasObligation || (partial ? partialPolicy == "ratio" && ratioMet
            : policy.Policy == "onlyencodes" ? seedMet : seedMet || ratioMet);
        result.State = "protected";
        result.Reason = partial ? "Partial download cannot clear through elapsed seeding time." : "Mandatory observed seeding or accounting requirements remain unmet.";
        if (!hasObligation) { result.State = "eligible"; result.Reason = "Download threshold has not been reached."; }
        else if (meets && healthy && observation.EligibleSince is { } since && now >= since.AddMinutes(policy.AccountingGraceMinutes))
        { result.State = "eligible"; result.Reason = "Configured requirements and accounting grace met using conservative local observations."; }
        else if (!healthy) { result.State = "unknown"; result.Reason = "Tracker health or accounting is unknown; preserve seeding."; }
        var hours = policy.FulfillmentHours ?? (policy.Policy == "onlyencodes" ? 168 : (double?)null);
        var start = policy.DeadlineStart == "completed" ? torrent.CompletionOn : torrent.AddedOn;
        if (hours.HasValue && start > 0 && start <= now.ToUnixTimeSeconds())
        {
            result.Deadline = DateTimeOffset.FromUnixTimeSeconds(start).AddHours(hours.Value);
            result.LatestCompletion = result.Deadline.Value.AddSeconds(-required).AddMinutes(-policy.AccountingGraceMinutes).AddHours(-policy.RecoveryReserveHours);
            result.LatestRecoveryStart = result.Deadline.Value.AddSeconds(-result.RemainingSeedSeconds)
                .AddMinutes(-policy.AccountingGraceMinutes).AddHours(-policy.RecoveryReserveHours);
            if (hasObligation && result.RemainingSeedSeconds > 0)
                result.DeadlineRisk = now.AddSeconds(result.RemainingSeedSeconds).AddMinutes(policy.AccountingGraceMinutes) > result.Deadline
                    ? "unachievable" : now >= result.LatestRecoveryStart ? "critical"
                    : now.AddHours(24) >= result.LatestRecoveryStart ? "warning" : "none";
            if (partial)
            {
                result.CompletionRisk = torrent.Eta <= 0 ? "unknown-eta" : "late";
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
        return result;
    }

    public static void Observe(ProtectionObservation observation, TorrentInfo torrent, bool healthy, DateTimeOffset now)
    {
        if (observation.AddedOn != 0 && observation.AddedOn != torrent.AddedOn)
        { observation.SeedSeconds = 0; observation.EligibleSince = null; observation.HealthySeeding = false; observation.LastObserved = null; }
        observation.AddedOn = torrent.AddedOn;
        if (observation.CompletionOn != torrent.CompletionOn || torrent.SeedingTime < observation.ClientSeedSeconds)
        { observation.SeedSeconds = 0; observation.EligibleSince = null; }
        var seeding = healthy && torrent.Progress >= 1 && torrent.CompletionOn > 0 && torrent.CompletionOn <= now.ToUnixTimeSeconds()
            && torrent.State.ToLowerInvariant() is "uploading" or "stalledup" or "forcedup" or "seeding";
        if (seeding && observation.HealthySeeding && observation.LastObserved is { } last)
        {
            var elapsed = (now - last).TotalSeconds;
            if (elapsed >= 0 && elapsed <= 330)
                observation.SeedSeconds += Math.Max(0, Math.Min(elapsed, torrent.SeedingTime - observation.ClientSeedSeconds));
        }
        if (double.IsFinite(torrent.Progress)) observation.MaximumDownloadPercent = Math.Max(observation.MaximumDownloadPercent, torrent.Progress * 100);
        observation.CompletionOn = torrent.CompletionOn;
        observation.ClientSeedSeconds = torrent.SeedingTime;
        if (!healthy) observation.EligibleSince = null;
        observation.HealthySeeding = seeding;
        observation.LastObserved = now;
    }
}

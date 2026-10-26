namespace Torrentarr.Core.Configuration;

public static class TrackerProtectionValidation
{
    public static string? Validate(TrackerProtectionConfig? config)
    {
        if (config == null) return null;
        if (config.Policy is not ("custom" or "torrentleech-power" or "onlyencodes")
            || config.PartialPolicy is not ("preserve" or "ratio") || config.DeadlineStart is not ("added" or "completed"))
            return "Unknown tracker protection policy, partial policy, or deadline start.";
        double[] values = [config.MinimumSeedHours, config.MinimumRatio, config.DownloadThresholdPercent,
            config.AdditionalSeedHours, config.AccountingGraceMinutes, config.RecoveryReserveHours];
        if (values.Any(v => !double.IsFinite(v) || v < 0) || config.DownloadThresholdPercent > 100
            || config.MinimumSeedHours > 876000 || config.AdditionalSeedHours > 876000 || config.RecoveryReserveHours > 876000 || config.AccountingGraceMinutes > 52560000
            || config.FulfillmentHours.HasValue && (!double.IsFinite(config.FulfillmentHours.Value) || config.FulfillmentHours <= 0 || config.FulfillmentHours > 876000))
            return "Tracker protection durations and ratios must be finite and non-negative; fulfillment windows must be positive. Durations cannot exceed 100 years.";
        return null;
    }
}

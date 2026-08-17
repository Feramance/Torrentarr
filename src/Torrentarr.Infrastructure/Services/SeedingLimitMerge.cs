using Torrentarr.Core.Configuration;

namespace Torrentarr.Infrastructure.Services;

/// <summary>
/// qBitrr 5.14.2: tracker -1/unset must not wipe positive Arr SeedingMode or CategorySeeding limits.
/// Unlimited (-1) only when no source sets a positive value.
/// Merge order: CategorySeeding → Arr SeedingMode positives → tracker positives.
/// </summary>
public static class SeedingLimitMerge
{
    public static CategorySeedingConfig Merge(
        CategorySeedingConfig categorySeeding,
        SeedingModeConfig? arrSeedingMode,
        TrackerConfig? tracker)
    {
        var result = Clone(categorySeeding);
        OverlayArrSeedingMode(result, arrSeedingMode);
        OverlayTracker(result, tracker);
        return result;
    }

    /// <summary>Tracker MaxETA of -1/unset keeps the Arr Torrent.MaximumETA when that is positive.</summary>
    public static int MergeMaxEta(int torrentMaximumEta, int? trackerMaxEta)
    {
        if (IsPositive(trackerMaxEta))
            return trackerMaxEta!.Value;
        if (IsPositive(torrentMaximumEta))
            return torrentMaximumEta;
        return trackerMaxEta ?? torrentMaximumEta;
    }

    public static bool IsPositive(int? value) => value.HasValue && value.Value > 0;

    public static bool IsPositive(int value) => value > 0;

    public static bool IsPositive(double? value) => value.HasValue && value.Value > 0;

    public static bool IsPositive(double value) => value > 0;

    private static CategorySeedingConfig Clone(CategorySeedingConfig src) => new()
    {
        DownloadRateLimitPerTorrent = src.DownloadRateLimitPerTorrent,
        UploadRateLimitPerTorrent = src.UploadRateLimitPerTorrent,
        MaxUploadRatio = src.MaxUploadRatio,
        MaxSeedingTime = src.MaxSeedingTime,
        RemoveTorrent = src.RemoveTorrent,
        HitAndRunMode = src.HitAndRunMode,
        MinSeedRatio = src.MinSeedRatio,
        MinSeedingTimeDays = src.MinSeedingTimeDays,
        HitAndRunMinimumDownloadPercent = src.HitAndRunMinimumDownloadPercent,
        HitAndRunPartialSeedRatio = src.HitAndRunPartialSeedRatio,
        TrackerUpdateBuffer = src.TrackerUpdateBuffer,
        StalledDelay = src.StalledDelay,
        IgnoreTorrentsYoungerThan = src.IgnoreTorrentsYoungerThan
    };

    private static void OverlayArrSeedingMode(CategorySeedingConfig result, SeedingModeConfig? arr)
    {
        if (arr == null) return;
        if (IsPositive(arr.MaxUploadRatio)) result.MaxUploadRatio = arr.MaxUploadRatio;
        if (IsPositive(arr.MaxSeedingTime)) result.MaxSeedingTime = arr.MaxSeedingTime;
        if (IsPositive(arr.DownloadRateLimitPerTorrent)) result.DownloadRateLimitPerTorrent = arr.DownloadRateLimitPerTorrent;
        if (IsPositive(arr.UploadRateLimitPerTorrent)) result.UploadRateLimitPerTorrent = arr.UploadRateLimitPerTorrent;
        if (IsPositive(arr.RemoveTorrent)) result.RemoveTorrent = arr.RemoveTorrent;
    }

    private static void OverlayTracker(CategorySeedingConfig result, TrackerConfig? tracker)
    {
        if (tracker == null) return;

        if (tracker.MaxUploadRatio is > 0 and var ratio)
            result.MaxUploadRatio = ratio;
        if (tracker.MaxSeedingTime is > 0 and var seedTime)
            result.MaxSeedingTime = seedTime;
        if (tracker.DownloadRateLimit is > 0 and var dl)
            result.DownloadRateLimitPerTorrent = dl;
        if (tracker.UploadRateLimit is > 0 and var ul)
            result.UploadRateLimitPerTorrent = ul;
        if (tracker.RemoveTorrent is > 0 and var remove)
            result.RemoveTorrent = remove;

        if (!string.IsNullOrWhiteSpace(tracker.HitAndRunMode))
            result.HitAndRunMode = tracker.HitAndRunMode;
        if (tracker.MinSeedRatio.HasValue)
            result.MinSeedRatio = tracker.MinSeedRatio.Value;
        if (tracker.MinSeedingTimeDays.HasValue)
            result.MinSeedingTimeDays = tracker.MinSeedingTimeDays.Value;
        if (tracker.HitAndRunMinimumDownloadPercent.HasValue)
            result.HitAndRunMinimumDownloadPercent = tracker.HitAndRunMinimumDownloadPercent.Value;
        if (tracker.HitAndRunPartialSeedRatio.HasValue)
            result.HitAndRunPartialSeedRatio = tracker.HitAndRunPartialSeedRatio.Value;
        if (tracker.TrackerUpdateBuffer.HasValue)
            result.TrackerUpdateBuffer = tracker.TrackerUpdateBuffer.Value;
    }
}

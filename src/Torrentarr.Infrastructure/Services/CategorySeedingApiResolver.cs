using Torrentarr.Core.Configuration;
using Torrentarr.Core.Models;

namespace Torrentarr.Infrastructure.Services;

public sealed record CategorySeedingApiConfig(
    double MaxRatio,
    int MaxTime,
    int RemoveMode,
    int DownloadLimit,
    int UploadLimit);

public sealed record CategorySeedingApiPolicy(
    double MaxRatio,
    int MaxTime,
    int RemoveMode,
    string HitAndRunMode,
    double MinSeedRatio,
    int MinSeedingTimeDays,
    int DownloadLimit,
    int UploadLimit);

public static class CategorySeedingApiResolver
{
    public static List<TorrentInfo> SelectAggregateTorrents(
        IEnumerable<TorrentInfo> torrents,
        string configuredCategory,
        bool matchSubcategories) => torrents
            .Where(torrent => CategoryPathHelper.MatchesConfigured(
                torrent.Category,
                new[] { configuredCategory },
                prefix: matchSubcategories) == CategoryPathHelper.NormalizeCategory(configuredCategory))
            .ToList();

    public static CategorySeedingApiConfig? ResolveAggregate(
        TorrentarrConfig config,
        string qbitSection,
        QBitConfig qbitConfig,
        string configuredCategory,
        IReadOnlyCollection<TorrentInfo> torrents)
    {
        var policy = ResolveAggregatePolicy(
            config, qbitSection, qbitConfig, configuredCategory, torrents);
        return policy == null
            ? null
            : new CategorySeedingApiConfig(
                policy.MaxRatio,
                policy.MaxTime,
                policy.RemoveMode,
                policy.DownloadLimit,
                policy.UploadLimit);
    }

    public static CategorySeedingApiPolicy? ResolveAggregatePolicy(
        TorrentarrConfig config,
        string qbitSection,
        QBitConfig qbitConfig,
        string configuredCategory,
        IReadOnlyCollection<TorrentInfo> torrents)
    {
        var actualCategories = torrents
            .Select(torrent => torrent.Category)
            .Where(category => !string.IsNullOrWhiteSpace(category))
            .Distinct(StringComparer.Ordinal)
            .ToList();
        if (actualCategories.Count == 0)
            actualCategories.Add(configuredCategory);

        CategorySeedingApiPolicy Resolve(string category)
        {
            var effective = SeedingService.ApplyCategoryOverride(
                qbitConfig.CategorySeeding, category, qbitConfig.MatchSubcategories);
            var ownerCategory = CategoryOwnershipHelper.ResolveOwningCategory(
                config, category, qbitSection) ?? configuredCategory;
            var arrSeedingMode = config.ArrInstances.Values
                .FirstOrDefault(arr => CategoryPathHelper.CategoryEquals(
                    arr.Category, ownerCategory))
                ?.Torrent.SeedingMode;
            effective = SeedingLimitMerge.Merge(effective, arrSeedingMode, tracker: null);
            return new CategorySeedingApiPolicy(
                effective.MaxUploadRatio,
                effective.MaxSeedingTime,
                effective.RemoveTorrent,
                effective.HitAndRunMode,
                effective.MinSeedRatio,
                effective.MinSeedingTimeDays,
                effective.DownloadRateLimitPerTorrent,
                effective.UploadRateLimitPerTorrent);
        }

        var first = Resolve(actualCategories[0]);
        return actualCategories.Skip(1).All(category => Resolve(category) == first)
            ? first
            : null;
    }
}

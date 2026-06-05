namespace Torrentarr.Core.Configuration;

/// <summary>Config validation helpers (qBitrr category_paths.py overlap parity).</summary>
public static class ConfigValidationHelper
{
    public static (bool Ok, string? Error) ValidateArrCategoryPaths(TorrentarrConfig config)
    {
        var categories = config.ArrInstances.Values
            .Select(a => a.Category)
            .Where(c => !string.IsNullOrWhiteSpace(c))
            .ToList();

        var conflicts = CategoryPathHelper.FindOverlapConflicts(categories);
        if (conflicts.Count > 0)
        {
            var (parent, child) = conflicts[0];
            return (false, $"Overlapping Arr categories: '{parent}' and '{child}' cannot both be configured.");
        }

        return (true, null);
    }

    public static (bool Ok, string? Error) ValidateManagedCategoryPaths(TorrentarrConfig config)
    {
        foreach (var (_, qbit) in config.QBitInstances)
        {
            var conflicts = CategoryPathHelper.FindOverlapConflicts(qbit.ManagedCategories);
            if (conflicts.Count > 0)
            {
                var (parent, child) = conflicts[0];
                return (false, $"Overlapping qBit ManagedCategories: '{parent}' and '{child}'.");
            }
        }

        return (true, null);
    }

    public static (bool Ok, string? Error) ValidateArrManagedCategoryOverlap(TorrentarrConfig config)
    {
        var arrCategories = config.ArrInstances.Values
            .Select(a => a.Category)
            .Where(c => !string.IsNullOrWhiteSpace(c))
            .ToList();

        foreach (var (_, qbit) in config.QBitInstances)
        {
            foreach (var managed in qbit.ManagedCategories)
            {
                var match = CategoryPathHelper.MatchesConfigured(managed, arrCategories, prefix: true)
                    ?? CategoryPathHelper.MatchesConfigured(managed, arrCategories, prefix: false);
                if (match is not null)
                {
                    return (false,
                        $"qBit ManagedCategory '{managed}' overlaps Arr category '{match}'.");
                }

                foreach (var arrCat in arrCategories)
                {
                    if (CategoryPathHelper.MatchesConfigured(arrCat, new[] { managed }, prefix: true) is not null
                        && !CategoryPathHelper.CategoryEquals(arrCat, managed))
                    {
                        return (false,
                            $"Arr category '{arrCat}' overlaps qBit ManagedCategory '{managed}'.");
                    }
                }
            }
        }

        return (true, null);
    }

    public static (bool Ok, string? Error) ValidateAll(TorrentarrConfig config)
    {
        foreach (var check in new Func<TorrentarrConfig, (bool, string?)>[]
        {
            ValidateArrCategoryPaths,
            ValidateManagedCategoryPaths,
            ValidateArrManagedCategoryOverlap
        })
        {
            var (ok, error) = check(config);
            if (!ok)
                return (false, error);
        }

        return (true, null);
    }
}

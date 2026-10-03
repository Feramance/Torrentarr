using Torrentarr.Core.Services;

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
        foreach (var (_, qbit) in config.GetAllTorrentClients())
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

        foreach (var (_, qbit) in config.GetAllTorrentClients())
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

    /// <summary>
    /// Refuse AuthDisabled on a public bind when AllowInsecureExposure is explicitly false.
    /// Omitted key (legacy) is allowed — Host logs a warning at startup instead.
    /// </summary>
    public static (bool Ok, string? Error) ValidateInsecureExposure(TorrentarrConfig config)
    {
        if (!config.WebUI.AuthDisabled)
            return (true, null);
        var host = config.WebUI.Host?.Trim() ?? "";
        if (host is not ("0.0.0.0" or "::" or "[::]"))
            return (true, null);
        if (config.WebUI.AllowInsecureExposure != false)
            return (true, null);
        return (false,
            "AllowInsecureExposure must be true when AuthDisabled is true and Host is 0.0.0.0 or ::");
    }

    public static (bool Ok, string? Error) ValidateTorrentClients(TorrentarrConfig config)
    {
        var duplicate = config.QBitInstances.Keys.Intersect(config.TorrentClients.Keys, StringComparer.OrdinalIgnoreCase).FirstOrDefault();
        if (duplicate != null) return (false, $"Torrent client instance '{duplicate}' is declared in both legacy and canonical sections.");
        foreach (var (id, client) in config.GetAllTorrentClients())
        {
            if (string.IsNullOrWhiteSpace(client.Type)) return (false, $"Torrent client '{id}' requires a Type.");
            if (client.Maintenance.Scope is not ("managed" or "all" or "explicit"))
                return (false, $"Torrent client '{id}' has invalid maintenance scope '{client.Maintenance.Scope}'.");
            if (client.Maintenance.PlanTtlMinutes <= 0)
                return (false, $"Torrent client '{id}' maintenance PlanTtlMinutes must be positive.");
            if (CronSchedule.Next(client.Maintenance.Schedule, DateTimeOffset.UtcNow) == null)
                return (false, $"Torrent client '{id}' has an invalid maintenance schedule.");
            var duplicateMapping = client.Maintenance.PathMappings
                .GroupBy(m => m.ClientPath.TrimEnd('/', '\\'), StringComparer.OrdinalIgnoreCase)
                .FirstOrDefault(group => group.Count() > 1)?.Key;
            if (duplicateMapping != null)
                return (false, $"Torrent client '{id}' has duplicate path mapping '{duplicateMapping}'.");
        }
        return (true, null);
    }

    public static (bool Ok, string? Error) ValidateAll(TorrentarrConfig config)
    {
        foreach (var check in new Func<TorrentarrConfig, (bool, string?)>[]
        {
            ValidateArrCategoryPaths,
            ValidateManagedCategoryPaths,
            ValidateArrManagedCategoryOverlap,
            ValidateInsecureExposure,
            ValidateTorrentClients
        })
        {
            var (ok, error) = check(config);
            if (!ok)
                return (false, error);
        }

        return (true, null);
    }
}

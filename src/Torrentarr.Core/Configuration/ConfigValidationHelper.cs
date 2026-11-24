using Torrentarr.Core.Models;
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
            if (client.CategorySeeding.MinimumReportedSeeders < 0 || client.CategorySeeding.MinimumUploadInactivitySeconds < 0
                || client.CategorySeeding.Categories.Any(category => category.MinimumReportedSeeders < 0 || category.MinimumUploadInactivitySeconds < 0))
                return (false, $"Torrent client '{id}': retirement thresholds must be zero or positive.");
            if (string.IsNullOrWhiteSpace(client.Type)) return (false, $"Torrent client '{id}' requires a Type.");
            if (client.AuthMode is not ("legacy" or "api-key"))
                return (false, $"Torrent client '{id}' has an unknown AuthMode.");
            if (!client.Disabled && client.AuthMode == "api-key" && !IsQBitApiKey(client.APIKey))
                return (false, $"Torrent client '{id}' requires a qBittorrent API key starting with qbt_.");
            foreach (var tracker in client.Trackers)
            {
                var protectionError = TrackerProtectionValidation.Validate(tracker.Protection);
                if (protectionError != null) return (false, $"Torrent client '{id}': {protectionError}");
            }
            var storageError = StorageConfigMigration.Validate(client) ?? MaintenanceConfigValidation.Validate(client.Maintenance) ?? StorageConfigurationValidation.Validate(client);
            if (storageError != null) return (false, $"Torrent client '{id}': {storageError}");
        }
        return (true, null);
    }

    public static bool IsQBitApiKey(string? value) => value is { Length: 32 }
        && value.StartsWith("qbt_", StringComparison.Ordinal) && value.Skip(4).All(char.IsAsciiLetterOrDigit);

    public static string? ValidateCorsOrigins(WebUIConfig webui)
    {
        const string error = "CorsAllowedOrigins must contain HTTP(S) origins with a host and optional port, without wildcards, credentials, paths or trailing slashes.";
        if (webui.CorsAllowedOrigins == null) return error;
        foreach (var origin in webui.CorsAllowedOrigins)
            if (string.IsNullOrWhiteSpace(origin) || origin.Trim() != origin || origin.Contains('*')
                || !Uri.TryCreate(origin, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https")
                || string.IsNullOrEmpty(uri.Host) || !uri.IsWellFormedOriginalString() || uri.UserInfo.Length > 0
                || uri.AbsolutePath != "/" || origin.EndsWith('/') || uri.Query.Length > 0 || uri.Fragment.Length > 0
                || origin.AsSpan(origin.IndexOf("://", StringComparison.Ordinal) + 3).IndexOfAny('/', '?', '#') >= 0)
                return error;
        return null;
    }

    public static string? ValidateHnrModes(TorrentarrConfig config)
    {
        static string? Check(string? mode, double? ratio, int? days, int? threshold, double? partial, int? buffer, int? removal)
        {
            if (mode is not (null or "disabled" or "and" or "or" or "ratio" or "time"))
                return "Unknown HitAndRunMode.";
            if (ratio is { } r && (!double.IsFinite(r) || r < 0) ||
                partial is { } p && (!double.IsFinite(p) || p < 0) ||
                days is < 0 || threshold is < 0 or > 100 || buffer is < 0 ||
                removal is not (null or -1 or 1 or 2 or 3 or 4))
                return "Invalid HnR ratio, days, threshold, buffer, or removal mode.";
            return mode switch
            {
                "ratio" when ratio is <= 0 => "Ratio only requires a positive minimum seed ratio.",
                "time" when days is <= 0 => "Seeding time only requires a positive minimum seeding time.",
                _ => null
            };
        }

        foreach (var (id, client) in config.GetAllTorrentClients())
        {
            var defaults = client.CategorySeeding;
            if (Check(defaults.HitAndRunMode, defaults.MinSeedRatio, defaults.MinSeedingTimeDays, defaults.HitAndRunMinimumDownloadPercent, defaults.HitAndRunPartialSeedRatio, defaults.TrackerUpdateBuffer, defaults.RemoveTorrent) is { } error)
                return $"Torrent client '{id}': {error}";
            foreach (var category in defaults.Categories)
                if (Check(category.HitAndRunMode ?? defaults.HitAndRunMode, category.MinSeedRatio ?? defaults.MinSeedRatio,
                    category.MinSeedingTimeDays ?? defaults.MinSeedingTimeDays,
                    category.HitAndRunMinimumDownloadPercent ?? defaults.HitAndRunMinimumDownloadPercent,
                    category.HitAndRunPartialSeedRatio ?? defaults.HitAndRunPartialSeedRatio,
                    category.TrackerUpdateBuffer ?? defaults.TrackerUpdateBuffer, category.RemoveTorrent) is { } categoryError)
                    return $"Torrent client '{id}', category '{category.Name}': {categoryError}";
        }
        foreach (var (id, arr) in config.ArrInstances)
        {
            if (arr.Torrent.MetadataStallMinutes < 0)
                return $"Integration '{id}': MetadataStallMinutes must be zero or positive.";
            if (arr.Search.UnsuccessfulSearchRestMinutes is < 0 or > 10080)
                return $"Integration '{id}': unsuccessful-search rest must be between zero and 10080 minutes.";
            if (arr.Search.IncludeTags.Concat(arr.Search.ExcludeTags).Any(tag => string.IsNullOrWhiteSpace(tag) || tag.Length > 100)
                || arr.Search.IncludeTags.Count > 100 || arr.Search.ExcludeTags.Count > 100)
                return $"Integration '{id}': search tags must be nonempty labels, at most 100 labels of 100 characters.";
            if (arr.Search.RequireRadarrAvailability && !arr.Type.Equals("radarr", StringComparison.OrdinalIgnoreCase))
                return $"Integration '{id}': the availability gate is supported only for Radarr.";
            if (SearchQuietHours.Validate(arr.Search) is { } quietError)
                return $"Integration '{id}': {quietError}";
            if (arr.Search.RecentGrabCooldownMinutes < 0 || arr.Search.ActiveDownloadCeiling < 0)
                return $"Integration '{id}': RecentGrabCooldownMinutes and ActiveDownloadCeiling must be zero or positive.";
            if (arr.Torrent.SeedingMode?.RemoveTorrent is not (null or -1 or 1 or 2 or 3 or 4))
                return $"Integration '{id}': invalid RemoveTorrent mode.";
        }

        foreach (var rule in TrackerRules.All(config))
            if (Check(rule.HitAndRunMode, rule.MinSeedRatio, rule.MinSeedingTimeDays, rule.HitAndRunMinimumDownloadPercent, rule.HitAndRunPartialSeedRatio, rule.TrackerUpdateBuffer, rule.RemoveTorrent) is { } error)
                return $"Tracker '{rule.Name ?? TrackerRules.Host(rule.Uri)}': {error}";

        foreach (var (id, client) in config.GetAllTorrentClients())
        {
            var defaults = client.CategorySeeding;
            var categories = defaults.Categories.Select(category => CategoryPathHelper.NormalizeCategory(category.Name))
                .Concat(config.ArrInstances.Values.Select(arr => CategoryPathHelper.NormalizeCategory(arr.Category)))
                .Prepend("").Distinct(StringComparer.Ordinal);
            foreach (var category in categories)
            {
                var categoryOverride = CategoryPathHelper.FindSeedingOverride(defaults, category, client.MatchSubcategories);
                var torrent = new TorrentInfo { ClientInstanceId = id, Category = category };
                foreach (var rule in TrackerRules.ForTorrent(config, torrent))
                {
                    if (rule.Protection is { RequireLegacyClearance: false }) continue;
                    var mode = rule.HitAndRunMode ?? categoryOverride?.HitAndRunMode ?? defaults.HitAndRunMode;
                    var ratio = rule.MinSeedRatio ?? categoryOverride?.MinSeedRatio ?? defaults.MinSeedRatio;
                    var days = rule.MinSeedingTimeDays ?? categoryOverride?.MinSeedingTimeDays ?? defaults.MinSeedingTimeDays;
                    if (Check(mode, ratio, days, null, null, null, null) is { } error)
                        return $"Tracker '{rule.Name ?? TrackerRules.Host(rule.Uri)}' on '{id}', category '{(category.Length == 0 ? "(default)" : category)}': {error}";
                }
            }
        }
        return null;
    }

    public static string? SharedFileFilterSourceError(string? source)
    {
        if (string.IsNullOrWhiteSpace(source) || source.Length > 2048 || source.Any(char.IsControl))
            return "Shared list source must contain at most 2048 printable characters.";
        if (Uri.TryCreate(source, UriKind.Absolute, out var uri) && !uri.IsFile)
            return uri.Scheme == Uri.UriSchemeHttps && uri.Port == 443 && uri.UserInfo.Length == 0
                && uri.Query.Length == 0 && uri.Fragment.Length == 0
                ? null : "Use an HTTPS URL on port 443 with no credentials, query, or fragment.";
        var segments = source.Split(['/', '\\']);
        return segments.Any(segment => segment.Length == 0 || segment is "." or ".." || segment.Contains(':'))
            ? "Use a relative list path below the Torrentarr data directory." : null;
    }

    public static (bool Ok, string? Error) ValidateAll(TorrentarrConfig config)
    {
        if (config.Settings.HistoryRetentionDays <= 0)
            return (false, "History retention must be a positive number of days.");
        var shared = config.Settings.SharedFileFilters;
        if (shared == null || shared.Count > 32 || shared.Any(item => item == null
            || string.IsNullOrWhiteSpace(item.Name) || item.Name.Length > 80 || item.Name.Any(char.IsControl)
            || SharedFileFilterSourceError(item.Source) != null)
            || shared.Select(item => item.Name).Distinct(StringComparer.Ordinal).Count() != shared.Count)
            return (false, "Shared file filter lists require up to 32 unique printable names and safe relative-file or HTTPS source syntax.");
        if (config.ArrInstances.Values.Any(arr => arr.Torrent.SharedFileFilters == null
            || arr.Torrent.SharedFileFilters.Count > 32
            || arr.Torrent.SharedFileFilters.Distinct(StringComparer.Ordinal).Count() != arr.Torrent.SharedFileFilters.Count
            || arr.Torrent.SharedFileFilters.Any(name => !shared.Any(item => item.Name == name))))
            return (false, "Arr shared file filter references must name a configured list.");

        foreach (var (name, arr) in config.ArrInstances)
            if (FailedTorrentRouting.Validate(config, arr) is { } routeError)
                return (false, $"Integration '{name}': {routeError}");
        if (ValidateHnrModes(config) is { } hnrError) return (false, hnrError);
        if (TrackerRules.Validate(config) is { } trackerError) return (false, trackerError);
        if (ValidateCorsOrigins(config.WebUI) is { } corsError) return (false, corsError);
        if (config.Settings.IssueAlertsEnabled && IssueAlertDestination(config.Settings) == null)
            return (false, "Issue alerts require an HTTP or HTTPS webhook destination without URL user information or a fragment.");
        if (config.Settings.IssueAlertTypes == null || config.Settings.IssueAlertTypes.Any(type => type is not ("worker" or "import" or "tracker")))
            return (false, "Issue alert types must be worker, import, or tracker.");
        if (config.WebUI.Port is < 1 or > 65535 || !System.Net.IPAddress.TryParse(config.WebUI.Host, out _))
            return (false, "WebUI requires an IP bind address and port between 1 and 65535.");

        foreach (var (id, arr) in config.ArrInstances)
            foreach (var tracker in arr.Torrent.Trackers)
            {
                var error = TrackerProtectionValidation.Validate(tracker.Protection);
                if (error != null) return (false, $"Arr '{id}': {error}");
            }

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

    public static Uri? IssueAlertDestination(SettingsConfig settings) =>
        Uri.TryCreate(settings.IssueAlertsSecretUrl, UriKind.Absolute, out var uri) &&
        uri.Scheme is "http" or "https" && uri.Host.Length > 0 && uri.UserInfo.Length == 0 && uri.Fragment.Length == 0
            ? uri : null;
}

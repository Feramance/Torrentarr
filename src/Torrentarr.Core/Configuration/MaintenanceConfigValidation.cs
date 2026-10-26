namespace Torrentarr.Core.Configuration;

public static class MaintenanceConfigValidation
{
    public static string? Validate(MaintenanceConfig? config)
    {
        if (config == null || config.PathMappings == null)
            return "Maintenance collections must be lists, not null.";
        if (config.Scope is not ("managed" or "all" or "explicit")) return "Maintenance.Scope must be managed, all, or explicit.";
        if (!ValidSchedule(config.Schedule)) return "Maintenance.Schedule must be a valid five-field cron expression (UTC).";
        if (config.Orphans == null || config.Hardlinks == null || config.Unregistered == null || config.Repair == null
            || config.RecycleBin == null || config.AutomaticManagement == null)
            return "Maintenance operation settings must not be null.";
        if (config.PlanTtlMinutes < 1 || config.MinimumTorrentAgeMinutes < 0
            || config.Orphans.MinimumAgeMinutes < 0 || config.Orphans.MaxFilesPerRun < 0
            || config.Unregistered.GraceMinutes < 0 || config.Unregistered.MaxPerTrackerPerRun < 0
            || config.Repair.MaxPerRun < 0 || config.RecycleBin.RetentionDays < 0 || config.RecycleBin.MaxTorrentMovesPerRun < 0)
            return "Maintenance ages, retention, and per-run limits must be non-negative; preview TTL must be positive.";
        if (config.CategoryTransitions == null || config.CategoryMappings == null || config.TrackerAssignments == null
            || config.ExplicitCategories == null || config.ExcludedCategories == null || config.ExcludedTags == null
            || config.Orphans.ExcludePatterns == null || config.Hardlinks.Categories == null || config.Hardlinks.ExcludedTags == null
            || config.AutomaticManagement.IgnoredTags == null || config.Unregistered.Messages == null)
            return "Maintenance collections must be lists, not null.";
        if (config.CategoryTransitions.Any(rule => rule == null || string.IsNullOrWhiteSpace(rule.From) || string.IsNullOrWhiteSpace(rule.To) || rule.DelayMinutes < 0))
            return "Category transitions require From, To, and a non-negative delay.";
        if (config.CategoryMappings.Any(rule => rule == null || string.IsNullOrWhiteSpace(rule.ClientPath) || string.IsNullOrWhiteSpace(rule.Category)))
            return "Category mappings require a save path and category.";
        if (config.TrackerAssignments.Any(rule => rule == null || string.IsNullOrWhiteSpace(rule.TrackerContains) || rule.Tags == null))
            return "Tracker assignments require tracker matching text and a tags list.";
        if (config.Operations == null || config.ErrorRecovery == null) return "Maintenance operation policies and recovery must not be null.";
        foreach (var (name, policy) in config.Operations)
        {
            if (!Enum.TryParse<Torrentarr.Core.Models.MaintenanceOperation>(name, out var operation) || !Enum.IsDefined(operation) || policy == null)
                return "Unknown maintenance operation policy.";
            if (policy.ExecutionMode is not ("manual" or "automatic")) return "Invalid operation execution mode.";
            if (policy.Schedule == null || policy.Schedule.Length > 0 && !ValidSchedule(policy.Schedule)) return "Invalid operation schedule (UTC cron).";
            if (policy.MaxActionsPerRun < 0 || policy.IncludedCategories == null || policy.ExcludedCategories == null || policy.ExcludedTags == null)
                return "Invalid operation scope or action cap.";
        }
        var recovery = config.ErrorRecovery;
        if (recovery.Outcome is not ("keep" or "recycle") || recovery.States == null || recovery.MaxRepairAttempts < 1
            || recovery.MaxPerRun < 0 || recovery.RetryMinutes < 1 || recovery.RecheckTimeoutMinutes < 1 || recovery.SustainedFailureMinutes < 1 || recovery.HealthyResetMinutes < 1)
            return "Invalid error recovery policy.";
        if (config.Orphans.ScanRoots == null || config.Orphans.MaxInspectedFilesPerRun < 1 || config.Hardlinks.MaxInspectedTorrentsPerRun < 1 || config.RecycleBin.MaxFilesDeletedPerRun < 0)
            return "Invalid scan roots or inspection/deletion cap.";
        if (config.Orphans.ScanRoots.Any(root => !config.PathMappings.Any(m => m != null && m.LocalPath == root)))
            return "Each orphan scan root must match an explicit local path mapping.";
        var roots = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (mapping, index) in config.PathMappings.Select((value, index) => (value, index)))
        {
            if (mapping == null || mapping.ClientPath == null || mapping.LocalPath == null) return $"PathMappings[{index}] requires both paths.";
            var prefix = mapping.ClientPath.Replace('\\', '/').TrimEnd('/');
            var windows = prefix.StartsWith("//", StringComparison.Ordinal) || prefix.Length >= 2 && char.IsAsciiLetter(prefix[0]) && prefix[1] == ':';
            if (!AbsoluteClientPath(mapping.ClientPath) || Traversal(mapping.ClientPath))
                return $"PathMappings[{index}].ClientPath must be absolute without traversal segments.";
            if (!Path.IsPathFullyQualified(mapping.LocalPath) || Traversal(mapping.LocalPath))
                return $"PathMappings[{index}].LocalPath must be absolute on the Torrentarr host without traversal segments.";
            if (!roots.Add(windows ? prefix.ToUpperInvariant() : prefix))
                return $"PathMappings[{index}].ClientPath duplicates another mapping.";
        }
        return null;
    }

    private static bool ValidSchedule(string? expression)
    {
        if (string.IsNullOrWhiteSpace(expression)) return false;
        var fields = expression.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        (int Min, int Max)[] bounds = [(0, 59), (0, 23), (1, 31), (1, 12), (0, 7)];
        return fields.Length == 5 && fields.Select((field, index) => field.Split(',').All(part =>
        {
            var match = System.Text.RegularExpressions.Regex.Match(part, @"^(\*|[0-9]+(?:-[0-9]+)?)(?:/([0-9]+))?$");
            if (!match.Success || match.Groups[2].Success && (!int.TryParse(match.Groups[2].Value, out var step) || step <= 0)) return false;
            if (match.Groups[1].Value == "*") return true;
            var range = match.Groups[1].Value.Split('-');
            return int.TryParse(range[0], out var start) && int.TryParse(range[^1], out var end)
                && start >= bounds[index].Min && end <= bounds[index].Max && start <= end;
        })).All(valid => valid);
    }

    private static bool Traversal(string path) => path.Replace('\\', '/').Split('/').Any(part => part is "." or "..");
    private static bool AbsoluteClientPath(string path)
    {
        var normalized = path.Replace('\\', '/');
        return normalized.StartsWith('/') || normalized.Length >= 3 && char.IsAsciiLetter(normalized[0]) && normalized[1] == ':' && normalized[2] == '/';
    }
}

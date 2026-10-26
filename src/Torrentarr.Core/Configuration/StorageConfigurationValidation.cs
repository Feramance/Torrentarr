namespace Torrentarr.Core.Configuration;

public static class StorageConfigurationValidation
{
    public static string? Validate(TorrentClientInstanceConfig client)
    {
        var storage = client.Maintenance;
        var recovery = storage?.ErrorRecovery;
        if (storage == null || storage.PathMappings == null || storage.Orphans == null || storage.RecycleBin == null || recovery == null)
            return "Maintenance settings must not be null.";
        var orphan = storage.Orphans;
        var recycle = storage.RecycleBin;
        if (orphan.ScanRoots == null || orphan.ExcludePatterns == null || recovery.States == null || client.Migration?.Notices == null)
            return "Maintenance collections must be lists.";
        if (orphan.MinimumAgeMinutes < 0 || orphan.MaxFilesPerRun < 0 || orphan.MaxInspectedFilesPerRun < 1
            || recycle.RetentionDays < 0 || recycle.MaxFilesDeletedPerRun < 0 || storage.MinimumTorrentAgeMinutes < 0
            || recovery.MaxRepairAttempts < 1 || recovery.MaxPerRun < 0 || recovery.RetryMinutes < 1
            || recovery.RecheckTimeoutMinutes < 1 || recovery.RecheckTimeoutMinutes > 52560000 || recovery.HealthyResetMinutes < 1)
            return "Ages and action limits must be nonnegative; inspection and recovery timeouts must be positive.";
        if (orphan.ScanRoots.Any(root => !storage.PathMappings.Any(m => m != null && m.LocalPath == root)))
            return "Each orphan scan root must match an explicit local path mapping.";
        var roots = new HashSet<string>(StringComparer.Ordinal);
        foreach (var mapping in storage.PathMappings)
        {
            if (mapping == null || mapping.ClientPath == null || mapping.LocalPath == null) return "Path mappings require both paths.";
            var prefix = mapping.ClientPath.Replace('\\', '/').TrimEnd('/');
            var windows = prefix.StartsWith("//", StringComparison.Ordinal) || prefix.Length > 1 && prefix[1] == ':';
            if (!(mapping.ClientPath.StartsWith('/') || windows && prefix.Length > 2 && prefix[2] == '/') || Traversal(mapping.ClientPath)
                || !Path.IsPathFullyQualified(mapping.LocalPath) || Traversal(mapping.LocalPath)) return "Path mappings require absolute paths without traversal.";
            if (!roots.Add(windows ? prefix.ToUpperInvariant() : prefix)) return "Duplicate client path mapping.";
        }
        if (recycle.Path == null || recycle.ResumeDataPath == null || new[] { recycle.Path, recycle.ResumeDataPath }.Any(p => p.Length > 0 && (!Path.IsPathFullyQualified(p) || Traversal(p))))
            return "Recycle and resume-data paths must be absolute without traversal.";
        if (orphan.ScanRoots.Any(p => Path.TrimEndingDirectorySeparator(Path.GetFullPath(p)) == Path.GetPathRoot(Path.GetFullPath(p))))
            return "Filesystem roots cannot be orphan scan roots.";
        return null;
    }
    private static bool Traversal(string path) => path.Replace('\\', '/').Split('/').Any(p => p is "." or "..");
}

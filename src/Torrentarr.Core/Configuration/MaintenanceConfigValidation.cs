namespace Torrentarr.Core.Configuration;

public static class MaintenanceConfigValidation
{
    public static string? Validate(MaintenanceConfig? config)
    {
        if (config == null || config.PathMappings == null)
            return "Maintenance collections must be lists, not null.";
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

    private static bool Traversal(string path) => path.Replace('\\', '/').Split('/').Any(part => part is "." or "..");
    private static bool AbsoluteClientPath(string path)
    {
        var normalized = path.Replace('\\', '/');
        return normalized.StartsWith('/') || normalized.Length >= 3 && char.IsAsciiLetter(normalized[0]) && normalized[1] == ':' && normalized[2] == '/';
    }
}

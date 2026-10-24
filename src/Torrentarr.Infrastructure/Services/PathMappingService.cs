using Torrentarr.Core.Configuration;
using Torrentarr.Core.Services;

namespace Torrentarr.Infrastructure.Services;

public sealed class PathMappingService : IPathMappingService
{
    private static StringComparison LocalComparison => OperatingSystem.IsWindows()
        ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    public string? MapToLocal(string clientPath, IReadOnlyList<PathMappingConfig> mappings)
        => Resolve(clientPath, mappings).LocalPath;

    public PathMappingResult Resolve(string clientPath, IReadOnlyList<PathMappingConfig> mappings)
    {
        if (mappings.Count == 0) return new(null, "missing-mapping", "No explicit path mapping is configured.");
        var normalized = Normalize(clientPath);
        if (!IsAbsoluteClientPath(normalized) || normalized.Split('/').Any(part => part is "." or ".."))
            return new(null, "unsafe-path", "Client file path must be absolute and cannot contain traversal segments.");
        var matches = mappings.Where(m => !string.IsNullOrWhiteSpace(m.ClientPath))
            .Select(m => new { Mapping = m, Prefix = Normalize(m.ClientPath) })
            .Where(m => Matches(normalized, m.Prefix)).OrderByDescending(m => m.Prefix.Length).ToList();
        if (matches.Count == 0) return new(null, "unmatched-prefix", "No configured client prefix matches this file path.");
        var match = matches[0];
        if (matches.Count(m => m.Prefix.Length == match.Prefix.Length) > 1)
            return new(null, "invalid-mapping", "Multiple mappings match the same client prefix.");
        if (!Path.IsPathFullyQualified(match.Mapping.LocalPath) || !IsAbsoluteClientPath(match.Prefix))
            return new(null, "invalid-mapping", "Mappings require absolute client and local roots.", match.Mapping.LocalPath);
        try
        {
            var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(match.Mapping.LocalPath));
            var suffix = normalized[match.Prefix.Length..].TrimStart('/');
            var candidate = Path.GetFullPath(Path.Combine(root, suffix.Replace('/', Path.DirectorySeparatorChar)));
            return IsSafeDescendant(candidate, root)
                ? new(candidate, LocalRoot: root)
                : new(null, "unsafe-path", "Mapped path escapes its configured root.", root);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return new(null, "invalid-mapping", "Mapping contains an invalid filesystem path.", match.Mapping.LocalPath);
        }
    }

    public IReadOnlyList<string> Validate(IReadOnlyList<PathMappingConfig> mappings)
    {
        var errors = new List<string>();
        var prefixes = new HashSet<string>(StringComparer.Ordinal);
        foreach (var mapping in mappings)
        {
            var prefix = Normalize(mapping.ClientPath);
            if (!IsAbsoluteClientPath(prefix) || prefix.Split('/').Any(p => p is "." or "..")
                || !Path.IsPathFullyQualified(mapping.LocalPath))
            {
                errors.Add("Path mappings require absolute ClientPath and LocalPath roots without traversal.");
                continue;
            }
            var key = IsWindowsClientPath(prefix) ? prefix.ToUpperInvariant() : prefix;
            if (!prefixes.Add(key)) errors.Add($"Duplicate or ambiguous client path mapping: {mapping.ClientPath}");
            var error = ValidateLocalPath(mapping.LocalPath, mapping.LocalPath);
            if (error != null) errors.Add($"{error} Local root: {mapping.LocalPath}");
        }
        return errors;
    }

    public string? ValidateLocalPath(string path, string root, bool requireFile = false)
    {
        try
        {
            if (!Path.IsPathFullyQualified(root) || !Path.IsPathFullyQualified(path) || !IsSafeDescendant(path, root))
                return "unsafe-path";
            if (!Directory.Exists(root)) return "inaccessible-local-root";
            // Probe enumeration so an existing but unreadable mount is not treated as usable.
            using (var entries = Directory.EnumerateFileSystemEntries(root).GetEnumerator()) entries.MoveNext();
            var current = Path.GetFullPath(path);
            while (current != null)
            {
                if ((File.Exists(current) || Directory.Exists(current))
                    && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) return "unsafe-path";
                current = Path.GetDirectoryName(current);
            }
            if (requireFile && !File.Exists(path)) return "missing-file";
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return "inaccessible-local-root";
        }
    }

    public bool IsSafeDescendant(string candidate, string root)
    {
        var fullRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        var fullCandidate = Path.GetFullPath(candidate);
        var prefix = Path.EndsInDirectorySeparator(fullRoot) ? fullRoot : fullRoot + Path.DirectorySeparatorChar;
        return fullCandidate.Equals(fullRoot, LocalComparison) || fullCandidate.StartsWith(prefix, LocalComparison);
    }

    private static string Normalize(string path)
    {
        var normalized = path.Replace('\\', '/');
        return normalized == "/" || normalized.Length == 3 && IsWindowsClientPath(normalized) ? normalized : normalized.TrimEnd('/');
    }

    private static bool IsWindowsClientPath(string path) => path.StartsWith("//", StringComparison.Ordinal)
        || (path.Length >= 3 && char.IsAsciiLetter(path[0]) && path[1] == ':' && path[2] == '/');

    private static bool IsAbsoluteClientPath(string path) => path.StartsWith('/') || IsWindowsClientPath(path);

    private static bool Matches(string path, string prefix)
    {
        var comparison = IsWindowsClientPath(prefix) ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        return path.Equals(prefix, comparison) || path.StartsWith(prefix.EndsWith('/') ? prefix : prefix + "/", comparison);
    }
}

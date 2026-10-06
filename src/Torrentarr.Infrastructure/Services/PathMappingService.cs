using Torrentarr.Core.Configuration;
using Torrentarr.Core.Services;

namespace Torrentarr.Infrastructure.Services;

public sealed class PathMappingService : IPathMappingService
{
    private static StringComparison PathComparison => OperatingSystem.IsWindows()
        ? StringComparison.OrdinalIgnoreCase
        : StringComparison.Ordinal;

    public string? MapToLocal(string clientPath, IReadOnlyList<PathMappingConfig> mappings)
    {
        if (string.IsNullOrWhiteSpace(clientPath)) return null;
        var match = mappings
            .Where(m => !string.IsNullOrWhiteSpace(m.ClientPath) && !string.IsNullOrWhiteSpace(m.LocalPath))
            .Select(m => new { Mapping = m, Prefix = NormalizeClientPrefix(m.ClientPath) })
            .Where(x => IsClientPathMatch(clientPath, x.Prefix))
            .OrderByDescending(x => x.Prefix.Length)
            .FirstOrDefault();
        if (match == null) return null;

        var normalizedClientPath = clientPath.Replace('\\', '/');
        var normalizedPrefix = match.Prefix.Replace('\\', '/');
        var suffix = normalizedClientPath[normalizedPrefix.Length..].TrimStart('/');
        var root = Path.GetFullPath(match.Mapping.LocalPath);
        var candidate = Path.GetFullPath(Path.Combine(root, suffix.Replace('/', Path.DirectorySeparatorChar)));
        return IsSafeDescendant(candidate, root) ? candidate : null;
    }

    public IReadOnlyList<string> Validate(IReadOnlyList<PathMappingConfig> mappings)
    {
        var errors = new List<string>();
        var normalized = new HashSet<string>(OperatingSystem.IsWindows()
            ? StringComparer.OrdinalIgnoreCase
            : StringComparer.Ordinal);
        foreach (var mapping in mappings)
        {
            if (string.IsNullOrWhiteSpace(mapping.ClientPath) || string.IsNullOrWhiteSpace(mapping.LocalPath))
            {
                errors.Add("Path mappings require both ClientPath and LocalPath.");
                continue;
            }
            var prefix = NormalizeClientPrefix(mapping.ClientPath);
            if (!normalized.Add(prefix))
                errors.Add($"Duplicate or ambiguous client path mapping: {mapping.ClientPath}");
            try
            {
                var local = Path.GetFullPath(mapping.LocalPath);
                if (!Directory.Exists(local)) errors.Add($"Mapped local root does not exist: {local}");
                else if ((File.GetAttributes(local) & FileAttributes.ReparsePoint) != 0)
                    errors.Add($"Mapped local root cannot be a symlink or reparse point: {local}");
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
            {
                errors.Add($"Invalid local path '{mapping.LocalPath}': {ex.Message}");
            }
        }
        return errors;
    }

    public bool IsSafeDescendant(string candidate, string root)
    {
        var fullRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        var fullCandidate = Path.GetFullPath(candidate);
        if (fullCandidate.Equals(fullRoot, PathComparison)) return true;
        return fullCandidate.StartsWith(fullRoot + Path.DirectorySeparatorChar, PathComparison);
    }

    private static string NormalizeClientPrefix(string path) => path.TrimEnd('/', '\\');

    private static bool IsClientPathMatch(string path, string prefix)
        => path.Equals(prefix, PathComparison)
           || path.StartsWith(prefix + "/", PathComparison)
           || path.StartsWith(prefix + "\\", PathComparison);
}
